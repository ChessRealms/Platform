using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Identity;

internal sealed class IdentityUsernameDirectory(IdentityDbContext database, ILookupNormalizer normalizer) : IUsernameDirectory
{
    public Task<Guid?> FindIdAsync(string username, CancellationToken cancellationToken)
    {
        string? normalizedUsername = normalizer.NormalizeName(username);
        return database.Users.Where(user => user.NormalizedUserName == normalizedUsername)
            .Select(user => (Guid?)user.Id).SingleOrDefaultAsync(cancellationToken);
    }

    public Task<string?> FindUsernameAsync(Guid userId, CancellationToken cancellationToken) =>
        database.Users.Where(user => user.Id == userId).Select(user => user.UserName).SingleOrDefaultAsync(cancellationToken);
}
