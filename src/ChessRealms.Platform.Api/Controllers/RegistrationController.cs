using ChessRealms.Platform.Application.Identity;

using Microsoft.AspNetCore.Mvc;

namespace ChessRealms.Platform.Api.Controllers;

[ApiController]
[Route("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RegistrationController(IRegistrationService registration) : ControllerBase
{
    [HttpPost("register")]
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
    public async Task<IActionResult> Status(CancellationToken cancellationToken) =>
        SessionResponse(await registration.GetStatusAsync(SessionToken(), cancellationToken));

    [HttpPost("email-confirmation/resend")]
    public async Task<IActionResult> Resend(CancellationToken cancellationToken) =>
        SessionResponse(await registration.ResendAsync(SessionToken(), cancellationToken));

    [HttpGet("confirm-email")]
    public async Task<IActionResult> Confirm([FromQuery] Guid userId, [FromQuery] string token, CancellationToken cancellationToken)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        string code = await registration.ConfirmAsync(userId, token, cancellationToken);
        return code == "InvalidConfirmationToken" ? BadRequest(new { code }) : Ok(new { code });
    }

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
