using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed class QuickPlayService(IQuickPlayStore store, IGamePlayerDirectory players, IGameRules rules)
{
    public Task<QuickPlayTicket?> ReadAsync(Guid playerId, Guid? ticketId, CancellationToken cancellationToken) =>
        store.ReadAsync(playerId, ticketId, cancellationToken);

    public async Task<(GameError Error, QuickPlayTicket? Ticket)> JoinAsync(Guid playerId, Guid requestId,
        Guid? previousTicketId, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty || previousTicketId == Guid.Empty)
        {
            return (GameError.InvalidRequestId, null);
        }
        if (!await players.IsEligibleAsync(playerId, cancellationToken))
        {
            return (GameError.Forbidden, null);
        }

        QuickPlayTicket? ticket = await store.JoinAsync(playerId, requestId, previousTicketId, rules.StartingPosition, cancellationToken);
        return (ticket is null ? GameError.RevisionConflict : GameError.None, ticket);
    }

    public Task<QuickPlayTicket?> RenewAsync(Guid playerId, Guid ticketId, CancellationToken cancellationToken) =>
        store.ChangeAsync(playerId, ticketId, static (ticket, now) => ticket.Renew(now), cancellationToken);

    public Task<QuickPlayTicket?> CancelAsync(Guid playerId, Guid ticketId, CancellationToken cancellationToken) =>
        store.ChangeAsync(playerId, ticketId, static (ticket, now) => ticket.Cancel(now), cancellationToken);
}
