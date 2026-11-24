using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Infrastructure.Games;
using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChessRealms.Platform.Infrastructure;

public static class DependencyInjection
{
    public static TBuilder AddInfrastructure<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        // Invitation writes own their transactions. Replaying a commit with an unknown outcome
        // could repeat a request without an idempotency key.
        builder.AddNpgsqlDbContext<PlatformDbContext>("platform", configureSettings: settings => settings.DisableRetry = true);
        builder.AddPlatformIdentity();
        builder.Services.AddSingleton<IGameRules, EngineGameRules>();
        builder.Services.AddScoped<IGameStore, PostgresGameStore>();
        builder.Services.AddScoped<IGamePlayerDirectory, IdentityGamePlayerDirectory>();
        return builder;
    }
}
