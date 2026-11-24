using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Games;

internal sealed class PostgresGameStore(PlatformDbContext database, TimeProvider clock) : IGameStore
{
    private const int MaxOpenInvitations = 3;
    private const int MaxPairInvitations = 5;
    private static readonly TimeSpan PairWindow = TimeSpan.FromMinutes(5);

    // Compare the revision and project the history in the same database snapshot.
    private static readonly Func<PlatformDbContext, Guid, Guid, long?, DateTimeOffset, CancellationToken,
        Task<GameBoardSnapshot?>> ReadBoardQuery =
        EF.CompileAsyncQuery((PlatformDbContext db, Guid id, Guid playerId, long? afterRevision, DateTimeOffset now,
            CancellationToken _) =>
            db.Games.AsNoTracking()
                .Where(game => game.Id == id && (game.WhitePlayerId == playerId || game.BlackPlayerId == playerId))
                .Select(game => new
                {
                    Game = game,
                    Expired = game.Status == GameStatus.Pending && game.InvitationExpiresAt <= now
                })
                .Select(item => new { item.Game, item.Expired, Revision = item.Game.Revision + (item.Expired ? 1 : 0) })
                .Select(item => new GameBoardSnapshot(item.Game.Id, item.Game.WhitePlayerId, item.Game.BlackPlayerId,
                    item.Expired ? GameStatus.Expired : item.Game.Status, item.Revision, item.Game.Fen, item.Game.SideToMove,
                    item.Game.Winner, item.Game.EndReason, item.Game.DrawOfferedBy, item.Game.InvitationExpiresAt,
                    item.Revision == afterRevision ? null : item.Game.RulesRecord))
                .SingleOrDefault());

    public Task<Game?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.Games.SingleOrDefaultAsync(game => game.Id == id, cancellationToken);

    public Task<GameBoardSnapshot?> ReadBoardAsync(Guid id, Guid playerId, long? afterRevision,
        CancellationToken cancellationToken) =>
        ReadBoardQuery(database, id, playerId, afterRevision, clock.GetUtcNow(), cancellationToken);

    public Task<Game?> FindByCreationRequestAsync(Guid creatorId, Guid requestId, CancellationToken cancellationToken) =>
        database.Games.AsNoTracking().SingleOrDefaultAsync(game => game.WhitePlayerId == creatorId
            && EF.Property<Guid?>(game, PlatformDbContext.GameCreationRequestId) == requestId, cancellationToken);

    public async Task<IReadOnlyList<GameListItem>> ListAsync(Guid playerId, GameListFilter filter, Guid? before, int limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        IQueryable<Game> participantGames = database.Games.AsNoTracking()
            .Where(game => game.WhitePlayerId == playerId || game.BlackPlayerId == playerId);
        IQueryable<Game> games = filter switch
        {
            GameListFilter.Pending => participantGames.Where(game => game.Status == GameStatus.Pending
                && game.InvitationExpiresAt > now),
            GameListFilter.Active => participantGames.Where(game => game.Status == GameStatus.Active),
            GameListFilter.Finished => participantGames.Where(game => game.Status == GameStatus.Finished),
            GameListFilter.ClosedInvitations => participantGames.Where(game =>
                game.Status == GameStatus.Declined || game.Status == GameStatus.Cancelled
                || (game.Status == GameStatus.Pending && game.InvitationExpiresAt <= now)),
            _ => throw new ArgumentOutOfRangeException(nameof(filter))
        };
        if (before is Guid cursor)
        {
            // Resolve the cursor even if that game moved to another status after the last read.
            var boundary = await participantGames.Where(game => game.Id == cursor)
                .Select(game => new { game.CreatedAt }).SingleOrDefaultAsync(cancellationToken);
            if (boundary is null)
            {
                return [];
            }

            games = boundary.CreatedAt is DateTimeOffset createdAt
                ? games.Where(game => game.CreatedAt == null || game.CreatedAt < createdAt
                    || (game.CreatedAt == createdAt && game.Id.CompareTo(cursor) < 0))
                : games.Where(game => game.CreatedAt == null && game.Id.CompareTo(cursor) < 0);
        }

        return await games.OrderBy(game => game.CreatedAt == null).ThenByDescending(game => game.CreatedAt)
            .ThenByDescending(game => game.Id).Take(limit)
            .Select(game => new GameListItem(game.Id,
                game.WhitePlayerId == playerId ? game.BlackPlayerId : game.WhitePlayerId,
                game.WhitePlayerId == playerId ? PlayerColor.White : PlayerColor.Black,
                game.Status == GameStatus.Pending && game.InvitationExpiresAt <= now ? GameStatus.Expired : game.Status,
                game.CreatedAt, game.InvitationExpiresAt,
                game.Status == GameStatus.Active && (game.SideToMove == PlayerColor.White
                    ? game.WhitePlayerId == playerId : game.BlackPlayerId == playerId), game.Winner, game.EndReason))
            .ToListAsync(cancellationToken);
    }

