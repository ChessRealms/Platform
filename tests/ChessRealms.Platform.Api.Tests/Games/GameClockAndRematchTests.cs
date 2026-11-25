using System.Net;
using System.Net.Http.Json;

using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace ChessRealms.Platform.Api.Tests.Games;

public sealed class GameClockAndRematchTests : GameApiTestBase
{
    [Fact]
    public async Task BothPlayersCanPlayOneRematchAndTheRecipientCanRetryAfterExpiry()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (_, string outsiderToken) = await RegisterAndSignInAsync("outsider");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        using HttpClient outsider = CreateClient(outsiderToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string url = $"/api/games/{pending.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        using HttpResponseMessage premature = await SendActionAsync(white, url, "rematch/offer", active.Revision);
        await AssertErrorAsync(premature, HttpStatusCode.Conflict, "RematchUnavailable");

        Clock.Advance(TimeSpan.FromMinutes(2));
        GameResponse finished = await ApplyActionAsync(white, url, "resign", active.Revision);
        DateTimeOffset deadline = Clock.GetUtcNow() + TimeSpan.FromMinutes(5);
        Assert.Equal(deadline, finished.RematchExpiresAt);
        Clock.Advance(TimeSpan.FromMilliseconds(299999));

        GameResponse offered = await ApplyActionAsync(white, url, "rematch/offer", finished.Revision);
        Assert.Equal(deadline, offered.RematchExpiresAt);
        Assert.Equal(deadline.AddMilliseconds(-1), offered.ServerTime);
        Assert.Null(offered.Clock);
        Assert.Equal("White", offered.RematchOfferedBy);
        Assert.Equal(offered, (await ReadBoardAsync(black, url)).Game);
        using HttpResponseMessage competingOffer = await SendActionAsync(black, url, "rematch/offer", finished.Revision);
        await AssertErrorAsync(competingOffer, HttpStatusCode.Conflict, "RematchOfferPending");
        using HttpResponseMessage selfAccept = await SendActionAsync(white, url, "rematch/accept", offered.Revision);
        await AssertErrorAsync(selfAccept, HttpStatusCode.Forbidden, "Forbidden");
        using HttpResponseMessage outsiderAccept = await SendActionAsync(outsider, url, "rematch/accept", offered.Revision);
        await AssertErrorAsync(outsiderAccept, HttpStatusCode.NotFound, "NotFound");

        HttpResponseMessage[] responses = await Task.WhenAll(
            SendActionAsync(black, url, "rematch/accept", offered.Revision),
            SendActionAsync(black, url, "rematch/accept", offered.Revision));
        GameResponse accepted;
        using (responses[0])
        using (responses[1])
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            accepted = Assert.IsType<GameResponse>(await responses[0].Content.ReadFromJsonAsync<GameResponse>(Cancellation));
            Assert.Equal(accepted, await responses[1].Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        }
        Guid rematchId = Assert.IsType<Guid>(accepted.RematchGameId);
        Assert.Equal(offered with { Revision = offered.Revision + 1, RematchGameId = rematchId }, accepted);

        Clock.Advance(TimeSpan.FromMilliseconds(1));
        using HttpResponseMessage replay = await SendActionAsync(black, url, "rematch/accept", offered.Revision);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        GameResponse afterExpiry = accepted with { ServerTime = Clock.GetUtcNow() };
        Assert.Equal(afterExpiry, await replay.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(afterExpiry, (await ReadBoardAsync(white, url)).Game);
        Assert.Equal(rematchId, Assert.Single((await ListGamesAsync(white, "?status=Active")).Items).Id);

        string rematchUrl = $"/api/games/{rematchId}";
        GameResponse rematch = (await ReadBoardAsync(black, rematchUrl)).Game;
        Assert.Equal((blackId, whiteId, "Active", "White", 0L),
            (rematch.WhitePlayerId, rematch.BlackPlayerId, rematch.Status, rematch.SideToMove, rematch.Revision));
        await MakeMoveAsync(black, rematchUrl, "e2e4", rematch.Revision);
        Assert.Equal(["e2e4"], (await ReadBoardAsync(white, rematchUrl)).Moves);
    }

    [Fact]
    public async Task RematchOfferAndAcceptanceExpireAtTheDeadline()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string url = $"/api/games/{pending.Id}";
        await AcceptGameAsync(black, url);
        GameResponse unoffered = await CreateGameAsync(black, whiteId);
        string secondUrl = $"/api/games/{unoffered.Id}";
        await AcceptGameAsync(white, secondUrl);
        GameResponse finished = await ApplyActionAsync(black, url, "resign", 1);
        GameResponse secondFinished = await ApplyActionAsync(white, secondUrl, "resign", 1);
        GameResponse offered = await ApplyActionAsync(black, url, "rematch/offer", finished.Revision);

        Clock.Advance(TimeSpan.FromMinutes(5));
        using HttpResponseMessage lateAccept = await SendActionAsync(white, url, "rematch/accept", offered.Revision);
        await AssertErrorAsync(lateAccept, HttpStatusCode.Gone, "RematchExpired");
        using HttpResponseMessage lateOffer = await SendActionAsync(black, secondUrl, "rematch/offer", secondFinished.Revision);
        await AssertErrorAsync(lateOffer, HttpStatusCode.Gone, "RematchExpired");
        Assert.Equal(offered with { ServerTime = Clock.GetUtcNow() }, (await ReadBoardAsync(white, url)).Game);
        Assert.Empty((await ListGamesAsync(white, "?status=Active")).Items);
    }

