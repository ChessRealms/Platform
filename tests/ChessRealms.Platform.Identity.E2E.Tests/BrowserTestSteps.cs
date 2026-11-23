using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Playwright;

using Xunit;

using static Microsoft.Playwright.Assertions;

namespace ChessRealms.Platform.Identity.E2E.Tests;

internal sealed class BrowserTestSteps(BrowserAppFixture fixture)
{
    private const string Password = "Strong-password9!";

    internal Task<IBrowser> LaunchBrowserAsync() => fixture.Chromium.LaunchAsync(new() { Channel = "chromium" });

    internal async Task<IBrowserContext> NewContextAsync(IBrowser browser, bool touch = false)
    {
        IBrowserContext context = await browser.NewContextAsync(new()
        {
            BaseURL = fixture.WebOrigin.AbsoluteUri,
            IgnoreHTTPSErrors = true,
            HasTouch = touch,
            ViewportSize = touch ? new ViewportSize { Width = 390, Height = 844 } : null
        });
        context.SetDefaultTimeout(15000);
        return context;
    }

    internal async Task<(IPage Page, string Email)> RegisterAndOpenSignInAsync(IBrowserContext context)
    {
        IPage page = await context.NewPageAsync();
        string username = $"player-{Guid.NewGuid():N}";
        string email = $"{username}@example.test";

        await page.GotoAsync("/register");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await page.GetByLabel("Username", new() { Exact = true }).FillAsync(username);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        IResponse registration = await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new() { Name = "Create account", Exact = true }).ClickAsync(),
            response => new Uri(response.Url).AbsolutePath == "/api/identity/register"
                && response.Request.Method == "POST",
            new() { Timeout = 30000 });
        Assert.True(registration.Ok, $"Registration returned HTTP {registration.Status}.");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();

        Uri link = await WaitForEmailLinkAsync(fixture.Mailbox, email, "Confirm your ChessRealms email",
            TestContext.Current.CancellationToken);
        Assert.True(link.GetLeftPart(UriPartial.Authority) == fixture.WebOrigin.GetLeftPart(UriPartial.Authority)
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
        await page.CloseAsync();
        return (confirmation, email);
    }

    internal async Task SignInAsync(IPage page, string email, string password = Password)
    {
        await SubmitSignInAsync(page, email, password);
        await ExpectSignedInAsync(page);
    }

    internal async Task SubmitSignInAsync(IPage page, string email, string password = Password)
    {
        ILocator form = page.GetByRole(AriaRole.Main).Locator("form");
        await TypeSignInFieldAsync(form.GetByLabel("Email", new() { Exact = true }), email);
        await TypeSignInFieldAsync(form.GetByLabel("Password", new() { Exact = true }), password);
        // Verify that Angular received the input before waiting for the HTTP response.
        await Expect(form).ToHaveClassAsync(new Regex(@"\bng-valid\b"));
        IResponse response = await ObserveWebPostAsync(page, "login",
            () => form.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync());
        Assert.Equal(200, response.Status);
    }

    private static async Task TypeSignInFieldAsync(ILocator field, string value)
    {
        // Focus the control with a pointer action before typing in a recently activated tab.
        await field.ClickAsync();
        await field.PressAsync("ControlOrMeta+a");
        await field.PressSequentiallyAsync(value);
    }

    internal async Task SignOutAsync(IPage page, string button, string action)
    {
        IResponse response = await ObserveWebPostAsync(page, action,
            () => page.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }).ClickAsync());
        Assert.Equal(204, response.Status);
        await ExpectSignedOutAsync(page);
    }

    internal async Task ExpectSignedInAsync(IPage page)
    {
        await Expect(page).ToHaveURLAsync(new Uri(fixture.WebOrigin, "/home").AbsoluteUri);
        await Expect(page.GetByText("You are signed in.", new() { Exact = true })).ToBeVisibleAsync();
    }

    internal async Task ExpectSignedOutAsync(IPage page)
    {
        await Expect(page).ToHaveURLAsync(new Uri(fixture.WebOrigin, "/login").AbsoluteUri);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in", Exact = true })).ToBeVisibleAsync();
    }

    internal static Task<IResponse> ObserveWebPostAsync(IPage page, string action, Func<Task> performAction)
    {
        string path = $"/api/identity/web/{action}";
        return page.RunAndWaitForResponseAsync(performAction, response => IsWebPost(response, path));
    }

    private static bool IsWebPost(IResponse response, string path) =>
        response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == path;

    internal static async Task<Uri> WaitForEmailLinkAsync(HttpClient mailbox, string email, string subject,
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
                    if (message.GetProperty("Subject").GetString() != subject
                        || !message.GetProperty("To").EnumerateArray().Any(address =>
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
                    throw new InvalidOperationException("The identity email has no HTTPS link.");
                }
            } while (await poll.WaitForNextTickAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Mailpit did not receive the expected identity email within 30 seconds.");
        }
        throw new TimeoutException("The Mailpit polling timer stopped before the identity email arrived.");
    }
}
