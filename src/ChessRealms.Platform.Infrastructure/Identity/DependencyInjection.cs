using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChessRealms.Platform.Infrastructure.Identity;

public static class DependencyInjection
{
    public static TBuilder AddPlatformIdentity<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.AddNpgsqlDbContext<IdentityDbContext>("platform",
            configureDbContextOptions: options => options.UseNpgsql(postgres =>
                postgres.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDbContext.Schema)));

        builder.Services.AddIdentityCore<PlatformUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedEmail = true;
        }).AddEntityFrameworkStores<IdentityDbContext>();

        return builder;
    }
}
