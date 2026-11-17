using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Testcontainers.PostgreSql;

using Xunit;

namespace ChessRealms.Platform.Infrastructure.Tests;

public sealed class RegistrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3").Build();
    private readonly TestClock _clock = new();
    private readonly RecordingSender _sender = new();
    private IHost _host = null!;
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(Cancellation);
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:platform"] = _postgres.GetConnectionString(),
            ["Identity:EmailConfirmation:MaxDeliveriesPerWindow"] = "2"
        });
        builder.AddInfrastructure();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton<IConfirmationEmailSender>(_sender);
        _host = builder.Build();
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync(Cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        _host?.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task RegisterQueuesEmailAndConfirmationLinkWorksWithoutSession()
    {
        RegistrationResult result = await RegisterAsync();
        Assert.Empty(result.Errors);
        Assert.NotNull(result.UserId);
        ConfirmationMetadata metadata = Assert.IsType<ConfirmationMetadata>(result.EmailConfirmation);
        Assert.Equal("Queued", metadata.DeliveryStatus);
        Assert.Equal(43, metadata.SessionToken!.Length);
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            PlatformUser user = await database.Users.SingleAsync(Cancellation);
            Assert.False(user.EmailConfirmed);
            Assert.NotEqual("Strong-password9!", user.PasswordHash);
            EmailConfirmationSession session = await database.EmailConfirmationSessions.SingleAsync(Cancellation);
            Assert.NotEqual(metadata.SessionToken, session.TokenHash);
            Assert.Equal(64, session.TokenHash.Length);
            Assert.True(await scope.ServiceProvider.GetRequiredService<ConfirmationEmailDispatcher>().DispatchNextAsync(Cancellation));
        }
        ConfirmationEmail email = Assert.Single(_sender.Emails);
        Assert.Equal("player@example.test", email.Recipient);
        string confirmationToken = QueryHelpers.ParseQuery(new Uri(email.ConfirmationLink).Query)["token"].ToString();
        Assert.NotEqual(metadata.SessionToken, confirmationToken);
        Assert.Equal("Sent", (await StatusAsync(metadata.SessionToken)).EmailConfirmation!.DeliveryStatus);
        _clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal("InvalidSession", (await StatusAsync(metadata.SessionToken)).Code);
        await using AsyncServiceScope confirmationScope = _host.Services.CreateAsyncScope();
        IRegistrationService service = confirmationScope.ServiceProvider.GetRequiredService<IRegistrationService>();
        Assert.Equal("InvalidConfirmationToken", await service.ConfirmAsync(result.UserId.Value, "bad!", Cancellation));
        Assert.Equal("InvalidConfirmationToken", await service.ConfirmAsync(Guid.NewGuid(), confirmationToken, Cancellation));
        Assert.Equal("EmailConfirmed", await service.ConfirmAsync(result.UserId.Value, confirmationToken, Cancellation));
        Assert.Equal("AlreadyConfirmed", await service.ConfirmAsync(result.UserId.Value, confirmationToken, Cancellation));
    }

    [Fact]
    public async Task InvalidAndDuplicateRegistrationsDoNotCreateWorkflowRows()
    {
        await RegisterAsync();
        Assert.NotEmpty((await RegisterAsync("PLAYER@example.test", "other")).Errors);
        Assert.NotEmpty((await RegisterAsync("other@example.test", "PLAYER")).Errors);
        Assert.NotEmpty((await RegisterAsync("invalid", "valid")).Errors);
        Assert.NotEmpty((await RegisterAsync("valid@example.test", "valid", "weak")).Errors);
        Assert.NotEmpty((await RegisterAsync("", "", "")).Errors);
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Equal(1, await database.Users.CountAsync(Cancellation));
        Assert.Equal(1, await database.EmailConfirmationSessions.CountAsync(Cancellation));
        Assert.Equal(1, await database.EmailConfirmationDeliveries.CountAsync(Cancellation));
    }

    [Fact]
    public async Task ConcurrentDuplicateRegistrationsCreateOnlyOneUserAndWorkflow()
    {
        RegistrationResult[] results = await Task.WhenAll(RegisterAsync(), RegisterAsync());
        Assert.Single(results, result => result.Errors.Count == 0);
        Assert.Single(results, result => result.Errors.Any(error => error.Code.StartsWith("Duplicate", StringComparison.Ordinal)));
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Equal(1, await database.Users.CountAsync(Cancellation));
        Assert.Equal(1, await database.EmailConfirmationSessions.CountAsync(Cancellation));
        Assert.Equal(1, await database.EmailConfirmationDeliveries.CountAsync(Cancellation));
    }

    [Fact]
    public async Task ConcurrentResendsShareUserLimitAcrossSessionsAndWindows()
    {
        RegistrationResult registered = await RegisterAsync();
        string token = registered.EmailConfirmation!.SessionToken!;
        Assert.Equal("RateLimited", (await ResendAsync(token)).Code);
        string secondToken = new('a', 43);
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            database.EmailConfirmationSessions.Add(new()
            {
                UserId = registered.UserId!.Value,
                TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secondToken))),
                ExpiresAt = _clock.GetUtcNow().AddHours(2)
            });
            await database.SaveChangesAsync(Cancellation);
        }
        _clock.Advance(TimeSpan.FromMinutes(1));
        ConfirmationSessionResult[] results = await Task.WhenAll(ResendAsync(token), ResendAsync(secondToken));
        Assert.Single(results, result => result.Code == "Queued");
        Assert.Single(results, result => result.Code == "RateLimited");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("RateLimited", (await ResendAsync(secondToken)).Code);
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("Queued", (await ResendAsync(secondToken)).Code);
        Assert.Equal("InvalidSession", (await ResendAsync("invalid")).Code);
    }

    [Fact]
    public async Task ProviderFailureDoesNotUndoRegistrationAndResendCanRecover()
    {
        RegistrationResult result = await RegisterAsync();
        _sender.Fail = true;
        await DispatchAsync();
        ConfirmationSessionResult failed = await StatusAsync(result.EmailConfirmation!.SessionToken!);
        Assert.Equal("Failed", failed.EmailConfirmation!.DeliveryStatus);
        Assert.Equal("ProviderFailure", failed.EmailConfirmation.FailureCode);
        _sender.Fail = false;
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("Queued", (await ResendAsync(result.EmailConfirmation.SessionToken!)).Code);
        await DispatchAsync();
        Assert.Equal("Sent", (await StatusAsync(result.EmailConfirmation.SessionToken!)).EmailConfirmation!.DeliveryStatus);
    }

    [Fact]
    public async Task ConfirmedUserCannotResendAndQueuedDeliveriesAreCancelled()
    {
        RegistrationResult result = await RegisterAsync();
        await DispatchAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await ResendAsync(result.EmailConfirmation!.SessionToken!);
        string token = QueryHelpers.ParseQuery(new Uri(Assert.Single(_sender.Emails).ConfirmationLink).Query)["token"].ToString();
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            Assert.Equal("EmailConfirmed", await scope.ServiceProvider.GetRequiredService<IRegistrationService>()
                .ConfirmAsync(result.UserId!.Value, token, Cancellation));
        }
        Assert.Equal("EmailConfirmed", (await ResendAsync(result.EmailConfirmation.SessionToken!)).Code);
        await DispatchAsync();
        Assert.Single(_sender.Emails);
        Assert.Equal("Cancelled", (await StatusAsync(result.EmailConfirmation.SessionToken!)).EmailConfirmation!.DeliveryStatus);
    }

    [Fact]
    public async Task ExpiredConfirmationTokenIsRejected()
    {
        RegistrationResult result = await RegisterAsync();
        await DispatchAsync();
        string token = QueryHelpers.ParseQuery(new Uri(Assert.Single(_sender.Emails).ConfirmationLink).Query)["token"].ToString();
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        // Identity's provider uses UTC directly; a negative lifetime makes the generated token expired without sleeping.
        scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DataProtectionTokenProviderOptions>>()
            .Value.TokenLifespan = TimeSpan.FromSeconds(-1);
        Assert.Equal("InvalidConfirmationToken", await scope.ServiceProvider.GetRequiredService<IRegistrationService>()
            .ConfirmAsync(result.UserId!.Value, token, Cancellation));
    }

    [Fact]
    public async Task PlaceholderNeverReportsSent()
    {
        EmailSendResult result = await new UnconfiguredConfirmationEmailSender()
            .SendAsync(new(Guid.NewGuid(), "player@example.test", "https://example.test"), Cancellation);
        Assert.False(result.Accepted);
        Assert.Equal("ProviderNotConfigured", result.FailureCode);
    }

    private async Task<RegistrationResult> RegisterAsync(string email = "player@example.test", string username = "player", string password = "Strong-password9!")
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRegistrationService>().RegisterAsync(new(email, username, password), Cancellation);
    }

    private async Task<ConfirmationSessionResult> ResendAsync(string token)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRegistrationService>().ResendAsync(token, Cancellation);
    }

    private async Task<ConfirmationSessionResult> StatusAsync(string token)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRegistrationService>().GetStatusAsync(token, Cancellation);
    }

    private async Task DispatchAsync()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ConfirmationEmailDispatcher>().DispatchNextAsync(Cancellation));
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class RecordingSender : IConfirmationEmailSender
    {
        public List<ConfirmationEmail> Emails { get; } = [];
        public bool Fail { get; set; }
        public Task<EmailSendResult> SendAsync(ConfirmationEmail email, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Simulated provider failure.");
            }
            Emails.Add(email);
            return Task.FromResult(new EmailSendResult(true));
        }
    }
}
