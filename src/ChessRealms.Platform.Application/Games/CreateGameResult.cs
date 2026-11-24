using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed record CreateGameResult(GameError Error, Game? Game = null, bool Created = false, TimeSpan? RetryAfter = null)
{
    public static CreateGameResult Replay(Game game, Guid opponentId, GameTimeControl? timeControl) =>
        game.BlackPlayerId == opponentId && game.TimeControl == timeControl
            ? new(GameError.None, game) : new(GameError.CreationRequestConflict);
}
