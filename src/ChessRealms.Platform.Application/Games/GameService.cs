using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed class GameService(IGameStore store, IGameRules rules, IGamePlayerDirectory players, IUsernameDirectory usernames,
    TimeProvider clock)
{
    private const int ListPageSize = 20;

    public async Task<CreateGameResult> CreateAsync(Guid creatorId, Guid? opponentId, string? opponentUsername,
        Guid? requestId, CancellationToken cancellationToken)
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
            return ReplayCreation(existing, blackPlayerId);
        }
        if (!Game.CanPair(creatorId, blackPlayerId))
        {
            return new(GameError.InvalidPlayers);
        }
        if (!await players.AreEligibleAsync(creatorId, blackPlayerId, cancellationToken))
        {
            return new(GameError.OpponentUnavailable);
        }

        Game game = new(creatorId, blackPlayerId, rules.StartingPosition(), clock.GetUtcNow());
        Game saved = await store.CreateOrGetAsync(game, requestId, cancellationToken);
        return saved.Id == game.Id ? new(GameError.None, saved, Created: true) : ReplayCreation(saved, blackPlayerId);
    }

    private static CreateGameResult ReplayCreation(Game game, Guid opponentId) =>
        game.BlackPlayerId == opponentId ? new(GameError.None, game) : new(GameError.CreationRequestConflict);

    public async Task<Game?> GetAsync(Guid gameId, Guid playerId, CancellationToken cancellationToken)
    {
        Game? game = await store.FindAsync(gameId, cancellationToken);
        return game?.HasPlayer(playerId) == true ? game : null;
    }

    public async Task<GameListPage> ListAsync(Guid playerId, GameListFilter filter, Guid? before, CancellationToken cancellationToken)
    {
        IReadOnlyList<GameListItem> rows = await store.ListAsync(playerId, filter, before, ListPageSize + 1, cancellationToken);
        if (rows.Count == 0)
        {
            return new([], null);
        }

        GameListItem[] items = rows.Take(ListPageSize).ToArray();
        Guid[] opponents = items.Select(item => item.OpponentId).Distinct().ToArray();
        IReadOnlyDictionary<Guid, string> names = await players.FindNamesAsync(opponents, cancellationToken);
        return new(items.Select(item => item with { OpponentName = names.GetValueOrDefault(item.OpponentId) }).ToArray(),
            rows.Count > ListPageSize ? items[^1].Id : null);
    }

    public async Task<(GameBoardSnapshot? Board, GameMoveList? Moves)> ReadBoardAsync(Guid gameId, Guid playerId,
        long? afterRevision, CancellationToken cancellationToken)
    {
        GameBoardSnapshot? board = await store.ReadBoardAsync(gameId, playerId, afterRevision, cancellationToken);
        if (board?.RulesRecord is not { } record)
        {
            return (board, null);
        }

        Guid playerToMove = board.SideToMove == PlayerColor.White ? board.WhitePlayerId : board.BlackPlayerId;
        return (board, rules.ReadMoves(record, board.Status == GameStatus.Active && playerId == playerToMove));
    }

    public Task<GameOperationResult> AcceptAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, game => game.Accept(playerId, expectedRevision), cancellationToken);

    public Task<GameOperationResult> DeclineAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, game => game.Decline(playerId, expectedRevision), cancellationToken);

    public Task<GameOperationResult> CancelAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, game => game.Cancel(playerId, expectedRevision), cancellationToken);

    public Task<GameOperationResult> PlayAsync(Guid gameId, Guid playerId, long expectedRevision, string move,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, game => game.Play(playerId, expectedRevision, record => rules.Play(record, move)), cancellationToken);

    public Task<GameOperationResult> ResignAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, game => game.Resign(playerId, expectedRevision, rules.HasInsufficientMatingMaterial), cancellationToken);

    public Task<GameOperationResult> OfferDrawAsync(Guid gameId, Guid playerId, long expectedRevision,
        CancellationToken cancellationToken) =>
        ChangeAsync(gameId, game => game.OfferDraw(playerId, expectedRevision), cancellationToken);

    public Task<GameOperationResult> ReplyToDrawOfferAsync(Guid gameId, Guid playerId, long expectedRevision,
        bool accept, CancellationToken cancellationToken) =>
        ChangeAsync(gameId, game => game.ReplyToDrawOffer(playerId, expectedRevision, accept), cancellationToken);

    public Task<GameOperationResult> ClaimDrawAsync(Guid gameId, Guid playerId, long expectedRevision,
        DrawClaimReason reason, string? intendedMove, CancellationToken cancellationToken) =>
        ChangeAsync(gameId, game => game.ClaimDraw(playerId, expectedRevision,
            record => rules.ClaimDraw(record, reason, intendedMove)), cancellationToken);

    private async Task<GameOperationResult> ChangeAsync(Guid gameId, Func<Game, GameError> change,
        CancellationToken cancellationToken)
    {
        Game? game = await store.FindAsync(gameId, cancellationToken);
        if (game is null)
        {
            return new(GameError.NotFound);
        }

        GameError error = change(game);
        if (error != GameError.None)
        {
            return new(error);
        }
        return await store.TrySaveChangesAsync(cancellationToken)
            ? new(GameError.None, game)
            : new(GameError.RevisionConflict);
    }
}
