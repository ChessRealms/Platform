namespace ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;

public sealed record ConfirmationEmail(Guid DeliveryId, string Recipient, string ConfirmationLink);
