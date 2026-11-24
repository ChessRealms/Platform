using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Infrastructure.Identity.Tokens;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Games;

internal sealed class PostgresGuestSessions(PlatformDbContext database, TimeProvider clock) : IGuestSessions
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromDays(1);

    public async Task<(GuestSession Session, string Token)> CreateAsync(string displayName, CancellationToken cancellationToken)
    {
        string token = OpaqueToken.Generate();
        GuestSession session = new(Guid.NewGuid(), displayName, clock.GetUtcNow() + Lifetime);
        database.GuestPlayers.Add(new(session.PlayerId, session.DisplayName, OpaqueToken.Hash(token), session.ExpiresAt));
        await database.SaveChangesAsync(cancellationToken);
        return (session, token);
    }

    public async Task<GuestSession?> FindAsync(string token, CancellationToken cancellationToken)
    {
        if (token.Length != OpaqueToken.EncodedLength)
        {
            return null;
        }

        string hash = OpaqueToken.Hash(token);
        DateTimeOffset now = clock.GetUtcNow();
        var active = database.GuestPlayers.Where(player => player.SessionHash == hash && player.ExpiresAt > now);
        GuestSession? session = await active.AsNoTracking()
            .Select(player => new GuestSession(player.Id, player.DisplayName, player.ExpiresAt))
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null || session.ExpiresAt > now + Lifetime - RenewalInterval)
        {
            return session;
        }

        DateTimeOffset renewed = now + Lifetime;
        int changed = await active.Where(player => player.ExpiresAt == session.ExpiresAt)
            .ExecuteUpdateAsync(update => update.SetProperty(player => player.ExpiresAt, renewed), cancellationToken);
        return changed == 1 ? session with { ExpiresAt = renewed, RenewCookie = true } : await active.AsNoTracking()
            .Select(player => new GuestSession(player.Id, player.DisplayName, player.ExpiresAt, true))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        if (token.Length == OpaqueToken.EncodedLength)
        {
            string hash = OpaqueToken.Hash(token);
            await database.GuestPlayers.Where(player => player.SessionHash == hash)
                .ExecuteUpdateAsync(update => update.SetProperty(player => player.SessionHash, (string?)null), cancellationToken);
        }
    }
}
