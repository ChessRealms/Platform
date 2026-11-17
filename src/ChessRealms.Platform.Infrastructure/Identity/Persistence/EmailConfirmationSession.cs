namespace ChessRealms.Platform.Infrastructure.Identity.Persistence;

public sealed class EmailConfirmationSession
{
    public required string TokenHash { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
