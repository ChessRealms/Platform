using System.Security.Cryptography;

using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;

using Testcontainers.PostgreSql;

using Xunit;

namespace ChessRealms.Platform.Infrastructure.Tests;

public sealed class SessionManagementTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3").Build();
    private readonly TestClock _clock = new();
    private IHost _host = null!;
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(Cancellation);
        using RSA rsa = RSA.Create(2048);
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:platform"] = _postgres.GetConnectionString(),
            ["Identity:Jwt:SigningKeyId"] = "session-test",
            ["Identity:Jwt:PrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem()
        });
        builder.AddInfrastructure();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddIdentityLogin(builder.Configuration);
        builder.Services.AddSingleton<TimeProvider>(_clock);
        _host = builder.Build();
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync(Cancellation);
        UserManager<PlatformUser> users = scope.ServiceProvider.GetRequiredService<UserManager<PlatformUser>>();
        Assert.True((await users.CreateAsync(new()
        {
            Email = "one@example.test", UserName = "one", EmailConfirmed = true
        }, "Strong-password9!")).Succeeded);
        Assert.True((await users.CreateAsync(new()
        {
            Email = "two@example.test", UserName = "two", EmailConfirmed = true
        }, "Strong-password9!")).Succeeded);
    }

    public async ValueTask DisposeAsync()
    {
        _host?.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task ListingShowsOnlyUsableOwnSessionsAndIdentifiesCurrent()
    {
        LoginResult first = await LoginAsync("one@example.test");
        _clock.Advance(TimeSpan.FromSeconds(1));
        LoginResult second = await LoginAsync("one@example.test");
        await LoginAsync("two@example.test");
        Guid firstId = SessionId(first);
        Guid secondId = SessionId(second);
        SessionListResult listing = await ListAsync(first);
        Assert.Equal(SessionOperationResult.Success, listing.Result);
        Assert.Equal([firstId, secondId], listing.Sessions.Select(session => session.Id));
        Assert.True(listing.Sessions[0].IsCurrent);
        Assert.False(listing.Sessions[1].IsCurrent);
        Assert.All(listing.Sessions, session => Assert.True(session.ExpiresAt > session.CreatedAt));

        Assert.Equal(SessionOperationResult.Success, await RevokeAsync(first, secondId));
        Assert.Equal(SessionOperationResult.Success, await RevokeAsync(first, secondId));
        Assert.Equal([firstId], (await ListAsync(first)).Sessions.Select(session => session.Id));
        Assert.Equal("InvalidRefreshToken", (await RefreshAsync(second.RefreshToken!)).Code);
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Equal(3, await database.RefreshTokens.CountAsync(Cancellation));
    }

    [Fact]
    public async Task ForeignAndUnknownTargetsHaveSameResultAndCannotBeChanged()
    {
        LoginResult own = await LoginAsync("one@example.test");
        LoginResult foreign = await LoginAsync("two@example.test");
        Assert.Equal(SessionOperationResult.NotFound, await RevokeAsync(own, SessionId(foreign)));
        Assert.Equal(SessionOperationResult.NotFound, await RevokeAsync(own, Guid.NewGuid()));
        Assert.Equal("Success", (await RefreshAsync(foreign.RefreshToken!)).Code);
        Assert.Equal(SessionOperationResult.Success, (await ListAsync(foreign)).Result);
    }

    [Fact]
    public async Task SelfLogoutIsIdempotentAndDoesNotAuthorizeOtherOperations()
    {
        LoginResult first = await LoginAsync("one@example.test");
        LoginResult other = await LoginAsync("one@example.test");
        Assert.Equal(SessionOperationResult.Success, await LogoutAsync(first));
        Assert.Equal(SessionOperationResult.Success, await LogoutAsync(first));
        Assert.Equal(SessionOperationResult.Success, await RevokeAsync(first, SessionId(first)));
        Assert.Equal(SessionOperationResult.Unauthorized, (await ListAsync(first)).Result);
        Assert.Equal(SessionOperationResult.Unauthorized, await RevokeAsync(first, SessionId(other)));
        Assert.Equal(SessionOperationResult.Unauthorized, await LogoutAllAsync(first));
        Assert.Equal("InvalidRefreshToken", (await RefreshAsync(first.RefreshToken!)).Code);
        Assert.Equal("Success", (await RefreshAsync(other.RefreshToken!)).Code);
    }

    [Fact]
    public async Task CleanedUpAndExpiredSelfSessionsCanLogout()
    {
        LoginResult expired = await LoginAsync("one@example.test");
        _clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(SessionOperationResult.Unauthorized, (await ListAsync(expired)).Result);
        Assert.Equal(SessionOperationResult.Success, await LogoutAsync(expired));
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await database.LoginSessions.Where(session => session.Id == SessionId(expired)).ExecuteDeleteAsync(Cancellation);
        }
        Assert.Equal(SessionOperationResult.Success, await LogoutAsync(expired));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("stamp")]
    [InlineData("revoked")]
    public async Task IneligibleCallerCannotManageOtherSessions(string change)
    {
        LoginResult caller = await LoginAsync("one@example.test");
        LoginResult target = await LoginAsync("one@example.test");
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            if (change == "revoked")
            {
                LoginSession session = await database.LoginSessions.SingleAsync(item => item.Id == SessionId(caller), Cancellation);
                session.RevokedAt = _clock.GetUtcNow();
                session.RevocationReason = "ManualRevocation";
                await database.SaveChangesAsync(Cancellation);
            }
            else
            {
                PlatformUser user = await database.Users.SingleAsync(item => item.Email == "one@example.test", Cancellation);
                if (change == "stamp")
                {
                    Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<PlatformUser>>()
                        .UpdateSecurityStampAsync(user)).Succeeded);
                }
                else
                {
                    user.EmailConfirmed = false;
                    await database.SaveChangesAsync(Cancellation);
                }
            }
        }
        Assert.Equal(SessionOperationResult.Unauthorized, (await ListAsync(caller)).Result);
        Assert.Equal(SessionOperationResult.Unauthorized, await RevokeAsync(caller, SessionId(target)));
        Assert.Equal(SessionOperationResult.Unauthorized, await LogoutAllAsync(caller));
    }

    [Fact]
    public async Task LogoutAllRevokesOnlyOwnSessionsAndPreservesRefreshHistory()
    {
        LoginResult first = await LoginAsync("one@example.test");
        LoginResult second = await LoginAsync("one@example.test");
        LoginResult foreign = await LoginAsync("two@example.test");
        Assert.Equal(SessionOperationResult.Success, await LogoutAllAsync(first));
        Assert.Equal(SessionOperationResult.Unauthorized, await LogoutAllAsync(first));
        Assert.Equal("InvalidRefreshToken", (await RefreshAsync(first.RefreshToken!)).Code);
        Assert.Equal("InvalidRefreshToken", (await RefreshAsync(second.RefreshToken!)).Code);
        Assert.Equal("Success", (await RefreshAsync(foreign.RefreshToken!)).Code);
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Equal(4, await database.RefreshTokens.CountAsync(Cancellation));
        Assert.Equal(2, await database.LoginSessions.CountAsync(session => session.RevocationReason == "LogoutAll", Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRefreshAndLogoutNeverLeaveUsableRefreshToken(bool all)
    {
        LoginResult login = await LoginAsync("one@example.test");
        Task<RefreshResult> refresh = RefreshAsync(login.RefreshToken!);
        Task<SessionOperationResult> logout = all ? LogoutAllAsync(login) : LogoutAsync(login);
        await Task.WhenAll(refresh, logout);
        Assert.Equal(SessionOperationResult.Success, await logout);
        RefreshResult refreshed = await refresh;
        if (refreshed.Code == "Success")
        {
            Assert.Equal("InvalidRefreshToken", (await RefreshAsync(refreshed.RefreshToken!)).Code);
        }
        Assert.Equal("InvalidRefreshToken", (await RefreshAsync(login.RefreshToken!)).Code);
        Assert.Equal(SessionOperationResult.Unauthorized, (await ListAsync(login)).Result);
    }

    private async Task<LoginResult> LoginAsync(string email)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        LoginResult result = await scope.ServiceProvider.GetRequiredService<ILoginService>()
            .LoginAsync(new(email, "Strong-password9!"), Cancellation);
        Assert.Equal("Success", result.Code);
        return result;
    }

    private async Task<RefreshResult> RefreshAsync(string token)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRefreshTokenService>().RefreshAsync(new(token), Cancellation);
    }

    private async Task<SessionListResult> ListAsync(LoginResult login)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILoginSessionService>()
            .ListAsync(UserId(login), SessionId(login), Cancellation);
    }

    private async Task<SessionOperationResult> LogoutAsync(LoginResult login)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILoginSessionService>()
            .LogoutAsync(UserId(login), SessionId(login), Cancellation);
    }

    private async Task<SessionOperationResult> LogoutAllAsync(LoginResult login)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILoginSessionService>()
            .LogoutAllAsync(UserId(login), SessionId(login), Cancellation);
    }

    private async Task<SessionOperationResult> RevokeAsync(LoginResult login, Guid target)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILoginSessionService>()
            .RevokeAsync(UserId(login), SessionId(login), target, Cancellation);
    }

    private static Guid UserId(LoginResult login) => Guid.Parse(new JsonWebToken(login.AccessToken!).GetClaim("sub").Value);
    private static Guid SessionId(LoginResult login) => Guid.Parse(new JsonWebToken(login.AccessToken!).GetClaim("sid").Value);

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
