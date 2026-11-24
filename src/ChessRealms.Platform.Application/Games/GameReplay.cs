using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed record GameReplayLine(string InitialFen, IReadOnlyList<GameReplayFrame> Frames);

public sealed record GameReplayFrame(string Move, string San, string Fen, string MoveNumber, PlayerColor Color);

public sealed record GameReplayMove(GameReplayFrame Frame, long? AtMs, GameReplayClock? Clock);

public sealed record GameReplayClock(double WhiteMilliseconds, double BlackMilliseconds)
{
    internal static GameReplayClock? From(TimeSpan? white, TimeSpan? black) =>
        white is { } whiteTime && black is { } blackTime ? new(whiteTime.TotalMilliseconds, blackTime.TotalMilliseconds) : null;
}

public sealed record GameReplayFinish(PlayerColor? Winner, GameEndReason? EndReason, long? AtMs, GameReplayClock? Clock);

public sealed record GameReplay(string InitialFen, PlayerColor ViewerColor, GameTimeControl? TimeControl,
    bool HasRecordedTiming, GameReplayClock? InitialClock, IReadOnlyList<GameReplayMove> Moves, GameReplayFinish Finish)
{
    internal static GameReplay From(Game game, Guid playerId, GameReplayLine line, IReadOnlyList<GameMoveTiming> timings)
    {
        DateTimeOffset? start = HasCompleteTiming(game, line.Frames.Count, timings) ? game.StartedAt : null;
        TimeSpan? initial = start.HasValue ? Game.InitialTime(game.TimeControl) : null;
        GameReplayMove[] moves = new GameReplayMove[line.Frames.Count];
        for (int index = 0; index < moves.Length; index++)
        {
            GameMoveTiming? timing = start.HasValue ? timings[index] : null;
            moves[index] = new(line.Frames[index], Elapsed(timing?.PlayedAt, start),
                GameReplayClock.From(timing?.WhiteTimeRemaining, timing?.BlackTimeRemaining));
        }

        PlayerColor viewer = game.WhitePlayerId == playerId ? PlayerColor.White : PlayerColor.Black;
        return new(line.InitialFen, viewer, game.TimeControl, start.HasValue, GameReplayClock.From(initial, initial), moves,
            new(game.Winner, game.EndReason, Elapsed(game.FinishedAt, start),
                GameReplayClock.From(game.WhiteTimeRemaining, game.BlackTimeRemaining)));
    }

    private static bool HasCompleteTiming(Game game, int moveCount, IReadOnlyList<GameMoveTiming> timings)
    {
        if (game.StartedAt is not { } start || game.FinishedAt is not { } finish || finish < start || timings.Count != moveCount)
        {
            return false;
        }
        DateTimeOffset previous = start;
        for (int index = 0; index < timings.Count; index++)
        {
            GameMoveTiming timing = timings[index];
            if (timing.Ply != index + 1 || timing.PlayedAt < previous || timing.PlayedAt > finish
                || (game.TimeControl.HasValue && (timing.WhiteTimeRemaining is null || timing.BlackTimeRemaining is null)))
            {
                return false;
            }
            previous = timing.PlayedAt;
        }
        return true;
    }

    private static long? Elapsed(DateTimeOffset? time, DateTimeOffset? start) => (time - start)?.Ticks / TimeSpan.TicksPerMillisecond;
}
