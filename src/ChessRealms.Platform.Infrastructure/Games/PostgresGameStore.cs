using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace ChessRealms.Platform.Infrastructure.Games;

internal sealed class PostgresGameStore(PlatformDbContext database) : IGameStore
{
    public Task<Game?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.Games.SingleOrDefaultAsync(game => game.Id == id, cancellationToken);

    public Task<Game?> FindByCreationRequestAsync(Guid creatorId, Guid requestId, CancellationToken cancellationToken) =>
        database.Games.AsNoTracking().SingleOrDefaultAsync(game => game.WhitePlayerId == creatorId
            && EF.Property<Guid?>(game, PlatformDbContext.GameCreationRequestId) == requestId, cancellationToken);

    public async Task<IReadOnlyList<GameListItem>> ListAsync(Guid playerId, GameStatus status, Guid? before, int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<Game> participantGames = database.Games.AsNoTracking()
            .Where(game => game.WhitePlayerId == playerId || game.BlackPlayerId == playerId);
        IQueryable<Game> games = participantGames.Where(game => game.Status == status);
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
                game.Status, game.CreatedAt,
                game.Status == GameStatus.Active && (game.SideToMove == PlayerColor.White
                    ? game.WhitePlayerId == playerId : game.BlackPlayerId == playerId), game.Winner, game.EndReason))
            .ToListAsync(cancellationToken);
    }

    public async Task<Game> CreateOrGetAsync(Game game, Guid? requestId, CancellationToken cancellationToken)
    {
        var entry = database.Games.Add(game);
        entry.Property<Guid?>(PlatformDbContext.GameCreationRequestId).CurrentValue = requestId;
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return game;
        }
        catch (DbUpdateException exception) when (requestId.HasValue && exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: PlatformDbContext.GameCreationRequestIndex
        })
        {
            // A concurrent insert has already saved the game for this creator and request.
            entry.State = EntityState.Detached;
            return await FindByCreationRequestAsync(game.WhitePlayerId, requestId.Value, cancellationToken)
                ?? throw new InvalidOperationException("The game for the committed creation request was not found.", exception);
        }
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