    [Fact]
    public async Task BlitzClocksStartAtAcceptanceAndALateMoveCannotBeatTheDeadline()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        CreateGameRequest request = new(blackId, Guid.NewGuid(), TimeControl: "Blitz5");
        using HttpResponseMessage created = await white.PostAsJsonAsync("/api/games", request, Cancellation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        GameResponse pending = Assert.IsType<GameResponse>(await created.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(300000, pending.Clock!.WhiteMilliseconds);
        Assert.Null(pending.Clock.TurnDeadline);
        using HttpResponseMessage mismatch = await white.PostAsJsonAsync("/api/games", request with { TimeControl = null }, Cancellation);
        await AssertErrorAsync(mismatch, HttpStatusCode.Conflict, "CreationRequestConflict");

        Clock.Advance(TimeSpan.FromSeconds(20));
        string url = $"/api/games/{pending.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        DateTimeOffset whiteDeadline = Clock.GetUtcNow().AddMinutes(5);
        Assert.Equal(whiteDeadline, active.Clock!.TurnDeadline);
        Clock.Advance(TimeSpan.FromMilliseconds(299999));
        GameResponse moved = await MakeMoveAsync(white, url, "e2e4", active.Revision);
        Assert.Equal(1, moved.Clock!.WhiteMilliseconds);
        Assert.Equal(300000, moved.Clock.BlackMilliseconds);
        DateTimeOffset blackDeadline = Clock.GetUtcNow().AddMinutes(5);
        Assert.Equal(blackDeadline, moved.Clock.TurnDeadline);
        Clock.Advance(TimeSpan.FromMinutes(5));

        Task<HttpResponseMessage> lateMove = PlayAsync(black, url, "e7e5", moved.Revision);
        Task<GameBoardResponse> boardRead = ReadBoardAsync(white, url);
        using HttpResponseMessage late = await lateMove;
        Assert.Contains(late.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        GameBoardResponse finished = await boardRead;
        Assert.Equal("Finished", finished.Game.Status);
        Assert.Equal("White", finished.Game.Winner);
        Assert.Equal("Timeout", finished.Game.EndReason);
        Assert.Equal(blackDeadline.AddMinutes(5), finished.Game.RematchExpiresAt);
        Assert.Equal(moved.Revision + 1, finished.Game.Revision);
        Assert.Equal(["e2e4"], finished.Moves);
        Assert.Null(finished.Game.Clock!.TurnDeadline);
        Assert.Equal(1, finished.Game.Clock.WhiteMilliseconds);
        Assert.Equal(0, finished.Game.Clock.BlackMilliseconds);

        GameReplayResponse replay = await ReadReplayAsync(white, url);
        Assert.Equal("recorded", replay.TimingMode);
        Assert.Equal(300000, replay.InitialClock!.WhiteMilliseconds);
        GameReplayMoveResponse frame = Assert.Single(replay.Moves);
        Assert.Equal(299999L, frame.AtMs);
        Assert.Equal("e4", frame.San);
        Assert.Equal(finished.Game.Fen, frame.Fen);
        Assert.Equal(1, frame.Clock!.WhiteMilliseconds);
        Assert.Equal(300000, frame.Clock.BlackMilliseconds);
        Assert.Equal(599999L, replay.Finish.AtMs);
        Assert.Equal("Timeout", replay.Finish.EndReason);
        Assert.Equal(0, replay.Finish.Clock!.BlackMilliseconds);

        GameResponse offered = await ApplyActionAsync(white, url, "rematch/offer", finished.Game.Revision);
        GameResponse accepted = await ApplyActionAsync(black, url, "rematch/accept", offered.Revision);
        GameResponse rematch = (await ReadBoardAsync(black, $"/api/games/{accepted.RematchGameId}")).Game;
        Assert.Equal(blackId, rematch.WhitePlayerId);
        Assert.Equal("Blitz5", rematch.Clock!.TimeControl);
        Assert.Equal(300000, rematch.Clock.WhiteMilliseconds);
        Assert.Equal(300000, rematch.Clock.BlackMilliseconds);
        Assert.Equal(Clock.GetUtcNow().AddMinutes(5), rematch.Clock.TurnDeadline);
    }

    [Fact]
    public async Task ReadingMyGamesSettlesATimeoutAsADrawIfTheOpponentCannotMate()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        await using AsyncServiceScope scope = CreateScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        const string fen = "7k/8/8/8/8/8/R7/K7 w - - 0 1";
        Game game = new(whiteId, blackId, new(PositionRecord(fen), fen, PlayerColor.White), Clock.GetUtcNow(), GameTimeControl.Blitz5);
        database.Games.Add(game);
        await database.SaveChangesAsync(Cancellation);
        string url = $"/api/games/{game.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        Clock.Advance(TimeSpan.FromMinutes(6));

        GameListItemResponse item = Assert.Single((await ListGamesAsync(white, "?status=Finished")).Items);
        Assert.Equal("TimeoutNoPossibleMate", item.EndReason);
        Assert.Null(item.Winner);
        Assert.Equal("Blitz5", item.TimeControl);
        Assert.Empty((await ListGamesAsync(black, "?status=Active")).Items);
        using HttpResponseMessage changed = await black.GetAsync($"{url}/board?afterRevision={active.Revision}", Cancellation);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        GameBoardResponse finished = Assert.IsType<GameBoardResponse>(
            await changed.Content.ReadFromJsonAsync<GameBoardResponse>(Cancellation));
        Assert.Equal(active.Clock!.TurnDeadline?.AddMinutes(5), finished.Game.RematchExpiresAt);
        Assert.Equal(0, finished.Game.Clock!.WhiteMilliseconds);
        Assert.Equal(300000, finished.Game.Clock.BlackMilliseconds);
    }
}
