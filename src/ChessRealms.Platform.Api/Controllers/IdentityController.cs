using ChessRealms.Platform.Application.Identity;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChessRealms.Platform.Api.Controllers;

[ApiController]
[Route("identity")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class IdentityController(
    IRegistrationService registration,
    IEmailConfirmationService confirmation,
    ILoginService login,
    IRefreshTokenService refreshTokens,
    ILoginSessionService sessions) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        RegistrationResult result = await registration.RegisterAsync(request, cancellationToken);
        if (result.Errors.Count > 0)
        {
            bool duplicate = result.Errors.Any(error => error.Code.StartsWith("Duplicate", StringComparison.Ordinal));
            return StatusCode(duplicate ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest, result);
        }
        return StatusCode(StatusCodes.Status201Created, result);
    }

    [HttpGet("email-confirmation")]
    [AllowAnonymous]
    public async Task<IActionResult> Status(CancellationToken cancellationToken) =>
        SessionResponse(await confirmation.GetStatusAsync(SessionToken(), cancellationToken));

    [HttpPost("email-confirmation/resend")]
    [AllowAnonymous]
    public async Task<IActionResult> Resend(CancellationToken cancellationToken) =>
        SessionResponse(await confirmation.ResendAsync(SessionToken(), cancellationToken));

    [HttpGet("confirm-email")]
    [AllowAnonymous]
    public async Task<IActionResult> Confirm([FromQuery] Guid userId, [FromQuery] string token, CancellationToken cancellationToken)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        string code = await confirmation.ConfirmAsync(userId, token, cancellationToken);
        return code switch
        {
            "EmailConfirmed" or "AlreadyConfirmed" => Ok(new { code }),
            "InvalidConfirmationToken" => BadRequest(new { code }),
            _ => throw new InvalidOperationException("Unexpected email confirmation result.")
        };
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        LoginResult result = await login.LoginAsync(request, cancellationToken);
        return result.Code switch
        {
            "Success" => Ok(result),
            "EmailNotConfirmed" => StatusCode(StatusCodes.Status403Forbidden, result),
            "InvalidCredentials" => Unauthorized(result),
            _ => throw new InvalidOperationException("Unexpected login result.")
        };
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new { userId = Guid.Parse(User.FindFirst("sub")!.Value) });

    [HttpGet("sessions")]
    [Authorize]
    public async Task<IActionResult> Sessions(CancellationToken cancellationToken)
    {
        SessionListResult result = await sessions.ListAsync(UserId(), CurrentSessionId(), cancellationToken);
        return result.Result == SessionOperationResult.Success ? Ok(result.Sessions) : Unauthorized();
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken) =>
        SessionResponse(await sessions.LogoutAsync(UserId(), CurrentSessionId(), cancellationToken));

    [HttpDelete("sessions/{sessionId:guid}")]
    [Authorize]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken cancellationToken) =>
        SessionResponse(await sessions.RevokeAsync(UserId(), CurrentSessionId(), sessionId, cancellationToken));

    [HttpPost("logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll(CancellationToken cancellationToken) =>
        SessionResponse(await sessions.LogoutAllAsync(UserId(), CurrentSessionId(), cancellationToken));

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        RefreshResult result = await refreshTokens.RefreshAsync(request, cancellationToken);
        return result.Code switch
        {
            "Success" => Ok(result),
            "InvalidRefreshToken" => Unauthorized(result),
            _ => throw new InvalidOperationException("Unexpected refresh result.")
        };
    }

    private string SessionToken()
    {
        string header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..] : "";
    }

    private Guid UserId() => Guid.Parse(User.FindFirst("sub")!.Value);
    private Guid CurrentSessionId() => Guid.Parse(User.FindFirst("sid")!.Value);

    private IActionResult SessionResponse(SessionOperationResult result) => result switch
    {
        SessionOperationResult.Success => NoContent(),
        SessionOperationResult.Unauthorized => Unauthorized(),
        SessionOperationResult.NotFound => NotFound(),
        _ => throw new InvalidOperationException("Unexpected session operation result.")
    };

    private ObjectResult SessionResponse(ConfirmationSessionResult result) => result.Code switch
    {
        "InvalidSession" => Unauthorized(result),
        "RateLimited" => StatusCode(StatusCodes.Status429TooManyRequests, result),
        "EmailConfirmed" or "EmailNotConfirmed" or "Queued" => Ok(result),
        _ => throw new InvalidOperationException("Unexpected confirmation session result.")
    };
}
