using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed record CreateGameResult(GameError Error, Game? Game = null, bool Created = false);
