using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Api.Controllers;

public sealed record LobbyResponse(Guid Id, string Status, long Revision, DateTimeOffset ExpiresAt, bool IsOwner,
    string? HostName, Guid? GameId, string? TimeControl, string? GuestName, DateTimeOffset ServerTime)
{
    internal static LobbyResponse From(PrivateLobby lobby, GameParticipant? player, string? hostName, DateTimeOffset now,
        string? guestName)
    {
        LobbyStatus status = lobby.StatusAt(now);
        bool isOwner = lobby.IsOwner(player);
        return new(lobby.Id, status.ToString(), lobby.Revision + (status == LobbyStatus.Expired ? 1 : 0),
            lobby.ExpiresAt, isOwner, hostName, isOwner || lobby.IsJoiner(player) ? lobby.GameId : null,
            lobby.TimeControl?.ToString(), guestName, now);
    }
}
