using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public interface IGameStore
{
    Task<Game?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<GameBoardSnapshot?> ReadBoardAsync(Guid id, Guid playerId, long? afterRevision, CancellationToken cancellationToken);
    Task<Game?> FindByCreationRequestAsync(Guid creatorId, Guid requestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GameListItem>> ListAsync(Guid playerId, GameStatus status, Guid? before, int limit,
        CancellationToken cancellationToken);
    Task<Game> CreateOrGetAsync(Game game, Guid? requestId, CancellationToken cancellationToken);
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken);
}
