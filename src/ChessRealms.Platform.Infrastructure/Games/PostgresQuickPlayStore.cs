using System.Security.Cryptography;

using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Games;

internal sealed class PostgresQuickPlayStore(PlatformDbContext database, TimeProvider clock) : IQuickPlayStore
{
    public Task<QuickPlayTicket?> ReadAsync(Guid playerId, Guid? ticketId, CancellationToken cancellationToken) =>
        ReadQuery(playerId, ticketId).FirstOrDefaultAsync(cancellationToken);

    public async Task<(QuickPlayTicket? Ticket, Game? NewGame)> JoinAsync(Guid playerId, Guid requestId, Guid? previousTicketId,
        Func<GamePosition> startingPosition, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockQueueAsync(cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();

        QuickPlayTicket? existing = await ReadAsync(playerId, requestId, cancellationToken);
        if (existing is not null)
        {
            return (existing, null);
        }

        QuickPlayTicket? current = await ReadAsync(playerId, null, cancellationToken);
        if (current?.StatusAt(now) == QuickPlayStatus.Waiting || current?.Id != previousTicketId)
        {
            // A delayed request cannot create a new search after another tab has moved on.
            return (current, null);
        }

        await database.QuickPlayTickets.Where(ticket => ticket.Status == QuickPlayStatus.Waiting && ticket.ExpiresAt <= now)
            .ExecuteUpdateAsync(update => update.SetProperty(ticket => ticket.Status, QuickPlayStatus.Expired), cancellationToken);

        QuickPlayTicket? opponent = await database.QuickPlayTickets
            .Where(ticket => ticket.Status == QuickPlayStatus.Waiting && ticket.PlayerId != playerId)
            .OrderBy(ticket => ticket.Sequence).FirstOrDefaultAsync(cancellationToken);
        QuickPlayTicket ticket = new(playerId, requestId, now);
        Game? game = null;
        if (opponent is not null)
        {
            game = ticket.MatchWith(opponent, RandomNumberGenerator.GetInt32(2) == 0, startingPosition(), now);
            database.Games.Add(game);
        }
        database.QuickPlayTickets.Add(ticket);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (ticket, game);
    }

    public async Task<QuickPlayTicket?> ChangeAsync(Guid playerId, Guid ticketId, Action<QuickPlayTicket, DateTimeOffset> change,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockQueueAsync(cancellationToken);
        QuickPlayTicket? ticket = await database.QuickPlayTickets
            .SingleOrDefaultAsync(item => item.PlayerId == playerId && item.Id == ticketId, cancellationToken);
        if (ticket is null)
        {
            return null;
        }
        change(ticket, clock.GetUtcNow());
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ticket;
    }

    private IQueryable<QuickPlayTicket> ReadQuery(Guid playerId, Guid? ticketId)
    {
        var tickets = database.QuickPlayTickets.AsNoTracking().Where(ticket => ticket.PlayerId == playerId);
        return ticketId is Guid id
            ? tickets.Where(ticket => ticket.Id == id)
            : tickets.OrderByDescending(ticket => ticket.Sequence);
    }

    private Task LockQueueAsync(CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(74031, 1)", cancellationToken);
}
