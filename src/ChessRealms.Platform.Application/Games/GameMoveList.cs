namespace ChessRealms.Platform.Application.Games;

public sealed record GameMoveList(IReadOnlyList<string> Moves, IReadOnlyList<string> LegalMoves);
