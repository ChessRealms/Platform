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
        CreationOrigin = GameCreationOrigin.DirectInvitation;
        CreatedAt = createdAt;
        InvitationExpiresAt = createdAt + InvitationLifetime;
        TimeControl = timeControl;
        WhiteTimeRemaining = BlackTimeRemaining = InitialTime(timeControl);
        UpdatePosition(startingPosition);
    }

    public Guid Id { get; private set; }
    public Guid WhitePlayerId { get; private set; }
    public Guid BlackPlayerId { get; private set; }
    public GameParticipantKind WhitePlayerKind { get; private set; } = GameParticipantKind.Account;
    public GameParticipantKind BlackPlayerKind { get; private set; } = GameParticipantKind.Account;
    public GameParticipant WhitePlayer => new(WhitePlayerId, WhitePlayerKind);
    public GameParticipant BlackPlayer => new(BlackPlayerId, BlackPlayerKind);
    public string? WhiteGuestName { get; private set; }
    public string? BlackGuestName { get; private set; }
    public GameCreationOrigin CreationOrigin { get; private set; }
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

    internal static Game StartActive(GameParticipant white, GameParticipant black, GamePosition position, DateTimeOffset now,
        GameCreationOrigin creationOrigin, GameTimeControl? timeControl, string? whiteGuestName = null, string? blackGuestName = null)
    {
        Game game = new(white.Id, black.Id, position, now, timeControl)
        {
            CreationOrigin = creationOrigin,
            InvitationExpiresAt = null,
            WhitePlayerKind = white.Kind,
            BlackPlayerKind = black.Kind,
            WhiteGuestName = whiteGuestName,
            BlackGuestName = blackGuestName
        };
        game.Start(now);
        return game;
    }

    public bool HasPlayer(GameParticipant player) => player == WhitePlayer || player == BlackPlayer;

    public bool IsInvitationExpired(DateTimeOffset now) => Status == GameStatus.Pending && InvitationExpiresAt <= now;

    public GameError Accept(GameParticipant player, long expectedRevision, DateTimeOffset now) =>
        ChangeInvitation(player, expectedRevision, now, BlackPlayer, GameStatus.Active);

    public GameError Decline(GameParticipant player, long expectedRevision, DateTimeOffset now) =>
        ChangeInvitation(player, expectedRevision, now, BlackPlayer, GameStatus.Declined);

    public GameError Cancel(GameParticipant player, long expectedRevision, DateTimeOffset now) =>
        ChangeInvitation(player, expectedRevision, now, WhitePlayer, GameStatus.Cancelled);

    public GameError OfferRematch(GameParticipant player, long expectedRevision, DateTimeOffset now)
    {
        GameError error = CheckRematchWindow(player, now);
        if (error != GameError.None)
        {
            return error;
        }

        PlayerColor color = ColorOf(player);
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

    public (GameError Error, Game? Rematch) AcceptRematch(GameParticipant player, long expectedRevision, DateTimeOffset now,
        Func<GamePosition> startingPosition)
    {
        if (!HasPlayer(player))
        {
            return (GameError.NotFound, null);
        }
        if (RematchOfferedBy == ColorOf(player))
        {
            return (GameError.Forbidden, null);
        }
        if (RematchGameId is not null)
        {
            return (GameError.None, null);
        }
        GameError error = CheckRematchWindow(player, now);
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

        Game rematch = StartActive(BlackPlayer, WhitePlayer, startingPosition(), now, GameCreationOrigin.Rematch,
            TimeControl, BlackGuestName, WhiteGuestName);
        RematchGameId = rematch.Id;
        Revision++;
        return (GameError.None, rematch);
    }

    public GameError Play(GameParticipant player, long expectedRevision, DateTimeOffset now, Func<string, GamePosition?> applyRules,
        out GameMoveTiming? timing)
    {
        timing = null;
        GameError error = CheckTurn(player, expectedRevision);
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

    public GameError Resign(GameParticipant player, long expectedRevision, DateTimeOffset now,
        Func<string, PlayerColor, bool> hasInsufficientMatingMaterial)
    {
        GameError error = CheckActive(player, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }

        PlayerColor opponent = player == WhitePlayer ? PlayerColor.Black : PlayerColor.White;
        bool drawn = hasInsufficientMatingMaterial(Fen, opponent);
        Finish(drawn ? GameEndReason.ResignationNoPossibleMate : GameEndReason.Resignation, drawn ? null : opponent, now);
        Revision++;
        return GameError.None;
    }

    public GameError OfferDraw(GameParticipant player, long expectedRevision)
    {
        GameError error = CheckActive(player, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }
        if (DrawOfferedBy is not null)
        {
            return GameError.DrawOfferPending;
        }

        DrawOfferedBy = ColorOf(player);
        Revision++;
        return GameError.None;
    }

    public GameError ReplyToDrawOffer(GameParticipant player, long expectedRevision, bool accept, DateTimeOffset now)
    {
        GameError error = CheckActive(player, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }
        if (DrawOfferedBy is not { } offerer)
        {
            return GameError.NoDrawOffer;
        }
        if (offerer == ColorOf(player))
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

    public GameError ClaimDraw(GameParticipant player, long expectedRevision, DateTimeOffset now, Func<string, GamePosition?> applyClaim)
    {
        GameError error = CheckTurn(player, expectedRevision);
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

    private GameError ChangeInvitation(GameParticipant player, long expectedRevision, DateTimeOffset now,
        GameParticipant allowedPlayer, GameStatus nextStatus)
    {
        if (player == allowedPlayer && IsInvitationExpired(now))
        {
            return GameError.InvitationExpired;
        }
        GameError error = CheckRevision(player, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }
        if (player != allowedPlayer)
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

    private GameError CheckRematchWindow(GameParticipant player, DateTimeOffset now)
    {
        if (!HasPlayer(player))
        {
            return GameError.NotFound;
        }
        if (Status != GameStatus.Finished || FinishedAt is not { } finishedAt || RematchGameId is not null)
        {
            return GameError.RematchUnavailable;
        }
        return now >= finishedAt + RematchWindow ? GameError.RematchExpired : GameError.None;
    }

    private PlayerColor ColorOf(GameParticipant player) => player == WhitePlayer ? PlayerColor.White : PlayerColor.Black;

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

    private GameError CheckActive(GameParticipant player, long expectedRevision)
    {
        GameError error = CheckRevision(player, expectedRevision);
        return error == GameError.None && Status != GameStatus.Active ? GameError.GameNotActive : error;
    }

    private GameError CheckTurn(GameParticipant player, long expectedRevision)
    {
        GameError error = CheckActive(player, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }

        return player == (SideToMove == PlayerColor.White ? WhitePlayer : BlackPlayer)
            ? GameError.None
            : GameError.NotYourTurn;
    }

    private GameError CheckRevision(GameParticipant player, long expectedRevision)
    {
        if (!HasPlayer(player))
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
