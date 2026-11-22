using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed record GameMoveList(IReadOnlyList<string> Moves, IReadOnlyList<string> LegalMoves,
    IReadOnlyList<DrawClaimReason> DrawClaims);
