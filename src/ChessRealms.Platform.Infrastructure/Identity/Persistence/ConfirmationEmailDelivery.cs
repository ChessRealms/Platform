namespace ChessRealms.Platform.Infrastructure.Identity.Persistence;

public sealed class ConfirmationEmailDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "Queued";
    public string? FailureCode { get; set; }
}
