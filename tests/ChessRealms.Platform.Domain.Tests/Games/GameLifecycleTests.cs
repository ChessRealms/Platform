using ChessRealms.Platform.Domain.Games;

using Xunit;

namespace ChessRealms.Platform.Domain.Tests.Games;

public sealed class GameLifecycleTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly GamePosition InitialPosition = new("initial record",
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", PlayerColor.White);

    [Fact]
    public void InvitationExpiresExactlyAtTheDeadline()
    {
        Game game = NewGame();
        DateTimeOffset deadline = Assert.IsType<DateTimeOffset>(game.InvitationExpiresAt);
        Assert.False(game.IsInvitationExpired(deadline.AddTicks(-1)));
        Assert.True(game.IsInvitationExpired(deadline));

        Assert.Equal(GameError.InvitationExpired, game.Accept(game.BlackPlayer, 0, deadline));
        Assert.Equal(GameError.InvitationExpired, game.Decline(game.BlackPlayer, 0, deadline));
        Assert.Equal(GameError.InvitationExpired, game.Cancel(game.WhitePlayer, 0, deadline));
        Assert.Equal(GameStatus.Pending, game.Status);
        Assert.Equal(0, game.Revision);
        Assert.Null(game.StartedAt);
        Assert.Null(game.TurnDeadline);
    }

    [Theory]
    [InlineData(true, -1, GameError.RevisionConflict)]
    [InlineData(false, 0, GameError.NotYourTurn)]
    [InlineData(true, 0, GameError.IllegalMove)]
    public void RejectedMovePreservesPositionRevisionClockAndDrawOffer(bool asWhite, int revisionOffset, GameError expectedError)
    {
        Game game = StartGame(GameTimeControl.Blitz5);
        Assert.Equal(GameError.None, game.OfferDraw(game.BlackPlayer, game.Revision));
        long revision = game.Revision;
        DateTimeOffset? deadline = game.TurnDeadline;
        GameParticipant actor = asWhite ? game.WhitePlayer : game.BlackPlayer;
        bool appliedRules = false;

        GameError error = game.Play(actor, revision + revisionOffset, Start.AddSeconds(10), record =>
        {
            Assert.Equal(InitialPosition.RulesRecord, record);
            appliedRules = true;
            return null;
        }, out GameMoveTiming? timing);

        Assert.Equal(expectedError, error);
        Assert.Equal(expectedError == GameError.IllegalMove, appliedRules);
        Assert.Null(timing);
        Assert.Equal(revision, game.Revision);
        Assert.Equal(GameStatus.Active, game.Status);
        Assert.Equal(InitialPosition.Fen, game.Fen);
        Assert.Equal(InitialPosition.RulesRecord, game.RulesRecord);
        Assert.Equal(PlayerColor.White, game.SideToMove);
        Assert.Equal(PlayerColor.Black, game.DrawOfferedBy);
        Assert.Equal(deadline, game.TurnDeadline);
        Assert.Equal(TimeSpan.FromMinutes(5), game.WhiteTimeRemaining);
        Assert.Equal(TimeSpan.FromMinutes(5), game.BlackTimeRemaining);
    }

    [Fact]
    public void MatchingIdWithADifferentKindCannotPlay()
    {
        Game game = StartGame(null);
        GameParticipant wrongKind = GameParticipant.Guest(game.WhitePlayerId);

        Assert.False(game.HasPlayer(wrongKind));
        Assert.Equal(GameError.NotFound, game.Play(wrongKind, game.Revision, Start,
            _ => throw new InvalidOperationException("The participant cannot act as White."), out GameMoveTiming? timing));
        Assert.Null(timing);
        Assert.Equal(1, game.Revision);
    }

    [Theory]
    [InlineData(0, false, GameEndReason.Timeout, PlayerColor.Black)]
    [InlineData(0, true, GameEndReason.TimeoutNoPossibleMate, null)]
    [InlineData(10, false, GameEndReason.Timeout, PlayerColor.Black)]
    public void TimeoutRecordsTheDeadlineAndOnlyFinishesOnce(int secondsLate, bool insufficientMaterial,
        GameEndReason reason, PlayerColor? winner)
    {
        Game game = StartGame(GameTimeControl.Blitz5);
        DateTimeOffset deadline = Start.AddMinutes(5);

        Assert.Equal(deadline, game.TurnDeadline);
        Assert.False(game.TryFinishOnTime(deadline.AddTicks(-1),
            (_, _) => throw new InvalidOperationException("Mating material is irrelevant before expiry.")));
        Assert.Equal(GameStatus.Active, game.Status);
        Assert.Equal(1, game.Revision);

        Assert.True(game.TryFinishOnTime(deadline.AddSeconds(secondsLate), (fen, opponent) =>
        {
            Assert.Equal(InitialPosition.Fen, fen);
            Assert.Equal(PlayerColor.Black, opponent);
            return insufficientMaterial;
        }));
        Assert.Equal(GameStatus.Finished, game.Status);
        Assert.Equal(reason, game.EndReason);
        Assert.Equal(winner, game.Winner);
        Assert.Equal(deadline, game.FinishedAt);
        Assert.Equal(TimeSpan.Zero, game.WhiteTimeRemaining);
        Assert.Equal(TimeSpan.FromMinutes(5), game.BlackTimeRemaining);
        Assert.Null(game.TurnDeadline);

        Assert.False(game.TryFinishOnTime(deadline.AddMinutes(1),
            (_, _) => throw new InvalidOperationException("The game has already finished.")));
        Assert.Equal(2, game.Revision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(GameTimeControl.Blitz5)]
    public void RematchSwapsColorsAndStartsWithFreshClocks(GameTimeControl? timeControl)
    {
        Game original = FinishGame(timeControl);
        DateTimeOffset finish = Assert.IsType<DateTimeOffset>(original.FinishedAt);
        DateTimeOffset acceptedAt = (finish + Game.RematchWindow).AddTicks(-1);
        Assert.Equal(GameError.None, original.OfferRematch(original.WhitePlayer, original.Revision, finish));
        long offeredRevision = original.Revision;

        (GameError error, Game? created) = original.AcceptRematch(original.BlackPlayer, offeredRevision, acceptedAt,
            () => InitialPosition);

        Assert.Equal(GameError.None, error);
        Game rematch = Assert.IsType<Game>(created);
        Assert.NotEqual(original.Id, rematch.Id);
        Assert.Equal(rematch.Id, original.RematchGameId);
        Assert.Equal(offeredRevision + 1, original.Revision);
        Assert.Equal(GameStatus.Finished, original.Status);
        Assert.Equal(finish, original.FinishedAt);
        Assert.Equal(original.WhitePlayerId, rematch.BlackPlayerId);
        Assert.Equal(original.BlackPlayerId, rematch.WhitePlayerId);
        Assert.Equal(GameStatus.Active, rematch.Status);
        Assert.Equal(0, rematch.Revision);
        Assert.Equal(PlayerColor.White, rematch.SideToMove);
        Assert.Equal(acceptedAt, rematch.StartedAt);
        Assert.Equal(timeControl, rematch.TimeControl);
        TimeSpan? balance = timeControl is null ? null : TimeSpan.FromMinutes(5);
        Assert.Equal(balance, rematch.WhiteTimeRemaining);
        Assert.Equal(balance, rematch.BlackTimeRemaining);
        Assert.Equal(acceptedAt + balance, rematch.TurnDeadline);

        (error, created) = original.AcceptRematch(original.BlackPlayer, offeredRevision, finish + Game.RematchWindow,
            () => throw new InvalidOperationException("A retry must reuse the existing rematch."));
        Assert.Equal(GameError.None, error);
        Assert.Null(created);
        Assert.Equal(rematch.Id, original.RematchGameId);
        Assert.Equal(offeredRevision + 1, original.Revision);
    }

    [Fact]
    public void RematchOfferAndAcceptanceExpireExactlyAtTheDeadline()
    {
        Game unoffered = FinishGame(null);
        DateTimeOffset finish = Assert.IsType<DateTimeOffset>(unoffered.FinishedAt);
        DateTimeOffset deadline = finish + Game.RematchWindow;
        Assert.Equal(GameError.RematchExpired, unoffered.OfferRematch(unoffered.WhitePlayer, 2, deadline));
        Assert.Null(unoffered.RematchOfferedBy);
        Assert.Equal(2, unoffered.Revision);

        Game offered = FinishGame(null);
        Assert.Equal(GameError.None, offered.OfferRematch(offered.WhitePlayer, 2, deadline.AddTicks(-1)));
        (GameError error, Game? rematch) = offered.AcceptRematch(offered.BlackPlayer, 3, deadline,
            () => throw new InvalidOperationException("The rematch window has closed."));
        Assert.Equal(GameError.RematchExpired, error);
        Assert.Null(rematch);
        Assert.Null(offered.RematchGameId);
        Assert.Equal(3, offered.Revision);
    }

    private static Game NewGame(GameTimeControl? timeControl = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), InitialPosition, Start.AddMinutes(-1), timeControl);

    private static Game StartGame(GameTimeControl? timeControl)
    {
        Game game = NewGame(timeControl);
        Assert.Equal(GameError.None, game.Accept(game.BlackPlayer, 0, Start));
        return game;
    }

    private static Game FinishGame(GameTimeControl? timeControl)
    {
        Game game = StartGame(timeControl);
        Assert.Equal(GameError.None, game.Resign(game.BlackPlayer, game.Revision, Start.AddSeconds(15), (_, _) => false));
        return game;
    }
}
