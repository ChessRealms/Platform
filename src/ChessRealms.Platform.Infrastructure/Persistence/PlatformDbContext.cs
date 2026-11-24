using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Games;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Persistence;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    internal const string GameCreationRequestId = "CreationRequestId";
    internal const string GameCreationRequestIndex = "IX_Games_WhitePlayerId_CreationRequestId";

    public DbSet<Game> Games => Set<Game>();
    public DbSet<GameMoveTiming> GameMoveTimings => Set<GameMoveTiming>();
    internal DbSet<GuestPlayer> GuestPlayers => Set<GuestPlayer>();
    public DbSet<PrivateLobby> PrivateLobbies => Set<PrivateLobby>();
    public DbSet<QuickPlayTicket> QuickPlayTickets => Set<QuickPlayTicket>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Game>(game =>
        {
            game.Property(item => item.WhiteGuestName).HasMaxLength(32);
            game.Property(item => item.BlackGuestName).HasMaxLength(32);
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
        builder.Entity<GuestPlayer>(guest =>
        {
            guest.ToTable("GuestPlayers");
            guest.HasKey(player => player.Id);
            guest.Property(player => player.Id).ValueGeneratedNever();
            guest.Property(player => player.DisplayName).HasMaxLength(32).IsRequired();
            guest.Property(player => player.SessionHash).HasMaxLength(64);
            guest.HasIndex(player => player.SessionHash).IsUnique();
        });
        builder.Entity<GameMoveTiming>(timing =>
        {
            timing.HasKey(item => new { item.GameId, item.Ply });
            timing.HasOne<Game>().WithMany().HasForeignKey(item => item.GameId);
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
        builder.Entity<QuickPlayTicket>(ticket =>
        {
            const string waiting = "\"Status\" = 'Waiting'";
            ticket.HasKey(item => new { item.PlayerId, item.Id });
            ticket.Property(item => item.Sequence).UseIdentityAlwaysColumn();
            ticket.Property(item => item.Status).HasConversion<string>().HasMaxLength(16);
            ticket.HasIndex(item => item.PlayerId).IsUnique().HasFilter(waiting);
            ticket.HasIndex(item => item.Sequence).HasFilter(waiting);
            ticket.HasIndex(item => item.ExpiresAt).HasFilter(waiting);
            ticket.HasIndex(item => new { item.PlayerId, item.Sequence });
            ticket.HasOne<Game>().WithMany().HasForeignKey(item => item.GameId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
