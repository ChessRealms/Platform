using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ChessRealms.Platform.Infrastructure.Identity.Sessions;

public sealed class LoginSessionService(IdentityDbContext database, TimeProvider clock) : ILoginSessionService
{
    public async Task<SessionListResult> ListAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        (PlatformUser? user, LoginSession? current) = await LockCallerAsync(userId, currentSessionId, cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
        if (!IsUsable(user, current, now))
        {
            return new(SessionOperationResult.Unauthorized, []);
        }

        string? securityStamp = user!.SecurityStamp;
        List<LoginSessionInfo> sessions = await database.LoginSessions.AsNoTracking()
            .Where(session => session.UserId == userId && session.RevokedAt == null && session.ExpiresAt > now
                && session.SecurityStamp == securityStamp)
            .OrderBy(session => session.CreatedAt).ThenBy(session => session.Id)
            .Select(session => new LoginSessionInfo(session.Id, session.CreatedAt, session.ExpiresAt,
                session.Id == currentSessionId))
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(SessionOperationResult.Success, sessions);
    }

    public async Task<SessionOperationResult> LogoutAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var (_, current) = await LockCallerAsync(userId, currentSessionId, cancellationToken);
        if (current is not null && current.UserId != userId)
        {
            return SessionOperationResult.Unauthorized;
        }
        await RevokeIfActiveAsync(current, "Logout", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SessionOperationResult.Success;
    }

    public async Task<SessionOperationResult> RevokeAsync(Guid userId, Guid currentSessionId, Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId == currentSessionId)
        {
            return await LogoutAsync(userId, currentSessionId, cancellationToken);
        }

        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        (PlatformUser? user, LoginSession? current) = await LockCallerAsync(userId, currentSessionId, cancellationToken);
        if (!IsUsable(user, current, clock.GetUtcNow()))
        {
            return SessionOperationResult.Unauthorized;
        }
        LoginSession? target = await LockSessionAsync(sessionId, cancellationToken);
        if (target is null || target.UserId != userId)
        {
            return SessionOperationResult.NotFound;
        }
        await RevokeIfActiveAsync(target, "ManualRevocation", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SessionOperationResult.Success;
    }

    public async Task<SessionOperationResult> LogoutAllAsync(Guid userId, Guid currentSessionId,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        (PlatformUser? user, LoginSession? current) = await LockCallerAsync(userId, currentSessionId, cancellationToken);
        if (!IsUsable(user, current, clock.GetUtcNow()))
        {
            return SessionOperationResult.Unauthorized;
        }
        DateTimeOffset now = clock.GetUtcNow();
        await database.LoginSessions.Where(session => session.UserId == userId && session.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAt, now)
                .SetProperty(session => session.RevocationReason, "LogoutAll"), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SessionOperationResult.Success;
    }

    private async Task<(PlatformUser?, LoginSession?)> LockCallerAsync(Guid userId, Guid sessionId,
        CancellationToken cancellationToken)
    {
        // Match login and refresh: lock the user before any session.
        PlatformUser? user = await database.Users.FromSqlInterpolated(
            $"SELECT * FROM identity.\"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        LoginSession? current = await LockSessionAsync(sessionId, cancellationToken);
        return (user, current);
    }

    private Task<LoginSession?> LockSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        database.LoginSessions.FromSqlInterpolated(
            $"SELECT * FROM identity.\"LoginSessions\" WHERE \"Id\" = {sessionId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);

    private static bool IsUsable(PlatformUser? user, LoginSession? session, DateTimeOffset now) =>
        user is { EmailConfirmed: true } && session is not null && session.UserId == user.Id
        && session.RevokedAt is null && session.ExpiresAt > now
        && string.Equals(session.SecurityStamp, user.SecurityStamp, StringComparison.Ordinal);

    private async Task RevokeIfActiveAsync(LoginSession? session, string reason, CancellationToken cancellationToken)
    {
        if (session is null || session.RevokedAt is not null)
        {
            return;
        }
        DateTimeOffset now = clock.GetUtcNow();
        await database.LoginSessions.Where(item => item.Id == session.Id && item.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.RevokedAt, now)
                .SetProperty(item => item.RevocationReason, reason), cancellationToken);
    }
}
