using ChessRealms.Platform.Application.Games;

using Microsoft.AspNetCore.Http.Connections;

namespace ChessRealms.Platform.Api.Realtime;

public static class GameHubEndpoint
{
    public static void AddGameRealtime(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<GameNotifications>();
        services.AddSingleton<IGameListNotifications>(provider => provider.GetRequiredService<GameNotifications>());
    }

    public static void MapGameHub(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapHub<GameHub>(GameHub.Path, options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.CloseOnAuthenticationExpiration = true;
        });
}
