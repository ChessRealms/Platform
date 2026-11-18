namespace ChessRealms.Platform.Application.Identity;

public sealed record ConfirmationSessionResult(string Code, ConfirmationMetadata? EmailConfirmation);
