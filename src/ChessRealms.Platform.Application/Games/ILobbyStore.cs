using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public interface ILobbyStore
{
    Task<PrivateLobby?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<LobbyResult> CreateAsync(Guid playerId, Guid requestId, GameTimeControl? timeControl, CancellationToken cancellationToken);
    Task<LobbyResult> JoinAsync(Guid id, Guid playerId, long expectedRevision, GamePosition position,
        CancellationToken cancellationToken);
    Task<LobbyResult> CancelAsync(Guid id, Guid playerId, long expectedRevision, CancellationToken cancellationToken);
}
