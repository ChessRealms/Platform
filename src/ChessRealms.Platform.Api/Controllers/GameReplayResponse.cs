using ChessRealms.Platform.Application.Games;

namespace ChessRealms.Platform.Api.Controllers;

public sealed record GameReplayResponse(string InitialFen, string ViewerColor, string? TimeControl, string TimingMode,
    GameReplayClock? InitialClock, IReadOnlyList<GameReplayMoveResponse> Moves, GameReplayFinishResponse Finish)
{
    internal static GameReplayResponse From(GameReplay replay) =>
        new(replay.InitialFen, replay.ViewerColor.ToString(), replay.TimeControl?.ToString(),
            replay.HasRecordedTiming ? "recorded" : "unavailable", replay.InitialClock,
            replay.Moves.Select(move => new GameReplayMoveResponse(move.Frame.Move, move.Frame.San, move.Frame.Fen,
                move.Frame.MoveNumber, move.Frame.Color.ToString(), move.AtMs, move.Clock)).ToArray(),
            new(replay.Finish.Winner?.ToString(), replay.Finish.EndReason?.ToString(), replay.Finish.AtMs, replay.Finish.Clock));
}

public sealed record GameReplayMoveResponse(string Move, string San, string Fen, string MoveNumber, string Color,
    long? AtMs, GameReplayClock? Clock);

public sealed record GameReplayFinishResponse(string? Winner, string? EndReason, long? AtMs, GameReplayClock? Clock);
