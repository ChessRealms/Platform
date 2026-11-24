namespace ChessRealms.Platform.Application.Games;

public sealed record GuestSession(Guid PlayerId, string DisplayName, DateTimeOffset ExpiresAt, bool RenewCookie = false);

public interface IGuestSessions
{
    Task<(GuestSession Session, string Token)> CreateAsync(string displayName, CancellationToken cancellationToken);
    Task<GuestSession?> FindAsync(string token, CancellationToken cancellationToken);
    Task RevokeAsync(string token, CancellationToken cancellationToken);
}
