namespace ChessRealms.Platform.Application.Identity;

public sealed record PasswordResetResult(string Code, IReadOnlyList<RegistrationError> Errors);
