using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public interface IGameRules
{
    GamePosition StartingPosition();
    GamePosition? Play(string rulesRecord, string move);
    bool HasInsufficientMatingMaterial(string fen, PlayerColor attacker);
    GamePosition? ClaimDraw(string rulesRecord, DrawClaimReason reason, string? intendedMove);
    GameMoveList ReadMoves(string rulesRecord, bool includeLegalMoves);
}
