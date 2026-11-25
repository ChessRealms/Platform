using System.Net;
using System.Net.Http.Json;

using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace ChessRealms.Platform.Api.Tests.Games;

public sealed class GamePlayTests : GameApiTestBase
{
    [Fact]
    public async Task PrivateGameSurvivesAnApiRestartAndFinishesAtCheckmate()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (_, string outsiderToken) = await RegisterAndSignInAsync("outsider");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        using HttpClient outsider = CreateClient(outsiderToken);
        using HttpClient anonymous = CreateClient();

        using HttpResponseMessage self = await white.PostAsJsonAsync("/api/games", new CreateGameRequest(whiteId), Cancellation);
        await AssertErrorAsync(self, HttpStatusCode.BadRequest, "InvalidPlayers");
        using HttpResponseMessage unknown = await white.PostAsJsonAsync("/api/games", new CreateGameRequest(Guid.NewGuid()), Cancellation);
        await AssertErrorAsync(unknown, HttpStatusCode.BadRequest, "OpponentUnavailable");

        GameResponse pending = await CreateGameAsync(white, blackId);
        string gameUrl = $"/api/games/{pending.Id}";
        Assert.Equal("Pending", pending.Status);
        Assert.Equal(0, pending.Revision);
        Assert.Equal(whiteId, pending.WhitePlayerId);
        Assert.Equal(blackId, pending.BlackPlayerId);
        Assert.Equal(pending, await black.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));
        using HttpResponseMessage unauthenticated = await anonymous.GetAsync(gameUrl, Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using HttpResponseMessage hidden = await outsider.GetAsync(gameUrl, Cancellation);
        await AssertErrorAsync(hidden, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage hiddenBoard = await outsider.GetAsync($"{gameUrl}/board?afterRevision=0", Cancellation);
        await AssertErrorAsync(hiddenBoard, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage hiddenMutation = await PlayAsync(outsider, gameUrl, "e2e4", 0);
        await AssertErrorAsync(hiddenMutation, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage pendingMove = await PlayAsync(white, gameUrl, "e2e4", 0);
        await AssertErrorAsync(pendingMove, HttpStatusCode.Conflict, "GameNotActive");
        using HttpResponseMessage selfAccept = await white.PostAsJsonAsync($"{gameUrl}/accept", new GameRevisionRequest(0), Cancellation);
        await AssertErrorAsync(selfAccept, HttpStatusCode.Forbidden, "Forbidden");
        using HttpResponseMessage missingRevision = await black.PostAsJsonAsync($"{gameUrl}/accept", new { }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, missingRevision.StatusCode);

        GameResponse active = await AcceptGameAsync(black, gameUrl);
        Assert.Equal("Active", active.Status);
        Assert.Equal("White", active.SideToMove);
        Assert.Equal(1, active.Revision);
        GameBoardResponse firstBoard = Assert.IsType<GameBoardResponse>(
            await white.GetFromJsonAsync<GameBoardResponse>($"{gameUrl}/board", Cancellation));
        Assert.Equal(active, firstBoard.Game);
        Assert.Equal(new GameBoardPlayerResponse("white", false), firstBoard.WhitePlayer);
        Assert.Equal(new GameBoardPlayerResponse("black", false), firstBoard.BlackPlayer);
        Assert.Empty(firstBoard.Moves);
        Assert.Equal(20, firstBoard.LegalMoves.Count);
        Assert.Contains("e2e4", firstBoard.LegalMoves);
        using HttpResponseMessage unchanged = await white.GetAsync($"{gameUrl}/board?afterRevision=1", Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);
        GameBoardResponse futureRevision = Assert.IsType<GameBoardResponse>(
            await white.GetFromJsonAsync<GameBoardResponse>($"{gameUrl}/board?afterRevision=99", Cancellation));
        Assert.Equal(active, futureRevision.Game);
        Assert.Contains("e2e4", futureRevision.LegalMoves);
        using HttpResponseMessage wrongTurn = await PlayAsync(black, gameUrl, "e7e5", 1);
        await AssertErrorAsync(wrongTurn, HttpStatusCode.Conflict, "NotYourTurn");
        using HttpResponseMessage illegal = await PlayAsync(white, gameUrl, "e2e5", 1);
        await AssertErrorAsync(illegal, HttpStatusCode.UnprocessableEntity, "IllegalMove");
        Assert.Equal(active, await white.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));

        GameResponse whiteMove = await MakeMoveAsync(white, gameUrl, "e2e4", 1);
        Assert.Equal("Black", whiteMove.SideToMove);
        Assert.Equal(whiteMove, await black.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));
        GameBoardResponse changedBoard = Assert.IsType<GameBoardResponse>(
            await black.GetFromJsonAsync<GameBoardResponse>($"{gameUrl}/board?afterRevision=1", Cancellation));
        Assert.Equal(whiteMove, changedBoard.Game);
        Assert.Equal(firstBoard.WhitePlayer, changedBoard.WhitePlayer);
        Assert.Equal(firstBoard.BlackPlayer, changedBoard.BlackPlayer);
        Assert.Equal(["e2e4"], changedBoard.Moves);
        Assert.Contains("e7e5", changedBoard.LegalMoves);
        GameBoardResponse waitingBoard = Assert.IsType<GameBoardResponse>(
            await white.GetFromJsonAsync<GameBoardResponse>($"{gameUrl}/board", Cancellation));
        Assert.Empty(waitingBoard.LegalMoves);
        GameResponse blackMove = await MakeMoveAsync(black, gameUrl, "e7e5", 2);
        Assert.StartsWith("rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w ", blackMove.Fen);

        await RestartApiAsync();
        using HttpClient restoredWhite = CreateClient(whiteToken);
        using HttpClient restoredBlack = CreateClient(blackToken);
        Assert.Equal(blackMove, await restoredWhite.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));

        await MakeMoveAsync(restoredWhite, gameUrl, "f1c4", 3);
        await MakeMoveAsync(restoredBlack, gameUrl, "b8c6", 4);
        await MakeMoveAsync(restoredWhite, gameUrl, "d1h5", 5);
        await MakeMoveAsync(restoredBlack, gameUrl, "g8f6", 6);
        GameResponse finished = await MakeMoveAsync(restoredWhite, gameUrl, "h5f7", 7);
        Assert.Equal("Finished", finished.Status);
        Assert.Equal("White", finished.Winner);
        Assert.Equal("Checkmate", finished.EndReason);
        Assert.Equal(Clock.GetUtcNow().AddMinutes(5), finished.RematchExpiresAt);
        Assert.Null(finished.SideToMove);
        using HttpResponseMessage afterFinish = await PlayAsync(restoredBlack, gameUrl, "e8f7", 8);
        await AssertErrorAsync(afterFinish, HttpStatusCode.Conflict, "GameNotActive");
        Assert.Equal(finished, await restoredBlack.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConcurrentMoveOrResignationCanOnlyWinOneWrite(bool resign)
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string gameUrl = $"/api/games/{pending.Id}";
        await AcceptGameAsync(black, gameUrl);

        HttpResponseMessage[] responses = await Task.WhenAll(PlayAsync(white, gameUrl, "e2e4", 1),
            resign ? SendActionAsync(black, gameUrl, "resign", 1) : PlayAsync(white, gameUrl, "d2d4", 1));
        using HttpResponseMessage winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        using HttpResponseMessage loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        await AssertErrorAsync(loser, HttpStatusCode.Conflict, "RevisionConflict");
        GameResponse updated = Assert.IsType<GameResponse>(await winner.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        GameResponse persisted = Assert.IsType<GameResponse>(await white.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));
        Assert.Equal(2, persisted.Revision);
        Assert.Equal(updated, persisted);
        Assert.Equal(resign && ReferenceEquals(responses[1], winner) ? "Resignation" : null, persisted.EndReason);
        await using AsyncServiceScope scope = CreateScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        List<GameMoveTiming> timings = await database.GameMoveTimings.Where(timing => timing.GameId == pending.Id)
            .ToListAsync(Cancellation);
        if (persisted.Status == "Finished")
        {
            Assert.Empty(timings);
        }
        else
        {
            Assert.Equal(1, Assert.Single(timings).Ply);
        }
        using HttpResponseMessage stale = await PlayAsync(white, gameUrl, "e2e4", 1);
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, "RevisionConflict");
    }

    [Fact]
    public async Task DrawOfferCanBeDeclinedOrAcceptedOnlyByItsRecipient()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (_, string outsiderToken) = await RegisterAndSignInAsync("outsider");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        using HttpClient outsider = CreateClient(outsiderToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string url = $"/api/games/{pending.Id}";
        using HttpResponseMessage premature = await SendActionAsync(white, url, "draw/offer", 0);
        await AssertErrorAsync(premature, HttpStatusCode.Conflict, "GameNotActive");
        GameResponse game = await AcceptGameAsync(black, url);
        using HttpResponseMessage hidden = await SendActionAsync(outsider, url, "resign", game.Revision);
        await AssertErrorAsync(hidden, HttpStatusCode.NotFound, "NotFound");

        GameResponse offered = await ApplyActionAsync(white, url, "draw/offer", game.Revision);
        Assert.Equal("White", offered.DrawOfferedBy);
        Assert.Equal(game.Fen, offered.Fen);
        Assert.Equal(offered, await black.GetFromJsonAsync<GameResponse>(url, Cancellation));
        using HttpResponseMessage ownOffer = await SendActionAsync(white, url, "draw/accept", offered.Revision);
        await AssertErrorAsync(ownOffer, HttpStatusCode.Forbidden, "Forbidden");
        using HttpResponseMessage counterOffer = await SendActionAsync(black, url, "draw/offer", offered.Revision);
        await AssertErrorAsync(counterOffer, HttpStatusCode.Conflict, "DrawOfferPending");
        using HttpResponseMessage stale = await SendActionAsync(black, url, "draw/accept", game.Revision);
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, "RevisionConflict");
        GameResponse declined = await ApplyActionAsync(black, url, "draw/decline", offered.Revision);
        Assert.Null(declined.DrawOfferedBy);
        using HttpResponseMessage alreadyDeclined = await SendActionAsync(black, url, "draw/accept", declined.Revision);
        await AssertErrorAsync(alreadyDeclined, HttpStatusCode.Conflict, "NoDrawOffer");

        offered = await ApplyActionAsync(white, url, "draw/offer", declined.Revision);
        GameResponse whiteMove = await MakeMoveAsync(white, url, "e2e4", offered.Revision);
        Assert.Equal("White", whiteMove.DrawOfferedBy);
        GameResponse blackMove = await MakeMoveAsync(black, url, "e7e5", whiteMove.Revision);
        Assert.Null(blackMove.DrawOfferedBy);

        offered = await ApplyActionAsync(black, url, "draw/offer", blackMove.Revision);
        GameResponse finished = await ApplyActionAsync(white, url, "draw/accept", offered.Revision);
        Assert.Equal(Clock.GetUtcNow().AddMinutes(5), finished.RematchExpiresAt);
        Assert.Equal("Finished", finished.Status);
        Assert.Equal("DrawAgreement", finished.EndReason);
        Assert.Equal(blackMove.Fen, finished.Fen);
        Assert.Null(finished.Winner);
        Assert.Null(finished.DrawOfferedBy);

        await RestartApiAsync();
        using HttpClient restoredWhite = CreateClient(whiteToken);
        GameBoardResponse restored = await ReadBoardAsync(restoredWhite, url);
        Assert.Equal(finished, restored.Game);
        Assert.Equal(["e2e4", "e7e5"], restored.Moves);
        Assert.Empty(restored.LegalMoves);
        using HttpResponseMessage closed = await PlayAsync(restoredWhite, url, "g1f3", finished.Revision);
        await AssertErrorAsync(closed, HttpStatusCode.Conflict, "GameNotActive");
    }

    [Fact]
    public async Task APlayerCanResignOutsideTheirTurn()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string url = $"/api/games/{pending.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        GameResponse result = await ApplyActionAsync(black, url, "resign", active.Revision);

        Assert.Equal("Finished", result.Status);
        Assert.Equal("White", result.Winner);
        Assert.Equal("Resignation", result.EndReason);
        Assert.Equal(active.Fen, result.Fen);
        Assert.Equal(result, await white.GetFromJsonAsync<GameResponse>(url, Cancellation));
    }

    [Fact]
    public async Task ResignationUsesTheOpponentsMatingMaterialAndPersistsTheResult()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        await using AsyncServiceScope scope = CreateScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        IGameRules rules = scope.ServiceProvider.GetRequiredService<IGameRules>();

        (string Fen, HttpClient Resigning, string? Winner)[] positions =
        [
            ("7k/8/8/8/8/8/R7/K7 w - - 0 1", white, null),
            ("k7/r7/8/8/8/8/8/7K w - - 0 1", black, null),
            ("7k/6n1/8/8/8/8/Q7/K7 w - - 0 1", white, null),
            ("7k/5b2/8/8/8/8/R7/K7 w - - 0 1", white, null),
            ("7k/8/8/8/8/8/NN6/K7 w - - 0 1", black, "White"),
            ("k1K5/p7/4N3/8/8/8/8/8 w - - 0 1", black, "White")
        ];

        // The defending pawn blocks its own king, so one knight is enough in this position.
        string matingRecord = PositionRecord(positions[^1].Fen);
        Assert.Equal(GameEndReason.Checkmate, rules.Play(matingRecord, "e6c7")?.EndReason);

        foreach ((string fen, HttpClient resigning, string? winner) in positions)
        {
            Game game = new(whiteId, blackId, new(PositionRecord(fen), fen, PlayerColor.White), DateTimeOffset.UtcNow);
            database.Games.Add(game);
            await database.SaveChangesAsync(Cancellation);
            string url = $"/api/games/{game.Id}";
            GameResponse active = await AcceptGameAsync(black, url);
            GameResponse offered = await ApplyActionAsync(resigning, url, "draw/offer", active.Revision);
            GameResponse result = await ApplyActionAsync(resigning, url, "resign", offered.Revision);

            string reason = winner is null ? "ResignationNoPossibleMate" : "Resignation";
            Assert.Equal("Finished", result.Status);
            Assert.Equal(offered.Revision + 1, result.Revision);
            Assert.Equal(winner, result.Winner);
            Assert.Equal(reason, result.EndReason);
            Assert.Equal(fen, result.Fen);
            Assert.Null(result.DrawOfferedBy);

            HttpClient opponent = resigning == white ? black : white;
            GameBoardResponse board = await ReadBoardAsync(opponent, url);
            Assert.Equal(result, board.Game);
            Assert.Empty(board.Moves);
            GameListItemResponse listed = Assert.Single((await ListGamesAsync(opponent, "?status=Finished")).Items,
                item => item.Id == game.Id);
            Assert.Equal(winner, listed.Winner);
            Assert.Equal(reason, listed.EndReason);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThreefoldClaimKeepsThePositionAndHistory(bool useIntendedMove)
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string url = $"/api/games/{pending.Id}";
        GameResponse game = await AcceptGameAsync(black, url);
        using HttpResponseMessage unavailable = await white.PostAsJsonAsync($"{url}/draw/claim",
            new ClaimGameDrawRequest(game.Revision, "FiftyMoveRule", null), Cancellation);
        await AssertErrorAsync(unavailable, HttpStatusCode.UnprocessableEntity, "DrawClaimUnavailable");

        (HttpClient Player, string Move)[] cycle = [(white, "g1f3"), (black, "g8f6"), (white, "f3g1"), (black, "f6g8")];
        int plies = useIntendedMove ? 7 : 8;
        for (int i = 0; i < plies; i++)
        {
            (HttpClient player, string move) = cycle[i % cycle.Length];
            game = await MakeMoveAsync(player, url, move, game.Revision);
        }
        HttpClient claimant = useIntendedMove ? black : white;
        HttpClient waiting = useIntendedMove ? white : black;
        Assert.Empty((await ReadBoardAsync(waiting, url)).DrawClaims);
        GameBoardResponse before = await ReadBoardAsync(claimant, url);
        if (useIntendedMove)
        {
            Assert.Empty(before.DrawClaims);
        }
        else
        {
            Assert.Equal(["ThreefoldRepetition"], before.DrawClaims);
        }
        using HttpResponseMessage wrongTurn = await waiting.PostAsJsonAsync($"{url}/draw/claim",
            new ClaimGameDrawRequest(game.Revision, "ThreefoldRepetition", null), Cancellation);
        await AssertErrorAsync(wrongTurn, HttpStatusCode.Conflict, "NotYourTurn");

        string? intendedMove = useIntendedMove ? "f6g8" : null;
        using HttpResponseMessage claimed = await claimant.PostAsJsonAsync($"{url}/draw/claim",
            new ClaimGameDrawRequest(game.Revision, "ThreefoldRepetition", intendedMove), Cancellation);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        GameBoardResponse after = await ReadBoardAsync(waiting, url);
        Assert.Equal(game.Revision + 1, after.Game.Revision);
        Assert.Equal(game.Fen, after.Game.Fen);
        Assert.Equal("Finished", after.Game.Status);
        Assert.Equal("ThreefoldRepetition", after.Game.EndReason);
        Assert.Equal(before.Moves, after.Moves);
        Assert.Empty(after.DrawClaims);
    }

    [Fact]
    public async Task FiftyMoveClaimUsesThePersistedEnginePosition()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);

        const string fen = "7k/8/8/8/8/8/R7/K7 w - - 100 60";
        string record = PositionRecord(fen);
        Game game = new(whiteId, blackId, new(record, fen, PlayerColor.White), DateTimeOffset.UtcNow);
        await using (AsyncServiceScope scope = CreateScope())
        {
            PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            database.Games.Add(game);
            await database.SaveChangesAsync(Cancellation);
        }
        string url = $"/api/games/{game.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        Assert.Equal(["FiftyMoveRule"], (await ReadBoardAsync(white, url)).DrawClaims);
        using HttpResponseMessage claimed = await white.PostAsJsonAsync($"{url}/draw/claim",
            new ClaimGameDrawRequest(active.Revision, "FiftyMoveRule", null), Cancellation);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        GameBoardResponse finished = await ReadBoardAsync(black, url);
        Assert.Equal(Clock.GetUtcNow().AddMinutes(5), finished.Game.RematchExpiresAt);
        Assert.Equal("Finished", finished.Game.Status);
        Assert.Equal("FiftyMoveRule", finished.Game.EndReason);
        Assert.Equal(fen, finished.Game.Fen);
        Assert.Null(finished.Game.Winner);
        Assert.Empty(finished.Moves);
    }

    [Fact]
    public async Task PersistedHistoryTriggersAutomaticFivefoldRepetition()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string gameUrl = $"/api/games/{pending.Id}";
        GameResponse game = await AcceptGameAsync(black, gameUrl);
        (HttpClient Player, string Move)[] cycle =
            [(white, "g1f3"), (black, "g8f6"), (white, "f3g1"), (black, "f6g8")];
        for (int repetition = 0; repetition < 4; repetition++)
        {
            Assert.Equal("Active", game.Status);
            foreach ((HttpClient player, string move) in cycle)
            {
                game = await MakeMoveAsync(player, gameUrl, move, game.Revision);
            }
        }
        Assert.Equal("Finished", game.Status);
        Assert.Equal("FivefoldRepetition", game.EndReason);
        Assert.Null(game.Winner);
    }
}
