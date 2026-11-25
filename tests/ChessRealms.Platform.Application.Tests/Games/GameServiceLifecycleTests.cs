using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Domain.Games;

using Xunit;

namespace ChessRealms.Platform.Application.Tests.Games;

public sealed class GameServiceLifecycleTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly GamePosition InitialPosition = new("initial position",
        "7k/p7/8/8/8/8/R7/K7 w - - 98 50", PlayerColor.White);
    private static readonly GamePosition AfterRookMove = new("position after Rb2",
        "7k/p7/8/8/8/8/1R6/K7 b - - 99 50", PlayerColor.Black, PlyCount: 1);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PlayJustBeforeTheDeadlineRecordsTheMoveAndHandsOverTheClock()
    {
        Game game = StartGame();
        DateTimeOffset playedAt = Start.AddMinutes(5).AddTicks(-1);
        LockedGameStore store = new(game, playedAt);
        GameService service = CreateService(store, new GameRulesStub { ApplyMove = PlayRookMove }, playedAt);

        GameOperationResult result = await service.PlayAsync(game.Id, game.WhitePlayer, 1, "a2b2", Cancellation);

        Assert.Equal(GameError.None, result.Error);
        Assert.Same(game, result.Game);
        Assert.Equal(GameStatus.Active, game.Status);
        Assert.Equal(2, game.Revision);
        Assert.Equal(AfterRookMove.Fen, game.Fen);
        Assert.Equal(PlayerColor.Black, game.SideToMove);
        Assert.Equal(playedAt.AddMinutes(5), game.TurnDeadline);
        Assert.Equal(new GameMoveTiming(game.Id, 1, playedAt, TimeSpan.FromTicks(1), TimeSpan.FromMinutes(5)),
            Assert.Single(store.MoveTimings));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task PlayChecksTheTimeUnderTheLockBeforeApplyingTheMove(int secondsLate)
    {
        Game game = StartGame();
        DateTimeOffset deadline = Start.AddMinutes(5);
        LockedGameStore store = new(game, deadline.AddSeconds(secondsLate));
        // The time at request arrival is still inside White's turn.
        GameService service = CreateService(store, new GameRulesStub(), deadline.AddTicks(-1));

        GameOperationResult result = await service.PlayAsync(game.Id, game.WhitePlayer, 1, "a2b2", Cancellation);

        Assert.Equal(GameError.None, result.Error);
        Assert.Same(game, result.Game);
        Assert.Equal(GameStatus.Finished, game.Status);
        Assert.Equal(GameEndReason.Timeout, game.EndReason);
        Assert.Equal(PlayerColor.Black, game.Winner);
        Assert.Equal(deadline, game.FinishedAt);
        Assert.Equal(2, game.Revision);
        Assert.Equal(InitialPosition.Fen, game.Fen);
        Assert.Equal(InitialPosition.RulesRecord, game.RulesRecord);
        Assert.Empty(store.MoveTimings);
    }

    [Fact]
    public async Task TimeoutTakesPrecedenceOverAnOpponentsResignationAtTheDeadline()
    {
        Game game = StartGame();
        DateTimeOffset deadline = Start.AddMinutes(5);
        LockedGameStore store = new(game, deadline);
        GameService service = CreateService(store, new GameRulesStub(), deadline.AddTicks(-1));

        GameOperationResult result = await service.ResignAsync(game.Id, game.BlackPlayer, 1, Cancellation);

        Assert.Equal(GameError.None, result.Error);
        Assert.Same(game, result.Game);
        Assert.Equal(GameStatus.Finished, game.Status);
        Assert.Equal(GameEndReason.Timeout, game.EndReason);
        Assert.Equal(PlayerColor.Black, game.Winner);
        Assert.Equal(deadline, game.FinishedAt);
        Assert.Equal(2, game.Revision);
    }

    [Fact]
    public async Task IntendedMoveInADrawClaimDoesNotAddAReplayFrameOrMoveTiming()
    {
        Game game = StartGame();
        DateTimeOffset playedAt = Start.AddSeconds(1);
        DateTimeOffset claimedAt = Start.AddSeconds(3);
        GameParticipant black = game.BlackPlayer;
        LockedGameStore store = new(game, playedAt);
        GamePosition claimedPosition = AfterRookMove with
        {
            RulesRecord = "position after Rb2 with draw claim",
            EndReason = GameEndReason.FiftyMoveRule
        };
        GameReplayFrame frame = new("a2b2", "Rb2", AfterRookMove.Fen, "50", PlayerColor.White);
        GameRulesStub rules = new()
        {
            ApplyMove = PlayRookMove,
            ApplyClaim = (record, reason, move) =>
            {
                Assert.Equal(AfterRookMove.RulesRecord, record);
                Assert.Equal(DrawClaimReason.FiftyMoveRule, reason);
                Assert.Equal("h8g8", move);
                return claimedPosition;
            },
            GetReplay = record =>
            {
                Assert.Equal(claimedPosition.RulesRecord, record);
                return new(InitialPosition.Fen, [frame]);
            }
        };
        GameService service = CreateService(store, rules, claimedAt);
        GameOperationResult played = await service.PlayAsync(game.Id, game.WhitePlayer, 1, frame.Move, Cancellation);
        Assert.Equal(GameError.None, played.Error);
        GameMoveTiming timing = Assert.Single(store.MoveTimings);

        store.LockedAt = claimedAt;
        GameOperationResult claimed = await service.ClaimDrawAsync(game.Id, black, 2,
            DrawClaimReason.FiftyMoveRule, "h8g8", Cancellation);

        Assert.Equal(GameError.None, claimed.Error);
        Assert.Equal(GameStatus.Finished, game.Status);
        Assert.Equal(3, game.Revision);
        Assert.Equal(AfterRookMove.Fen, game.Fen);
        Assert.Equal(PlayerColor.Black, game.SideToMove);
        Assert.Equal(timing, Assert.Single(store.MoveTimings));

        (GameError error, GameReplay? replay) = await service.ReadReplayAsync(game.Id, black, Cancellation);
        Assert.Equal(GameError.None, error);
        GameReplay recorded = Assert.IsType<GameReplay>(replay);
        Assert.True(recorded.HasRecordedTiming);
        GameReplayMove replayMove = Assert.Single(recorded.Moves);
        Assert.Equal(frame, replayMove.Frame);
        Assert.Equal(1000, replayMove.AtMs);
        Assert.Equal(GameEndReason.FiftyMoveRule, recorded.Finish.EndReason);
        Assert.Equal(3000, recorded.Finish.AtMs);
        Assert.Null(recorded.Finish.Winner);
    }

    private static Game StartGame()
    {
        Game game = new(Guid.NewGuid(), Guid.NewGuid(), InitialPosition, Start.AddMinutes(-1), GameTimeControl.Blitz5);
        Assert.Equal(GameError.None, game.Accept(game.BlackPlayer, 0, Start));
        return game;
    }

    private static GamePosition PlayRookMove(string record, string move)
    {
        Assert.Equal(InitialPosition.RulesRecord, record);
        Assert.Equal("a2b2", move);
        return AfterRookMove;
    }

    private static GameService CreateService(LockedGameStore store, IGameRules rules, DateTimeOffset now)
    {
        UnusedPlayerDirectory directory = new();
        return new(store, rules, directory, directory, new FixedClock(now));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class LockedGameStore(Game game, DateTimeOffset lockedAt) : IGameStore
    {
        private readonly List<GameMoveTiming> moveTimings = [];

        public DateTimeOffset LockedAt { get; set; } = lockedAt;
        public IReadOnlyList<GameMoveTiming> MoveTimings => moveTimings;

        public Task<Game?> FindAsync(Guid id, CancellationToken cancellationToken)
        {
            Assert.Equal(game.Id, id);
            return Task.FromResult<Game?>(game);
        }

        public Task<IReadOnlyList<GameMoveTiming>> ReadMoveTimingsAsync(Guid gameId, CancellationToken cancellationToken)
        {
            Assert.Equal(game.Id, gameId);
            return Task.FromResult(MoveTimings);
        }

        public Task<GameOperationResult> ChangeLockedAsync(Guid gameId, GameParticipant player,
            Func<Game, DateTimeOffset, GameError> change, CancellationToken cancellationToken)
        {
            Assert.Equal(game.Id, gameId);
            Assert.True(game.HasPlayer(player));
            GameError error = change(game, LockedAt);
            return Task.FromResult(new GameOperationResult(error, error == GameError.None ? game : null));
        }

        public Task<GameOperationResult> PlayAsync(Guid gameId, GameParticipant player,
            Func<Game, DateTimeOffset, (GameError Error, GameMoveTiming? Timing)> play, CancellationToken cancellationToken) =>
            ChangeLockedAsync(gameId, player, (current, now) =>
            {
                (GameError error, GameMoveTiming? timing) = play(current, now);
                if (error == GameError.None && timing is not null)
                {
                    moveTimings.Add(timing);
                }
                return error;
            }, cancellationToken);

        public Task<GameBoardSnapshot?> ReadBoardAsync(Guid id, GameParticipant player, long? afterRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListDueAsync(Guid playerId, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Game?> FindByCreationRequestAsync(Guid creatorId, Guid requestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GameListItem>> ListAsync(Guid playerId, GameListFilter filter, Guid? before, int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CreateGameResult> CreateAsync(Guid creatorId, Guid opponentId, GamePosition startingPosition, Guid? requestId,
            CancellationToken cancellationToken, GameTimeControl? timeControl) => throw new NotSupportedException();

        public Task<GameOperationResult> AcceptRematchAsync(Guid gameId, GameParticipant player, long expectedRevision,
            Func<GamePosition> startingPosition, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class GameRulesStub : IGameRules
    {
        public Func<string, string, GamePosition?> ApplyMove { get; init; } =
            (_, _) => throw new InvalidOperationException("Move validation was not expected.");

        public Func<string, DrawClaimReason, string?, GamePosition?> ApplyClaim { get; init; } =
            (_, _, _) => throw new InvalidOperationException("Draw claim validation was not expected.");

        public Func<string, GameReplayLine> GetReplay { get; init; } = _ => throw new NotSupportedException();

        public GamePosition? Play(string rulesRecord, string move) => ApplyMove(rulesRecord, move);
        public GamePosition? ClaimDraw(string rulesRecord, DrawClaimReason reason, string? intendedMove) =>
            ApplyClaim(rulesRecord, reason, intendedMove);

        public GameReplayLine ReadReplay(string rulesRecord) => GetReplay(rulesRecord);
        public bool HasInsufficientMatingMaterial(string fen, PlayerColor attacker) => false;
        public GamePosition StartingPosition() => throw new NotSupportedException();
        public GameMoveList ReadMoves(string rulesRecord, bool includeLegalMoves) => throw new NotSupportedException();
    }

    private sealed class UnusedPlayerDirectory : IGamePlayerDirectory, IUsernameDirectory
    {
        public Task<bool> IsEligibleAsync(Guid playerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> AreEligibleAsync(Guid whitePlayerId, Guid blackPlayerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(IReadOnlyCollection<Guid> playerIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> FindIdAsync(string username, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> FindUsernameAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
