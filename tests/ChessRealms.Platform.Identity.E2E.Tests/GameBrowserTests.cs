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
    private static readonly Regex InvitationCountdown = new(@"^Expires in (?:4:[0-5][0-9]|5:00)$");

    [Fact]
    public async Task PlayersOpenACopiedLobbyLinkAcrossSignInAndPlayTheFirstMove()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext whiteContext = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext blackContext = await browserSteps.NewContextAsync(browser);
        await OffsetBrowserClockAsync(whiteContext, TimeSpan.FromMinutes(20));
        await OffsetBrowserClockAsync(blackContext, TimeSpan.FromMinutes(-20));
        await whiteContext.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        (IPage white, string whiteEmail) = await browserSteps.RegisterAndOpenSignInAsync(whiteContext);
        (IPage black, string blackEmail) = await browserSteps.RegisterAndOpenSignInAsync(blackContext);
        await browserSteps.SignInAsync(white, whiteEmail);

        await white.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        string host = await ReadUsernameAsync(white);
        await white.GetByRole(AriaRole.Button, new() { Name = "Create private link", Exact = true }).ClickAsync();
        ILocator link = white.GetByLabel("Invitation link", new() { Exact = true });
        await Expect(link).ToHaveValueAsync(new Regex(@"/lobbies/[0-9a-f-]{36}$"));
        await Expect(white.GetByText(InvitationCountdown)).ToBeVisibleAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "Copy link", Exact = true }).ClickAsync();
        await Expect(white.GetByText("Link copied.", new() { Exact = true })).ToBeVisibleAsync();
        string copiedLink = await white.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        Assert.Equal(await link.InputValueAsync(), copiedLink);

        await white.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = "Games", Exact = true }).ClickAsync();
        await Expect(white.GetByRole(AriaRole.Region, new() { Name = "Your games" }).GetByText(InvitationCountdown)).ToBeVisibleAsync();
        await white.GetByRole(AriaRole.Region, new() { Name = "Your games" })
            .GetByRole(AriaRole.Link).Filter(new() { HasText = "Waiting for a player" }).ClickAsync();
        await Expect(white).ToHaveURLAsync(copiedLink);

        await black.GotoAsync(copiedLink);
        string lobbyId = new Uri(copiedLink).Segments[^1];
        await Expect(black.GetByRole(AriaRole.Button, new() { Name = "Play as guest" })).ToBeVisibleAsync();
        await Expect(black.GetByText(InvitationCountdown)).ToBeVisibleAsync();
        await black.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Expect(black).ToHaveURLAsync(new Uri(fixture.WebOrigin, $"/login?lobbyId={lobbyId}").AbsoluteUri);
        await browserSteps.SubmitSignInAsync(black, blackEmail);
        await Expect(black).ToHaveURLAsync(copiedLink);
        await Expect(black.GetByText($"Invitation from {host}", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(black.GetByText(InvitationCountdown)).ToBeVisibleAsync();
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
    public async Task GuestJoinsBlitzAndKeepsTheirSeatAcrossReload()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext hostContext = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext guestContext = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext otherContext = await browserSteps.NewContextAsync(browser);
        (IPage host, string email) = await browserSteps.RegisterAndOpenSignInAsync(hostContext);
        await browserSteps.SignInAsync(host, email);
        IPage guest = await guestContext.NewPageAsync();
        IPage other = await otherContext.NewPageAsync();
        await host.GetByRole(AriaRole.Link, new() { Name = "Play chess" }).ClickAsync();
        await host.GetByLabel("Link time control").SelectOptionAsync("Blitz5");
        await host.GetByRole(AriaRole.Button, new() { Name = "Create private link", Exact = true }).ClickAsync();
        ILocator link = host.GetByLabel("Invitation link");
        await Expect(link).ToHaveValueAsync(new Regex(@"/lobbies/[0-9a-f-]{36}$"));
        string invite = await link.InputValueAsync();

        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        guest.WebSocket += (page, socket) =>
        {
            if (new Uri(socket.Url).AbsolutePath != "/api/games/live")
            {
                return;
            }
            socket.FrameReceived += (connection, frame) =>
            {
                foreach (string payload in frame.Text?.Split('\u001e', StringSplitOptions.RemoveEmptyEntries) ?? [])
                {
                    using JsonDocument message = JsonDocument.Parse(payload);
                    if (message.RootElement.TryGetProperty("type", out JsonElement type) && type.GetInt32() == 3
                        && !message.RootElement.TryGetProperty("error", out _))
                    {
                        subscribed.TrySetResult();
                    }
                }
            };
        };

        await guest.GotoAsync(invite);
        await Expect(guest.GetByText("5+0 · 5 minutes per side", new() { Exact = true })).ToBeVisibleAsync();
        await guest.GetByLabel("Display name").FillAsync("Guest Knight");
        await guest.GetByRole(AriaRole.Button, new() { Name = "Play as guest" }).ClickAsync();
        await Expect(guest).ToHaveURLAsync(new Regex(@"/guest/games/[0-9a-f-]{36}$"));
        await Expect(host.GetByText("Guest Knight (guest) plays Black.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(guest.GetByText("You play Black.", new() { Exact = true })).ToBeVisibleAsync();
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        string guestUrl = guest.Url;
        await other.GotoAsync(invite);
        await Expect(other.GetByText("This invitation has already been used.", new() { Exact = true })).ToBeVisibleAsync();
        await other.GotoAsync(guestUrl);
        await Expect(other.GetByText("This guest session is unavailable.", new() { Exact = true })).ToBeVisibleAsync();

        await host.GetByRole(AriaRole.Button, new() { Name = "e2, White pawn", Exact = true }).ClickAsync();
        await host.GetByRole(AriaRole.Button, new() { Name = "e4, empty", Exact = true }).ClickAsync();
        await Expect(guest.GetByText("Your turn", new() { Exact = true })).ToBeVisibleAsync();
        await guest.ReloadAsync();
        await Expect(guest.GetByRole(AriaRole.Button, new() { Name = "e4, White pawn", Exact = true })).ToBeVisibleAsync();
        await guest.GetByRole(AriaRole.Button, new() { Name = "e7, Black pawn", Exact = true }).ClickAsync();
        await guest.GetByRole(AriaRole.Button, new() { Name = "e5, empty", Exact = true }).ClickAsync();
        await Expect(host.GetByRole(AriaRole.Button, new() { Name = "e5, Black pawn", Exact = true })).ToBeVisibleAsync();

        await guest.GotoAsync(invite);
        await Expect(guest).ToHaveURLAsync(guestUrl);
        await Expect(guest.GetByRole(AriaRole.Button, new() { Name = "e5, Black pawn", Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task QuickPlayMatchesTwoAccountsAndStartsBlitz()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext firstContext = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext secondContext = await browserSteps.NewContextAsync(browser);
        (IPage first, string firstEmail) = await browserSteps.RegisterAndOpenSignInAsync(firstContext);
        (IPage second, string secondEmail) = await browserSteps.RegisterAndOpenSignInAsync(secondContext);
        await browserSteps.SignInAsync(first, firstEmail);
        await browserSteps.SignInAsync(second, secondEmail);

        foreach (IPage page in new[] { first, second })
        {
            await page.GetByRole(AriaRole.Link, new() { Name = "Play chess", Exact = true }).ClickAsync();
        }
        ILocator find = first.GetByRole(AriaRole.Button, new() { Name = "Find game · 5+0", Exact = true });
        await find.ClickAsync();
        await Expect(first.GetByText("Looking for an opponent…", new() { Exact = true })).ToBeVisibleAsync();
        await first.GetByRole(AriaRole.Button, new() { Name = "Cancel search", Exact = true }).ClickAsync();
        await Expect(find).ToBeEnabledAsync();
        await find.ClickAsync();
        await Expect(first.GetByText("Looking for an opponent…", new() { Exact = true })).ToBeVisibleAsync();
        await first.ReloadAsync();
        await Expect(first.GetByText("Looking for an opponent…", new() { Exact = true })).ToBeVisibleAsync();

        await second.GetByRole(AriaRole.Button, new() { Name = "Find game · 5+0", Exact = true }).ClickAsync();
        await Expect(first).ToHaveURLAsync(new Regex(@"/games/[0-9a-f-]{36}$"));
        await Expect(second).ToHaveURLAsync(first.Url);
        await Expect(first.GetByRole(AriaRole.Group, new() { Name = "5+0 chess clocks" })).ToBeVisibleAsync();
        await Expect(second.GetByRole(AriaRole.Group, new() { Name = "5+0 chess clocks" })).ToBeVisibleAsync();
        await Expect(second.GetByText(new Regex(@"^You play (White|Black)\.$"))).ToBeVisibleAsync();
        bool secondIsWhite = await second.GetByText("You play White.", new() { Exact = true }).IsVisibleAsync();
        IPage white = secondIsWhite ? second : first;
        IPage black = secondIsWhite ? first : second;
        await Expect(white.GetByText("You play White.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(black.GetByText("You play Black.", new() { Exact = true })).ToBeVisibleAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "e2, White pawn", Exact = true }).ClickAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "e4, empty", Exact = true }).ClickAsync();
        await Expect(black.GetByRole(AriaRole.Button, new() { Name = "e4, White pawn", Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task FinishedGameReplaysCastlingWithKeyboardAndAutoplay()
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
        ILocator invite = white.GetByLabel("Game link", new() { Exact = true });
        await Expect(invite).ToBeVisibleAsync();
        string gameUrl = await invite.InputValueAsync();
        await black.GotoAsync(gameUrl);
        await black.GetByRole(AriaRole.Button, new() { Name = "Accept game", Exact = true }).ClickAsync();

        string[] moves = ["e2e4", "e7e5", "g1f3", "b8c6", "f1b5", "a7a6", "b5a4", "g8f6", "e1g1"];
        for (int ply = 0; ply < moves.Length; ply++)
        {
            IPage player = ply % 2 == 0 ? white : black;
            await Expect(player.GetByText("Your turn", new() { Exact = true })).ToBeVisibleAsync();
            await player.Locator($"[data-square='{moves[ply][..2]}']").ClickAsync();
            await player.Locator($"[data-square='{moves[ply][2..]}']").ClickAsync();
        }

        await Expect(black.GetByRole(AriaRole.Button, new() { Name = "g1, White king", Exact = true })).ToBeVisibleAsync();
        await black.GetByRole(AriaRole.Button, new() { Name = "Resign", Exact = true }).ClickAsync();
        await black.GetByRole(AriaRole.Button, new() { Name = "Confirm resignation", Exact = true }).ClickAsync();
        await Expect(white.GetByRole(AriaRole.Link, new() { Name = "Review game" })).ToBeVisibleAsync();
        await white.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = "Games", Exact = true }).ClickAsync();
        ILocator gameList = white.GetByRole(AriaRole.Region, new() { Name = "Your games" });
        await gameList.GetByRole(AriaRole.Button, new() { Name = "Finished", Exact = true }).ClickAsync();
        await gameList.GetByRole(AriaRole.Link).Filter(new() { HasText = blackUsername }).ClickAsync();
        int replayRequests = 0;
        white.Request += (_, request) =>
        {
            string path = new Uri(request.Url).AbsolutePath;
            if (path.StartsWith("/api/games/", StringComparison.Ordinal) && path.EndsWith("/replay", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref replayRequests);
            }
        };

        await white.GetByRole(AriaRole.Link, new() { Name = "Review game" }).ClickAsync();
        await Expect(white).ToHaveURLAsync(gameUrl + "/replay");
        await Expect(white.GetByText("Starting position", new() { Exact = true })).ToBeVisibleAsync();
        Assert.Equal(1, Volatile.Read(ref replayRequests));
        int laterGameRequests = 0;
        white.Request += (_, request) =>
        {
            if (new Uri(request.Url).AbsolutePath.StartsWith("/api/games/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref laterGameRequests);
            }
        };
        ILocator beforeCastle = white.GetByRole(AriaRole.Button, new() { Name = "4… Nf6", Exact = true });
        await beforeCastle.ClickAsync();
        await Expect(white.GetByRole(AriaRole.Button, new() { Name = "e1, White king", Exact = true })).ToBeVisibleAsync();
        await Expect(white.GetByRole(AriaRole.Button, new() { Name = "h1, White rook", Exact = true })).ToBeVisibleAsync();
        await beforeCastle.PressAsync("ArrowRight");
        await Expect(white.GetByRole(AriaRole.Button, new() { Name = "g1, White king", Exact = true })).ToBeVisibleAsync();
        await Expect(white.GetByRole(AriaRole.Button, new() { Name = "f1, White rook", Exact = true })).ToBeVisibleAsync();
        await beforeCastle.PressAsync("ArrowLeft");
        await Expect(white.GetByRole(AriaRole.Button, new() { Name = "h1, White rook", Exact = true })).ToBeVisibleAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "Play", Exact = true }).ClickAsync();
        await Expect(white.GetByRole(AriaRole.Button, new() { Name = "g1, White king", Exact = true })).ToBeVisibleAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true }).ClickAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "End", Exact = true }).ClickAsync();
        await Expect(white.GetByText("White wins · Resignation", new() { Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, Volatile.Read(ref laterGameRequests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayersSendAndAcceptAnInvitationByUsernameAndPlayTheFirstMove(bool timed)
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
        if (timed)
        {
            await white.GetByLabel("Time control", new() { Exact = true }).SelectOptionAsync("Blitz5");
        }
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
        ILocator blackClocks = black.GetByRole(AriaRole.Group, new() { Name = "5+0 chess clocks" });
        if (timed)
        {
            await Expect(blackClocks.Locator("time")).ToHaveTextAsync(["5:00", "5:00"]);
        }
        await black.GetByRole(AriaRole.Button, new() { Name = "Accept game", Exact = true }).ClickAsync();

        await Expect(white.GetByText("Your turn", new() { Exact = true })).ToBeVisibleAsync();
        if (timed)
        {
            ILocator whiteClocks = white.GetByRole(AriaRole.Group, new() { Name = "5+0 chess clocks" });
            await Expect(whiteClocks.Locator("time").First).ToHaveTextAsync(new Regex(@"^4:[0-5][0-9]$"));
            await Expect(whiteClocks.Locator("time").Last).ToHaveTextAsync("5:00");
        }
        await white.GetByRole(AriaRole.Button, new() { Name = "e2, White pawn", Exact = true }).ClickAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "e4, empty", Exact = true }).ClickAsync();
        await Expect(black.GetByRole(AriaRole.Button, new() { Name = "e4, White pawn", Exact = true })).ToBeVisibleAsync();
        await Expect(black.GetByText("Your turn", new() { Exact = true })).ToBeVisibleAsync();
        if (timed)
        {
            string whiteRemaining = await blackClocks.Locator("time").First.InnerTextAsync();
            await Expect(blackClocks.Locator("time").Last).ToHaveTextAsync(new Regex(@"^4:[0-5][0-9]$"));
            await black.ReloadAsync();
            await Expect(blackClocks.Locator("time").First).ToHaveTextAsync(whiteRemaining);
            await Expect(blackClocks.Locator("time").Last).ToHaveTextAsync(new Regex(@"^4:[0-5][0-9]$"));
        }
    }

    [Fact]
    public async Task PlayersAcceptARematchWithSwappedColorsAndPlayTheFirstMove()
    {
        await using IBrowser browser = await browserSteps.LaunchBrowserAsync();
        await using IBrowserContext whiteContext = await browserSteps.NewContextAsync(browser);
        await using IBrowserContext blackContext = await browserSteps.NewContextAsync(browser);
        await OffsetBrowserClockAsync(whiteContext, TimeSpan.FromMinutes(20));
        await OffsetBrowserClockAsync(blackContext, TimeSpan.FromMinutes(-20));
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
        await Expect(white.GetByText(InvitationCountdown)).ToBeVisibleAsync();
        string originalUrl = await gameLink.InputValueAsync();
        await black.GotoAsync(originalUrl);
        await Expect(black.GetByText(InvitationCountdown)).ToBeVisibleAsync();
        await black.GetByRole(AriaRole.Button, new() { Name = "Accept game", Exact = true }).ClickAsync();

        await white.GetByRole(AriaRole.Button, new() { Name = "Resign", Exact = true }).ClickAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "Confirm resignation", Exact = true }).ClickAsync();
        await Expect(black.GetByText("Black wins · Resignation", new() { Exact = true })).ToBeVisibleAsync();
        await white.GetByRole(AriaRole.Button, new() { Name = "Rematch", Exact = true }).ClickAsync();
        await Expect(white.GetByText("Rematch offered.", new() { Exact = true })).ToBeVisibleAsync();
        await white.ReloadAsync();
        await Expect(white.GetByText("Rematch offered.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(white.GetByText(InvitationCountdown)).ToBeVisibleAsync();
        await Expect(black.GetByText(InvitationCountdown)).ToBeVisibleAsync();
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

    private static Task OffsetBrowserClockAsync(IBrowserContext context, TimeSpan offset) =>
        context.AddInitScriptAsync(FormattableString.Invariant($"""
            const now = Date.now.bind(Date);
            Date.now = () => now() + ({offset.TotalMilliseconds});
            """));

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
