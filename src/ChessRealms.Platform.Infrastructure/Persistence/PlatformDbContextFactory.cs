using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChessRealms.Platform.Infrastructure.Persistence;

public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        // Scaffolding migrations does not require a running database.
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__platform")
            ?? "Host=localhost;Database=platform;Username=postgres";
        DbContextOptions<PlatformDbContext> options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new PlatformDbContext(options);
    }
}
