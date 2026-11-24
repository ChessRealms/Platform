using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Playwright;

using Xunit;

using static Microsoft.Playwright.Assertions;

namespace ChessRealms.Platform.Identity.E2E.Tests;

[Collection(nameof(BrowserTestCollection))]
public sealed class GameBrowserTests(BrowserAppFixture fixture)
{
    private readonly BrowserTestSteps browserSteps = new(fixture);

    [Fact]
    public async Task PlayersOpenACopiedLobbyLinkAcrossSignInAndPlayTheFirstMove()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext whiteContext = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext blackContext = await browserSteps.NewContextAsync(browser);
        await whiteContext.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        (IPage white, string whiteEmail) = await browserSteps.RegisterAndOpenSignInAsync(whiteContext);
        (IPage black, string blackEmail) = await browserSteps.RegisterAndOpenSignInAsync(blackContext);
        await browserSteps.SignInAsync(white, whiteEmail);

        await white.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        string host = await ReadUsernameAsync(white);
        await white.GetByRole(AriaRole.Button, new() { Name = "Create private link", Exact = true }).ClickAsync();
        ILocator link = white.GetByLabel("Invitation link", new() { Exact = true });
        await Expect(link).ToHaveValueAsync(new Regex(@"/lobbies/[0-9a-f-]{36}$"));
        await white.GetByRole(AriaRole.Button, new() { Name = "Copy link", Exact = true }).ClickAsync();
        await Expect(white.GetByText("Link copied.", new() { Exact = true })).ToBeVisibleAsync();
        string copiedLink = await white.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        Assert.Equal(await link.InputValueAsync(), copiedLink);

