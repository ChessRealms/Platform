using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;

using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Api.Realtime;

using Xunit;

namespace ChessRealms.Platform.Api.Tests.Games;

public sealed class GameRealtimeTests : GameApiTestBase
{
    [Fact]
    public async Task MyGamesUpdatesReachOnlyTheAccountsWhoseListChanged()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (_, string outsiderToken) = await RegisterAndSignInAsync("outsider");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        using ClientWebSocket whiteLive = new();
        using ClientWebSocket blackLive = new();
        using ClientWebSocket outsiderLive = new();
        await ConnectLiveAsync(whiteLive, whiteToken);
        await ConnectLiveAsync(blackLive, blackToken);
        await ConnectLiveAsync(outsiderLive, outsiderToken);
        Assert.False((await SubscribeMyGamesAsync(whiteLive)).TryGetProperty("error", out _));
        Assert.False((await SubscribeMyGamesAsync(blackLive)).TryGetProperty("error", out _));
        Assert.False((await SubscribeMyGamesAsync(outsiderLive)).TryGetProperty("error", out _));

        using HttpResponseMessage create = await white.PostAsJsonAsync("/api/games",
            new CreateGameRequest(blackId, TimeControl: "Blitz5"), Cancellation);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        GameResponse pending = Assert.IsType<GameResponse>(await create.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        await AssertMyGamesChangedAsync(whiteLive);
        await AssertMyGamesChangedAsync(blackLive);
        Assert.Equal(pending.Id, Assert.Single((await ListGamesAsync(black)).Items).Id);

        string url = $"/api/games/{pending.Id}";
        using HttpResponseMessage rejected = await SendActionAsync(white, url, "accept", pending.Revision);
        await AssertErrorAsync(rejected, HttpStatusCode.Forbidden, "Forbidden");
        // A completion proves that there was no list event queued for this account.
        Assert.False((await SubscribeMyGamesAsync(whiteLive)).TryGetProperty("error", out _));
        Assert.False((await SubscribeMyGamesAsync(blackLive)).TryGetProperty("error", out _));

        GameResponse accepted = await AcceptGameAsync(black, url);
        await AssertMyGamesChangedAsync(whiteLive);
        await AssertMyGamesChangedAsync(blackLive);
        Assert.Equal(accepted.Id, Assert.Single((await ListGamesAsync(white, "?status=Active")).Items).Id);

        Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty((await ListGamesAsync(white, "?status=Active")).Items);
        await AssertMyGamesChangedAsync(whiteLive);
        await AssertMyGamesChangedAsync(blackLive);
        Assert.Equal("Timeout", Assert.Single((await ListGamesAsync(black, "?status=Finished")).Items).EndReason);
        Assert.False((await SubscribeMyGamesAsync(outsiderLive)).TryGetProperty("error", out _));
    }

    [Fact]
    public async Task MyGamesUpdatesCoverPrivateLinksAndQuickPlayMatches()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (_, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        using ClientWebSocket whiteLive = new();
        using ClientWebSocket blackLive = new();
        await ConnectLiveAsync(whiteLive, whiteToken);
        await ConnectLiveAsync(blackLive, blackToken);
        Assert.False((await SubscribeMyGamesAsync(whiteLive)).TryGetProperty("error", out _));
        Assert.False((await SubscribeMyGamesAsync(blackLive)).TryGetProperty("error", out _));

        LobbyResponse lobby = await CreateLobbyAsync(white);
        await AssertMyGamesChangedAsync(whiteLive);
        Assert.False((await SubscribeMyGamesAsync(blackLive)).TryGetProperty("error", out _));

        using HttpResponseMessage join = await SendActionAsync(black, $"/api/lobbies/{lobby.Id}", "join", lobby.Revision);
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        await AssertMyGamesChangedAsync(whiteLive);
        await AssertMyGamesChangedAsync(blackLive);
        Assert.Empty((await ListGamesAsync(white)).Items);
        Assert.Single((await ListGamesAsync(black, "?status=Active")).Items);

        LobbyResponse cancelled = await CreateLobbyAsync(white);
        await AssertMyGamesChangedAsync(whiteLive);
        using HttpResponseMessage cancel = await SendActionAsync(white, $"/api/lobbies/{cancelled.Id}", "cancel", cancelled.Revision);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await AssertMyGamesChangedAsync(whiteLive);
        Assert.False((await SubscribeMyGamesAsync(blackLive)).TryGetProperty("error", out _));

        using HttpResponseMessage queued = await white.PostAsJsonAsync("/api/quick-play", new JoinQuickPlayRequest(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        Assert.False((await SubscribeMyGamesAsync(whiteLive)).TryGetProperty("error", out _));
        using HttpResponseMessage matched = await black.PostAsJsonAsync("/api/quick-play", new JoinQuickPlayRequest(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);
        await AssertMyGamesChangedAsync(whiteLive);
        await AssertMyGamesChangedAsync(blackLive);
        Assert.Equal(2, (await ListGamesAsync(black, "?status=Active")).Items.Count);
    }

    [Fact]
    public async Task LiveUpdatesRequireMembershipAndReportOnlyCommittedRevisions()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (_, string outsiderToken) = await RegisterAndSignInAsync("outsider");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse game = await CreateGameAsync(white, blackId);
        string gameUrl = $"/api/games/{game.Id}";

        using ClientWebSocket anonymous = new();
        anonymous.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => anonymous.ConnectAsync(LiveUri(), Cancellation));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.HttpStatusCode);

        using ClientWebSocket participant = new();
        using ClientWebSocket outsider = new();
        await ConnectLiveAsync(participant, whiteToken);
        await ConnectLiveAsync(outsider, outsiderToken);

        JsonElement privateGame = await SubscribeAsync(outsider, game.Id);
        JsonElement unknownGame = await SubscribeAsync(outsider, Guid.NewGuid());
        string? denial = privateGame.GetProperty("error").GetString();
        Assert.Contains("This game is unavailable.", denial);
        Assert.Equal(denial, unknownGame.GetProperty("error").GetString());
        Assert.False((await SubscribeAsync(participant, game.Id)).TryGetProperty("error", out _));

        using HttpResponseMessage rejected = await PlayAsync(white, gameUrl, "e2e4", 0);
        await AssertErrorAsync(rejected, HttpStatusCode.Conflict, "GameNotActive");
        GameResponse active = await AcceptGameAsync(black, gameUrl);
        JsonElement notified = await ReadLiveMessageAsync(participant);
        Assert.Equal(GameHub.ChangeEvent, notified.GetProperty("target").GetString());
        Assert.Equal(game.Id, notified.GetProperty("arguments")[0].GetGuid());
        Assert.Equal(active.Revision, notified.GetProperty("arguments")[1].GetInt64());

        // A completion on the same socket also proves no update was queued for the outsider.
        Assert.Equal(denial, (await SubscribeAsync(outsider, game.Id)).GetProperty("error").GetString());
    }

    private async Task AssertMyGamesChangedAsync(ClientWebSocket socket)
    {
        JsonElement message = await ReadLiveMessageAsync(socket);
        Assert.Equal(GameHub.MyGamesChangeEvent, message.GetProperty("target").GetString());
        Assert.Empty(message.GetProperty("arguments").EnumerateArray());
    }
}
