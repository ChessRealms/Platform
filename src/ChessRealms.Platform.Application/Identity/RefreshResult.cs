namespace ChessRealms.Platform.Application.Identity;

public sealed record RefreshResult(string Code, string? AccessToken = null, string? TokenType = null,
    long? ExpiresIn = null, string? RefreshToken = null, DateTimeOffset? RefreshTokenExpiresAt = null);
