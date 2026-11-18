namespace ChessRealms.Platform.Application.Identity;

public sealed record RegisterRequest(string Email, string Username, string Password);
