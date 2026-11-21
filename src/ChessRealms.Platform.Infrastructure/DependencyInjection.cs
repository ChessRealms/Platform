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
        builder.AddNpgsqlDbContext<PlatformDbContext>("platform");
        builder.AddPlatformIdentity();
        builder.Services.AddSingleton<IGameRules, EngineGameRules>();
        builder.Services.AddScoped<IGameStore, PostgresGameStore>();
        builder.Services.AddScoped<IGamePlayerDirectory, IdentityGamePlayerDirectory>();
        return builder;
    }
}
