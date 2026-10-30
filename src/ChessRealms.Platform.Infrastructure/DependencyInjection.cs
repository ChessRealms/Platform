using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.Extensions.Hosting;

namespace ChessRealms.Platform.Infrastructure;

public static class DependencyInjection
{
    public static TBuilder AddInfrastructure<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.AddNpgsqlDbContext<PlatformDbContext>("platform");
        return builder;
    }
}