    public async Task<CreateGameResult> CreateAsync(Guid creatorId, Guid opponentId, GamePosition startingPosition,
        Guid? requestId, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        string lockKey = $"game-invitations:{creatorId:D}";
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        if (requestId is Guid key
            && await FindByCreationRequestAsync(creatorId, key, cancellationToken) is { } existing)
        {
            return CreateGameResult.Replay(existing, opponentId);
        }

        // Match PostgreSQL timestamp precision so the creation response and subsequent reads agree.
        DateTimeOffset utcNow = clock.GetUtcNow();
        DateTimeOffset now = utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerMicrosecond));
        IQueryable<Game> sent = database.Games.AsNoTracking().Where(game => game.WhitePlayerId == creatorId);
        IQueryable<Game> pending = sent.Where(game => game.Status == GameStatus.Pending && game.InvitationExpiresAt > now);
        Game? duplicate = await pending.Where(game => game.BlackPlayerId == opponentId)
            .OrderByDescending(game => game.CreatedAt).ThenByDescending(game => game.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (duplicate is not null)
        {
            return new(GameError.InvitationAlreadyPending, duplicate);
        }

        DateTimeOffset cutoff = now - PairWindow;
        DateTimeOffset? windowStart = await sent.Where(game => game.BlackPlayerId == opponentId && game.CreatedAt > cutoff)
            .OrderByDescending(game => game.CreatedAt).Skip(MaxPairInvitations - 1)
            .Select(game => game.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (windowStart is { } first)
        {
            return new(GameError.InvitationRateLimited, RetryAfter: first + PairWindow - now);
        }

        if (await pending.CountAsync(cancellationToken) >= MaxOpenInvitations)
        {
            return new(GameError.InvitationLimitReached);
        }

        Game game = new(creatorId, opponentId, startingPosition, now);
        var entry = database.Games.Add(game);
        entry.Property<Guid?>(PlatformDbContext.GameCreationRequestId).CurrentValue = requestId;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(GameError.None, game, Created: true);
    }

    public async Task<GameOperationResult> ChangeInvitationAsync(Guid gameId, Guid playerId,
        Func<Game, DateTimeOffset, GameError> change, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        Game? game = await database.Games.FromSql($"""
            SELECT * FROM "Games"
            WHERE "Id" = {gameId} AND ("WhitePlayerId" = {playerId} OR "BlackPlayerId" = {playerId})
            FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);
        if (game is null)
        {
            return new(GameError.NotFound);
        }
        GameError error = change(game, clock.GetUtcNow());
        if (error != GameError.None)
        {
            return new(error);
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(GameError.None, game);
    }

    public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            foreach (var entry in exception.Entries)
            {
                entry.State = EntityState.Detached;
            }
            return false;
        }
    }
}
