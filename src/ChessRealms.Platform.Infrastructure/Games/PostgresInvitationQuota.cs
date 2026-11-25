using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Games;

internal static class PostgresInvitationQuota
{
    private const int MaxOpenInvitations = 3;

    // Both creation paths take this transaction lock before counting or inserting invitations.
    internal static Task LockCreatorAsync(PlatformDbContext database, Guid creatorId, CancellationToken cancellationToken)
    {
        string key = $"game-invitations:{creatorId:D}";
        return database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }

    internal static async Task<bool> HasCapacityAsync(PlatformDbContext database, Guid creatorId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IQueryable<Guid> direct = database.Games.Where(game => game.WhitePlayerId == creatorId
            && game.CreationOrigin == GameCreationOrigin.DirectInvitation
            && game.Status == GameStatus.Pending && game.InvitationExpiresAt > now).Select(game => game.Id);
        IQueryable<Guid> links = database.PrivateLobbies.Where(lobby => lobby.CreatorId == creatorId
            && lobby.Status == LobbyStatus.Open && lobby.ExpiresAt > now).Select(lobby => lobby.Id);
        return await direct.Concat(links).CountAsync(cancellationToken) < MaxOpenInvitations;
    }

    internal static DateTimeOffset GetCreationTime(TimeProvider clock)
    {
        // Match PostgreSQL timestamp precision in the creation response and subsequent reads.
        DateTimeOffset now = clock.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
