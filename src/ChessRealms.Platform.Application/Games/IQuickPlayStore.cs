using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public interface IQuickPlayStore
{
    Task<QuickPlayTicket?> ReadAsync(Guid playerId, Guid? ticketId, CancellationToken cancellationToken);
    Task<QuickPlayTicket?> JoinAsync(Guid playerId, Guid requestId, Guid? previousTicketId, Func<GamePosition> startingPosition,
        CancellationToken cancellationToken);
    Task<QuickPlayTicket?> ChangeAsync(Guid playerId, Guid ticketId, Action<QuickPlayTicket, DateTimeOffset> change,
        CancellationToken cancellationToken);
}
