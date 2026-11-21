using ChessRealms.Engine;
using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Infrastructure.Games;

internal sealed class EngineGameRules : IGameRules
{
    public GamePosition StartingPosition() => ReadPosition(new ChessGame());

    public GamePosition? Play(string rulesRecord, string move)
    {
        if (!CoordinateMove.TryParse(move, out CoordinateMove coordinate))
        {
            return null;
        }

        ChessGame game = ChessGame.FromRecord(GameRecord.Deserialize(rulesRecord));
        return game.MakeMove(coordinate) == MoveResult.None ? null : ReadPosition(game);
    }

    private static GamePosition ReadPosition(ChessGame game)
    {
        GameEndReason? endReason = game.Outcome.Reason switch
        {
            FinishReason.None => null,
            FinishReason.Checkmate => GameEndReason.Checkmate,
            FinishReason.Stalemate => GameEndReason.Stalemate,
            FinishReason.DeadPosition => GameEndReason.DeadPosition,
            FinishReason.FivefoldRepetition => GameEndReason.FivefoldRepetition,
            FinishReason.SeventyFiveMoveRule => GameEndReason.SeventyFiveMoveRule,
            _ => throw new InvalidOperationException("Unexpected chess finish reason.")
        };
        PlayerColor? winner = game.Outcome.Winner == PieceColor.None ? null : ToColor(game.Outcome.Winner);
        return new(GameRecord.Serialize(game.ExportRecord()), game.ToFen(), ToColor(game.CurrentColor), winner, endReason);
    }

    private static PlayerColor ToColor(PieceColor color) => color switch
    {
        PieceColor.White => PlayerColor.White,
        PieceColor.Black => PlayerColor.Black,
        _ => throw new InvalidOperationException("Unexpected chess player color.")
    };
}
