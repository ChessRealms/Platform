using System.Security.Cryptography;

using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;
using ChessRealms.Platform.Infrastructure.Identity.Jwt;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;

using Testcontainers.PostgreSql;

using Xunit;

namespace ChessRealms.Platform.Infrastructure.Tests;

public sealed class LoginTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3").Build();
    private readonly TestClock _clock = new();
    private IHost _host = null!;
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private const string Password = "Strong-password9!";

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(Cancellation);
        using RSA rsa = RSA.Create(2048);
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:platform"] = _postgres.GetConnectionString(),
            ["Identity:Jwt:SigningKeyId"] = "test-key",
            ["Identity:Jwt:PrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem()
        });
        builder.AddInfrastructure();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddIdentityLogin(builder.Configuration);
        builder.Services.AddSingleton<TimeProvider>(_clock);
        _host = builder.Build();
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync(Cancellation);
        RegistrationResult registered = await scope.ServiceProvider.GetRequiredService<IRegistrationService>()
            .RegisterAsync(new("player@example.test", "player", Password), Cancellation);
        Assert.Empty(registered.Errors);
    }

    public async ValueTask DisposeAsync()
    {
        _host?.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task ConfirmedLoginIssuesMinimalAccessTokenAndResetsFailures()
    {
        await UpdateUserAsync(confirmed: true);
        Assert.Equal("InvalidCredentials", (await LoginAsync(password: "wrong")).Code);
        LoginResult result = await LoginAsync(email: " PLAYER@example.test ");
        Assert.Equal("Success", result.Code);
        Assert.Equal("Bearer", result.TokenType);
        Assert.Equal(600, result.ExpiresIn);
        Assert.Null(result.EmailConfirmation);
        JsonWebToken jwt = new(result.AccessToken!);
        Assert.Equal("RS256", jwt.Alg);
        Assert.Equal("at+jwt", jwt.Typ);
        Assert.Equal("test-key", jwt.Kid);
        Assert.Equal("chessrealms-platform", jwt.Issuer);
        Assert.Equal("chessrealms-platform-api", Assert.Single(jwt.Audiences));
        Assert.Equal(TimeSpan.FromMinutes(10), jwt.ValidTo - jwt.IssuedAt);
        Assert.Equal(jwt.IssuedAt, jwt.ValidFrom);
        Assert.Equal(["aud", "exp", "iat", "iss", "jti", "nbf", "sid", "sub"], jwt.Claims.Select(claim => claim.Type).Order().ToArray());
        Assert.NotEqual(jwt.Id, new JsonWebToken((await LoginAsync()).AccessToken!).Id);
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        PlatformUser user = await database.Users.SingleAsync(Cancellation);
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.Equal(1, await database.EmailConfirmationSessions.CountAsync(Cancellation));
    }

    [Fact]
    public async Task InvalidCredentialsNeverExposeConfirmationOrIssueTokens()
    {
        foreach (LoginResult result in new[]
        {
            await LoginAsync(password: "wrong"), await LoginAsync(email: "unknown@example.test"),
            await LoginAsync(email: ""), await LoginAsync(password: ""), await LoginAsync(password: new string('x', 1025))
        })
        {
            Assert.Equal(new LoginResult("InvalidCredentials"), result);
        }
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Equal(1, await database.EmailConfirmationSessions.CountAsync(Cancellation));
        Assert.Equal(1, await database.EmailConfirmationDeliveries.CountAsync(Cancellation));
        Assert.Empty(await database.LoginSessions.ToListAsync(Cancellation));
        Assert.Empty(await database.RefreshTokens.ToListAsync(Cancellation));
    }

    [Fact]
    public async Task UnconfirmedLoginsShareDeliveryLimitsAndRecoverFromPlaceholderFailure()
    {
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<ConfirmationEmailDispatcher>().DispatchNextAsync(Cancellation));
        }
        LoginResult initial = await LoginAsync();
        Assert.Equal("EmailNotConfirmed", initial.Code);
        Assert.Null(initial.AccessToken);
        Assert.Null(initial.RefreshToken);
        Assert.Null(initial.RefreshTokenExpiresAt);
        Assert.Null(initial.TokenType);
        Assert.Null(initial.ExpiresIn);
        Assert.Equal("Failed", initial.EmailConfirmation!.DeliveryStatus);
        Assert.Equal("ProviderNotConfigured", initial.EmailConfirmation.FailureCode);
        _clock.Advance(TimeSpan.FromMinutes(1));
        LoginResult[] concurrent = await Task.WhenAll(LoginAsync(), LoginAsync());
        Assert.All(concurrent, result =>
        {
            Assert.Equal("EmailNotConfirmed", result.Code);
            Assert.Null(result.AccessToken);
            Assert.Equal("Queued", result.EmailConfirmation!.DeliveryStatus);
        });
        Assert.NotEqual(concurrent[0].EmailConfirmation!.SessionToken, concurrent[1].EmailConfirmation!.SessionToken);
        await using AsyncServiceScope check = _host.Services.CreateAsyncScope();
        IdentityDbContext database = check.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Equal(2, await database.EmailConfirmationDeliveries.CountAsync(Cancellation));
        Assert.Empty(await database.LoginSessions.ToListAsync(Cancellation));
        IEmailConfirmationService confirmation = check.ServiceProvider.GetRequiredService<IEmailConfirmationService>();
        string sessionToken = initial.EmailConfirmation.SessionToken!;
        Assert.Equal("EmailNotConfirmed", (await confirmation.GetStatusAsync(sessionToken, Cancellation)).Code);
        Assert.Equal("RateLimited", (await confirmation.ResendAsync(sessionToken, Cancellation)).Code);
    }

    [Fact]
    public async Task ConcurrentFailuresLockAccountAndExpiredLockoutAllowsLogin()
    {
        await UpdateUserAsync(confirmed: true);
        LoginResult[] failures = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => LoginAsync(password: "wrong")));
        Assert.All(failures, result => Assert.Equal("InvalidCredentials", result.Code));
        Assert.Equal(new LoginResult("InvalidCredentials"), await LoginAsync());
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            PlatformUser user = await database.Users.SingleAsync(Cancellation);
            Assert.True(user.LockoutEnd > DateTimeOffset.UtcNow);
            user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(-1);
            await database.SaveChangesAsync(Cancellation);
        }
        Assert.Equal("Success", (await LoginAsync()).Code);
    }

    [Fact]
    public async Task CorrectPasswordResetsFailuresEvenWhenEmailIsUnconfirmed()
    {
        await LoginAsync(password: "wrong");
        Assert.Equal("EmailNotConfirmed", (await LoginAsync()).Code);
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Users.SingleAsync(Cancellation)).AccessFailedCount);
    }

    private async Task<LoginResult> LoginAsync(string email = "player@example.test", string password = Password)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILoginService>().LoginAsync(new(email, password), Cancellation);
    }

    private async Task UpdateUserAsync(bool confirmed)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        PlatformUser user = await database.Users.SingleAsync(Cancellation);
        user.EmailConfirmed = confirmed;
        await database.SaveChangesAsync(Cancellation);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
