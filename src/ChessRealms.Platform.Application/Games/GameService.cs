using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed class GameService(IGameStore store, IGameRules rules, IGamePlayerDirectory players, IUsernameDirectory usernames,
    TimeProvider clock)
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
                return replay with { Game = await ResolveTimeoutAsync(existing.Id, creatorId, cancellationToken) };
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

    public async Task<Game?> GetAsync(Guid gameId, Guid playerId, CancellationToken cancellationToken)
    {
        Game? game = await store.FindAsync(gameId, cancellationToken);
        if (game?.HasPlayer(playerId) != true)
        {
            return null;
        }
        return game.TurnDeadline <= clock.GetUtcNow()
            ? await ResolveTimeoutAsync(gameId, playerId, cancellationToken)
            : game;
    }

    public async Task<GameListPage> ListAsync(Guid playerId, GameListFilter filter, Guid? before, CancellationToken cancellationToken)
    {
        if (filter is GameListFilter.Active or GameListFilter.Finished)
        {
            IReadOnlyList<Guid> due = await store.ListDueAsync(playerId, clock.GetUtcNow(), cancellationToken);
            foreach (Guid gameId in due)
            {
                await ResolveTimeoutAsync(gameId, playerId, cancellationToken);
            }
        }
        IReadOnlyList<GameListItem> rows = await store.ListAsync(playerId, filter, before, ListPageSize + 1, cancellationToken);
        if (rows.Count == 0)
        {
            return new([], null);
        }

        GameListItem[] items = rows.Take(ListPageSize).ToArray();
        Guid[] opponents = items.Select(item => item.OpponentId).OfType<Guid>().Distinct().ToArray();
        IReadOnlyDictionary<Guid, string> names = await players.FindNamesAsync(opponents, cancellationToken);
        return new(items.Select(item => item.OpponentId is Guid id ? item with { OpponentName = names.GetValueOrDefault(id) } : item).ToArray(),
            rows.Count > ListPageSize ? items[^1].Id : null);
    }

    public async Task<(GameBoardSnapshot? Board, GameMoveList? Moves)> ReadBoardAsync(Guid gameId, Guid playerId,
        long? afterRevision, CancellationToken cancellationToken)
    {
        GameBoardSnapshot? board = await store.ReadBoardAsync(gameId, playerId, afterRevision, cancellationToken);
        if (board?.TurnDeadline <= clock.GetUtcNow())
        {
            await ResolveTimeoutAsync(gameId, playerId, cancellationToken);
            board = await store.ReadBoardAsync(gameId, playerId, afterRevision, cancellationToken);
        }
        if (board?.RulesRecord is not { } record)
        {
            return (board, null);
        }

        Guid playerToMove = board.SideToMove == PlayerColor.White ? board.WhitePlayerId : board.BlackPlayerId;
        return (board, rules.ReadMoves(record, board.Status == GameStatus.Active && playerId == playerToMove));
    }

    public Task<GameOperationResult> AcceptAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        store.ChangeLockedAsync(gameId, playerId, (game, now) => game.Accept(playerId, expectedRevision, now), cancellationToken);

    public Task<GameOperationResult> DeclineAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        store.ChangeLockedAsync(gameId, playerId, (game, now) => game.Decline(playerId, expectedRevision, now), cancellationToken);

    public Task<GameOperationResult> CancelAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        store.ChangeLockedAsync(gameId, playerId, (game, now) => game.Cancel(playerId, expectedRevision, now), cancellationToken);

    public Task<GameOperationResult> OfferRematchAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        store.ChangeLockedAsync(gameId, playerId, (game, now) => game.OfferRematch(playerId, expectedRevision, now), cancellationToken);

    public Task<GameOperationResult> AcceptRematchAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        store.AcceptRematchAsync(gameId, playerId, expectedRevision, rules.StartingPosition, cancellationToken);

    public Task<GameOperationResult> PlayAsync(Guid gameId, Guid playerId, long expectedRevision, string move,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, playerId, (game, now) => game.Play(playerId, expectedRevision, now, record => rules.Play(record, move)),
            cancellationToken);

    public Task<GameOperationResult> ResignAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, playerId, (game, now) => game.Resign(playerId, expectedRevision, now, rules.HasInsufficientMatingMaterial),
            cancellationToken);

    public Task<GameOperationResult> OfferDrawAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, playerId, (game, _) => game.OfferDraw(playerId, expectedRevision), cancellationToken);

    public Task<GameOperationResult> ReplyToDrawOfferAsync(Guid gameId, Guid playerId, long expectedRevision,
        bool accept, CancellationToken cancellationToken) =>
        ChangeAsync(gameId, playerId, (game, now) => game.ReplyToDrawOffer(playerId, expectedRevision, accept, now), cancellationToken);

    public Task<GameOperationResult> ClaimDrawAsync(Guid gameId, Guid playerId, long expectedRevision,
        DrawClaimReason reason, string? intendedMove, CancellationToken cancellationToken) =>
        ChangeAsync(gameId, playerId, (game, now) => game.ClaimDraw(playerId, expectedRevision, now,
            record => rules.ClaimDraw(record, reason, intendedMove)), cancellationToken);

    private async Task<Game?> ResolveTimeoutAsync(Guid gameId, Guid playerId, CancellationToken cancellationToken)
    {
        GameOperationResult result = await store.ChangeLockedAsync(gameId, playerId, (game, now) =>
        {
            game.TryFinishOnTime(now, rules.HasInsufficientMatingMaterial);
            return GameError.None;
        }, cancellationToken);
        return result.Game;
    }

    private Task<GameOperationResult> ChangeAsync(Guid gameId, Guid playerId, Func<Game, DateTimeOffset, GameError> change,
        CancellationToken cancellationToken) =>
        store.ChangeLockedAsync(gameId, playerId, (game, now) =>
        {
            if (game.TryFinishOnTime(now, rules.HasInsufficientMatingMaterial))
            {
                return GameError.None;
            }
            return game.IsInvitationExpired(now) ? GameError.GameNotActive : change(game, now);
        }, cancellationToken);
}
