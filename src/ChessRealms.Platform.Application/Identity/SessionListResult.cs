namespace ChessRealms.Platform.Application.Identity;

public sealed record SessionListResult(SessionOperationResult Result, IReadOnlyList<LoginSessionInfo> Sessions);
