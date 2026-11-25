namespace ChessRealms.Platform.Domain.Games;

public readonly record struct GameParticipant(Guid Id, GameParticipantKind Kind)
{
    public bool IsGuest => Kind == GameParticipantKind.Guest;

    public static GameParticipant Account(Guid id) => new(id, GameParticipantKind.Account);
    public static GameParticipant Guest(Guid id) => new(id, GameParticipantKind.Guest);
}
