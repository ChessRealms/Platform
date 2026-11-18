using System.Security.Cryptography;
using System.Text;

using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace ChessRealms.Platform.Infrastructure.Identity;

public sealed class RefreshTokenService(IdentityDbContext database, IAccessTokenIssuer accessTokens,
    IOptions<RefreshTokenOptions> options, TimeProvider clock) : IRefreshTokenService
{
    // Login owns the transaction and holds the user lock through the commit.
    internal async Task<RefreshResult> CreateSessionAsync(PlatformUser user, CancellationToken cancellationToken)
    {
        // PostgreSQL timestamps have lower precision than .NET ticks. Use milliseconds
        // so login and later refresh responses expose exactly the same expiration.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
        LoginSession session = new()
        {
            UserId = user.Id,
            SecurityStamp = user.SecurityStamp ?? throw new InvalidOperationException("A login session requires a security stamp."),
            CreatedAt = now,
            ExpiresAt = now + options.Value.SessionLifetime
        };
        database.LoginSessions.Add(session);
        return await IssuePairAsync(session, now, cancellationToken);
    }

    public async Task<RefreshResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.RefreshToken) || request.RefreshToken.Length != 43)
        {
            return new("InvalidRefreshToken");
        }
        string hash = Hash(request.RefreshToken);
        // Only discover the owner here. All authorization decisions are made again
        // after locking, including token consumption and expiration.
        LoginSession? owner = await (from token in database.RefreshTokens.AsNoTracking()
                                     join session in database.LoginSessions.AsNoTracking() on token.SessionId equals session.Id
                                     where token.TokenHash == hash
                                     select session).SingleOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            return new("InvalidRefreshToken");
        }

        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // Consistent user -> session lock order with login and future account changes.
        PlatformUser? user = await database.Users.FromSqlInterpolated(
            $"SELECT * FROM identity.\"AspNetUsers\" WHERE \"Id\" = {owner.UserId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        LoginSession? current = await database.LoginSessions.FromSqlInterpolated(
            $"SELECT * FROM identity.\"LoginSessions\" WHERE \"Id\" = {owner.Id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
        if (user is null || current is null || current.RevokedAt is not null || current.ExpiresAt <= now)
        {
            return new("InvalidRefreshToken");
        }
        RefreshToken? stored = await database.RefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(token => token.TokenHash == hash && token.SessionId == current.Id, cancellationToken);
        if (stored is null)
        {
            return new("InvalidRefreshToken");
        }

        string? revocationReason = stored.UsedAt is not null ? "RefreshTokenReuse"
            : !user.EmailConfirmed ? "EmailNotConfirmed"
            : !string.Equals(user.SecurityStamp, current.SecurityStamp, StringComparison.Ordinal) ? "SecurityStampChanged"
            : null;
        if (revocationReason is not null)
        {
            await database.LoginSessions.Where(session => session.Id == current.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAt, now)
                    .SetProperty(session => session.RevocationReason, revocationReason), cancellationToken);
            // Revocation is a successful database operation even though HTTP returns 401.
            await transaction.CommitAsync(cancellationToken);
            return new("InvalidRefreshToken");
        }

        await database.RefreshTokens.Where(token => token.TokenHash == hash)
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.UsedAt, now), cancellationToken);
        RefreshResult result = await IssuePairAsync(current, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<RefreshResult> IssuePairAsync(LoginSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string refreshToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        database.RefreshTokens.Add(new()
        {
            TokenHash = Hash(refreshToken),
            SessionId = session.Id,
            CreatedAt = now
        });
        await database.SaveChangesAsync(cancellationToken);
        AccessToken accessToken = accessTokens.Issue(session.UserId, session.Id);
        return new("Success", accessToken.Value, "Bearer", accessToken.ExpiresIn, refreshToken, session.ExpiresAt);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
