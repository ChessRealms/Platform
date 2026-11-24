namespace ChessRealms.Platform.Infrastructure.Games;

internal sealed class GuestPlayer(Guid id, string displayName, string sessionHash, DateTimeOffset expiresAt)
{
    public Guid Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public string? SessionHash { get; private set; } = sessionHash;
    public DateTimeOffset ExpiresAt { get; private set; } = expiresAt;
}
