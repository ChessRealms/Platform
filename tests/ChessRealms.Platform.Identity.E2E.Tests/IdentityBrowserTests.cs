using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Playwright;

using Xunit;

using static Microsoft.Playwright.Assertions;

namespace ChessRealms.Platform.Identity.E2E.Tests;

public sealed class IdentityBrowserTests(IdentityAppFixture fixture) : IClassFixture<IdentityAppFixture>
{
    private const string Password = "Strong-password9!";
    private const string RefreshPath = "/api/identity/web/refresh";
    private const string SessionLockName = "chessrealms.web-session";

    [Fact]
    public async Task RegisterConfirmAndSignInRestoresTheSessionAfterReload()
    {
        await using IBrowser browser = await LaunchBrowserAsync();
        await using IBrowserContext context = await NewContextAsync(browser);
        (IPage page, string email) = await RegisterAndOpenSignInAsync(context);
        await SignInAsync(page, email);
        await ReloadAndExpectSignedInAsync(page);
    }

    [Fact]
    public async Task PlayersSignInFromInviteAndSyncPrivateGameAcrossBrowsers()
    {
        await using IBrowser browser = await LaunchBrowserAsync();
        await using IBrowserContext whiteContext = await NewContextAsync(browser);
        await using IBrowserContext blackContext = await NewContextAsync(browser, touch: true);
        (IPage white, string whiteEmail) = await RegisterAndOpenSignInAsync(whiteContext);
        (IPage black, string blackEmail) = await RegisterAndOpenSignInAsync(blackContext);
        await SignInAsync(white, whiteEmail);
        await SignInAsync(black, blackEmail);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        TimeSpan liveTimeout = TimeSpan.FromSeconds(15);
        var subscribed = new TaskCompletionSource<IWebSocketRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var missedMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await white.RouteWebSocketAsync(url => new Uri(url).AbsolutePath == "/api/games/live", socket =>
        {
            bool reconnecting = subscribed.Task.IsCompleted;
            IWebSocketRoute server = socket.ConnectToServer();
            server.OnMessage(frame =>
            {
                foreach (string payload in frame.Text?.Split('\u001e', StringSplitOptions.RemoveEmptyEntries) ?? [])
                {
                    using JsonDocument document = JsonDocument.Parse(payload);
                    JsonElement message = document.RootElement;
                    if (!reconnecting && message.TryGetProperty("target", out JsonElement target)
                        && target.GetString() == "GameChanged")
                    {
                        long revision = message.GetProperty("arguments")[1].GetInt64();
                        if (revision == 1)
                        {
                            accepted.TrySetResult();
                        }
                        else if (revision == 3)
                        {
                            // Drop Black's move notification so White has to read after reconnecting.
                            missedMove.TrySetResult();
                            continue;
                        }
                    }

                    socket.Send(payload + '\u001e');
                    if (message.TryGetProperty("type", out JsonElement type) && type.GetInt32() == 3
                        && !message.TryGetProperty("error", out _))
                    {
                        if (reconnecting)
                        {
                            resubscribed.TrySetResult();
                        }
                        else
                        {
                            subscribed.TrySetResult(socket);
                        }
                    }
                }
            });
        });

        await black.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        ILocator playerId = black.GetByLabel("Your player ID");
        await Expect(playerId).ToHaveValueAsync(new Regex(@"^[0-9a-f-]{36}$"));
        string opponent = await playerId.InputValueAsync();
        await black.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = "Home", Exact = true }).ClickAsync();
        await SignOutAsync(black, "Sign out", "logout");

        await white.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        await Expect(white.GetByLabel("Your player ID")).ToBeVisibleAsync();
        await white.GetByLabel("Opponent’s player ID").FillAsync(opponent);
        await white.GetByRole(AriaRole.Button, new() { Name = "Create private game" }).ClickAsync();
        ILocator invite = white.GetByLabel("Share this link with your opponent");
        await Expect(invite).ToHaveValueAsync(new Regex(@"/games/[0-9a-f-]{36}$"));
        string gameLink = await invite.InputValueAsync();
        IWebSocketRoute whiteSocket = await subscribed.Task.WaitAsync(liveTimeout, cancellationToken);
        await black.GotoAsync(gameLink);
        string gameId = new Uri(gameLink).Segments[^1];
        await Expect(black).ToHaveURLAsync(new Uri(fixture.WebOrigin, $"/login?gameId={gameId}").AbsoluteUri);
        await SubmitSignInAsync(black, blackEmail);
        await Expect(black).ToHaveURLAsync(gameLink);
        ILocator gamesLink = black.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = "Games", Exact = true });
        ILocator gamesList = black.GetByRole(AriaRole.Region, new() { Name = "Your games", Exact = true });
        await gamesLink.ClickAsync();
        await gamesList.GetByRole(AriaRole.Link).Filter(new() { HasText = "Invitation received" }).TapAsync();
        await Expect(black).ToHaveURLAsync(gameLink);
        await black.GetByRole(AriaRole.Button, new() { Name = "Accept game" }).TapAsync();
        await accepted.Task.WaitAsync(liveTimeout, cancellationToken);
        await Expect(white.GetByText("Your turn", new() { Exact = true })).ToBeVisibleAsync();
        await gamesLink.ClickAsync();
        await gamesList.GetByRole(AriaRole.Button, new() { Name = "Active", Exact = true }).TapAsync();
        await gamesList.GetByRole(AriaRole.Link).Filter(new() { HasText = "Opponent’s turn" }).TapAsync();
        await Expect(black).ToHaveURLAsync(gameLink);

        ILocator whitePawn = white.GetByRole(AriaRole.Button, new() { Name = "e2, White pawn" });
        await whitePawn.PressAsync("Enter");
        await Expect(white.Locator("[data-square=e4]")).ToHaveClassAsync(new Regex(@"\blegal\b"));
        await white.Keyboard.PressAsync("ArrowUp");
        await Expect(white.Locator("[data-square=e3]")).ToBeFocusedAsync();
        await white.Keyboard.PressAsync("ArrowUp");
        await white.Keyboard.PressAsync("Enter");
        await Expect(black.GetByRole(AriaRole.Button, new() { Name = "e4, White pawn" })).ToBeVisibleAsync();
        await Expect(black.GetByText("Your turn", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(black.Locator(".square").First).ToHaveAttributeAsync("data-square", "h1");

        await black.GetByRole(AriaRole.Button, new() { Name = "e7, Black pawn" }).TapAsync();
        await black.GetByRole(AriaRole.Button, new() { Name = "e5, empty" }).TapAsync();
        await missedMove.Task.WaitAsync(liveTimeout, cancellationToken);
        await whiteSocket.CloseAsync();
        await resubscribed.Task.WaitAsync(liveTimeout, cancellationToken);
        await Expect(white.GetByRole(AriaRole.Button, new() { Name = "e5, Black pawn" })).ToBeVisibleAsync();
        await white.ReloadAsync();
        await Expect(white.GetByRole(AriaRole.Button, new() { Name = "e5, Black pawn" })).ToBeVisibleAsync();
        await Expect(white.GetByText("e2e4", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(white.GetByText("e7e5", new() { Exact = true })).ToBeVisibleAsync();

        await white.GetByRole(AriaRole.Button, new() { Name = "Offer draw", Exact = true }).ClickAsync();
        await Expect(black.GetByText("Your opponent offered a draw.", new() { Exact = true })).ToBeVisibleAsync();
        await black.GetByRole(AriaRole.Button, new() { Name = "Accept draw", Exact = true }).TapAsync();
        await Expect(black.GetByText("Draw · By agreement", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(white.GetByText("Draw · By agreement", new() { Exact = true })).ToBeVisibleAsync();
        await gamesLink.ClickAsync();
        await gamesList.GetByRole(AriaRole.Button, new() { Name = "Finished", Exact = true }).TapAsync();
        await gamesList.GetByRole(AriaRole.Link).Filter(new() { HasText = "Draw · By agreement" }).TapAsync();
        await Expect(black).ToHaveURLAsync(gameLink);
        await black.ReloadAsync();
        await Expect(black.GetByText("Draw · By agreement", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(black.GetByRole(AriaRole.Button, new() { Name = "Resign", Exact = true })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task OpenGameAndListFollowAccountChangesInTheSameBrowser()
    {
        await using IBrowser browser = await LaunchBrowserAsync();
        await using IBrowserContext context = await NewContextAsync(browser);
        (IPage account, string blackEmail) = await RegisterAndOpenSignInAsync(context);
        await SignInAsync(account, blackEmail);
        await account.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        ILocator playerId = account.GetByLabel("Your player ID");
        await Expect(playerId).ToHaveValueAsync(new Regex(@"^[0-9a-f-]{36}$"));
        string blackId = await playerId.InputValueAsync();

        (IPage board, string whiteEmail) = await RegisterAndOpenSignInAsync(context);
        await SignInAsync(board, whiteEmail);
        await board.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        await board.GetByLabel("Opponent’s player ID").FillAsync(blackId);
        await board.GetByRole(AriaRole.Button, new() { Name = "Create private game" }).ClickAsync();
        await Expect(board.GetByLabel("Share this link with your opponent")).ToBeVisibleAsync();
        string invite = board.Url;
        string inviteLogin = $"/login?gameId={new Uri(invite).Segments[^1]}";
        IPage list = await context.NewPageAsync();
        await list.GotoAsync("/games");
        await Expect(list.GetByText("Waiting for acceptance", new() { Exact = true })).ToBeVisibleAsync();

        await account.BringToFrontAsync();
        await account.GotoAsync("/login");
        await SignInAsync(account, blackEmail);
        await Expect(board.GetByText("You play Black.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(board.GetByRole(AriaRole.Button, new() { Name = "Accept game" })).ToBeEnabledAsync();
        await Expect(list.GetByLabel("Your player ID")).ToHaveValueAsync(blackId);
        await Expect(list.GetByText("Invitation received", new() { Exact = true })).ToBeVisibleAsync();

        await SignOutAsync(account, "Sign out", "logout");
        ILocator boardSignIn = board.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = "Sign in" });
        await Expect(boardSignIn).ToHaveAttributeAsync("href", inviteLogin);
        await Expect(board.Locator(".board")).ToHaveCountAsync(0);
        await Expect(list.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = "Sign in" })).ToBeVisibleAsync();
        await Expect(list.GetByLabel("Your player ID")).ToHaveCountAsync(0);
        await Expect(list.GetByText("Invitation received", new() { Exact = true })).ToHaveCountAsync(0);

        await board.BringToFrontAsync();
        await boardSignIn.ClickAsync();
        await Expect(board).ToHaveURLAsync(new Uri(fixture.WebOrigin, inviteLogin).AbsoluteUri);
        await SubmitSignInAsync(board, whiteEmail);
        await Expect(board).ToHaveURLAsync(invite);
        await Expect(board.GetByText("You play White.", new() { Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task PasswordResetThroughMailpitAllowsNewPasswordAndRevokesExistingSession()
    {
        const string newPassword = "Replacement-password8!";
        await using IBrowser browser = await LaunchBrowserAsync();
        await using IBrowserContext signedIn = await NewContextAsync(browser);
        await using IBrowserContext recovery = await NewContextAsync(browser);
        (IPage existing, string email) = await RegisterAndOpenSignInAsync(signedIn);
        await SignInAsync(existing, email);

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

        Uri link = await WaitForEmailLinkAsync(fixture.Mailbox, email, "Reset your ChessRealms password",
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
        await SignInAsync(page, email, newPassword);
    }

    [Fact]
    public async Task ClosingAndRelaunchingChromiumRestoresTheSessionFromTheSameProfile()
    {
        DirectoryInfo profile = Directory.CreateTempSubdirectory("chessrealms-identity-e2e-");
        try
        {
            await using (IBrowserContext original = await LaunchPersistentProfileAsync(profile))
            {
                (IPage page, string email) = await RegisterAndOpenSignInAsync(original);
                await SignInAsync(page, email);
            }

            await using IBrowserContext reopened = await LaunchPersistentProfileAsync(profile);
            IPage restored = await reopened.NewPageAsync();
            IResponse refresh = await ObserveWebPostAsync(restored, "refresh", () => restored.GotoAsync("/home"));
            Assert.Equal(200, refresh.Status);
            await ExpectSignedInAsync(restored);
        }
        finally
        {
            profile.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentTabReloadsSerializeRefreshAndKeepBothTabsSignedIn()
    {
        await using IBrowser browser = await LaunchBrowserAsync();
        await using IBrowserContext context = await NewContextAsync(browser);
        (IPage first, string email) = await RegisterAndOpenSignInAsync(context);
        await SignInAsync(first, email);
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
        await ExpectSignedInAsync(first);
        await ExpectSignedInAsync(second);

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
        await using IBrowser browser = await LaunchBrowserAsync();
        await using IBrowserContext context = await NewContextAsync(browser);
        (IPage interrupted, string email) = await RegisterAndOpenSignInAsync(context);
        await SignInAsync(interrupted, email);

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
        await SignInAsync(restored, email);
        await ReloadAndExpectSignedInAsync(restored);
    }

    [Fact]
    public async Task SigningOutEndsTheCurrentSessionAndKeepsTheOtherBrowserSignedIn()
    {
        await using IBrowser browser = await LaunchBrowserAsync();
        await using IBrowserContext currentContext = await NewContextAsync(browser);
        await using IBrowserContext otherContext = await NewContextAsync(browser);
        (IPage current, string email) = await RegisterAndOpenSignInAsync(currentContext);
        await SignInAsync(current, email);
        IPage sibling = await OpenSignedInTabAsync(currentContext);
        IPage other = await OpenIndependentSessionAsync(otherContext, email);

        await SignOutAsync(current, "Sign out", "logout");
        await ExpectSignedOutAsync(sibling);
        await ExpectRefreshRejectedAsync(current);
        await ReloadAndExpectSignedInAsync(other);
    }

    [Fact]
    public async Task SigningOutEverywhereRejectsTheOtherBrowsersNextRefresh()
    {
        await using IBrowser browser = await LaunchBrowserAsync();
        await using IBrowserContext currentContext = await NewContextAsync(browser);
        await using IBrowserContext otherContext = await NewContextAsync(browser);
        (IPage current, string email) = await RegisterAndOpenSignInAsync(currentContext);
        await SignInAsync(current, email);
        IPage other = await OpenIndependentSessionAsync(otherContext, email);

        await SignOutAsync(current, "Sign out of all sessions", "logout-all");
        await ExpectRefreshRejectedAsync(current);
        IResponse refresh = await ObserveWebPostAsync(other, "refresh", () => ReloadAsync(other));
        Assert.Equal(401, refresh.Status);
        await ExpectSignedOutAsync(other);
    }

    private Task<IBrowser> LaunchBrowserAsync() => fixture.Chromium.LaunchAsync(new() { Channel = "chromium" });

    private async Task<IBrowserContext> NewContextAsync(IBrowser browser, bool touch = false)
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

    private async Task<(IPage Page, string Email)> RegisterAndOpenSignInAsync(IBrowserContext context)
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

    private async Task SignInAsync(IPage page, string email, string password = Password)
    {
        await SubmitSignInAsync(page, email, password);
        await ExpectSignedInAsync(page);
    }

    private async Task SubmitSignInAsync(IPage page, string email, string password = Password)
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

    private async Task<IPage> OpenSignedInTabAsync(IBrowserContext context)
    {
        IPage page = await context.NewPageAsync();
        IResponse refresh = await ObserveWebPostAsync(page, "refresh", () => page.GotoAsync("/home"));
        Assert.Equal(200, refresh.Status);
        await ExpectSignedInAsync(page);
        return page;
    }

    private async Task<IPage> OpenIndependentSessionAsync(IBrowserContext context, string email)
    {
        IPage page = await context.NewPageAsync();
        await page.GotoAsync("/login");
        await SignInAsync(page, email);
        return page;
    }

    private async Task ReloadAndExpectSignedInAsync(IPage page)
    {
        IResponse refresh = await ObserveWebPostAsync(page, "refresh", () => ReloadAsync(page));
        Assert.Equal(200, refresh.Status);
        await ExpectSignedInAsync(page);
    }

    private async Task SignOutAsync(IPage page, string button, string action)
    {
        IResponse response = await ObserveWebPostAsync(page, action,
            () => page.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }).ClickAsync());
        Assert.Equal(204, response.Status);
        await ExpectSignedOutAsync(page);
    }

    private async Task ExpectSignedInAsync(IPage page)
    {
        await Expect(page).ToHaveURLAsync(new Uri(fixture.WebOrigin, "/home").AbsoluteUri);
        await Expect(page.GetByText("You are signed in.", new() { Exact = true })).ToBeVisibleAsync();
    }

    private async Task ExpectSignedOutAsync(IPage page)
    {
        await Expect(page).ToHaveURLAsync(new Uri(fixture.WebOrigin, "/login").AbsoluteUri);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in", Exact = true })).ToBeVisibleAsync();
    }

    private static async Task ExpectRefreshRejectedAsync(IPage page)
    {
        // Probe the browser's cookie-backed session directly, even though Angular remembers the sign-out.
        IResponse response = await ObserveWebPostAsync(page, "refresh", () => page.EvaluateAsync("""
            async path => {
                await fetch(path, { method: 'POST', headers: { 'X-ChessRealms-Web': '1' } });
            }
            """, RefreshPath));
        Assert.Equal(401, response.Status);
    }

    private static Task<IResponse?> ReloadAsync(IPage page) =>
        page.ReloadAsync(new() { WaitUntil = WaitUntilState.Commit });

    private static Task<IResponse> ObserveWebPostAsync(IPage page, string action, Func<Task> performAction)
    {
        string path = $"/api/identity/web/{action}";
        return page.RunAndWaitForResponseAsync(performAction, response => IsWebPost(response, path));
    }

    private static bool IsWebPost(IResponse response, string path) =>
        response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == path;

    private static async Task<Uri> WaitForEmailLinkAsync(HttpClient mailbox, string email, string subject,
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
