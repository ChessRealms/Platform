namespace ChessRealms.Platform.Infrastructure.Identity.Persistence;

public sealed class PasswordResetEmailDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string SecurityStamp { get; init; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "Queued";
}
