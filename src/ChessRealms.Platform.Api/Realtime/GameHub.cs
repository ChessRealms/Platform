using ChessRealms.Platform.Application.Games;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChessRealms.Platform.Api.Realtime;

[Authorize]
public sealed class GameHub(GameService games) : Hub
{
    public const string Path = "/api/games/live";
    public const string ChangeEvent = "GameChanged";

    public async Task Subscribe(Guid gameId)
    {
        if (Context.Items.TryGetValue(nameof(Subscribe), out object? subscription))
        {
            if (subscription is Guid subscribedGameId && subscribedGameId == gameId)
            {
                return;
            }

            throw new HubException("This connection already has a game subscription.");
        }

        Guid playerId = Guid.Parse(Context.User!.FindFirst("sub")!.Value);
        if (await games.GetAsync(gameId, playerId, Context.ConnectionAborted) is null)
        {
            throw new HubException("This game is unavailable.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(gameId), Context.ConnectionAborted);
        Context.Items[nameof(Subscribe)] = gameId;
    }

    internal static string GroupName(Guid gameId) => $"game:{gameId:D}";
}
