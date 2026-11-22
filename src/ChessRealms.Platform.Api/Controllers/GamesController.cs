using ChessRealms.Platform.Api.Realtime;
using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace ChessRealms.Platform.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/games")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class GamesController(GameService games, IHubContext<GameHub> live,
    ILogger<GamesController> logger) : ControllerBase
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
    public async Task<IActionResult> Accept(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.AcceptAsync(id, PlayerId(), request.ExpectedRevision!.Value, cancellationToken));

    [HttpGet("{id:guid}/board")]
    public async Task<ActionResult<GameBoardResponse>> Board(Guid id, [FromQuery] long? afterRevision,
        CancellationToken cancellationToken)
    {
        Guid playerId = PlayerId();
        Game? game = await games.GetAsync(id, playerId, cancellationToken);
        if (game is null)
        {
            return ErrorResponse(GameError.NotFound);
        }
        if (game.Revision == afterRevision)
        {
            return NoContent();
        }

        GameMoveList moves = games.ReadMoves(game, playerId);
        return new GameBoardResponse(GameResponse.From(game), moves.Moves, moves.LegalMoves,
            moves.DrawClaims.Select(reason => reason.ToString()).ToArray());
    }

    [HttpPost("{id:guid}/moves")]
    public async Task<IActionResult> Play(Guid id, PlayGameMoveRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.PlayAsync(id, PlayerId(), request.ExpectedRevision!.Value, request.Move, cancellationToken));

    [HttpPost("{id:guid}/resign")]
    public async Task<IActionResult> Resign(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.ResignAsync(id, PlayerId(), request.ExpectedRevision!.Value, cancellationToken));

    [HttpPost("{id:guid}/draw/offer")]
    public async Task<IActionResult> OfferDraw(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.OfferDrawAsync(id, PlayerId(), request.ExpectedRevision!.Value, cancellationToken));

    [HttpPost("{id:guid}/draw/accept")]
    public async Task<IActionResult> AcceptDraw(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.ReplyToDrawOfferAsync(id, PlayerId(), request.ExpectedRevision!.Value, true, cancellationToken));

    [HttpPost("{id:guid}/draw/decline")]
    public async Task<IActionResult> DeclineDraw(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.ReplyToDrawOfferAsync(id, PlayerId(), request.ExpectedRevision!.Value, false, cancellationToken));

    [HttpPost("{id:guid}/draw/claim")]
    public async Task<IActionResult> ClaimDraw(Guid id, ClaimGameDrawRequest request, CancellationToken cancellationToken)
    {
        DrawClaimReason? reason = request.Reason switch
        {
            nameof(DrawClaimReason.ThreefoldRepetition) => DrawClaimReason.ThreefoldRepetition,
            nameof(DrawClaimReason.FiftyMoveRule) => DrawClaimReason.FiftyMoveRule,
            _ => null
        };
        if (reason is null)
        {
            return BadRequest(new { code = "InvalidDrawClaimReason" });
        }

        return await ChangeResponseAsync(await games.ClaimDrawAsync(id, PlayerId(), request.ExpectedRevision!.Value,
            reason.Value, request.IntendedMove, cancellationToken));
    }

    private Guid PlayerId() => Guid.Parse(User.FindFirst("sub")!.Value);

    private async Task<IActionResult> ChangeResponseAsync(GameOperationResult result)
    {
        if (result.Error != GameError.None)
        {
            return ErrorResponse(result.Error);
        }

        Game game = result.Game!;
        using CancellationTokenSource notification = new(TimeSpan.FromSeconds(1));
        try
        {
            await live.Clients.Group(GameHub.GroupName(game.Id))
                .SendAsync(GameHub.ChangeEvent, game.Id, game.Revision, notification.Token);
        }
        catch (Exception exception)
        {
            // The command has already committed. Clients can recover from the board endpoint.
            logger.LogWarning(exception, "Could not notify players of game {GameId} at revision {Revision}",
                game.Id, game.Revision);
        }

        return Ok(GameResponse.From(game));
    }

    private ObjectResult ErrorResponse(GameError error)
    {
        int status = error switch
        {
            GameError.NotFound => StatusCodes.Status404NotFound,
            GameError.Forbidden => StatusCodes.Status403Forbidden,
            GameError.InvalidPlayers or GameError.OpponentUnavailable => StatusCodes.Status400BadRequest,
            GameError.RevisionConflict or GameError.GameNotPending or GameError.GameNotActive or GameError.NotYourTurn
                or GameError.DrawOfferPending or GameError.NoDrawOffer
                => StatusCodes.Status409Conflict,
            GameError.IllegalMove or GameError.DrawClaimUnavailable => StatusCodes.Status422UnprocessableEntity,
            _ => throw new InvalidOperationException("Unexpected game operation result.")
        };
        return StatusCode(status, new { code = error.ToString() });
    }
}
