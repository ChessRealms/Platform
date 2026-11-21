using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Games;

internal sealed class PostgresGameStore(PlatformDbContext database) : IGameStore
{
    public Task<Game?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.Games.SingleOrDefaultAsync(game => game.Id == id, cancellationToken);

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
