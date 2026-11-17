using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Identity.Persistence;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityUserContext<PlatformUser, Guid>(options)
{
    public const string Schema = "identity";

    public DbSet<EmailConfirmationSession> EmailConfirmationSessions => Set<EmailConfirmationSession>();
    public DbSet<ConfirmationEmailDelivery> EmailConfirmationDeliveries => Set<ConfirmationEmailDelivery>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
        builder.Entity<PlatformUser>().HasIndex(user => user.NormalizedEmail)
            .IsUnique();
        builder.Entity<EmailConfirmationSession>(entity =>
        {
            entity.HasKey(session => session.TokenHash);
            entity.Property(session => session.TokenHash).HasMaxLength(64);
            entity.HasIndex(session => session.ExpiresAt);
            entity.HasOne<PlatformUser>().WithMany().HasForeignKey(session => session.UserId);
        });
        builder.Entity<ConfirmationEmailDelivery>(entity =>
        {
            entity.HasKey(delivery => delivery.Id);
            entity.Property(delivery => delivery.Status).HasMaxLength(16);
            entity.Property(delivery => delivery.FailureCode).HasMaxLength(64);
            entity.HasIndex(delivery => new { delivery.UserId, delivery.CreatedAt });
            entity.HasIndex(delivery => new { delivery.Status, delivery.CreatedAt });
            entity.HasOne<PlatformUser>().WithMany().HasForeignKey(delivery => delivery.UserId);
        });
    }
}
