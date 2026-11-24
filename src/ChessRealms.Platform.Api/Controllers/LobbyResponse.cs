using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Api.Controllers;

public sealed record LobbyResponse(Guid Id, string Status, long Revision, DateTimeOffset ExpiresAt, bool IsOwner,
    string? HostName, Guid? GameId, string? TimeControl)
{
    internal static LobbyResponse From(PrivateLobby lobby, Guid playerId, string? hostName, DateTimeOffset now)
    {
        LobbyStatus status = lobby.StatusAt(now);
        bool isOwner = lobby.CreatorId == playerId;
        return new(lobby.Id, status.ToString(), lobby.Revision + (status == LobbyStatus.Expired ? 1 : 0),
            lobby.ExpiresAt, isOwner, hostName, isOwner || lobby.JoinedPlayerId == playerId ? lobby.GameId : null,
            lobby.TimeControl?.ToString());
    }
}
