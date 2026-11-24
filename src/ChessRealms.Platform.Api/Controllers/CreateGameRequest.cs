namespace ChessRealms.Platform.Api.Controllers;

public sealed record CreateGameRequest(Guid? OpponentId = null, Guid? RequestId = null, string? OpponentUsername = null);
