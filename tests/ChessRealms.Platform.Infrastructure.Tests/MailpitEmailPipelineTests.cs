using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;
using ChessRealms.Platform.Infrastructure.Identity.Jwt;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Testcontainers.PostgreSql;

using Xunit;

namespace ChessRealms.Platform.Infrastructure.Tests;

public sealed class MailpitEmailPipelineTests : IAsyncLifetime
{
    private const string Password = "Strong-password9!";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3").Build();
    private readonly IContainer _mailpit = new ContainerBuilder("axllent/mailpit:v1.34.0")
        .WithPortBinding(1025, true)
        .WithPortBinding(8025, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
            request.ForPort(8025).ForPath("/api/v1/messages")))
        .Build();
    private readonly HttpClient _mailpitApi = new();
    private IHost _host = null!;
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(Cancellation), _mailpit.StartAsync(Cancellation));
        _mailpitApi.BaseAddress = new Uri($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(8025)}");

        using RSA rsa = RSA.Create(2048);
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:platform"] = _postgres.GetConnectionString(),
            ["Identity:Jwt:SigningKeyId"] = "test-key",
            ["Identity:Jwt:PrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["Identity:EmailConfirmation:ConfirmationUrl"] = "https://localhost:7011/api/identity/confirm-email",
            ["Identity:EmailConfirmation:ResendCooldown"] = "00:00:01",
            ["Identity:PasswordReset:ResetUrl"] = "https://localhost:7011/reset-password",
            ["Identity:EmailConfirmation:Smtp:Host"] = _mailpit.Hostname,
            ["Identity:EmailConfirmation:Smtp:Port"] = _mailpit.GetMappedPublicPort(1025).ToString(),
            ["Identity:EmailConfirmation:Smtp:FromAddress"] = "no-reply@chessrealms.test",
            ["Identity:EmailConfirmation:Smtp:Security"] = "None"
        });
        builder.AddInfrastructure();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddIdentityLogin(builder.Configuration);
        builder.Services.AddIdentityEmailWorker();
        _host = builder.Build();
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync(Cancellation);
        }
        await _host.StartAsync(Cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        _mailpitApi.Dispose();
        await _mailpit.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task RegistrationResendConfirmationAndSmtpFailureUseRealTransport()
    {
        string recipient = $"player-{Guid.NewGuid():N}@example.test";
        RegistrationResult registration = await RegisterAsync(recipient);
        Assert.Empty(registration.Errors);
        Assert.NotNull(registration.UserId);
        string sessionToken = Assert.IsType<ConfirmationMetadata>(registration.EmailConfirmation).SessionToken!;

        Assert.Equal("Sent", await WaitForDeliveryStatusAsync(sessionToken, "Sent"));
        JsonElement[] initialMessages = await WaitForMessagesAsync(recipient, 1);
        JsonElement first = initialMessages[0];
        Uri confirmationLink = await GetConfirmationLinkAsync(first);
        Assert.Equal(Uri.UriSchemeHttps, confirmationLink.Scheme);
        Assert.Equal("/api/identity/confirm-email", confirmationLink.AbsolutePath);

        ConfirmationSessionResult resend;
        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            do
            {
                resend = await ResendAsync(sessionToken);
                if (resend.Code == "Queued")
                {
                    break;
                }
                Assert.Equal("RateLimited", resend.Code);
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            } while (true);
        }
        Assert.Equal("Queued", resend.Code);
        JsonElement[] messages = await WaitForMessagesAsync(recipient, initialMessages.Length + 1);
        Assert.Contains(messages, message => initialMessages.All(initial =>
            initial.GetProperty("ID").GetString() != message.GetProperty("ID").GetString()));
        Assert.Equal("Sent", await WaitForDeliveryStatusAsync(sessionToken, "Sent"));

        string token = QueryHelpers.ParseQuery(confirmationLink.Query)["token"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            IEmailConfirmationService confirmation = scope.ServiceProvider.GetRequiredService<IEmailConfirmationService>();
            Assert.Equal("EmailConfirmed", await confirmation.ConfirmAsync(registration.UserId.Value, token, Cancellation));
        }
        Assert.Equal("Success", (await LoginAsync(recipient)).Code);

        await _mailpit.StopAsync(Cancellation);
        string failedRecipient = $"offline-{Guid.NewGuid():N}@example.test";
        RegistrationResult offlineRegistration = await RegisterAsync(failedRecipient);
        Assert.Empty(offlineRegistration.Errors);
        Assert.Equal("Failed", await WaitForDeliveryStatusAsync(offlineRegistration.EmailConfirmation!.SessionToken!, "Failed"));
        ConfirmationSessionResult failure = await StatusAsync(offlineRegistration.EmailConfirmation.SessionToken!);
        Assert.Equal("ProviderFailure", failure.EmailConfirmation!.FailureCode);
    }

    private async Task<RegistrationResult> RegisterAsync(string email)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRegistrationService>()
            .RegisterAsync(new(email, $"user{Guid.NewGuid():N}", Password), Cancellation);
    }

    [Fact]
    public async Task PasswordResetUsesMailpitAndRevokesAllSessionsExactlyOnce()
    {
        const string newPassword = "Replacement-password8!";
        string email = $"reset-{Guid.NewGuid():N}@example.test";
        RegistrationResult registration = await RegisterAsync(email);
        Guid userId = Assert.IsType<Guid>(registration.UserId);
        await RequestResetAsync(email);
        await RequestResetAsync("unknown@example.test");
        Assert.Equal(0, await CountResetDeliveriesAsync());

        Uri confirmation = await GetConfirmationLinkAsync(Assert.Single(await WaitForMessagesAsync(email, 1)));
        string confirmationToken = QueryHelpers.ParseQuery(confirmation.Query)["token"].ToString();
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            IEmailConfirmationService service = scope.ServiceProvider.GetRequiredService<IEmailConfirmationService>();
            Assert.Equal("EmailConfirmed", await service.ConfirmAsync(userId, confirmationToken, Cancellation));
        }
        Assert.Equal("InvalidResetToken", (await CompleteResetAsync(new(userId, confirmationToken, newPassword))).Code);

        LoginResult firstSession = await LoginAsync(email);
        LoginResult secondSession = await LoginAsync(email);
        Assert.Equal("Success", firstSession.Code);
        Assert.Equal("Success", secondSession.Code);
        await Task.WhenAll(RequestResetAsync(email), RequestResetAsync(email.ToUpperInvariant()));
        Assert.Equal(1, await CountResetDeliveriesAsync());
        JsonElement message = Assert.Single(await WaitForMessagesAsync(email, 2), summary =>
            summary.GetProperty("Subject").GetString() == "Reset your ChessRealms password");
        Uri resetLink = await GetLinkAsync(message);
        Assert.Equal("/reset-password", resetLink.AbsolutePath);
        Assert.Empty(resetLink.Query);
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> linkParameters =
            QueryHelpers.ParseQuery(resetLink.Fragment[1..]);
        Assert.Equal(userId, Guid.Parse(linkParameters["userId"].ToString()));
        ResetPasswordRequest request = new(userId, linkParameters["token"].ToString(), newPassword);

        Assert.Equal("InvalidPassword", (await CompleteResetAsync(request with { NewPassword = "weak" })).Code);
        PasswordResetResult[] attempts = await Task.WhenAll(CompleteResetAsync(request), CompleteResetAsync(request));
        Assert.Single(attempts, result => result.Code == "PasswordReset");
        Assert.Single(attempts, result => result.Code == "InvalidResetToken");
        Assert.Equal("InvalidCredentials", (await LoginAsync(email)).Code);
        Assert.Equal("Success", (await LoginAsync(email, newPassword)).Code);

        await using AsyncServiceScope verification = _host.Services.CreateAsyncScope();
        var database = verification.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Equal(2, await database.LoginSessions.CountAsync(
            session => session.UserId == userId && session.RevocationReason == "PasswordReset", Cancellation));
        var refresh = verification.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        Assert.Equal("InvalidRefreshToken", (await refresh.RefreshAsync(new(firstSession.RefreshToken!), Cancellation)).Code);
        Assert.Equal("InvalidRefreshToken", (await refresh.RefreshAsync(new(secondSession.RefreshToken!), Cancellation)).Code);
    }

    private async Task RequestResetAsync(string email)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPasswordResetService>().RequestAsync(email, Cancellation);
    }

    private async Task<int> CountResetDeliveriesAsync()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>()
            .PasswordResetEmailDeliveries.CountAsync(Cancellation);
    }

    private async Task<PasswordResetResult> CompleteResetAsync(ResetPasswordRequest request)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPasswordResetService>().ResetAsync(request, Cancellation);
    }

    private async Task<LoginResult> LoginAsync(string email, string password = Password)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILoginService>()
            .LoginAsync(new(email, password), Cancellation);
    }

    private async Task<ConfirmationSessionResult> ResendAsync(string token)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailConfirmationService>()
            .ResendAsync(token, Cancellation);
    }

    private async Task<ConfirmationSessionResult> StatusAsync(string token)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailConfirmationService>()
            .GetStatusAsync(token, Cancellation);
    }

    private async Task<string> WaitForDeliveryStatusAsync(string token, string expected)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            string? status = (await StatusAsync(token)).EmailConfirmation?.DeliveryStatus;
            if (status == expected)
            {
                return status;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
        throw new TimeoutException("Email delivery did not reach the expected status.");
    }

    private async Task<JsonElement[]> WaitForMessagesAsync(string recipient, int count)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            using HttpResponseMessage response = await _mailpitApi.GetAsync("/api/v1/messages", timeout.Token);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            JsonElement[] messages = document.RootElement.GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("To").EnumerateArray().Any(address =>
                    address.GetProperty("Address").GetString() == recipient))
                .Select(message => message.Clone()).ToArray();
            if (messages.Length >= count)
            {
                return messages;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
        throw new TimeoutException("Mailpit did not receive the expected message.");
    }

    private Task<Uri> GetConfirmationLinkAsync(JsonElement summary)
    {
        Assert.Equal("Confirm your ChessRealms email", summary.GetProperty("Subject").GetString());
        return GetLinkAsync(summary);
    }

    private async Task<Uri> GetLinkAsync(JsonElement summary)
    {
        string id = summary.GetProperty("ID").GetString()!;
        using HttpResponseMessage response = await _mailpitApi.GetAsync($"/api/v1/message/{Uri.EscapeDataString(id)}", Cancellation);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
        string body = document.RootElement.GetProperty("Text").GetString()!;
        Match link = Regex.Match(body, @"https://\S+");
        Assert.True(link.Success);
        return new Uri(link.Value);
    }
}
