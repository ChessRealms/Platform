using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;

using Microsoft.AspNetCore.SignalR;

namespace ChessRealms.Platform.Api.Realtime;

public sealed class GameNotifications(IHubContext<GameHub> hub, ILogger<GameNotifications> logger) : IGameListNotifications
{
    public Task BoardChangedAsync(Game game) => SendAsync(token => hub.Clients.Group(GameHub.GroupName(game.Id))
        .SendAsync(GameHub.ChangeEvent, game.Id, game.Revision, token));

    public Task NotifyAsync(Game game) => ListsChangedAsync(
        game.WhiteGuestName is null ? game.WhitePlayerId : null,
        game.BlackGuestName is null ? game.BlackPlayerId : null);

    public Task ListsChangedAsync(Guid? firstAccount, Guid? secondAccount = null)
    {
        List<string> groups = new(2);
        if (firstAccount is Guid first)
        {
            groups.Add(GameHub.MyGamesGroupName(first));
        }
        if (secondAccount is Guid second && second != firstAccount)
        {
            groups.Add(GameHub.MyGamesGroupName(second));
        }
        return groups.Count > 0
            ? SendAsync(token => hub.Clients.Groups(groups).SendAsync(GameHub.MyGamesChangeEvent, token))
            : Task.CompletedTask;
    }

    private async Task SendAsync(Func<CancellationToken, Task> send)
    {
        using CancellationTokenSource notification = new(TimeSpan.FromSeconds(1));
        try
        {
            await send(notification.Token);
        }
        catch (Exception exception)
        {
            // The change has committed. Clients can recover the current state through HTTP.
            logger.LogWarning(exception, "Could not deliver a game update.");
        }
    }
}
