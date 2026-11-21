using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed class GameService(IGameStore store, IGameRules rules, IGamePlayerDirectory players)
{
    public async Task<GameOperationResult> CreateAsync(Guid creatorId, Guid opponentId, CancellationToken cancellationToken)
    {
        if (!Game.CanPair(creatorId, opponentId))
        {
            return new(GameError.InvalidPlayers);
        }
        if (!await players.AreEligibleAsync(creatorId, opponentId, cancellationToken))
        {
            return new(GameError.OpponentUnavailable);
        }

        Game game = new(creatorId, opponentId, rules.StartingPosition());
        await store.CreateAsync(game, cancellationToken);
        return new(GameError.None, game);
    }

    public async Task<Game?> GetAsync(Guid gameId, Guid playerId, CancellationToken cancellationToken)
    {
        Game? game = await store.FindAsync(gameId, cancellationToken);
        return game?.HasPlayer(playerId) == true ? game : null;
    }

    public Task<GameOperationResult> AcceptAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, game => game.Accept(playerId, expectedRevision), cancellationToken);

    public Task<GameOperationResult> PlayAsync(Guid gameId, Guid playerId, long expectedRevision, string move,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, game => game.Play(playerId, expectedRevision, record => rules.Play(record, move)), cancellationToken);

    private async Task<GameOperationResult> ChangeAsync(Guid gameId, Func<Game, GameError> change,
        CancellationToken cancellationToken)
    {
        Game? game = await store.FindAsync(gameId, cancellationToken);
        if (game is null)
        {
            return new(GameError.NotFound);
        }

        GameError error = change(game);
        if (error != GameError.None)
        {
            return new(error);
        }
        return await store.TrySaveChangesAsync(cancellationToken)
            ? new(GameError.None, game)
            : new(GameError.RevisionConflict);
    }
}
