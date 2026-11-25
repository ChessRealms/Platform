using System.Net;

using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace ChessRealms.Platform.Api.Tests.Games;

public sealed class GameReplayTests : GameApiTestBase
{
    [Fact]
    public async Task ReplayIsPrivateAndResignationWithoutMovesHasItsOwnTime()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (_, string outsiderToken) = await RegisterAndSignInAsync("outsider");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        using HttpClient outsider = CreateClient(outsiderToken);
        using HttpClient anonymous = CreateClient();
        GameResponse pending = await CreateGameAsync(white, blackId);
        string url = $"/api/games/{pending.Id}";
        Clock.Advance(TimeSpan.FromSeconds(30));
        GameResponse active = await AcceptGameAsync(black, url);
        using HttpResponseMessage tooEarly = await white.GetAsync($"{url}/replay", Cancellation);
        await AssertErrorAsync(tooEarly, HttpStatusCode.Conflict, "GameNotFinished");
        using HttpResponseMessage prematureExport = await white.GetAsync($"{url}/pgn", Cancellation);
        await AssertErrorAsync(prematureExport, HttpStatusCode.Conflict, "GameNotFinished");
        Clock.Advance(TimeSpan.FromSeconds(7));
        GameResponse finished = await ApplyActionAsync(black, url, "resign", active.Revision);
        using HttpResponseMessage inaccessible = await outsider.GetAsync($"{url}/replay", Cancellation);
        await AssertErrorAsync(inaccessible, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage outsiderExport = await outsider.GetAsync($"{url}/pgn", Cancellation);
        await AssertErrorAsync(outsiderExport, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage anonymousExport = await anonymous.GetAsync($"{url}/pgn", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousExport.StatusCode);

        GameReplayResponse replay = await ReadReplayAsync(black, url);
        Assert.Equal("Black", replay.ViewerColor);
        Assert.Equal("recorded", replay.TimingMode);
        Assert.Equal(finished.Fen, replay.InitialFen);
        Assert.Empty(replay.Moves);
        Assert.Equal("White", replay.Finish.Winner);
        Assert.Equal("Resignation", replay.Finish.EndReason);
        Assert.Equal(7000L, replay.Finish.AtMs);
        string pgn = await DownloadPgnAsync(white, url);
        Assert.Equal(pgn, await DownloadPgnAsync(black, url));
        Assert.Contains("[White \"white\"]\n[Black \"black\"]\n[Result \"1-0\"]", pgn);
        Assert.DoesNotContain("[FEN ", pgn);
        Assert.EndsWith("\n\n1-0\n\n", pgn);
    }

    [Theory]
    [InlineData("7k/8/8/3pP3/8/8/8/7K w - d6 0 2", "e5d6", "exd6", "7k/8/3P4/8/8/8/8/7K", "White", "2", false, "1/2-1/2")]
    [InlineData("7k/P7/8/8/8/8/8/7K w - - 0 1", "a7a8q", "a8=Q+", "Q6k/8/8/8/8/8/8/7K", "White", "1", false, "1/2-1/2")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R b KQkq - 0 37", "e8c8", "O-O-O", "2kr3r/8/8/8/8/8/8/R3K2R", "Black", "37", true, "0-1")]
    public async Task ReplayUsesTheRecordedInitialPositionAndHandlesSpecialMoves(string fen, string coordinate, string san,
        string board, string color, string moveNumber, bool legacy, string result)
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        await using AsyncServiceScope scope = CreateScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        PlayerColor startingColor = Enum.Parse<PlayerColor>(color);
        Game game = new(whiteId, blackId, new(PositionRecord(fen), fen, startingColor), Clock.GetUtcNow());
        database.Games.Add(game);
        await database.SaveChangesAsync(Cancellation);
        string url = $"/api/games/{game.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        if (legacy)
        {
            await database.Games.Where(row => row.Id == game.Id).ExecuteUpdateAsync(
                updates => updates.SetProperty(row => row.StartedAt, (DateTimeOffset?)null), Cancellation);
        }

        Clock.Advance(TimeSpan.FromSeconds(3));
        GameResponse moved = await MakeMoveAsync(startingColor == PlayerColor.White ? white : black, url, coordinate, active.Revision);
        if (legacy)
        {
            await database.GameMoveTimings.Where(timing => timing.GameId == game.Id).ExecuteDeleteAsync(Cancellation);
        }
        Clock.Advance(TimeSpan.FromSeconds(8));
        await ApplyActionAsync(white, url, "resign", moved.Revision);
        GameReplayResponse replay = await ReadReplayAsync(black, url);
        GameReplayMoveResponse frame = Assert.Single(replay.Moves);
        Assert.Equal(fen, replay.InitialFen);
        Assert.Equal(san, frame.San);
        Assert.Equal(moveNumber, frame.MoveNumber);
        Assert.Equal(color, frame.Color);
        Assert.Equal(board, frame.Fen.Split(' ')[0]);
        Assert.Equal(legacy ? "unavailable" : "recorded", replay.TimingMode);
        Assert.Equal(legacy ? null : 3000L, frame.AtMs);
        Assert.Equal(legacy ? null : 11000L, replay.Finish.AtMs);
        string pgn = await DownloadPgnAsync(black, url);
        Assert.Contains($"[FEN \"{fen}\"]\n[SetUp \"1\"]", pgn);
        Assert.Contains($"[Result \"{result}\"]", pgn);
        if (legacy)
        {
            Assert.Contains("[Date \"????.??.??\"]", pgn);
        }
        string number = moveNumber + (startingColor == PlayerColor.White ? "." : "...");
        Assert.EndsWith($"\n\n{number} {san} {result}\n\n", pgn);
    }
}
