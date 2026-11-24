using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Domain.Games;

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
    public async Task<IActionResult> Create(CreateLobbyRequest request, CancellationToken cancellationToken) =>
        await RespondAsync(await lobbies.CreateAsync(PlayerId(), request.RequestId, cancellationToken), cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        await RespondAsync(await lobbies.GetAsync(id, PlayerId(), cancellationToken), cancellationToken);

    [HttpPost("{id:guid}/join")]
    public async Task<IActionResult> Join(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await RespondAsync(await lobbies.JoinAsync(id, PlayerId(), request.ExpectedRevision!.Value, cancellationToken),
            cancellationToken);

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await RespondAsync(await lobbies.CancelAsync(id, PlayerId(), request.ExpectedRevision!.Value, cancellationToken),
            cancellationToken);

    private Guid PlayerId() => Guid.Parse(User.FindFirst("sub")!.Value);

    private async Task<IActionResult> RespondAsync(LobbyResult result, CancellationToken cancellationToken)
    {
        if (result.Lobby is { } lobby)
        {
            DateTimeOffset now = clock.GetUtcNow();
            string? hostName = lobby.StatusAt(now) == LobbyStatus.Open
                ? await usernames.FindUsernameAsync(lobby.CreatorId, cancellationToken) : null;
            LobbyResponse response = LobbyResponse.From(lobby, PlayerId(), hostName, now);
            return result.Created ? CreatedAtAction(nameof(Get), new { id = lobby.Id }, response) : Ok(response);
        }

        int status = result.Error switch
        {
            GameError.InvalidRequestId => StatusCodes.Status400BadRequest,
            GameError.Forbidden => StatusCodes.Status403Forbidden,
            GameError.NotFound => StatusCodes.Status404NotFound,
            GameError.InvitationExpired => StatusCodes.Status410Gone,
            GameError.InvitationLimitReached or GameError.RevisionConflict or GameError.LobbyNotOpen or GameError.LobbyAlreadyJoined
                => StatusCodes.Status409Conflict,
            _ => throw new InvalidOperationException("Unexpected lobby operation result.")
        };
        return StatusCode(status, new { code = result.Error.ToString() });
    }
}
