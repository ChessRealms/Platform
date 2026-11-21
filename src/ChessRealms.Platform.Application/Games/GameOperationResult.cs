using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed record GameOperationResult(GameError Error, Game? Game = null);
