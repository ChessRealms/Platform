using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Identity.Sessions;

public sealed class LoginSessionCleanup(IdentityDbContext database, TimeProvider clock)
{
    public const int BatchSize = 1000;

    public Task<int> DeleteExpiredBatchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        // Keep even revoked sessions and consumed hashes until absolute expiration.
        // The foreign key cascades deletion to the entire token history.
        return database.LoginSessions.Where(session => session.ExpiresAt <= now)
            .OrderBy(session => session.ExpiresAt).Take(BatchSize).ExecuteDeleteAsync(cancellationToken);
    }
}