        await white.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = "Games", Exact = true }).ClickAsync();
        await white.GetByRole(AriaRole.Region, new() { Name = "Your games" })
            .GetByRole(AriaRole.Link).Filter(new() { HasText = "Waiting for a player" }).ClickAsync();
        await Expect(white).ToHaveURLAsync(copiedLink);

        await black.GotoAsync(copiedLink);
        string lobbyId = new Uri(copiedLink).Segments[^1];
        await Expect(black).ToHaveURLAsync(new Uri(fixture.WebOrigin, $"/login?lobbyId={lobbyId}").AbsoluteUri);
        await browserSteps.SubmitSignInAsync(black, blackEmail);
        await Expect(black).ToHaveURLAsync(copiedLink);
        await Expect(black.GetByText($"Invitation from {host}", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(white.GetByText("Waiting for a player", new() { Exact = true })).ToBeVisibleAsync();
        await black.GetByRole(AriaRole.Button, new() { Name = "Join game", Exact = true }).ClickAsync();

        await Expect(white.GetByText("Your turn", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(black).ToHaveURLAsync(white.Url);
        await white.GetByRole(AriaRole.Button, new() { Name = "e2, White pawn", Exact = true }).ClickAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "e4, empty", Exact = true }).ClickAsync();
        await Expect(black.GetByRole(AriaRole.Button, new() { Name = "e4, White pawn", Exact = true })).ToBeVisibleAsync();
        await Expect(black.GetByText("Your turn", new() { Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task PlayersSendAndAcceptAnInvitationByUsernameAndPlayTheFirstMove()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext whiteContext = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext blackContext = await browserSteps.NewContextAsync(browser);
        (IPage white, string whiteEmail) = await browserSteps.RegisterAndOpenSignInAsync(whiteContext);
        (IPage black, string blackEmail) = await browserSteps.RegisterAndOpenSignInAsync(blackContext);
        await browserSteps.SignInAsync(white, whiteEmail);
        await browserSteps.SignInAsync(black, blackEmail);

        await black.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        string blackUsername = await ReadUsernameAsync(black);
        await Expect(black.GetByText("No pending invitations.", new() { Exact = true })).ToBeVisibleAsync();

        await white.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        string whiteUsername = await ReadUsernameAsync(white);
        await SendInvitationAsync(white, blackUsername);
        await Expect(white.GetByLabel("Game link")).ToBeVisibleAsync();
        await Expect(white.GetByText(new Regex(@"^Expires in [0-5]:[0-5][0-9]$"))).ToBeVisibleAsync();

        ILocator gamesList = black.GetByRole(AriaRole.Region, new() { Name = "Your games", Exact = true });
        await gamesList.GetByRole(AriaRole.Button, new() { Name = "Refresh", Exact = true }).ClickAsync();
        await gamesList.GetByRole(AriaRole.Link).Filter(new()
        {
            Has = black.GetByText(whiteUsername, new() { Exact = true }),
            HasText = "Invitation received"
        }).ClickAsync();
        await black.GetByRole(AriaRole.Button, new() { Name = "Accept game", Exact = true }).ClickAsync();

        await Expect(white.GetByText("Your turn", new() { Exact = true })).ToBeVisibleAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "e2, White pawn", Exact = true }).ClickAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "e4, empty", Exact = true }).ClickAsync();
        await Expect(black.GetByRole(AriaRole.Button, new() { Name = "e4, White pawn", Exact = true })).ToBeVisibleAsync();
        await Expect(black.GetByText("Your turn", new() { Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task PlayersAcceptARematchWithSwappedColorsAndPlayTheFirstMove()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext whiteContext = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext blackContext = await browserSteps.NewContextAsync(browser);
        (IPage white, string whiteEmail) = await browserSteps.RegisterAndOpenSignInAsync(whiteContext);
        (IPage black, string blackEmail) = await browserSteps.RegisterAndOpenSignInAsync(blackContext);
        await browserSteps.SignInAsync(white, whiteEmail);
        await browserSteps.SignInAsync(black, blackEmail);
        await black.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        string blackUsername = await ReadUsernameAsync(black);
        await white.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        await SendInvitationAsync(white, blackUsername);
        ILocator gameLink = white.GetByLabel("Game link", new() { Exact = true });
        await Expect(gameLink).ToBeVisibleAsync();
        string originalUrl = await gameLink.InputValueAsync();
        await black.GotoAsync(originalUrl);
        await black.GetByRole(AriaRole.Button, new() { Name = "Accept game", Exact = true }).ClickAsync();

        await white.GetByRole(AriaRole.Button, new() { Name = "Resign", Exact = true }).ClickAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "Confirm resignation", Exact = true }).ClickAsync();
        await Expect(black.GetByText("Black wins · Resignation", new() { Exact = true })).ToBeVisibleAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "Rematch", Exact = true }).ClickAsync();
        await Expect(white.GetByText("Rematch offered.", new() { Exact = true })).ToBeVisibleAsync();
        await white.ReloadAsync();
        await Expect(white.GetByText("Rematch offered.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(black.GetByText(new Regex(@"^Expires in [0-5]:[0-5][0-9]$"))).ToBeVisibleAsync();
        await black.GetByRole(AriaRole.Button, new() { Name = "Accept rematch", Exact = true }).ClickAsync();

        await Expect(black.GetByText("You play White.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(white.GetByText("You play Black.", new() { Exact = true })).ToBeVisibleAsync();
        Assert.NotEqual(originalUrl, black.Url);
        await Expect(white).ToHaveURLAsync(black.Url);
        await black.GetByRole(AriaRole.Button, new() { Name = "e2, White pawn", Exact = true }).ClickAsync();
        await black.GetByRole(AriaRole.Button, new() { Name = "e4, empty", Exact = true }).ClickAsync();
        await Expect(white.GetByRole(AriaRole.Button, new() { Name = "e4, White pawn", Exact = true })).ToBeVisibleAsync();

        await white.GotoAsync(originalUrl);
        await Expect(white.GetByText("Black wins · Resignation", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(white.GetByRole(AriaRole.Link, new() { Name = "Open rematch" }))
            .ToHaveAttributeAsync("href", new Uri(black.Url).AbsolutePath);
    }

    [Theory]
    [InlineData(true, "Decline", "Invitation declined")]
    [InlineData(false, "Cancel invitation", "Invitation cancelled")]
    public async Task BothPlayersSeeTheClosedInvitation(bool recipientActs, string action, string outcome)
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext whiteContext = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext blackContext = await browserSteps.NewContextAsync(browser);
        (IPage white, string whiteEmail) = await browserSteps.RegisterAndOpenSignInAsync(whiteContext);
        (IPage black, string blackEmail) = await browserSteps.RegisterAndOpenSignInAsync(blackContext);
        await browserSteps.SignInAsync(white, whiteEmail);
        await browserSteps.SignInAsync(black, blackEmail);

        await black.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        string blackUsername = await ReadUsernameAsync(black);
        await white.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        await SendInvitationAsync(white, blackUsername);
        await Expect(white.GetByLabel("Game link")).ToBeVisibleAsync();
        ILocator blackList = black.GetByRole(AriaRole.Region, new() { Name = "Your games", Exact = true });
        await blackList.GetByRole(AriaRole.Button, new() { Name = "Refresh", Exact = true }).ClickAsync();
        await blackList.GetByRole(AriaRole.Link).Filter(new() { HasText = "Invitation received" }).ClickAsync();
        await Expect(black.GetByRole(AriaRole.Button, new() { Name = "Accept game", Exact = true })).ToBeEnabledAsync();
        await Expect(black.GetByRole(AriaRole.Button, new() { Name = "Cancel invitation", Exact = true })).ToHaveCountAsync(0);
        await Expect(white.GetByRole(AriaRole.Button, new() { Name = "Decline", Exact = true })).ToHaveCountAsync(0);

        IPage actor = recipientActs ? black : white;
        await actor.GetByRole(AriaRole.Button, new() { Name = action, Exact = true }).ClickAsync();
        foreach (IPage page in new[] { white, black })
        {
            await Expect(page.GetByText(outcome, new() { Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Accept game", Exact = true })).ToHaveCountAsync(0);
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Decline", Exact = true })).ToHaveCountAsync(0);
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Cancel invitation", Exact = true })).ToHaveCountAsync(0);

            await page.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = "Games", Exact = true }).ClickAsync();
            await Expect(page.GetByText("No pending invitations.", new() { Exact = true })).ToBeVisibleAsync();
            ILocator list = page.GetByRole(AriaRole.Region, new() { Name = "Your games", Exact = true });
            await list.GetByRole(AriaRole.Button, new() { Name = "Closed invitations", Exact = true }).ClickAsync();
            await list.GetByRole(AriaRole.Link).Filter(new() { HasText = outcome }).ClickAsync();
            await Expect(page.GetByText(outcome, new() { Exact = true })).ToBeVisibleAsync();
        }
    }

    [Fact]
    public async Task PlayersSignInFromInviteAndSyncPrivateGameAcrossBrowsers()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext whiteContext = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext blackContext = await browserSteps.NewContextAsync(browser, touch: true);
        (IPage white, string whiteEmail) = await browserSteps.RegisterAndOpenSignInAsync(whiteContext);
        (IPage black, string blackEmail) = await browserSteps.RegisterAndOpenSignInAsync(blackContext);
        await browserSteps.SignInAsync(white, whiteEmail);
        await browserSteps.SignInAsync(black, blackEmail);

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
        string opponent = await ReadUsernameAsync(black);
        await black.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = "Home", Exact = true }).ClickAsync();
        await browserSteps.SignOutAsync(black, "Sign out", "logout");

        await white.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        await SendInvitationAsync(white, opponent);
        ILocator invite = white.GetByLabel("Game link");
        await Expect(invite).ToHaveValueAsync(new Regex(@"/games/[0-9a-f-]{36}$"));
        string gameLink = await invite.InputValueAsync();
        IWebSocketRoute whiteSocket = await subscribed.Task.WaitAsync(liveTimeout, cancellationToken);
        await black.GotoAsync(gameLink);
        string gameId = new Uri(gameLink).Segments[^1];
        await Expect(black).ToHaveURLAsync(new Uri(fixture.WebOrigin, $"/login?gameId={gameId}").AbsoluteUri);
        await browserSteps.SubmitSignInAsync(black, blackEmail);
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
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext context = await browserSteps.NewContextAsync(browser);
        (IPage account, string blackEmail) = await browserSteps.RegisterAndOpenSignInAsync(context);
        await browserSteps.SignInAsync(account, blackEmail);
        await account.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        string blackUsername = await ReadUsernameAsync(account);

        (IPage board, string whiteEmail) = await browserSteps.RegisterAndOpenSignInAsync(context);
        await browserSteps.SignInAsync(board, whiteEmail);
        await board.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        await SendInvitationAsync(board, blackUsername);
        await Expect(board.GetByLabel("Game link")).ToBeVisibleAsync();
        string invite = board.Url;
        string inviteLogin = $"/login?gameId={new Uri(invite).Segments[^1]}";
        IPage list = await context.NewPageAsync();
        await list.GotoAsync("/games");
        await Expect(list.GetByText("Waiting for acceptance", new() { Exact = true })).ToBeVisibleAsync();

        await account.BringToFrontAsync();
        await account.GotoAsync("/login");
        await browserSteps.SignInAsync(account, blackEmail);
        await Expect(board.GetByText("You play Black.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(board.GetByRole(AriaRole.Button, new() { Name = "Accept game" })).ToBeEnabledAsync();
        await Expect(list.GetByLabel("Your username")).ToHaveValueAsync(blackUsername);
        await Expect(list.GetByText("Invitation received", new() { Exact = true })).ToBeVisibleAsync();

        await browserSteps.SignOutAsync(account, "Sign out", "logout");
        ILocator boardSignIn = board.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = "Sign in" });
        await Expect(boardSignIn).ToHaveAttributeAsync("href", inviteLogin);
        await Expect(board.Locator(".board")).ToHaveCountAsync(0);
        await Expect(list.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = "Sign in" })).ToBeVisibleAsync();
        await Expect(list.GetByLabel("Your username")).ToHaveCountAsync(0);
        await Expect(list.GetByText("Invitation received", new() { Exact = true })).ToHaveCountAsync(0);

        await board.BringToFrontAsync();
        await boardSignIn.ClickAsync();
        await Expect(board).ToHaveURLAsync(new Uri(fixture.WebOrigin, inviteLogin).AbsoluteUri);
        await browserSteps.SubmitSignInAsync(board, whiteEmail);
        await Expect(board).ToHaveURLAsync(invite);
        await Expect(board.GetByText("You play White.", new() { Exact = true })).ToBeVisibleAsync();
    }

    private static async Task<string> ReadUsernameAsync(IPage page)
    {
        ILocator username = page.GetByLabel("Your username", new() { Exact = true });
        await Expect(username).ToHaveValueAsync(new Regex(@"^player-[0-9a-f]{32}$"));
        return await username.InputValueAsync();
    }

    private static async Task SendInvitationAsync(IPage page, string username)
    {
        await page.GetByLabel("Opponent’s username").FillAsync(username);
        await page.GetByRole(AriaRole.Button, new() { Name = "Send invitation", Exact = true }).ClickAsync();
    }
}
