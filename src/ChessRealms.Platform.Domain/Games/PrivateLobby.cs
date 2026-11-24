namespace ChessRealms.Platform.Domain.Games;

public sealed class PrivateLobby
{
    private PrivateLobby() { }

    public PrivateLobby(Guid creatorId, Guid requestId, DateTimeOffset createdAt, GameTimeControl? timeControl = null)
    {
        Id = Guid.NewGuid();
        CreatorId = creatorId;
        RequestId = requestId;
        CreatedAt = createdAt;
        ExpiresAt = createdAt + Game.InvitationLifetime;
        TimeControl = timeControl;
    }

    public Guid Id { get; private set; }
    public Guid CreatorId { get; private set; }
    public Guid RequestId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public LobbyStatus Status { get; private set; }
    public long Revision { get; private set; }
    public Guid? JoinedPlayerId { get; private set; }
    public bool JoinedAsGuest { get; private set; }
    public Guid? GameId { get; private set; }
    public GameTimeControl? TimeControl { get; private set; }

    public LobbyStatus StatusAt(DateTimeOffset now) =>
        Status == LobbyStatus.Open && now >= ExpiresAt ? LobbyStatus.Expired : Status;

    public bool IsOwner(GameParticipant? player) => player is { IsGuest: false } account && account.Id == CreatorId;

    public bool IsJoiner(GameParticipant? player) =>
        player is { } joiner && joiner.Id == JoinedPlayerId && joiner.IsGuest == JoinedAsGuest;

    public GameError Join(GameParticipant player, string? guestName, long expectedRevision, DateTimeOffset now,
        GamePosition position, out Game? game)
    {
        game = null;
        if (!Game.CanPair(CreatorId, player.Id) || player.IsGuest != (guestName is not null))
        {
            return GameError.Forbidden;
        }
        if (Status == LobbyStatus.Joined)
        {
            return IsJoiner(player) ? GameError.None : GameError.LobbyAlreadyJoined;
        }
        GameError error = CheckOpen(expectedRevision, now);
        if (error != GameError.None)
        {
            return error;
        }

        game = Game.StartActive(CreatorId, player.Id, position, now, TimeControl, blackGuestName: guestName);
        JoinedPlayerId = player.Id;
        JoinedAsGuest = player.IsGuest;
        GameId = game.Id;
        Status = LobbyStatus.Joined;
        Revision++;
        return GameError.None;
    }

    public GameError Cancel(Guid playerId, long expectedRevision, DateTimeOffset now)
    {
        if (playerId != CreatorId)
        {
            return GameError.Forbidden;
        }
        GameError error = CheckOpen(expectedRevision, now);
        if (error != GameError.None)
        {
            return error;
        }

        Status = LobbyStatus.Cancelled;
        Revision++;
        return GameError.None;
    }

    private GameError CheckOpen(long expectedRevision, DateTimeOffset now)
    {
        if (StatusAt(now) == LobbyStatus.Expired)
        {
            return GameError.InvitationExpired;
        }
        if (Revision != expectedRevision)
        {
            return GameError.RevisionConflict;
        }
        return Status == LobbyStatus.Open ? GameError.None : GameError.LobbyNotOpen;
    }
}
