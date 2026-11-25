using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public interface IGameListNotifications
{
    Task NotifyAsync(Game game);
}
