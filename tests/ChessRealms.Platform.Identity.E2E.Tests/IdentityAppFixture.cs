using System.Security.Cryptography;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

using Microsoft.Playwright;

using Xunit;

namespace ChessRealms.Platform.Identity.E2E.Tests;

public sealed class IdentityAppFixture : IAsyncLifetime
{
    private IDistributedApplicationTestingBuilder? appHost;
    private DistributedApplication? app;
    private IPlaywright? playwright;
    private HttpClient? mailbox;

    internal Uri WebOrigin { get; private set; } = null!;
    internal HttpClient Mailbox => mailbox!;
    internal IBrowserType Chromium => playwright!.Chromium;

    public async ValueTask InitializeAsync()
    {
        string? runId = Environment.GetEnvironmentVariable("CHESSREALMS_IDENTITY_E2E_RUN_ID");
        Assert.True(Guid.TryParseExact(runId, "N", out _), "Run scripts/test-identity-e2e.ps1 to start these tests.");

        playwright = await Playwright.CreateAsync();
        using RSA signingKey = RSA.Create(2048);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        startup.CancelAfter(TimeSpan.FromMinutes(2));

        appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.ChessRealms_Platform_AppHost>(
                ["--environment=Development", "--Mailpit:Enabled=true", $"--IdentityE2E:RunId={runId}",
                    "--Identity:EmailConfirmation:AutoConfirmOnRegistration=false"], startup.Token);
        ProjectResource api = appHost.Resources.OfType<ProjectResource>().Single(resource => resource.Name == "api");
        appHost.CreateResourceBuilder(api)
            .WithEnvironment("Identity__Jwt__SigningKeyId", "identity-e2e")
            .WithEnvironment("Identity__Jwt__PrivateKeyPem", signingKey.ExportPkcs8PrivateKeyPem())
            .WithEnvironment("Identity__EmailConfirmation__Smtp__Username", string.Empty)
            .WithEnvironment("Identity__EmailConfirmation__Smtp__Password", string.Empty);

        app = await appHost.BuildAsync(startup.Token);
        await WaitForWebAsync(app, startup, cancellation);

        WebOrigin = app.GetEndpoint("web", "https");
        Assert.True(WebOrigin.IsLoopback && WebOrigin.Scheme == Uri.UriSchemeHttps,
            "The browser tests require the AppHost's local HTTPS endpoint.");
        mailbox = app.CreateHttpClient("mailpit", "http");
    }

    public async ValueTask DisposeAsync()
    {
        mailbox?.Dispose();
        playwright?.Dispose();
        try
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }
        finally
        {
            if (appHost is not null)
            {
                await appHost.DisposeAsync();
            }
        }
    }

    private static async Task WaitForWebAsync(DistributedApplication app, CancellationTokenSource startup,
        CancellationToken testCancellation)
    {
        string stage = "starting the Aspire AppHost";
        try
        {
            await app.StartAsync(startup.Token);
            stage = "waiting for Angular to become healthy";
            // The web resource waits for the API, which waits for migrations and Mailpit.
            await app.ResourceNotifications.WaitForResourceHealthyAsync("web", startup.Token);
        }
        catch (OperationCanceledException exception) when (startup.IsCancellationRequested
            && !testCancellation.IsCancellationRequested)
        {
            string[] names = ["postgres", "migrations", "mailpit", "api", "web"];
            string states = string.Join(", ", names.Select(name =>
                app.ResourceNotifications.TryGetCurrentState(name, out ResourceEvent? update)
                    ? $"{name}={update.Snapshot.State?.Text ?? "unknown"} ({update.Snapshot.HealthStatus})"
                    : $"{name}=unreported"));
            throw new TimeoutException($"Timed out while {stage}. Current resource states were {states}.", exception);
        }
    }
}
