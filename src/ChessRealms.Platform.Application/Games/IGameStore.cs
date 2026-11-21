using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public interface IGameStore
{
    Task<Game?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task CreateAsync(Game game, CancellationToken cancellationToken);
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken);
}
