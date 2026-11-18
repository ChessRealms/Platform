namespace ChessRealms.Platform.Application.Identity;

public sealed record RegistrationResult(
    Guid? UserId, 
    ConfirmationMetadata? EmailConfirmation,
    IReadOnlyList<RegistrationError> Errors);
