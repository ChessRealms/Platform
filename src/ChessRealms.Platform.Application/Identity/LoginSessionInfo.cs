namespace ChessRealms.Platform.Application.Identity;

public sealed record LoginSessionInfo(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool IsCurrent);
