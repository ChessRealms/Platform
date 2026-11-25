using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed class GameService(IGameStore store, IGameRules rules, IGamePlayerDirectory players, IUsernameDirectory usernames,
    TimeProvider clock, IGameListNotifications? listNotifications = null)
{
    private const int ListPageSize = 20;

    public async Task<CreateGameResult> CreateAsync(Guid creatorId, Guid? opponentId, string? opponentUsername,
        Guid? requestId, CancellationToken cancellationToken, GameTimeControl? timeControl = null)
    {
        if (requestId == Guid.Empty)
        {
            return new(GameError.InvalidRequestId);
        }
        if (opponentId.HasValue == (opponentUsername is not null))
        {
            return new(GameError.InvalidPlayers);
        }
        if (opponentUsername is not null)
        {
            string username = opponentUsername.Trim();
            if (username.Length is 0 or > 256)
            {
                return new(GameError.InvalidOpponentUsername);
            }

            opponentId = await usernames.FindIdAsync(username, cancellationToken);
            if (opponentId is null)
            {
                return new(GameError.OpponentUnavailable);
            }
        }

        Guid blackPlayerId = opponentId.GetValueOrDefault();
        if (requestId is Guid key
            && await store.FindByCreationRequestAsync(creatorId, key, cancellationToken) is { } existing)
        {
            CreateGameResult replay = CreateGameResult.Replay(existing, blackPlayerId, timeControl);
            if (replay.Game?.TurnDeadline <= clock.GetUtcNow())
            {
                return replay with { Game = await ResolveTimeoutAsync(existing.Id, GameParticipant.Account(creatorId), cancellationToken) };
            }
            return replay;
        }
        if (!Game.CanPair(creatorId, blackPlayerId))
        {
            return new(GameError.InvalidPlayers);
        }
        if (!await players.AreEligibleAsync(creatorId, blackPlayerId, cancellationToken))
        {
            return new(GameError.OpponentUnavailable);
        }

        return await store.CreateAsync(creatorId, blackPlayerId, rules.StartingPosition(), requestId, cancellationToken, timeControl);
    }

    public async Task<Game?> GetAsync(Guid gameId, GameParticipant player, CancellationToken cancellationToken)
    {
        Game? game = await store.FindAsync(gameId, cancellationToken);
        if (game?.HasPlayer(player) != true)
        {
            return null;
        }
        return game.TurnDeadline <= clock.GetUtcNow()
            ? await ResolveTimeoutAsync(gameId, player, cancellationToken)
            : game;
    }

    public async Task<GameListPage> ListAsync(Guid playerId, GameListFilter filter, Guid? before, CancellationToken cancellationToken)
    {
        if (filter is GameListFilter.Active or GameListFilter.Finished)
        {
            IReadOnlyList<Guid> due = await store.ListDueAsync(playerId, clock.GetUtcNow(), cancellationToken);
            foreach (Guid gameId in due)
            {
                await ResolveTimeoutAsync(gameId, GameParticipant.Account(playerId), cancellationToken);
            }
        }
        IReadOnlyList<GameListItem> rows = await store.ListAsync(playerId, filter, before, ListPageSize + 1, cancellationToken);
        if (rows.Count == 0)
        {
            return new([], null);
        }

        GameListItem[] items = rows.Take(ListPageSize).ToArray();
        Guid[] opponents = items.Where(item => item.OpponentKind == GameParticipantKind.Account)
            .Select(item => item.OpponentId).OfType<Guid>().Distinct().ToArray();
        IReadOnlyDictionary<Guid, string> names = await players.FindNamesAsync(opponents, cancellationToken);
        return new(items.Select(item => item.OpponentId is Guid id
            ? item with
            {
                OpponentName = item.OpponentKind == GameParticipantKind.Guest ? item.OpponentGuestName : names.GetValueOrDefault(id)
            }
            : item).ToArray(),
            rows.Count > ListPageSize ? items[^1].Id : null);
    }

    public async Task<(GameBoardSnapshot? Board, GameMoveList? Moves)> ReadBoardAsync(Guid gameId, GameParticipant player,
        long? afterRevision, CancellationToken cancellationToken)
    {
        GameBoardSnapshot? board = await store.ReadBoardAsync(gameId, player, afterRevision, cancellationToken);
        if (board?.TurnDeadline <= clock.GetUtcNow())
        {
            await ResolveTimeoutAsync(gameId, player, cancellationToken);
            board = await store.ReadBoardAsync(gameId, player, afterRevision, cancellationToken);
        }
        if (board?.RulesRecord is not { } record)
        {
            return (board, null);
        }

        GameParticipant playerToMove = board.SideToMove == PlayerColor.White ? board.WhitePlayer : board.BlackPlayer;
        return (board, rules.ReadMoves(record, board.Status == GameStatus.Active && player == playerToMove));
    }

    public Task<GameOperationResult> AcceptAsync(Guid gameId, GameParticipant player, long expectedRevision,
        CancellationToken cancellationToken) =>
        store.ChangeLockedAsync(gameId, player, (game, now) => game.Accept(player, expectedRevision, now), cancellationToken);

    public Task<GameOperationResult> DeclineAsync(Guid gameId, GameParticipant player, long expectedRevision,
        CancellationToken cancellationToken) =>
        store.ChangeLockedAsync(gameId, player, (game, now) => game.Decline(player, expectedRevision, now), cancellationToken);

    public Task<GameOperationResult> CancelAsync(Guid gameId, GameParticipant player, long expectedRevision,
        CancellationToken cancellationToken) =>
        store.ChangeLockedAsync(gameId, player, (game, now) => game.Cancel(player, expectedRevision, now), cancellationToken);

    public Task<GameOperationResult> OfferRematchAsync(Guid gameId, GameParticipant player, long expectedRevision,
        CancellationToken cancellationToken) =>
        store.ChangeLockedAsync(gameId, player, (game, now) => game.OfferRematch(player, expectedRevision, now), cancellationToken);

    public Task<GameOperationResult> AcceptRematchAsync(Guid gameId, GameParticipant player, long expectedRevision,
        CancellationToken cancellationToken) =>
        store.AcceptRematchAsync(gameId, player, expectedRevision, rules.StartingPosition, cancellationToken);

    public Task<GameOperationResult> PlayAsync(Guid gameId, GameParticipant player, long expectedRevision, string move,
        CancellationToken cancellationToken) =>
        store.PlayAsync(gameId, player, (game, now) =>
        {
            GameMoveTiming? timing = null;
            GameError error = ApplyActiveCommand(game, now,
                (current, at) => current.Play(player, expectedRevision, at, record => rules.Play(record, move), out timing));
            return (error, timing);
        }, cancellationToken);

    public async Task<(GameError Error, GameReplay? Replay)> ReadReplayAsync(Guid gameId, GameParticipant player,
        CancellationToken cancellationToken)
    {
        Game? game = await GetAsync(gameId, player, cancellationToken);
        if (game is null)
        {
            return (GameError.NotFound, null);
        }
        if (game.Status != GameStatus.Finished)
        {
            return (GameError.GameNotFinished, null);
        }

        IReadOnlyList<GameMoveTiming> timings = game.StartedAt.HasValue
            ? await store.ReadMoveTimingsAsync(game.Id, cancellationToken) : [];
        return (GameError.None, GameReplay.From(game, player, rules.ReadReplay(game.RulesRecord), timings));
    }

    public Task<GameOperationResult> ResignAsync(Guid gameId, GameParticipant player, long expectedRevision,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, player, (game, now) => game.Resign(player, expectedRevision, now, rules.HasInsufficientMatingMaterial),
            cancellationToken);

    public Task<GameOperationResult> OfferDrawAsync(Guid gameId, GameParticipant player, long expectedRevision,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, player, (game, _) => game.OfferDraw(player, expectedRevision), cancellationToken);

    public Task<GameOperationResult> ReplyToDrawOfferAsync(Guid gameId, GameParticipant player, long expectedRevision,
        bool accept, CancellationToken cancellationToken) =>
        ChangeAsync(gameId, player, (game, now) => game.ReplyToDrawOffer(player, expectedRevision, accept, now), cancellationToken);

    public Task<GameOperationResult> ClaimDrawAsync(Guid gameId, GameParticipant player, long expectedRevision,
        DrawClaimReason reason, string? intendedMove, CancellationToken cancellationToken) =>
        ChangeAsync(gameId, player, (game, now) => game.ClaimDraw(player, expectedRevision, now,
            record => rules.ClaimDraw(record, reason, intendedMove)), cancellationToken);

    private async Task<Game?> ResolveTimeoutAsync(Guid gameId, GameParticipant player, CancellationToken cancellationToken)
    {
        bool finished = false;
        GameOperationResult result = await store.ChangeLockedAsync(gameId, player, (game, now) =>
        {
            finished = game.TryFinishOnTime(now, rules.HasInsufficientMatingMaterial);
            return GameError.None;
        }, cancellationToken);
        if (finished && result.Game is { } game && listNotifications is not null)
        {
            await listNotifications.NotifyAsync(game);
        }
        return result.Game;
    }

    private Task<GameOperationResult> ChangeAsync(Guid gameId, GameParticipant player, Func<Game, DateTimeOffset, GameError> change,
        CancellationToken cancellationToken) =>
        store.ChangeLockedAsync(gameId, player, (game, now) => ApplyActiveCommand(game, now, change), cancellationToken);

    private GameError ApplyActiveCommand(Game game, DateTimeOffset now, Func<Game, DateTimeOffset, GameError> change)
    {
        if (game.TryFinishOnTime(now, rules.HasInsufficientMatingMaterial))
        {
            return GameError.None;
        }
        return game.IsInvitationExpired(now) ? GameError.GameNotActive : change(game, now);
    }
}
