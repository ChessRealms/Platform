using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChessRealms.Platform.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/games")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class GamesController(GameService games) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateGameRequest request, CancellationToken cancellationToken)
    {
        GameOperationResult result = await games.CreateAsync(PlayerId(), request.OpponentId, cancellationToken);
        if (result.Error != GameError.None)
        {
            return ErrorResponse(result.Error);
        }

        GameResponse response = GameResponse.From(result.Game!);
        return CreatedAtAction(nameof(Get), new { id = response.Id }, response);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        Game? game = await games.GetAsync(id, PlayerId(), cancellationToken);
        return game is null ? ErrorResponse(GameError.NotFound) : Ok(GameResponse.From(game));
    }

    [HttpPost("{id:guid}/accept")]
    public async Task<IActionResult> Accept(Guid id, AcceptGameRequest request, CancellationToken cancellationToken) =>
        ChangeResponse(await games.AcceptAsync(id, PlayerId(), request.ExpectedRevision!.Value, cancellationToken));

    [HttpPost("{id:guid}/moves")]
    public async Task<IActionResult> Play(Guid id, PlayGameMoveRequest request, CancellationToken cancellationToken) =>
        ChangeResponse(await games.PlayAsync(id, PlayerId(), request.ExpectedRevision!.Value, request.Move, cancellationToken));

    private Guid PlayerId() => Guid.Parse(User.FindFirst("sub")!.Value);

    private IActionResult ChangeResponse(GameOperationResult result) =>
        result.Error == GameError.None ? Ok(GameResponse.From(result.Game!)) : ErrorResponse(result.Error);

    private ObjectResult ErrorResponse(GameError error)
    {
        int status = error switch
        {
            GameError.NotFound => StatusCodes.Status404NotFound,
            GameError.Forbidden => StatusCodes.Status403Forbidden,
            GameError.InvalidPlayers or GameError.OpponentUnavailable => StatusCodes.Status400BadRequest,
            GameError.RevisionConflict or GameError.GameNotPending or GameError.GameNotActive or GameError.NotYourTurn
                => StatusCodes.Status409Conflict,
            GameError.IllegalMove => StatusCodes.Status422UnprocessableEntity,
            _ => throw new InvalidOperationException("Unexpected game operation result.")
        };
        return StatusCode(status, new { code = error.ToString() });
    }
}
