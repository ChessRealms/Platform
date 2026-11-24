using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed class LobbyService(ILobbyStore store, IGamePlayerDirectory players, IGameRules rules)
{
    public async Task<LobbyResult> CreateAsync(Guid playerId, Guid requestId, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            return new(GameError.InvalidRequestId);
        }
        if (!await players.IsEligibleAsync(playerId, cancellationToken))
        {
            return new(GameError.Forbidden);
        }

        return await store.CreateAsync(playerId, requestId, cancellationToken);
    }

    public async Task<LobbyResult> GetAsync(Guid id, Guid playerId, CancellationToken cancellationToken)
    {
        PrivateLobby? lobby = await store.FindAsync(id, cancellationToken);
        if (lobby is null)
        {
            return new(GameError.NotFound);
        }
        if (lobby.Status == LobbyStatus.Joined && playerId != lobby.CreatorId && playerId != lobby.JoinedPlayerId)
        {
            return new(GameError.LobbyAlreadyJoined);
        }

        return new(GameError.None, lobby);
    }

    public async Task<LobbyResult> JoinAsync(Guid id, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken)
    {
        LobbyResult read = await GetAsync(id, playerId, cancellationToken);
        if (read.Lobby is not { } lobby)
        {
            return read;
        }
        if (lobby.CreatorId == playerId)
        {
            return new(GameError.Forbidden);
        }
        if (lobby.JoinedPlayerId == playerId)
        {
            return read;
        }
        if (!await players.AreEligibleAsync(lobby.CreatorId, playerId, cancellationToken))
        {
            return new(GameError.Forbidden);
        }

        return await store.JoinAsync(id, playerId, expectedRevision, rules.StartingPosition(), cancellationToken);
    }

    public Task<LobbyResult> CancelAsync(Guid id, Guid playerId, long expectedRevision, CancellationToken cancellationToken) =>
        store.CancelAsync(id, playerId, expectedRevision, cancellationToken);
}
