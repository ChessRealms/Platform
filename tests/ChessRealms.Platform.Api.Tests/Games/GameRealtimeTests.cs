using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Api.Realtime;

using Xunit;

namespace ChessRealms.Platform.Api.Tests.Games;

public sealed class GameRealtimeTests : GameApiTestBase
{
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
}
