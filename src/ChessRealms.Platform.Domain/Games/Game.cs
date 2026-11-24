namespace ChessRealms.Platform.Domain.Games;

public sealed class Game
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RematchWindow = TimeSpan.FromMinutes(5);

    private Game()
    {
    }

    public Game(Guid whitePlayerId, Guid blackPlayerId, GamePosition startingPosition, DateTimeOffset createdAt,
        GameTimeControl? timeControl = null)
    {
        if (!CanPair(whitePlayerId, blackPlayerId))
        {
            throw new ArgumentException("A game requires two distinct player IDs.");
        }
        if (startingPosition.EndReason is not null)
        {
            throw new ArgumentException("A game must start from a playable position.", nameof(startingPosition));
        }
        Id = Guid.NewGuid();
        WhitePlayerId = whitePlayerId;
        BlackPlayerId = blackPlayerId;
        CreatedAt = createdAt;
        InvitationExpiresAt = createdAt + InvitationLifetime;
        TimeControl = timeControl;
        WhiteTimeRemaining = BlackTimeRemaining = InitialTime(timeControl);
        UpdatePosition(startingPosition);
    }

    public Guid Id { get; private set; }
    public Guid WhitePlayerId { get; private set; }
    public Guid BlackPlayerId { get; private set; }
    public DateTimeOffset? CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? InvitationExpiresAt { get; private set; }
    public GameStatus Status { get; private set; } = GameStatus.Pending;
    public long Revision { get; private set; }
    public string RulesRecord { get; private set; } = null!;
    public string Fen { get; private set; } = null!;
    public PlayerColor SideToMove { get; private set; }
    public PlayerColor? Winner { get; private set; }
    public GameEndReason? EndReason { get; private set; }
    public PlayerColor? DrawOfferedBy { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public PlayerColor? RematchOfferedBy { get; private set; }
    public Guid? RematchGameId { get; private set; }
    public GameTimeControl? TimeControl { get; private set; }
    public TimeSpan? WhiteTimeRemaining { get; private set; }
    public TimeSpan? BlackTimeRemaining { get; private set; }
    public DateTimeOffset? TurnDeadline { get; private set; }

    public static bool CanPair(Guid whitePlayerId, Guid blackPlayerId) =>
        whitePlayerId != Guid.Empty && blackPlayerId != Guid.Empty && whitePlayerId != blackPlayerId;

    public static TimeSpan? InitialTime(GameTimeControl? timeControl) => timeControl switch
    {
        GameTimeControl.Blitz5 => TimeSpan.FromMinutes(5),
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(timeControl))
    };

    internal static Game StartActive(Guid whitePlayerId, Guid blackPlayerId, GamePosition position, DateTimeOffset now,
        GameTimeControl? timeControl)
    {
        Game game = new(whitePlayerId, blackPlayerId, position, now, timeControl)
        {
            InvitationExpiresAt = null
        };
        game.Start(now);
        return game;
    }

    public bool HasPlayer(Guid playerId) => playerId == WhitePlayerId || playerId == BlackPlayerId;

    public bool IsInvitationExpired(DateTimeOffset now) => Status == GameStatus.Pending && InvitationExpiresAt <= now;

    public GameError Accept(Guid playerId, long expectedRevision, DateTimeOffset now) =>
        ChangeInvitation(playerId, expectedRevision, now, BlackPlayerId, GameStatus.Active);

    public GameError Decline(Guid playerId, long expectedRevision, DateTimeOffset now) =>
        ChangeInvitation(playerId, expectedRevision, now, BlackPlayerId, GameStatus.Declined);

    public GameError Cancel(Guid playerId, long expectedRevision, DateTimeOffset now) =>
        ChangeInvitation(playerId, expectedRevision, now, WhitePlayerId, GameStatus.Cancelled);

    public GameError OfferRematch(Guid playerId, long expectedRevision, DateTimeOffset now)
    {
        GameError error = CheckRematchWindow(playerId, now);
        if (error != GameError.None)
        {
            return error;
        }

        PlayerColor color = ColorOf(playerId);
        if (RematchOfferedBy is { } offerer)
        {
            return offerer == color ? GameError.None : GameError.RematchOfferPending;
        }
        if (expectedRevision != Revision)
        {
            return GameError.RevisionConflict;
        }

        RematchOfferedBy = color;
        Revision++;
        return GameError.None;
    }

    public (GameError Error, Game? Rematch) AcceptRematch(Guid playerId, long expectedRevision, DateTimeOffset now,
        Func<GamePosition> startingPosition)
    {
        if (!HasPlayer(playerId))
        {
            return (GameError.NotFound, null);
        }
        if (RematchOfferedBy == ColorOf(playerId))
        {
            return (GameError.Forbidden, null);
        }
        if (RematchGameId is not null)
        {
            return (GameError.None, null);
        }
        GameError error = CheckRematchWindow(playerId, now);
        if (error != GameError.None)
        {
            return (error, null);
        }
        if (expectedRevision != Revision)
        {
            return (GameError.RevisionConflict, null);
        }
        if (RematchOfferedBy is null)
        {
            return (GameError.NoRematchOffer, null);
        }

        Game rematch = StartActive(BlackPlayerId, WhitePlayerId, startingPosition(), now, TimeControl);
        RematchGameId = rematch.Id;
        Revision++;
        return (GameError.None, rematch);
    }

    public GameError Play(Guid playerId, long expectedRevision, DateTimeOffset now, Func<string, GamePosition?> applyRules,
        out GameMoveTiming? timing)
    {
        timing = null;
        GameError error = CheckTurn(playerId, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }

        GamePosition? position = applyRules(RulesRecord);
        if (position is null)
        {
            return GameError.IllegalMove;
        }

        if (DrawOfferedBy is { } offerer && offerer != SideToMove)
        {
            DrawOfferedBy = null;
        }
        StopTurnClock(now);
        ApplyPosition(position, now);
        if (Status == GameStatus.Active)
        {
            StartTurnClock(now);
        }
        if (StartedAt is not null)
        {
            timing = new(Id, position.PlyCount, now, WhiteTimeRemaining, BlackTimeRemaining);
        }
        return GameError.None;
    }

    public bool TryFinishOnTime(DateTimeOffset now, Func<string, PlayerColor, bool> hasInsufficientMatingMaterial)
    {
        if (TurnDeadline is not { } deadline || now < deadline)
        {
            return false;
        }

        PlayerColor opponent = SideToMove == PlayerColor.White ? PlayerColor.Black : PlayerColor.White;
        bool drawn = hasInsufficientMatingMaterial(Fen, opponent);
        Finish(drawn ? GameEndReason.TimeoutNoPossibleMate : GameEndReason.Timeout, drawn ? null : opponent, deadline);
        Revision++;
        return true;
    }

    public GameError Resign(Guid playerId, long expectedRevision, DateTimeOffset now,
        Func<string, PlayerColor, bool> hasInsufficientMatingMaterial)
    {
        GameError error = CheckActive(playerId, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }

        PlayerColor opponent = playerId == WhitePlayerId ? PlayerColor.Black : PlayerColor.White;
        bool drawn = hasInsufficientMatingMaterial(Fen, opponent);
        Finish(drawn ? GameEndReason.ResignationNoPossibleMate : GameEndReason.Resignation, drawn ? null : opponent, now);
        Revision++;
        return GameError.None;
    }

    public GameError OfferDraw(Guid playerId, long expectedRevision)
    {
        GameError error = CheckActive(playerId, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }
        if (DrawOfferedBy is not null)
        {
            return GameError.DrawOfferPending;
        }

        DrawOfferedBy = ColorOf(playerId);
        Revision++;
        return GameError.None;
    }

    public GameError ReplyToDrawOffer(Guid playerId, long expectedRevision, bool accept, DateTimeOffset now)
    {
        GameError error = CheckActive(playerId, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }
        if (DrawOfferedBy is not { } offerer)
        {
            return GameError.NoDrawOffer;
        }
        if (offerer == ColorOf(playerId))
        {
            return GameError.Forbidden;
        }

        if (accept)
        {
            Finish(GameEndReason.DrawAgreement, null, now);
        }
        else
        {
            DrawOfferedBy = null;
        }
        Revision++;
        return GameError.None;
    }

    public GameError ClaimDraw(Guid playerId, long expectedRevision, DateTimeOffset now, Func<string, GamePosition?> applyClaim)
    {
        GameError error = CheckTurn(playerId, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }

        GamePosition? position = applyClaim(RulesRecord);
        if (position is null)
        {
            return GameError.DrawClaimUnavailable;
        }

        StopTurnClock(now);
        ApplyPosition(position, now);
        return GameError.None;
    }

    private GameError ChangeInvitation(Guid playerId, long expectedRevision, DateTimeOffset now,
        Guid allowedPlayerId, GameStatus nextStatus)
    {
        if (playerId == allowedPlayerId && IsInvitationExpired(now))
        {
            return GameError.InvitationExpired;
        }
        GameError error = CheckRevision(playerId, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }
        if (playerId != allowedPlayerId)
        {
            return GameError.Forbidden;
        }
        if (Status != GameStatus.Pending)
        {
            return GameError.GameNotPending;
        }

        Status = nextStatus;
        if (Status == GameStatus.Active)
        {
            Start(now);
        }
        Revision++;
        return GameError.None;
    }

    private GameError CheckRematchWindow(Guid playerId, DateTimeOffset now)
    {
        if (!HasPlayer(playerId))
        {
            return GameError.NotFound;
        }
        if (Status != GameStatus.Finished || FinishedAt is not { } finishedAt || RematchGameId is not null)
        {
            return GameError.RematchUnavailable;
        }
        return now >= finishedAt + RematchWindow ? GameError.RematchExpired : GameError.None;
    }

    private PlayerColor ColorOf(Guid playerId) => playerId == WhitePlayerId ? PlayerColor.White : PlayerColor.Black;

    private void ApplyPosition(GamePosition position, DateTimeOffset now)
    {
        UpdatePosition(position);
        if (position.EndReason is { } reason)
        {
            Finish(reason, position.Winner, now);
        }
        Revision++;
    }

    private void Finish(GameEndReason reason, PlayerColor? winner, DateTimeOffset now)
    {
        StopTurnClock(now);
        Status = GameStatus.Finished;
        FinishedAt = now;
        EndReason = reason;
        Winner = winner;
        DrawOfferedBy = null;
    }

    private void StartTurnClock(DateTimeOffset now) =>
        TurnDeadline = now + (SideToMove == PlayerColor.White ? WhiteTimeRemaining : BlackTimeRemaining);

    private void Start(DateTimeOffset now)
    {
        Status = GameStatus.Active;
        StartedAt = now;
        StartTurnClock(now);
    }

    private void StopTurnClock(DateTimeOffset now)
    {
        if (TurnDeadline is not { } deadline)
        {
            return;
        }

        TimeSpan remaining = deadline > now ? deadline - now : TimeSpan.Zero;
        if (SideToMove == PlayerColor.White)
        {
            WhiteTimeRemaining = remaining < WhiteTimeRemaining!.Value ? remaining : WhiteTimeRemaining;
        }
        else
        {
            BlackTimeRemaining = remaining < BlackTimeRemaining!.Value ? remaining : BlackTimeRemaining;
        }
        TurnDeadline = null;
    }

    private GameError CheckActive(Guid playerId, long expectedRevision)
    {
        GameError error = CheckRevision(playerId, expectedRevision);
        return error == GameError.None && Status != GameStatus.Active ? GameError.GameNotActive : error;
    }

    private GameError CheckTurn(Guid playerId, long expectedRevision)
    {
        GameError error = CheckActive(playerId, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }

        return playerId == (SideToMove == PlayerColor.White ? WhitePlayerId : BlackPlayerId)
            ? GameError.None
            : GameError.NotYourTurn;
    }

    private GameError CheckRevision(Guid playerId, long expectedRevision)
    {
        if (!HasPlayer(playerId))
        {
            return GameError.NotFound;
        }
        return expectedRevision == Revision ? GameError.None : GameError.RevisionConflict;
    }

    private void UpdatePosition(GamePosition position)
    {
        RulesRecord = position.RulesRecord;
        Fen = position.Fen;
        SideToMove = position.SideToMove;
        Winner = position.Winner;
        EndReason = position.EndReason;
    }
}
