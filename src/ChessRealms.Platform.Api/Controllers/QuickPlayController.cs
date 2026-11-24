using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChessRealms.Platform.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/quick-play")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class QuickPlayController(QuickPlayService quickPlay, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<QuickPlayResponse>> Current(CancellationToken cancellationToken)
    {
        QuickPlayTicket? ticket = await quickPlay.ReadAsync(PlayerId(), null, cancellationToken);
        return ticket is null ? NoContent() : ResponseFor(ticket);
    }

    [HttpGet("{ticketId:guid}")]
    public async Task<ActionResult<QuickPlayResponse>> Get(Guid ticketId, CancellationToken cancellationToken) =>
        ResponseFor(await quickPlay.ReadAsync(PlayerId(), ticketId, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<QuickPlayResponse>> Join(JoinQuickPlayRequest request, CancellationToken cancellationToken)
    {
        (GameError error, QuickPlayTicket? ticket) = await quickPlay.JoinAsync(PlayerId(), request.RequestId,
            request.PreviousTicketId, cancellationToken);
        return error switch
        {
            GameError.None => ResponseFor(ticket),
            GameError.InvalidRequestId => BadRequest(new { code = error.ToString() }),
            GameError.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { code = error.ToString() }),
            GameError.RevisionConflict => Conflict(new { code = error.ToString() }),
            _ => throw new InvalidOperationException("Unexpected quick play operation result.")
        };
    }

    [HttpPost("{ticketId:guid}/heartbeat")]
    public async Task<ActionResult<QuickPlayResponse>> Renew(Guid ticketId, CancellationToken cancellationToken) =>
        ResponseFor(await quickPlay.RenewAsync(PlayerId(), ticketId, cancellationToken));

    [HttpPost("{ticketId:guid}/cancel")]
    public async Task<ActionResult<QuickPlayResponse>> Cancel(Guid ticketId, CancellationToken cancellationToken) =>
        ResponseFor(await quickPlay.CancelAsync(PlayerId(), ticketId, cancellationToken));

    private Guid PlayerId() => Guid.Parse(User.FindFirst("sub")!.Value);

    private ActionResult<QuickPlayResponse> ResponseFor(QuickPlayTicket? ticket) => ticket is null
        ? NotFound(new { code = "NotFound" })
        : new QuickPlayResponse(ticket.Id, ticket.StatusAt(clock.GetUtcNow()).ToString(), ticket.EnqueuedAt,
            ticket.ExpiresAt, ticket.GameId);
}

public sealed record JoinQuickPlayRequest(Guid RequestId, Guid? PreviousTicketId = null);

public sealed record QuickPlayResponse(Guid Id, string Status, DateTimeOffset EnqueuedAt, DateTimeOffset ExpiresAt, Guid? GameId);
