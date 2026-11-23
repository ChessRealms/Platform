using System.Collections.Concurrent;

using Microsoft.Playwright;

using Xunit;

using static Microsoft.Playwright.Assertions;

namespace ChessRealms.Platform.Identity.E2E.Tests;

[Collection(nameof(BrowserTestCollection))]
public sealed class IdentityBrowserTests(BrowserAppFixture fixture)
{
    private readonly BrowserTestSteps browserSteps = new(fixture);

    private const string RefreshPath = "/api/identity/web/refresh";
    private const string SessionLockName = "chessrealms.web-session";

    [Fact]
    public async Task RegisterConfirmAndSignInRestoresTheSessionAfterReload()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext context = await browserSteps.NewContextAsync(browser);
        (IPage page, string email) = await browserSteps.RegisterAndOpenSignInAsync(context);
        await browserSteps.SignInAsync(page, email);
        await ReloadAndExpectSignedInAsync(page);
    }

    [Fact]
    public async Task PasswordResetThroughMailpitAllowsNewPasswordAndRevokesExistingSession()
    {
        const string newPassword = "Replacement-password8!";
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext signedIn = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext recovery = await browserSteps.NewContextAsync(browser);
        (IPage existing, string email) = await browserSteps.RegisterAndOpenSignInAsync(signedIn);
        await browserSteps.SignInAsync(existing, email);

        IPage page = await recovery.NewPageAsync();
        await page.GotoAsync("/login");
        await page.GetByRole(AriaRole.Link, new() { Name = "Forgot your password?" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Uri(fixture.WebOrigin, "/forgot-password").AbsoluteUri);
        ILocator emailField = page.GetByLabel("Email", new() { Exact = true });
        await emailField.FillAsync(email);
        await Expect(emailField).ToHaveValueAsync(email);
        IResponse requested = await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new() { Name = "Send reset link" }).ClickAsync(),
            response => response.Request.Method == "POST"
                && new Uri(response.Url).AbsolutePath == "/api/identity/password-reset/request");
        Assert.Equal(202, requested.Status);
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("If a confirmed account uses that email");

        Uri link = await BrowserTestSteps.WaitForEmailLinkAsync(fixture.Mailbox, email, "Reset your ChessRealms password",
            TestContext.Current.CancellationToken);
        Assert.True(link.GetLeftPart(UriPartial.Authority) == fixture.WebOrigin.GetLeftPart(UriPartial.Authority)
            && link.AbsolutePath == "/reset-password" && link.Query.Length == 0 && link.Fragment.Length > 0,
            "The reset email must target the local Angular page and carry the token in the fragment.");
        try
        {
            await page.GotoAsync(link.AbsoluteUri);
            await page.GetByLabel("New password", new() { Exact = true }).WaitForAsync(new() { Timeout = 10000 });
        }
        catch (PlaywrightException)
        {
            Assert.Fail("The Angular password reset form did not open from the Mailpit link.");
        }
        Assert.True(new Uri(page.Url).Fragment.Length == 0, "The reset token should be removed from the address.");
        await page.GetByLabel("New password", new() { Exact = true }).FillAsync(newPassword);
        await page.GetByLabel("Confirm new password", new() { Exact = true }).FillAsync(newPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Reset password" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Your password has been reset.");
        await ExpectRefreshRejectedAsync(existing);
        await page.GetByRole(AriaRole.Link, new() { Name = "Go to sign in" }).ClickAsync();
        await browserSteps.SignInAsync(page, email, newPassword);
    }

    [Fact]
    public async Task ClosingAndRelaunchingChromiumRestoresTheSessionFromTheSameProfile()
    {
        DirectoryInfo profile = Directory.CreateTempSubdirectory("chessrealms-identity-e2e-");
        try
        {
            await using (IBrowserContext original = await LaunchPersistentProfileAsync(profile))
            {
                (IPage page, string email) = await browserSteps.RegisterAndOpenSignInAsync(original);
                await browserSteps.SignInAsync(page, email);
            }

            await using IBrowserContext reopened = await LaunchPersistentProfileAsync(profile);
            IPage restored = await reopened.NewPageAsync();
            IResponse refresh = await BrowserTestSteps.ObserveWebPostAsync(restored, "refresh", () => restored.GotoAsync("/home"));
            Assert.Equal(200, refresh.Status);
            await browserSteps.ExpectSignedInAsync(restored);
        }
        finally
        {
            profile.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentTabReloadsSerializeRefreshAndKeepBothTabsSignedIn()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext context = await browserSteps.NewContextAsync(browser);
        (IPage first, string email) = await browserSteps.RegisterAndOpenSignInAsync(context);
        await browserSteps.SignInAsync(first, email);
        IPage second = await OpenSignedInTabAsync(context);
        IPage lockOwner = await context.NewPageAsync();
        const string lockOwnerPath = "/__identity-e2e/lock-owner";
        await lockOwner.RouteAsync("**" + lockOwnerPath, route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<!doctype html><title>Session lock owner</title>"
        }));
        await lockOwner.GotoAsync(lockOwnerPath);

        // Observe at the context level before either navigation replaces its document.
        ConcurrentQueue<string> firstEvents = new();
        ConcurrentQueue<string> secondEvents = new();
        TaskCompletionSource<int> firstResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<int> secondResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Request += (_, request) => RecordRefresh(request, "sent");
        context.Response += (_, response) => RecordRefresh(response.Request, $"HTTP {response.Status}", response.Status);
        context.RequestFailed += (_, request) => RecordRefresh(request, "failed", 0);

        // Hold the same browser lock so both reloads contend for it without delaying network traffic.
        await lockOwner.EvaluateAsync("""
            name => new Promise((acquired, fail) => {
                navigator.locks.request(name, () => {
                    return new Promise(release => {
                        globalThis.releaseSessionTestLock = release;
                        acquired();
                    });
                }).catch(fail);
            })
            """, SessionLockName).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await Task.WhenAll(ReloadAsync(first), ReloadAsync(second));
        await using IJSHandle queued = await lockOwner.WaitForFunctionAsync("""
            async name => {
                const { pending } = await navigator.locks.query();
                return pending.filter(lock => lock.name === name).length === 2;
            }
            """, SessionLockName, new() { PollingInterval = 50, Timeout = 10000 });

        Assert.Empty(firstEvents);
        Assert.Empty(secondEvents);
        await lockOwner.EvaluateAsync("() => globalThis.releaseSessionTestLock()");
        try
        {
            await Task.WhenAll(firstResponse.Task, secondResponse.Task)
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            string lockState = await DescribeLockStateAsync();
            Assert.Fail($"Expected a refresh response from both tabs. {DescribeRefreshes()} {lockState}");
        }
        Assert.True(await firstResponse.Task == 200 && await secondResponse.Task == 200, DescribeRefreshes());
        await browserSteps.ExpectSignedInAsync(first);
        await browserSteps.ExpectSignedInAsync(second);

        void RecordRefresh(IRequest request, string observation, int? status = null)
        {
            if (request.Method != "POST" || new Uri(request.Url).AbsolutePath != RefreshPath)
            {
                return;
            }

            IPage page = request.Frame.Page;
            if (page == first)
            {
                firstEvents.Enqueue(observation);
                if (status is { } firstStatus)
                {
                    firstResponse.TrySetResult(firstStatus);
                }
            }
            else if (page == second)
            {
                secondEvents.Enqueue(observation);
                if (status is { } secondStatus)
                {
                    secondResponse.TrySetResult(secondStatus);
                }
            }
        }

        string DescribeRefreshes() =>
            $"First tab: [{string.Join(", ", firstEvents)}]. Second tab: [{string.Join(", ", secondEvents)}].";

        async Task<string> DescribeLockStateAsync()
        {
            try
            {
                return await lockOwner.EvaluateAsync<string>("""
                    async name => {
                        const { held, pending } = await navigator.locks.query();
                        const marker = localStorage.getItem(name)?.split(':', 1)[0] ?? 'none';
                        const refreshing = localStorage.getItem(name + '.refresh-in-progress') !== null;
                        return `Session state: ${marker}; compatibility marker present: ${refreshing}; `
                            + `held locks: ${held.filter(lock => lock.name === name).length}; `
                            + `pending locks: ${pending.filter(lock => lock.name === name).length}.`;
                    }
                    """, SessionLockName).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
                return "Shared session diagnostics unavailable.";
            }
        }
    }

    [Fact]
    public async Task ClosingATabDuringRefreshRequiresSignInBeforeAnotherRefresh()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext context = await browserSteps.NewContextAsync(browser);
        (IPage interrupted, string email) = await browserSteps.RegisterAndOpenSignInAsync(context);
        await browserSteps.SignInAsync(interrupted, email);

        TaskCompletionSource refreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await interrupted.RouteAsync("**" + RefreshPath, _ =>
        {
            // Leave the request paused until closing the document aborts it.
            refreshStarted.TrySetResult();
            return Task.CompletedTask;
        });
        await ReloadAsync(interrupted);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await interrupted.CloseAsync();

        IPage restored = await context.NewPageAsync();
        int refreshAttempts = 0;
        restored.Request += (_, request) =>
        {
            if (request.Method == "POST" && new Uri(request.Url).AbsolutePath == RefreshPath)
            {
                Interlocked.Increment(ref refreshAttempts);
            }
        };
        await restored.GotoAsync("/home");
        await Expect(restored.GetByText("We couldn’t verify your session after an interrupted request. Sign in again to continue."))
            .ToBeVisibleAsync();
        Assert.Equal(0, Volatile.Read(ref refreshAttempts));
        await restored.GetByRole(AriaRole.Link, new() { Name = "Go to sign in" }).ClickAsync();
        await browserSteps.SignInAsync(restored, email);
        await ReloadAndExpectSignedInAsync(restored);
    }

    [Fact]
    public async Task SigningOutEndsTheCurrentSessionAndKeepsTheOtherBrowserSignedIn()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext currentContext = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext otherContext = await browserSteps.NewContextAsync(browser);
        (IPage current, string email) = await browserSteps.RegisterAndOpenSignInAsync(currentContext);
        await browserSteps.SignInAsync(current, email);
        IPage sibling = await OpenSignedInTabAsync(currentContext);
        IPage other = await OpenIndependentSessionAsync(otherContext, email);

        await browserSteps.SignOutAsync(current, "Sign out", "logout");
        await browserSteps.ExpectSignedOutAsync(sibling);
        await ExpectRefreshRejectedAsync(current);
        await ReloadAndExpectSignedInAsync(other);
    }

    [Fact]
    public async Task SigningOutEverywhereRejectsTheOtherBrowsersNextRefresh()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext currentContext = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext otherContext = await browserSteps.NewContextAsync(browser);
        (IPage current, string email) = await browserSteps.RegisterAndOpenSignInAsync(currentContext);
        await browserSteps.SignInAsync(current, email);
        IPage other = await OpenIndependentSessionAsync(otherContext, email);

        await browserSteps.SignOutAsync(current, "Sign out of all sessions", "logout-all");
        await ExpectRefreshRejectedAsync(current);
        IResponse refresh = await BrowserTestSteps.ObserveWebPostAsync(other, "refresh", () => ReloadAsync(other));
        Assert.Equal(401, refresh.Status);
        await browserSteps.ExpectSignedOutAsync(other);
    }

    private async Task<IBrowserContext> LaunchPersistentProfileAsync(DirectoryInfo profile)
    {
        IBrowserContext context = await fixture.Chromium.LaunchPersistentContextAsync(profile.FullName, new()
        {
            Channel = "chromium",
            BaseURL = fixture.WebOrigin.AbsoluteUri,
            IgnoreHTTPSErrors = true
        });
        context.SetDefaultTimeout(15000);
        return context;
    }

    private async Task<IPage> OpenSignedInTabAsync(IBrowserContext context)
    {
        IPage page = await context.NewPageAsync();
        IResponse refresh = await BrowserTestSteps.ObserveWebPostAsync(page, "refresh", () => page.GotoAsync("/home"));
        Assert.Equal(200, refresh.Status);
        await browserSteps.ExpectSignedInAsync(page);
        return page;
    }

    private async Task<IPage> OpenIndependentSessionAsync(IBrowserContext context, string email)
    {
        IPage page = await context.NewPageAsync();
        await page.GotoAsync("/login");
        await browserSteps.SignInAsync(page, email);
        return page;
    }

    private async Task ReloadAndExpectSignedInAsync(IPage page)
    {
        IResponse refresh = await BrowserTestSteps.ObserveWebPostAsync(page, "refresh", () => ReloadAsync(page));
        Assert.Equal(200, refresh.Status);
        await browserSteps.ExpectSignedInAsync(page);
    }

    private static async Task ExpectRefreshRejectedAsync(IPage page)
    {
        // Probe the browser's cookie-backed session directly, even though Angular remembers the sign-out.
        IResponse response = await BrowserTestSteps.ObserveWebPostAsync(page, "refresh", () => page.EvaluateAsync("""
            async path => {
                await fetch(path, { method: 'POST', headers: { 'X-ChessRealms-Web': '1' } });
            }
            """, RefreshPath));
        Assert.Equal(401, response.Status);
    }

    private static Task<IResponse?> ReloadAsync(IPage page) =>
        page.ReloadAsync(new() { WaitUntil = WaitUntilState.Commit });
}
