using ChessRealms.Platform.Domain.Games;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Persistence;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    internal const string GameCreationRequestId = "CreationRequestId";
    internal const string GameCreationRequestIndex = "IX_Games_WhitePlayerId_CreationRequestId";

    public DbSet<Game> Games => Set<Game>();
    public DbSet<PrivateLobby> PrivateLobbies => Set<PrivateLobby>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Game>(game =>
        {
            game.Property(item => item.Revision).IsConcurrencyToken();
            game.Property(item => item.Status).HasConversion<string>().HasMaxLength(16);
            game.Property(item => item.SideToMove).HasConversion<string>().HasMaxLength(8);
            game.Property(item => item.Winner).HasConversion<string>().HasMaxLength(8);
            game.Property(item => item.DrawOfferedBy).HasConversion<string>().HasMaxLength(8);
            game.Property(item => item.RematchOfferedBy).HasConversion<string>().HasMaxLength(8);
            game.Property(item => item.EndReason).HasConversion<string>().HasMaxLength(32);
            game.Property(item => item.TimeControl).HasConversion<string>().HasMaxLength(16);
            game.Property(item => item.RulesRecord).HasColumnType("jsonb");
            game.Property<Guid?>(GameCreationRequestId);
            game.HasIndex(nameof(Game.WhitePlayerId), GameCreationRequestId)
                .HasDatabaseName(GameCreationRequestIndex).IsUnique();
            game.HasIndex(item => new { item.WhitePlayerId, item.Status });
            game.HasIndex(item => new { item.BlackPlayerId, item.Status });
            game.HasIndex(item => new { item.WhitePlayerId, item.BlackPlayerId, item.CreatedAt });
            game.HasOne<Game>().WithOne().HasForeignKey<Game>(item => item.RematchGameId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<PrivateLobby>(lobby =>
        {
            lobby.Property(item => item.Status).HasConversion<string>().HasMaxLength(16);
            lobby.Property(item => item.TimeControl).HasConversion<string>().HasMaxLength(16);
            lobby.HasIndex(item => new { item.CreatorId, item.RequestId }).IsUnique();
            lobby.HasIndex(item => new { item.CreatorId, item.Status, item.ExpiresAt });
            lobby.HasIndex(item => item.GameId).IsUnique();
            lobby.HasOne<Game>().WithOne().HasForeignKey<PrivateLobby>(item => item.GameId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
