using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Domain.Games;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChessRealms.Platform.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/lobbies")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LobbiesController(LobbyService lobbies, IUsernameDirectory usernames, TimeProvider clock) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateLobbyRequest request, CancellationToken cancellationToken)
    {
        if (!GameTimeControlRequest.TryParse(request.TimeControl, out GameTimeControl? timeControl))
        {
            return await RespondAsync(new(GameError.InvalidTimeControl), null, cancellationToken);
        }
        GameParticipant player = GuestAuthentication.Participant(User);
        return await RespondAsync(await lobbies.CreateAsync(player.Id, request.RequestId, timeControl, cancellationToken),
            player, cancellationToken);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        GameParticipant player = GuestAuthentication.Participant(User);
        return await RespondAsync(await lobbies.GetAsync(id, player, cancellationToken), player, cancellationToken);
    }

    [HttpGet("{id:guid}/guest")]
    [AllowAnonymous]
    public async Task<IActionResult> GuestPreview(Guid id, CancellationToken cancellationToken)
    {
        AuthenticateResult session = await HttpContext.AuthenticateAsync(GuestAuthentication.GuestScheme);
        GameParticipant? player = session.Principal is { } principal ? GuestAuthentication.Participant(principal) : null;
        return await RespondAsync(await lobbies.GetAsync(id, player, cancellationToken), player, cancellationToken,
            session.Principal?.Identity?.Name);
    }

    [HttpPost("{id:guid}/join")]
    [Authorize(Policy = GuestAuthentication.GamePolicy)]
    public async Task<IActionResult> Join(Guid id, GameRevisionRequest request, CancellationToken cancellationToken)
    {
        GameParticipant player = GuestAuthentication.Participant(User);
        string? guestName = player.IsGuest ? User.Identity!.Name : null;
        return await RespondAsync(await lobbies.JoinAsync(id, player, guestName, request.ExpectedRevision!.Value, cancellationToken),
            player, cancellationToken, guestName);
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, GameRevisionRequest request, CancellationToken cancellationToken)
    {
        GameParticipant player = GuestAuthentication.Participant(User);
        return await RespondAsync(await lobbies.CancelAsync(id, player.Id, request.ExpectedRevision!.Value, cancellationToken),
            player, cancellationToken);
    }

    private async Task<IActionResult> RespondAsync(LobbyResult result, GameParticipant? player, CancellationToken cancellationToken,
        string? guestName = null)
    {
        if (result.Lobby is { } lobby)
        {
            DateTimeOffset now = clock.GetUtcNow();
            string? hostName = lobby.StatusAt(now) == LobbyStatus.Open
                ? await usernames.FindUsernameAsync(lobby.CreatorId, cancellationToken) : null;
            LobbyResponse response = LobbyResponse.From(lobby, player, hostName, now, guestName);
            return result.Created ? CreatedAtAction(nameof(Get), new { id = lobby.Id }, response) : Ok(response);
        }

        int status = result.Error switch
        {
            GameError.InvalidRequestId or GameError.InvalidTimeControl => StatusCodes.Status400BadRequest,
            GameError.Forbidden => StatusCodes.Status403Forbidden,
            GameError.NotFound => StatusCodes.Status404NotFound,
            GameError.InvitationExpired => StatusCodes.Status410Gone,
            GameError.InvitationLimitReached or GameError.RevisionConflict or GameError.LobbyNotOpen or GameError.LobbyAlreadyJoined
                or GameError.CreationRequestConflict
                => StatusCodes.Status409Conflict,
            _ => throw new InvalidOperationException("Unexpected lobby operation result.")
        };
        return StatusCode(status, new { code = result.Error.ToString() });
    }
}
