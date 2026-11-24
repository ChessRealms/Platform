using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Games;

internal sealed class PostgresLobbyStore(PlatformDbContext database, TimeProvider clock) : ILobbyStore
{
    public Task<PrivateLobby?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.PrivateLobbies.AsNoTracking().SingleOrDefaultAsync(lobby => lobby.Id == id, cancellationToken);

    public async Task<LobbyResult> CreateAsync(Guid playerId, Guid requestId, GameTimeControl? timeControl,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await PostgresInvitationQuota.LockCreatorAsync(database, playerId, cancellationToken);
        PrivateLobby? existing = await database.PrivateLobbies.AsNoTracking().SingleOrDefaultAsync(
            lobby => lobby.CreatorId == playerId && lobby.RequestId == requestId, cancellationToken);
        if (existing is not null)
        {
            return existing.TimeControl == timeControl
                ? new(GameError.None, existing) : new(GameError.CreationRequestConflict);
        }
        DateTimeOffset now = PostgresInvitationQuota.GetCreationTime(clock);
        if (!await PostgresInvitationQuota.HasCapacityAsync(database, playerId, now, cancellationToken))
        {
            return new(GameError.InvitationLimitReached);
        }

        PrivateLobby lobby = new(playerId, requestId, now, timeControl);
        database.PrivateLobbies.Add(lobby);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(GameError.None, lobby, Created: true);
    }

    public Task<LobbyResult> JoinAsync(Guid id, Guid playerId, long expectedRevision, GamePosition position,
        CancellationToken cancellationToken) =>
        ChangeAsync(id, (lobby, now) =>
        {
            GameError error = lobby.Join(playerId, expectedRevision, now, position, out Game? game);
            if (game is not null)
            {
                database.Games.Add(game);
            }
            return error;
        }, cancellationToken);

    public Task<LobbyResult> CancelAsync(Guid id, Guid playerId, long expectedRevision, CancellationToken cancellationToken) =>
        ChangeAsync(id, (lobby, now) => lobby.Cancel(playerId, expectedRevision, now), cancellationToken);

    private async Task<LobbyResult> ChangeAsync(Guid id, Func<PrivateLobby, DateTimeOffset, GameError> change,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        PrivateLobby? lobby = await database.PrivateLobbies.FromSql($"""
            SELECT * FROM "PrivateLobbies" WHERE "Id" = {id} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);
        if (lobby is null)
        {
            return new(GameError.NotFound);
        }
        GameError error = change(lobby, clock.GetUtcNow());
        if (error != GameError.None)
        {
            return new(error);
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(GameError.None, lobby);
    }
}
