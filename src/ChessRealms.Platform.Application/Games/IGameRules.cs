using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public interface IGameRules
{
    GamePosition StartingPosition();
    GamePosition? Play(string rulesRecord, string move);
    GameMoveList ReadMoves(string rulesRecord, bool includeLegalMoves);
}
