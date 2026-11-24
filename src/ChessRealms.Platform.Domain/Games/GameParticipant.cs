namespace ChessRealms.Platform.Domain.Games;

public readonly record struct GameParticipant(Guid Id, bool IsGuest);
