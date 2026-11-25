namespace ChessRealms.Platform.Domain.Games;

public sealed class QuickPlayTicket
{
    private static readonly TimeSpan Presence = TimeSpan.FromSeconds(45);

    private QuickPlayTicket() { }

    public QuickPlayTicket(Guid playerId, Guid id, DateTimeOffset now)
    {
        PlayerId = playerId;
        Id = id;
        EnqueuedAt = now;
        ExpiresAt = now + Presence;
    }

    public Guid PlayerId { get; private set; }
    public Guid Id { get; private set; }
    public long Sequence { get; private set; }
    public DateTimeOffset EnqueuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public QuickPlayStatus Status { get; private set; }
    public Guid? GameId { get; private set; }

    public QuickPlayStatus StatusAt(DateTimeOffset now) =>
        Status == QuickPlayStatus.Waiting && ExpiresAt <= now ? QuickPlayStatus.Expired : Status;

    public void Renew(DateTimeOffset now)
    {
        Status = StatusAt(now);
        if (Status == QuickPlayStatus.Waiting && now + Presence > ExpiresAt)
        {
            ExpiresAt = now + Presence;
        }
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = StatusAt(now);
        if (Status == QuickPlayStatus.Waiting)
        {
            Status = QuickPlayStatus.Cancelled;
        }
    }

    public Game MatchWith(QuickPlayTicket opponent, bool playsWhite, GamePosition position, DateTimeOffset now)
    {
        if (StatusAt(now) != QuickPlayStatus.Waiting || opponent.StatusAt(now) != QuickPlayStatus.Waiting)
        {
            throw new InvalidOperationException("Both players must still be waiting.");
        }

        Guid white = playsWhite ? PlayerId : opponent.PlayerId;
        Guid black = playsWhite ? opponent.PlayerId : PlayerId;
        Game game = Game.StartActive(GameParticipant.Account(white), GameParticipant.Account(black), position, now,
            GameCreationOrigin.QuickPlay, GameTimeControl.Blitz5);
        Status = opponent.Status = QuickPlayStatus.Matched;
        GameId = opponent.GameId = game.Id;
        return game;
    }
}
