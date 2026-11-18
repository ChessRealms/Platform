namespace ChessRealms.Platform.Infrastructure.Identity.Persistence;

public sealed class RefreshToken
{
    public required string TokenHash { get; set; }
    public Guid SessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
