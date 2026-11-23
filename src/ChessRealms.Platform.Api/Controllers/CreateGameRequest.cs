namespace ChessRealms.Platform.Api.Controllers;

public sealed record CreateGameRequest(Guid OpponentId, Guid? RequestId = null);
