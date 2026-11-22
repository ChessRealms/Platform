using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Games;

internal sealed class IdentityGamePlayerDirectory(IdentityDbContext database) : IGamePlayerDirectory
{
    public async Task<bool> AreEligibleAsync(Guid whitePlayerId, Guid blackPlayerId, CancellationToken cancellationToken)
    {
        int eligiblePlayers = await database.Users
            .CountAsync(user => user.EmailConfirmed && (user.Id == whitePlayerId || user.Id == blackPlayerId), cancellationToken);
        return eligiblePlayers == 2;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(IReadOnlyCollection<Guid> playerIds,
        CancellationToken cancellationToken) =>
        await database.Users.Where(user => playerIds.Contains(user.Id) && user.UserName != null)
            .Select(user => new { user.Id, user.UserName })
            .ToDictionaryAsync(user => user.Id, user => user.UserName!, cancellationToken);
}
