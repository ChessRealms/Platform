using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Persistence;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
}
