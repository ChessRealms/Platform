using ChessRealms.Platform.Application.Games;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChessRealms.Platform.Api.Realtime;

[Authorize(Policy = GuestAuthentication.GamePolicy)]
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

        if (await games.GetAsync(gameId, GuestAuthentication.Participant(Context.User!), Context.ConnectionAborted) is null)
        {
            throw new HubException("This game is unavailable.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(gameId), Context.ConnectionAborted);
        Context.Items[nameof(Subscribe)] = gameId;
    }

    internal static string GroupName(Guid gameId) => $"game:{gameId:D}";
}
