using System.Security.Cryptography;
using System.Text;

using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Jwt;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;
using ChessRealms.Platform.Infrastructure.Identity.Sessions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

using Testcontainers.PostgreSql;

using Xunit;

namespace ChessRealms.Platform.Infrastructure.Tests;

public sealed class RefreshTokenTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3").Build();
    private readonly TestClock _clock = new();
    private IHost _host = null!;
    private FailingIssuer _issuer = null!;
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(Cancellation);
        using RSA rsa = RSA.Create(2048);
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:platform"] = _postgres.GetConnectionString(),
            ["Identity:Jwt:SigningKeyId"] = "refresh-test",
            ["Identity:Jwt:PrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem()
        });
        builder.AddInfrastructure();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddIdentityLogin(builder.Configuration);
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton<IAccessTokenIssuer>(provider => new FailingIssuer(new JwtAccessTokenIssuer(
            provider.GetRequiredService<JwtSigningKey>(), provider.GetRequiredService<IOptions<JwtOptions>>(), _clock)));
        _host = builder.Build();
        _issuer = (FailingIssuer)_host.Services.GetRequiredService<IAccessTokenIssuer>();
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await database.Database.MigrateAsync(Cancellation);
        UserManager<PlatformUser> users = scope.ServiceProvider.GetRequiredService<UserManager<PlatformUser>>();
        IdentityResult created = await users.CreateAsync(new()
        {
            Email = "player@example.test",
            UserName = "player",
            EmailConfirmed = true
        }, "Strong-password9!");
        Assert.True(created.Succeeded);
    }

    public async ValueTask DisposeAsync()
    {
        _host?.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task LoginPersistsOnlyHashAndRotationKeepsSessionAndAbsoluteExpiration()
    {
        LoginResult login = await LoginAsync();
        Assert.Equal("Success", login.Code);
        Assert.Equal(43, login.RefreshToken!.Length);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(_clock.GetUtcNow().ToUnixTimeMilliseconds()).AddDays(30), login.RefreshTokenExpiresAt);
        Guid sessionId = SessionId(login.AccessToken!);
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            RefreshToken stored = await database.RefreshTokens.SingleAsync(Cancellation);
            Assert.Equal(Hash(login.RefreshToken), stored.TokenHash);
            Assert.NotEqual(login.RefreshToken, stored.TokenHash);
            Assert.Null(stored.UsedAt);
            Assert.Equal(sessionId, stored.SessionId);
            LoginSession session = await database.LoginSessions.SingleAsync(Cancellation);
            Assert.Equal(login.RefreshTokenExpiresAt, session.ExpiresAt);
            Assert.Equal((await database.Users.SingleAsync(Cancellation)).SecurityStamp, session.SecurityStamp);
        }

        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True(new JsonWebToken(login.AccessToken!).ValidTo < _clock.GetUtcNow().UtcDateTime);
        RefreshResult rotated = await RefreshAsync(login.RefreshToken);
        Assert.Equal("Success", rotated.Code);
        Assert.Equal("Bearer", rotated.TokenType);
        Assert.Equal(600, rotated.ExpiresIn);
        Assert.NotEqual(login.RefreshToken, rotated.RefreshToken);
        Assert.NotEqual(login.AccessToken, rotated.AccessToken);
        Assert.Equal(sessionId, SessionId(rotated.AccessToken!));
        Assert.NotEqual(new JsonWebToken(login.AccessToken!).Id, new JsonWebToken(rotated.AccessToken!).Id);
        Assert.Equal(login.RefreshTokenExpiresAt, rotated.RefreshTokenExpiresAt);
        await using AsyncServiceScope check = _host.Services.CreateAsyncScope();
        IdentityDbContext db = check.ServiceProvider.GetRequiredService<IdentityDbContext>();
        List<RefreshToken> tokens = await db.RefreshTokens.ToListAsync(Cancellation);
        Assert.Equal(2, tokens.Count);
        Assert.Single(tokens, token => token.UsedAt is null);
        Assert.NotNull(tokens.Single(token => token.TokenHash == Hash(login.RefreshToken)).UsedAt);
    }

    [Fact]
    public async Task ReusingAncestorRevokesEntireSessionButNotAnotherLogin()
    {
        LoginResult first = await LoginAsync();
        LoginResult other = await LoginAsync();
        Assert.NotEqual(SessionId(first.AccessToken!), SessionId(other.AccessToken!));
        RefreshResult second = await RefreshAsync(first.RefreshToken!);
        RefreshResult third = await RefreshAsync(second.RefreshToken!);
        Assert.Equal(new RefreshResult("InvalidRefreshToken"), await RefreshAsync(first.RefreshToken!));
        Assert.Equal(new RefreshResult("InvalidRefreshToken"), await RefreshAsync(third.RefreshToken!));
        Assert.Equal("Success", (await RefreshAsync(other.RefreshToken!)).Code);
        await AssertRevokedAsync(SessionId(first.AccessToken!), "RefreshTokenReuse");
    }

    [Fact]
    public async Task ConcurrentRefreshesReturnOnePairAndRevokeItsSession()
    {
        LoginResult login = await LoginAsync();
        RefreshResult[] results = await Task.WhenAll(RefreshAsync(login.RefreshToken!), RefreshAsync(login.RefreshToken!));
        RefreshResult winner = Assert.Single(results, result => result.Code == "Success");
        Assert.Single(results, result => result.Code == "InvalidRefreshToken");
        Assert.Equal("InvalidRefreshToken", (await RefreshAsync(winner.RefreshToken!)).Code);
        await AssertRevokedAsync(SessionId(login.AccessToken!), "RefreshTokenReuse");
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().RefreshTokens.CountAsync(Cancellation));
    }

    [Fact]
    public async Task UnknownMalformedAndCrossPurposeTokensAreRejectedWithoutRevokingValidSession()
    {
        LoginResult login = await LoginAsync();
        foreach (string token in new[] { "", "bad", new string('x', 43), new string('x', 4096), login.AccessToken! })
        {
            Assert.Equal(new RefreshResult("InvalidRefreshToken"), await RefreshAsync(token));
        }
        Assert.Equal("Success", (await RefreshAsync(login.RefreshToken!)).Code);
    }

    [Fact]
    public async Task RefreshAtAbsoluteExpirationIsRejectedEvenAfterRecentRotation()
    {
        LoginResult login = await LoginAsync();
        _clock.Advance(login.RefreshTokenExpiresAt!.Value - _clock.GetUtcNow() - TimeSpan.FromSeconds(1));
        RefreshResult rotated = await RefreshAsync(login.RefreshToken!);
        Assert.Equal("Success", rotated.Code);
        Assert.Equal(login.RefreshTokenExpiresAt, rotated.RefreshTokenExpiresAt);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new RefreshResult("InvalidRefreshToken"), await RefreshAsync(rotated.RefreshToken!));
    }

    [Theory]
    [InlineData("stamp", "SecurityStampChanged")]
    [InlineData("email", "EmailNotConfirmed")]
    [InlineData("revoked", "ManualRevocation")]
    [InlineData("deleted", null)]
    public async Task IneligibleUserOrRevokedSessionCannotRefresh(string change, string? reason)
    {
        LoginResult login = await LoginAsync();
        Guid sessionId = SessionId(login.AccessToken!);
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            PlatformUser user = await database.Users.SingleAsync(Cancellation);
            switch (change)
            {
                case "stamp":
                    IdentityResult updated = await scope.ServiceProvider.GetRequiredService<UserManager<PlatformUser>>().UpdateSecurityStampAsync(user);
                    Assert.True(updated.Succeeded);
                    break;
                case "email": user.EmailConfirmed = false; break;
                case "deleted": database.Users.Remove(user); break;
                case "revoked":
                    LoginSession session = await database.LoginSessions.SingleAsync(Cancellation);
                    session.RevokedAt = _clock.GetUtcNow();
                    session.RevocationReason = reason;
                    break;
            }
            await database.SaveChangesAsync(Cancellation);
        }
        Assert.Equal(new RefreshResult("InvalidRefreshToken"), await RefreshAsync(login.RefreshToken!));
        if (reason is not null)
        {
            await AssertRevokedAsync(sessionId, reason);
        }
    }

    [Fact]
    public async Task LoginLockoutDoesNotRevokeExistingSession()
    {
        LoginResult login = await LoginAsync();
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            PlatformUser user = await database.Users.SingleAsync(Cancellation);
            user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(5);
            await database.SaveChangesAsync(Cancellation);
        }
        Assert.Equal("Success", (await RefreshAsync(login.RefreshToken!)).Code);
    }

    [Fact]
    public async Task FailedRotationRollsBackConsumptionAndSuccessorInsertion()
    {
        LoginResult login = await LoginAsync();
        _issuer.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RefreshAsync(login.RefreshToken!));
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            RefreshToken token = await database.RefreshTokens.SingleAsync(Cancellation);
            Assert.Equal(Hash(login.RefreshToken!), token.TokenHash);
            Assert.Null(token.UsedAt);
            Assert.Null((await database.LoginSessions.SingleAsync(Cancellation)).RevokedAt);
        }
        _issuer.Fail = false;
        Assert.Equal("Success", (await RefreshAsync(login.RefreshToken!)).Code);
    }

    [Fact]
    public async Task FailedLoginTokenIssuanceDoesNotPersistSessionOrRefreshToken()
    {
        _issuer.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(LoginAsync);
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Empty(await database.LoginSessions.ToListAsync(Cancellation));
        Assert.Empty(await database.RefreshTokens.ToListAsync(Cancellation));
    }

    [Fact]
    public async Task ConfiguredLifetimeIsCapturedForEachNewSession()
    {
        LoginResult original = await LoginAsync();
        _host.Services.GetRequiredService<IOptions<RefreshTokenOptions>>().Value.SessionLifetime = TimeSpan.FromDays(7);
        LoginResult shorter = await LoginAsync();
        Assert.Equal(TimeSpan.FromDays(23), original.RefreshTokenExpiresAt - shorter.RefreshTokenExpiresAt);
        Assert.Equal(original.RefreshTokenExpiresAt, (await RefreshAsync(original.RefreshToken!)).RefreshTokenExpiresAt);
        Assert.Equal(shorter.RefreshTokenExpiresAt, (await RefreshAsync(shorter.RefreshToken!)).RefreshTokenExpiresAt);
    }

    [Fact]
    public async Task CleanupRetainsReplayHistoryUntilExpirationAndCascadesDeletion()
    {
        LoginResult expired = await LoginAsync();
        await RefreshAsync(expired.RefreshToken!);
        await RefreshAsync(expired.RefreshToken!);
        Assert.Equal(0, await CleanupAsync());
        _clock.Advance(TimeSpan.FromDays(1));
        LoginResult active = await LoginAsync();
        _clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(1, await CleanupAsync());
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Equal(SessionId(active.AccessToken!), (await database.LoginSessions.SingleAsync(Cancellation)).Id);
        Assert.Equal(Hash(active.RefreshToken!), (await database.RefreshTokens.SingleAsync(Cancellation)).TokenHash);
    }

    private async Task<LoginResult> LoginAsync()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILoginService>()
            .LoginAsync(new("player@example.test", "Strong-password9!"), Cancellation);
    }

    private async Task<RefreshResult> RefreshAsync(string token)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRefreshTokenService>().RefreshAsync(new(token), Cancellation);
    }

    private async Task<int> CleanupAsync()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LoginSessionCleanup>().DeleteExpiredBatchAsync(Cancellation);
    }

    private async Task AssertRevokedAsync(Guid sessionId, string reason)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        LoginSession session = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().LoginSessions.SingleAsync(item => item.Id == sessionId, Cancellation);
        Assert.NotNull(session.RevokedAt);
        Assert.Equal(reason, session.RevocationReason);
    }

    private static Guid SessionId(string token) => Guid.Parse(new JsonWebToken(token).GetClaim("sid").Value);
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class TestClock : TimeProvider
    {
        // Exercise sub-microsecond .NET ticks that PostgreSQL cannot preserve.
        private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).AddTicks(7);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FailingIssuer(IAccessTokenIssuer inner) : IAccessTokenIssuer
    {
        public bool Fail { get; set; }
        public AccessToken Issue(Guid userId, Guid sessionId) => Fail
            ? throw new InvalidOperationException("Simulated signing failure after persistence.")
            : inner.Issue(userId, sessionId);
    }
}
