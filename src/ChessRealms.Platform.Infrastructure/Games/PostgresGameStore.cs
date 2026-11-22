using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Games;

internal sealed class PostgresGameStore(PlatformDbContext database) : IGameStore
{
    public Task<Game?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.Games.SingleOrDefaultAsync(game => game.Id == id, cancellationToken);

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

    public async Task CreateAsync(Game game, CancellationToken cancellationToken)
    {
        database.Games.Add(game);
        await database.SaveChangesAsync(cancellationToken);
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
