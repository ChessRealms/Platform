using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Identity.Persistence;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityUserContext<PlatformUser, Guid>(options)
{
    public const string Schema = "identity";

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
        builder.Entity<PlatformUser>().HasIndex(user => user.NormalizedEmail)
            .IsUnique();
    }
}
