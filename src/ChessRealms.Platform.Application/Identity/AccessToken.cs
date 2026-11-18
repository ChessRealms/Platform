namespace ChessRealms.Platform.Application.Identity;

public sealed record AccessToken(string Value, long ExpiresIn);
