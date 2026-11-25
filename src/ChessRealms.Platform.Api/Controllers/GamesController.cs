using System.Globalization;

using ChessRealms.Platform.Api.Realtime;
using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChessRealms.Platform.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/games")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class GamesController(GameService games, GameNotifications notifications, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<GameListResponse>> List([FromQuery] GameListFilter? status, [FromQuery] Guid? before,
        CancellationToken cancellationToken)
    {
        GameListFilter filter = status ?? GameListFilter.Pending;
        if (!Enum.IsDefined(filter))
        {
            return BadRequest(new { code = "InvalidGameStatus" });
        }

        GameListPage page = await games.ListAsync(PlayerId(), filter, before, cancellationToken);
        return GameListResponse.From(page, clock.GetUtcNow());
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateGameRequest request, CancellationToken cancellationToken)
    {
        if (!GameTimeControlRequest.TryParse(request.TimeControl, out GameTimeControl? timeControl))
        {
            return ErrorResponse(GameError.InvalidTimeControl);
        }
        CreateGameResult result = await games.CreateAsync(PlayerId(), request.OpponentId, request.OpponentUsername,
            request.RequestId, cancellationToken, timeControl);
        if (result.Error == GameError.InvitationAlreadyPending)
        {
            return Conflict(new { code = result.Error.ToString(), gameId = result.Game!.Id });
        }
        if (result.RetryAfter is { } retryAfter)
        {
            Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }
        if (result.Error != GameError.None)
        {
            return ErrorResponse(result.Error);
        }

        Game game = result.Game!;
        if (result.Created)
        {
            await notifications.NotifyAsync(game);
        }
        GameResponse response = GameResponse.From(game, clock.GetUtcNow());
        return result.Created ? CreatedAtAction(nameof(Get), new { id = response.Id }, response) : Ok(response);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = GuestAuthentication.GamePolicy)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        Game? game = await games.GetAsync(id, GuestAuthentication.Participant(User), cancellationToken);
        return game is null ? ErrorResponse(GameError.NotFound) : Ok(GameResponse.From(game, clock.GetUtcNow()));
    }

    [HttpPost("{id:guid}/accept")]
    public async Task<IActionResult> Accept(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.AcceptAsync(id, GuestAuthentication.Participant(User), request.ExpectedRevision!.Value, cancellationToken));

    [HttpPost("{id:guid}/decline")]
    public async Task<IActionResult> Decline(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.DeclineAsync(id, GuestAuthentication.Participant(User), request.ExpectedRevision!.Value, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.CancelAsync(id, GuestAuthentication.Participant(User), request.ExpectedRevision!.Value, cancellationToken));

    [HttpGet("{id:guid}/board")]
    [Authorize(Policy = GuestAuthentication.GamePolicy)]
    public async Task<ActionResult<GameBoardResponse>> Board(Guid id, [FromQuery] long? afterRevision,
        CancellationToken cancellationToken)
    {
        (GameBoardSnapshot? board, GameMoveList? moves) = await games.ReadBoardAsync(id, GuestAuthentication.Participant(User), afterRevision,
            cancellationToken);
        if (board is null)
        {
            return ErrorResponse(GameError.NotFound);
        }
        if (moves is null)
        {
            return NoContent();
        }

        return new GameBoardResponse(GameResponse.From(board, clock.GetUtcNow()), moves.Moves, moves.LegalMoves,
            moves.DrawClaims.Select(reason => reason.ToString()).ToArray());
    }

    [HttpPost("{id:guid}/moves")]
    [Authorize(Policy = GuestAuthentication.GamePolicy)]
    public async Task<IActionResult> Play(Guid id, PlayGameMoveRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.PlayAsync(id, GuestAuthentication.Participant(User), request.ExpectedRevision!.Value, request.Move, cancellationToken));

    [HttpGet("{id:guid}/replay")]
    [Authorize(Policy = GuestAuthentication.GamePolicy)]
    public async Task<ActionResult<GameReplayResponse>> Replay(Guid id, CancellationToken cancellationToken)
    {
        (GameError error, GameReplay? replay) = await games.ReadReplayAsync(id, GuestAuthentication.Participant(User), cancellationToken);
        return replay is null ? ErrorResponse(error) : GameReplayResponse.From(replay);
    }

    [HttpPost("{id:guid}/resign")]
    [Authorize(Policy = GuestAuthentication.GamePolicy)]
    public async Task<IActionResult> Resign(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.ResignAsync(id, GuestAuthentication.Participant(User), request.ExpectedRevision!.Value, cancellationToken));

    [HttpPost("{id:guid}/rematch/offer")]
    [Authorize(Policy = GuestAuthentication.GamePolicy)]
    public async Task<IActionResult> OfferRematch(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.OfferRematchAsync(id, GuestAuthentication.Participant(User), request.ExpectedRevision!.Value, cancellationToken));

    [HttpPost("{id:guid}/rematch/accept")]
    [Authorize(Policy = GuestAuthentication.GamePolicy)]
    public async Task<IActionResult> AcceptRematch(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.AcceptRematchAsync(id, GuestAuthentication.Participant(User), request.ExpectedRevision!.Value, cancellationToken));

    [HttpPost("{id:guid}/draw/offer")]
    [Authorize(Policy = GuestAuthentication.GamePolicy)]
    public async Task<IActionResult> OfferDraw(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.OfferDrawAsync(id, GuestAuthentication.Participant(User), request.ExpectedRevision!.Value, cancellationToken));

    [HttpPost("{id:guid}/draw/accept")]
    [Authorize(Policy = GuestAuthentication.GamePolicy)]
    public async Task<IActionResult> AcceptDraw(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.ReplyToDrawOfferAsync(id, GuestAuthentication.Participant(User), request.ExpectedRevision!.Value, true, cancellationToken));

    [HttpPost("{id:guid}/draw/decline")]
    [Authorize(Policy = GuestAuthentication.GamePolicy)]
    public async Task<IActionResult> DeclineDraw(Guid id, GameRevisionRequest request, CancellationToken cancellationToken) =>
        await ChangeResponseAsync(await games.ReplyToDrawOfferAsync(id, GuestAuthentication.Participant(User), request.ExpectedRevision!.Value, false, cancellationToken));

    [HttpPost("{id:guid}/draw/claim")]
    [Authorize(Policy = GuestAuthentication.GamePolicy)]
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

        return await ChangeResponseAsync(await games.ClaimDrawAsync(id, GuestAuthentication.Participant(User), request.ExpectedRevision!.Value,
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
        await Task.WhenAll(notifications.BoardChangedAsync(game), notifications.NotifyAsync(game));
        return Ok(GameResponse.From(game, clock.GetUtcNow()));
    }

    private ObjectResult ErrorResponse(GameError error)
    {
        int status = error switch
        {
            GameError.NotFound => StatusCodes.Status404NotFound,
            GameError.Forbidden => StatusCodes.Status403Forbidden,
            GameError.InvalidPlayers or GameError.InvalidOpponentUsername or GameError.OpponentUnavailable or GameError.InvalidRequestId
                or GameError.InvalidTimeControl
                => StatusCodes.Status400BadRequest,
            GameError.RevisionConflict or GameError.GameNotPending or GameError.GameNotActive or GameError.GameNotFinished or GameError.NotYourTurn
                or GameError.DrawOfferPending or GameError.NoDrawOffer or GameError.CreationRequestConflict
                or GameError.InvitationLimitReached or GameError.RematchUnavailable or GameError.RematchOfferPending or GameError.NoRematchOffer
                => StatusCodes.Status409Conflict,
            GameError.InvitationExpired or GameError.RematchExpired => StatusCodes.Status410Gone,
            GameError.InvitationRateLimited => StatusCodes.Status429TooManyRequests,
            GameError.IllegalMove or GameError.DrawClaimUnavailable => StatusCodes.Status422UnprocessableEntity,
            _ => throw new InvalidOperationException("Unexpected game operation result.")
        };
        return StatusCode(status, new { code = error.ToString() });
    }
}
