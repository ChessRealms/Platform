using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

using Microsoft.Playwright;

using Xunit;

using static Microsoft.Playwright.Assertions;

namespace ChessRealms.Platform.Identity.E2E.Tests;

public sealed class IdentityBrowserTests
{
    [Fact]
    public async Task RegisterConfirmAndSignInRestoresTheSessionAfterReload()
    {
        string? runId = Environment.GetEnvironmentVariable("CHESSREALMS_IDENTITY_E2E_RUN_ID");
        Assert.True(Guid.TryParseExact(runId, "N", out _), "Run scripts/test-identity-e2e.ps1 to start this test.");

        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync();
        using RSA signingKey = RSA.Create(2048);

        using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        startup.CancelAfter(TimeSpan.FromMinutes(2));
        await using IDistributedApplicationTestingBuilder appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.ChessRealms_Platform_AppHost>(
                ["--environment=Development", "--Mailpit:Enabled=true", $"--IdentityE2E:RunId={runId}",
                    "--Identity:EmailConfirmation:AutoConfirmOnRegistration=false"], startup.Token);
        ProjectResource api = appHost.Resources.OfType<ProjectResource>().Single(resource => resource.Name == "api");
        appHost.CreateResourceBuilder(api)
            .WithEnvironment("Identity__Jwt__SigningKeyId", "identity-e2e")
            .WithEnvironment("Identity__Jwt__PrivateKeyPem", signingKey.ExportPkcs8PrivateKeyPem())
            .WithEnvironment("Identity__EmailConfirmation__Smtp__Username", string.Empty)
            .WithEnvironment("Identity__EmailConfirmation__Smtp__Password", string.Empty);

        await using DistributedApplication app = await appHost.BuildAsync(startup.Token);
        await app.StartAsync(startup.Token);
        // The web resource waits for the API, which waits for migrations and Mailpit.
        await app.ResourceNotifications.WaitForResourceHealthyAsync("web", startup.Token);

        Uri webOrigin = app.GetEndpoint("web", "https");
        Assert.True(webOrigin.IsLoopback && webOrigin.Scheme == Uri.UriSchemeHttps,
            "The browser test requires the AppHost's local HTTPS endpoint.");
        using HttpClient mailbox = app.CreateHttpClient("mailpit", "http");

        await using IBrowserContext context = await browser.NewContextAsync(new()
        {
            BaseURL = webOrigin.AbsoluteUri,
            // The test uses Aspire's local development certificate in an isolated context.
            IgnoreHTTPSErrors = true
        });
        IPage page = await context.NewPageAsync();
        page.SetDefaultTimeout(15000);

        string username = $"player-{Guid.NewGuid():N}";
        string email = $"{username}@example.test";
        const string password = "Strong-password9!";

        await page.GotoAsync("/register");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await page.GetByLabel("Username", new() { Exact = true }).FillAsync(username);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();

        Uri link = await WaitForConfirmationLinkAsync(mailbox, email, cancellation);
        Assert.True(link.GetLeftPart(UriPartial.Authority) == webOrigin.GetLeftPart(UriPartial.Authority)
            && link.AbsolutePath == "/confirm-email" && !string.IsNullOrEmpty(link.Query),
            "The confirmation email must target the local Angular confirmation page.");
        IPage confirmation = await context.NewPageAsync();
        try
        {
            await confirmation.GotoAsync(link.AbsoluteUri);
            await confirmation.GetByText("Your email is confirmed. You can now sign in.", new() { Exact = true })
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        }
        catch (PlaywrightException)
        {
            // Playwright navigation failures may contain the confirmation token in the URL.
            Assert.Fail("The Angular page did not confirm the email from the Mailpit link.");
        }
        Assert.True(new Uri(confirmation.Url).Query.Length == 0, "The confirmation token should be removed from the address.");

        await confirmation.GetByRole(AriaRole.Link, new() { Name = "Go to sign in", Exact = true }).ClickAsync();
        await confirmation.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await confirmation.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await confirmation.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Expect(confirmation).ToHaveURLAsync(new Uri(webOrigin, "/home").AbsoluteUri);
        await Expect(confirmation.GetByText("You are signed in.", new() { Exact = true })).ToBeVisibleAsync();

        IResponse refresh = await confirmation.RunAndWaitForResponseAsync(() => confirmation.ReloadAsync(),
            response => new Uri(response.Url).AbsolutePath == "/api/identity/web/refresh"
                && response.Request.Method == "POST");
        Assert.Equal(200, refresh.Status);
        await Expect(confirmation).ToHaveURLAsync(new Uri(webOrigin, "/home").AbsoluteUri);
        await Expect(confirmation.GetByText("You are signed in.", new() { Exact = true })).ToBeVisibleAsync();
    }

    private static async Task<Uri> WaitForConfirmationLinkAsync(HttpClient mailbox, string email,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using PeriodicTimer poll = new(TimeSpan.FromMilliseconds(250));
            do
            {
                JsonElement result = await mailbox.GetFromJsonAsync<JsonElement>("/api/v1/messages", timeout.Token);
                foreach (JsonElement message in result.GetProperty("messages").EnumerateArray())
                {
                    if (!message.GetProperty("To").EnumerateArray().Any(address =>
                        address.GetProperty("Address").GetString() == email))
                    {
                        continue;
                    }

                    string id = message.GetProperty("ID").GetString()!;
                    JsonElement detail = await mailbox.GetFromJsonAsync<JsonElement>(
                        $"/api/v1/message/{Uri.EscapeDataString(id)}", timeout.Token);
                    string body = detail.GetProperty("Text").GetString() ?? "";
                    Match link = Regex.Match(body, @"https://\S+");
                    if (link.Success && Uri.TryCreate(link.Value, UriKind.Absolute, out Uri? uri))
                    {
                        return uri;
                    }
                    throw new InvalidOperationException("The confirmation email has no HTTPS link.");
                }
            } while (await poll.WaitForNextTickAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Mailpit did not receive the test account's confirmation email within 30 seconds.");
        }
        throw new TimeoutException("The Mailpit polling timer stopped before the confirmation email arrived.");
    }
}
