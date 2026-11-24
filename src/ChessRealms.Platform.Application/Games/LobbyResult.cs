using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed record LobbyResult(GameError Error, PrivateLobby? Lobby = null, bool Created = false);
