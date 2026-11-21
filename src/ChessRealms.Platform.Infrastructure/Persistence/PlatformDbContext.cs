using ChessRealms.Platform.Domain.Games;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Persistence;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<Game> Games => Set<Game>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Game>(game =>
        {
            game.Property(item => item.Revision).IsConcurrencyToken();
            game.Property(item => item.Status).HasConversion<string>().HasMaxLength(16);
            game.Property(item => item.SideToMove).HasConversion<string>().HasMaxLength(8);
            game.Property(item => item.Winner).HasConversion<string>().HasMaxLength(8);
            game.Property(item => item.EndReason).HasConversion<string>().HasMaxLength(32);
            game.Property(item => item.RulesRecord).HasColumnType("jsonb");
        });
    }
}
