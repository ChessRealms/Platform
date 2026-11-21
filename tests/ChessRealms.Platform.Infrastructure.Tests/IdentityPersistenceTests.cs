using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Testcontainers.PostgreSql;

using Xunit;

namespace ChessRealms.Platform.Infrastructure.Tests;

public sealed class IdentityPersistenceTests
{
    [Fact]
    public async Task MigrationsAndIdentityStoresWorkWithPostgreSql()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.3").Build();
        await postgres.StartAsync(cancellationToken);

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:platform"] = postgres.GetConnectionString()
        });
        builder.AddInfrastructure();
        using IHost host = builder.Build();

        Guid userId;
        const string password = "Integration-test-Password9!";
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            PlatformDbContext platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            IdentityDbContext identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await platform.Database.MigrateAsync(cancellationToken);
            await identity.Database.MigrateAsync(cancellationToken);
            // A second worker run must be safe and both snapshots must match their models.
            await platform.Database.MigrateAsync(cancellationToken);
            await identity.Database.MigrateAsync(cancellationToken);
            Assert.False(platform.Database.HasPendingModelChanges());
            Assert.False(identity.Database.HasPendingModelChanges());
            Assert.Empty(await identity.Database.GetPendingMigrationsAsync(cancellationToken));

            UserManager<PlatformUser> users = scope.ServiceProvider.GetRequiredService<UserManager<PlatformUser>>();
            PlatformUser user = new() { UserName = "player", Email = "player@example.test" };
            IdentityResult created = await users.CreateAsync(user, password);
            Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(error => error.Description)));
            Assert.NotEqual(Guid.Empty, user.Id);
            Assert.NotEqual(password, user.PasswordHash);
            userId = user.Id;

            IdentityResult duplicate = await users.CreateAsync(new PlatformUser
            {
                UserName = "another-player", Email = "PLAYER@example.test"
            }, password);
            Assert.False(duplicate.Succeeded);
            Assert.Contains(duplicate.Errors, error => error.Code == "DuplicateEmail");
        }

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            UserManager<PlatformUser> users = scope.ServiceProvider.GetRequiredService<UserManager<PlatformUser>>();
            PlatformUser? user = await users.FindByEmailAsync("PLAYER@example.test");
            Assert.NotNull(user);
            Assert.Equal(userId, user.Id);
            Assert.True(await users.CheckPasswordAsync(user, password));
            Assert.False(await users.CheckPasswordAsync(user, "wrong-password"));
            Assert.False(user.EmailConfirmed);
            Assert.True(users.Options.SignIn.RequireConfirmedEmail);

            IdentityDbContext identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            identity.Users.Add(new PlatformUser
            {
                UserName = "race-player", NormalizedUserName = "RACE-PLAYER",
                Email = "player@example.test", NormalizedEmail = "PLAYER@EXAMPLE.TEST"
            });
            // Database uniqueness also protects against concurrent registrations.
            await Assert.ThrowsAsync<DbUpdateException>(() => identity.SaveChangesAsync(cancellationToken));
            identity.ChangeTracker.Clear();

            PlatformDbContext platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            string[] platformMigrations = (await platform.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
            await identity.GetService<IMigrator>().MigrateAsync("0", cancellationToken);
            Assert.Equal(platformMigrations, await platform.Database.GetAppliedMigrationsAsync(cancellationToken));
            await identity.Database.MigrateAsync(cancellationToken);
            Assert.Empty(await identity.Users.ToListAsync(cancellationToken));
        }
    }
}
