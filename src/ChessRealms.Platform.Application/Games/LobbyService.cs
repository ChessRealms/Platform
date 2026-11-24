using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed class LobbyService(ILobbyStore store, IGamePlayerDirectory players, IGameRules rules)
{
    public async Task<LobbyResult> CreateAsync(Guid playerId, Guid requestId, GameTimeControl? timeControl,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            return new(GameError.InvalidRequestId);
        }
        if (!await players.IsEligibleAsync(playerId, cancellationToken))
        {
            return new(GameError.Forbidden);
        }

        return await store.CreateAsync(playerId, requestId, timeControl, cancellationToken);
    }

    public async Task<LobbyResult> GetAsync(Guid id, GameParticipant? player, CancellationToken cancellationToken)
    {
        PrivateLobby? lobby = await store.FindAsync(id, cancellationToken);
        if (lobby is null)
        {
            return new(GameError.NotFound);
        }
        if (lobby.Status == LobbyStatus.Joined && !lobby.IsOwner(player) && !lobby.IsJoiner(player))
        {
            return new(GameError.LobbyAlreadyJoined);
        }

        return new(GameError.None, lobby);
    }

    public async Task<LobbyResult> JoinAsync(Guid id, GameParticipant player, string? guestName, long expectedRevision,
        CancellationToken cancellationToken)
    {
        LobbyResult read = await GetAsync(id, player, cancellationToken);
        if (read.Lobby is not { } lobby)
        {
            return read;
        }
        if (lobby.IsOwner(player))
        {
            return new(GameError.Forbidden);
        }
        if (lobby.IsJoiner(player))
        {
            return read;
        }
        bool eligible = player.IsGuest
            ? await players.IsEligibleAsync(lobby.CreatorId, cancellationToken)
            : await players.AreEligibleAsync(lobby.CreatorId, player.Id, cancellationToken);
        if (!eligible)
        {
            return new(GameError.Forbidden);
        }

        return await store.JoinAsync(id, player, guestName, expectedRevision, rules.StartingPosition(), cancellationToken);
    }

    public Task<LobbyResult> CancelAsync(Guid id, Guid playerId, long expectedRevision, CancellationToken cancellationToken) =>
        store.CancelAsync(id, playerId, expectedRevision, cancellationToken);
}
