using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed record GameBoardSnapshot(
    Guid Id,
    Guid WhitePlayerId,
    Guid BlackPlayerId,
    GameStatus Status,
    long Revision,
    string Fen,
    PlayerColor SideToMove,
    PlayerColor? Winner,
    GameEndReason? EndReason,
    PlayerColor? DrawOfferedBy,
    string? RulesRecord);
