namespace ChessRealms.Platform.Domain.Games;

public sealed class Game
{
    private Game()
    {
    }

    public Game(Guid whitePlayerId, Guid blackPlayerId, GamePosition startingPosition)
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
        UpdatePosition(startingPosition);
    }

    public Guid Id { get; private set; }
    public Guid WhitePlayerId { get; private set; }
    public Guid BlackPlayerId { get; private set; }
    public GameStatus Status { get; private set; } = GameStatus.Pending;
    public long Revision { get; private set; }
    public string RulesRecord { get; private set; } = null!;
    public string Fen { get; private set; } = null!;
    public PlayerColor SideToMove { get; private set; }
    public PlayerColor? Winner { get; private set; }
    public GameEndReason? EndReason { get; private set; }

    public static bool CanPair(Guid whitePlayerId, Guid blackPlayerId) =>
        whitePlayerId != Guid.Empty && blackPlayerId != Guid.Empty && whitePlayerId != blackPlayerId;

    public bool HasPlayer(Guid playerId) => playerId == WhitePlayerId || playerId == BlackPlayerId;

    public GameError Accept(Guid playerId, long expectedRevision)
    {
        GameError error = CheckRevision(playerId, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }
        if (playerId != BlackPlayerId)
        {
            return GameError.Forbidden;
        }
        if (Status != GameStatus.Pending)
        {
            return GameError.GameNotPending;
        }

        Status = GameStatus.Active;
        Revision++;
        return GameError.None;
    }

    public GameError Play(Guid playerId, long expectedRevision, Func<string, GamePosition?> applyRules)
    {
        GameError error = CheckRevision(playerId, expectedRevision);
        if (error != GameError.None)
        {
            return error;
        }
        if (Status != GameStatus.Active)
        {
            return GameError.GameNotActive;
        }
        if (playerId != (SideToMove == PlayerColor.White ? WhitePlayerId : BlackPlayerId))
        {
            return GameError.NotYourTurn;
        }

        GamePosition? position = applyRules(RulesRecord);
        if (position is null)
        {
            return GameError.IllegalMove;
        }

        UpdatePosition(position);
        if (position.EndReason is not null)
        {
            Status = GameStatus.Finished;
        }
        Revision++;
        return GameError.None;
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
