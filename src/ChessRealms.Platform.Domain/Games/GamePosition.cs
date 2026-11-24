namespace ChessRealms.Platform.Domain.Games;

public sealed record GamePosition(
    string RulesRecord,
    string Fen,
    PlayerColor SideToMove,
    PlayerColor? Winner = null,
    GameEndReason? EndReason = null,
    int PlyCount = 0);
