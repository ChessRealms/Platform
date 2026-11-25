using System.Globalization;
using System.Text;

using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

internal static class GamePgn
{
    private const string StandardInitialFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const int MovetextLineLength = 79;

    internal static string Format(Game game, GameReplayLine line, string? whiteName, string? blackName)
    {
        string result = game.Winner switch
        {
            PlayerColor.White => "1-0",
            PlayerColor.Black => "0-1",
            _ => "1/2-1/2"
        };
        StringBuilder pgn = new();
        AppendTag("Event", "ChessRealms game");
        AppendTag("Site", "ChessRealms");
        AppendTag("Date", game.StartedAt?.UtcDateTime.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) ?? "????.??.??");
        AppendTag("Round", "-");
        AppendTag("White", whiteName ?? "?");
        AppendTag("Black", blackName ?? "?");
        AppendTag("Result", result);
        if (!string.Equals(line.InitialFen, StandardInitialFen, StringComparison.Ordinal))
        {
            AppendTag("FEN", line.InitialFen);
            AppendTag("SetUp", "1");
        }
        AppendTag("Termination", game.EndReason is GameEndReason.Timeout or GameEndReason.TimeoutNoPossibleMate
            ? "time forfeit" : "normal");
        AppendTag("TimeControl", Game.InitialTime(game.TimeControl)?.TotalSeconds.ToString(CultureInfo.InvariantCulture) ?? "-");

        pgn.Append('\n');
        int column = 0;
        for (int index = 0; index < line.Frames.Count; index++)
        {
            GameReplayFrame frame = line.Frames[index];
            if (frame.Color == PlayerColor.White)
            {
                AppendToken(frame.MoveNumber + ".");
            }
            else if (index == 0)
            {
                AppendToken(frame.MoveNumber + "...");
            }
            AppendToken(frame.San);
        }
        AppendToken(result);
        pgn.Append("\n\n");
        return pgn.ToString();

        void AppendTag(string name, string value)
        {
            pgn.Append('[').Append(name).Append(" \"");
            foreach (char character in value)
            {
                if (character is '\\' or '"')
                {
                    pgn.Append('\\');
                }
                pgn.Append(char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character);
            }
            pgn.Append("\"]\n");
        }

        void AppendToken(string token)
        {
            if (column > 0)
            {
                if (column + 1 + token.Length > MovetextLineLength)
                {
                    pgn.Append('\n');
                    column = 0;
                }
                else
                {
                    pgn.Append(' ');
                    column++;
                }
            }
            pgn.Append(token);
            column += token.Length;
        }
    }
}
