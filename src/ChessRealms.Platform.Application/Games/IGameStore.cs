using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public interface IGameStore
{
    Task<Game?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<GameBoardSnapshot?> ReadBoardAsync(Guid id, Guid playerId, long? afterRevision, CancellationToken cancellationToken);
    Task<Game?> FindByCreationRequestAsync(Guid creatorId, Guid requestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GameListItem>> ListAsync(Guid playerId, GameListFilter filter, Guid? before, int limit,
        CancellationToken cancellationToken);
    Task<CreateGameResult> CreateAsync(Guid creatorId, Guid opponentId, GamePosition startingPosition, Guid? requestId,
        CancellationToken cancellationToken);
    Task<GameOperationResult> ChangeLockedAsync(Guid gameId, Guid playerId, Func<Game, DateTimeOffset, GameError> change,
        CancellationToken cancellationToken);
    Task<GameOperationResult> AcceptRematchAsync(Guid gameId, Guid playerId, long expectedRevision, Func<GamePosition> startingPosition,
        CancellationToken cancellationToken);
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken);
}
