namespace ChessRealms.Platform.Application.Identity;

public sealed record LoginResult(string Code, string? AccessToken = null, string? TokenType = null,
    long? ExpiresIn = null, ConfirmationMetadata? EmailConfirmation = null,
    string? RefreshToken = null, DateTimeOffset? RefreshTokenExpiresAt = null);
