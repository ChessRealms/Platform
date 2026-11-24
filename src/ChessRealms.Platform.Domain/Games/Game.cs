namespace ChessRealms.Platform.Domain.Games;

public sealed class Game
{
    private Game()
    {
    }

    public Game(Guid whitePlayerId, Guid blackPlayerId, GamePosition startingPosition, DateTimeOffset createdAt)
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
        UpdatePosition(startingPosition);
    }

    public Guid Id { get; private set; }
    public Guid WhitePlayerId { get; private set; }
    public Guid BlackPlayerId { get; private set; }
    public DateTimeOffset? CreatedAt { get; private set; }
    public GameStatus Status { get; private set; } = GameStatus.Pending;
    public long Revision { get; private set; }
    public string RulesRecord { get; private set; } = null!;
    public string Fen { get; private set; } = null!;
    public PlayerColor SideToMove { get; private set; }
    public PlayerColor? Winner { get; private set; }
    public GameEndReason? EndReason { get; private set; }
    public PlayerColor? DrawOfferedBy { get; private set; }

    public static bool CanPair(Guid whitePlayerId, Guid blackPlayerId) =>
        whitePlayerId != Guid.Empty && blackPlayerId != Guid.Empty && whitePlayerId != blackPlayerId;

    public bool HasPlayer(Guid playerId) => playerId == WhitePlayerId || playerId == BlackPlayerId;

    public GameError Accept(Guid playerId, long expectedRevision) =>
        ChangeInvitation(playerId, expectedRevision, BlackPlayerId, GameStatus.Active);

    public GameError Decline(Guid playerId, long expectedRevision) =>
        ChangeInvitation(playerId, expectedRevision, BlackPlayerId, GameStatus.Declined);

    public GameError Cancel(Guid playerId, long expectedRevision) =>
        ChangeInvitation(playerId, expectedRevision, WhitePlayerId, GameStatus.Cancelled);

    public GameError Play(Guid playerId, long expectedRevision, Func<string, GamePosition?> applyRules)
    {
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
        ApplyPosition(position);
        return GameError.None;
    }

    public GameError Resign(Guid playerId, long expectedRevision, Func<string, PlayerColor, bool> hasInsufficientMatingMaterial)
    {
        GameError error = CheckActive(playerId, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }

        PlayerColor opponent = playerId == WhitePlayerId ? PlayerColor.Black : PlayerColor.White;
        bool drawn = hasInsufficientMatingMaterial(Fen, opponent);
        Finish(drawn ? GameEndReason.ResignationNoPossibleMate : GameEndReason.Resignation, drawn ? null : opponent);
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

        DrawOfferedBy = playerId == WhitePlayerId ? PlayerColor.White : PlayerColor.Black;
        Revision++;
        return GameError.None;
    }

    public GameError ReplyToDrawOffer(Guid playerId, long expectedRevision, bool accept)
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
        if (offerer == (playerId == WhitePlayerId ? PlayerColor.White : PlayerColor.Black))
        {
            return GameError.Forbidden;
        }

        if (accept)
        {
            Finish(GameEndReason.DrawAgreement, null);
        }
        else
        {
            DrawOfferedBy = null;
        }
        Revision++;
        return GameError.None;
    }

    public GameError ClaimDraw(Guid playerId, long expectedRevision, Func<string, GamePosition?> applyClaim)
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

        ApplyPosition(position);
        return GameError.None;
    }

    private GameError ChangeInvitation(Guid playerId, long expectedRevision, Guid allowedPlayerId, GameStatus nextStatus)
    {
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
        Revision++;
        return GameError.None;
    }

    private void ApplyPosition(GamePosition position)
    {
        UpdatePosition(position);
        if (position.EndReason is not null)
        {
            Status = GameStatus.Finished;
            DrawOfferedBy = null;
        }
        Revision++;
    }

    private void Finish(GameEndReason reason, PlayerColor? winner)
    {
        Status = GameStatus.Finished;
        EndReason = reason;
        Winner = winner;
        DrawOfferedBy = null;
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
