using ChessRealms.Engine;
using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Infrastructure.Games;

internal sealed class EngineGameRules : IGameRules
{
    public GamePosition StartingPosition() => ReadPosition(new ChessGame());

    public GameMoveList ReadMoves(string rulesRecord, bool includeLegalMoves)
    {
        ChessGame? game = includeLegalMoves ? ChessGame.FromRecordJson(rulesRecord) : null;
        GameRecord record = game?.ExportRecord() ?? GameRecord.Deserialize(rulesRecord);
        string[] legalMoves = game?.GetLegalMoves().Select(move => move.ToString()).ToArray() ?? [];
        DrawClaim available = game?.AvailableDrawClaims ?? DrawClaim.None;
        List<DrawClaimReason> drawClaims = [];
        if ((available & DrawClaim.ThreefoldRepetition) != 0)
        {
            drawClaims.Add(DrawClaimReason.ThreefoldRepetition);
        }
        if ((available & DrawClaim.FiftyMoveRule) != 0)
        {
            drawClaims.Add(DrawClaimReason.FiftyMoveRule);
        }
        return new(record.Moves.Select(move => move.ToString()).ToArray(), legalMoves, drawClaims);
    }

    public GamePosition? Play(string rulesRecord, string move)
    {
        if (!CoordinateMove.TryParse(move, out CoordinateMove coordinate))
        {
            return null;
        }

        ChessGame game = ChessGame.FromRecordJson(rulesRecord);
        return game.MakeMove(coordinate) == MoveResult.None ? null : ReadPosition(game);
    }

    public GamePosition? ClaimDraw(string rulesRecord, DrawClaimReason reason, string? intendedMove)
    {
        CoordinateMove? move = null;
        if (intendedMove is not null)
        {
            if (!CoordinateMove.TryParse(intendedMove, out CoordinateMove coordinate))
            {
                return null;
            }
            move = coordinate;
        }

        DrawClaim claim = reason switch
        {
            DrawClaimReason.ThreefoldRepetition => DrawClaim.ThreefoldRepetition,
            DrawClaimReason.FiftyMoveRule => DrawClaim.FiftyMoveRule,
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        ChessGame game = ChessGame.FromRecordJson(rulesRecord);
        return game.ClaimDraw(claim, move) ? ReadPosition(game) : null;
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
            FinishReason.ThreefoldRepetition => GameEndReason.ThreefoldRepetition,
            FinishReason.FiftyMoveRule => GameEndReason.FiftyMoveRule,
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
