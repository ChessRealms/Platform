namespace ChessRealms.Platform.Application.Identity;

public sealed record ConfirmationMetadata(string? SessionToken, DateTimeOffset ExpiresAt,
    string DeliveryStatus, DateTimeOffset? ResendAvailableAt, string? FailureCode);
