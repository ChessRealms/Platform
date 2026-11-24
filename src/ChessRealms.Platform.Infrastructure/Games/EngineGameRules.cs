using System.Globalization;

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

    public GameReplayLine ReadReplay(string rulesRecord)
    {
        GameRecord record = GameRecord.Deserialize(rulesRecord);
        ChessGame game = ChessGame.FromFen(record.InitialFen);
        GameReplayFrame[] frames = new GameReplayFrame[record.Moves.Count];
        for (int index = 0; index < frames.Length; index++)
        {
            CoordinateMove move = record.Moves[index];
            string san = game.FormatSan(move);
            string moveNumber = game.FullmoveNumber.ToString(CultureInfo.InvariantCulture);
            PlayerColor color = ToColor(game.CurrentColor);
            game.MakeMove(move);
            frames[index] = new(move.ToString(), san, game.ToFen(), moveNumber, color);
        }
        return new(record.InitialFen, frames);
    }

    public bool HasInsufficientMatingMaterial(string fen, PlayerColor attacker)
    {
        PieceColor attackingColor = attacker == PlayerColor.White ? PieceColor.White : PieceColor.Black;
        Span<ChessPiece> board = stackalloc ChessPiece[64];
        ChessGame.FromFen(fen).CopyBoardTo(board);

        int attackingMinors = 0;
        bool attackerHasKnight = false;
        int bishopSquareColors = 0;
        bool defenderHasNonQueen = false;
        bool defenderHasPawnOrKnight = false;
        for (int index = 0; index < board.Length; index++)
        {
            ChessPiece piece = board[index];
            if (piece.IsEmpty || piece.Value == PieceValue.King)
            {
                continue;
            }

            if (piece.Color == attackingColor)
            {
                if (piece.Value is PieceValue.Pawn or PieceValue.Rook or PieceValue.Queen)
                {
                    return false;
                }
                attackingMinors++;
                attackerHasKnight |= piece.Value == PieceValue.Knight;
            }
            else
            {
                defenderHasNonQueen |= piece.Value != PieceValue.Queen;
                defenderHasPawnOrKnight |= piece.Value is PieceValue.Pawn or PieceValue.Knight;
            }

            if (piece.Value == PieceValue.Bishop)
            {
                int squareColor = (index / 8 + index % 8) % 2;
                bishopSquareColors |= 1 << squareColor;
            }
        }

        if (attackingMinors == 0)
        {
            return true;
        }

        // Count the defender's pieces too. They can block their own king in a mating position.
        if (attackerHasKnight)
        {
            return attackingMinors == 1 && !defenderHasNonQueen;
        }

        const int bothSquareColors = 0b11;
        return bishopSquareColors != bothSquareColors && !defenderHasPawnOrKnight;
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
        return new(GameRecord.Serialize(game.ExportRecord()), game.ToFen(), ToColor(game.CurrentColor), winner, endReason, game.PlyCount);
    }

    private static PlayerColor ToColor(PieceColor color) => color switch
    {
        PieceColor.White => PlayerColor.White,
        PieceColor.Black => PlayerColor.Black,
        _ => throw new InvalidOperationException("Unexpected chess player color.")
    };
}
