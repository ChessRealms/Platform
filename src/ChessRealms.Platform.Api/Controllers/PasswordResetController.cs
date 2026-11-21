using ChessRealms.Platform.Application.Identity;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ChessRealms.Platform.Api.Controllers;

[ApiController]
[Route("api/identity/password-reset")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16 * 1024)]
[EnableRateLimiting(RateLimitPolicy)]
[AllowAnonymous]
public sealed class PasswordResetController(IPasswordResetService passwordReset) : ControllerBase
{
    internal const string RateLimitPolicy = "PasswordReset";

    [HttpPost("request")]
    public async Task<IActionResult> RequestReset(PasswordResetEmailRequest request, CancellationToken cancellationToken)
    {
        await passwordReset.RequestAsync(request.Email, cancellationToken);
        return Accepted();
    }

    [HttpPost("complete")]
    public async Task<IActionResult> Complete(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        PasswordResetResult result = await passwordReset.ResetAsync(request, cancellationToken);
        return result.Code switch
        {
            "PasswordReset" => NoContent(),
            "InvalidResetToken" or "InvalidPassword" => BadRequest(result),
            _ => throw new InvalidOperationException("Unexpected password reset result.")
        };
    }
}
