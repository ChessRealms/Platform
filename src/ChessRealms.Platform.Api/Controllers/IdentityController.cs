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
    ILoginService login
    ) : ControllerBase
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
        return code == "InvalidConfirmationToken" ? BadRequest(new { code }) : Ok(new { code });
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
            _ => Unauthorized(result)
        };
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new { userId = Guid.Parse(User.FindFirst("sub")!.Value) });

    private string SessionToken()
    {
        string header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..] : "";
    }

    private IActionResult SessionResponse(ConfirmationSessionResult result) => result.Code switch
    {
        "InvalidSession" => Unauthorized(result),
        "RateLimited" => StatusCode(StatusCodes.Status429TooManyRequests, result),
        _ => Ok(result)
    };
}
