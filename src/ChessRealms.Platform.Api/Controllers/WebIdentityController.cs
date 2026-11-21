using ChessRealms.Platform.Application.Identity;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChessRealms.Platform.Api.Controllers;

[ApiController]
[Route("api/identity/web")]
[RequireSameOriginWebRequest]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WebIdentityController(ILoginService login, IRefreshTokenService refreshTokens,
    ILoginSessionService sessions) : ControllerBase
{
    private const string CookieName = "__Secure-ChessRealms.Refresh";
    private const string CookiePath = "/api/identity/web";

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        LoginResult result = await login.LoginAsync(request, cancellationToken);
        if (result.Code == "Success")
        {
            WriteRefreshCookie(result.RefreshToken, result.RefreshTokenExpiresAt);
        }

        WebSessionResponse response = new(result.Code, result.AccessToken, result.TokenType,
            result.ExpiresIn, result.RefreshTokenExpiresAt, result.EmailConfirmation);
        return result.Code switch
        {
            "Success" => Ok(response),
            "EmailNotConfirmed" => StatusCode(StatusCodes.Status403Forbidden, response),
            "InvalidCredentials" => Unauthorized(response),
            _ => throw new InvalidOperationException("Unexpected login result.")
        };
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue(CookieName, out string? token) || string.IsNullOrEmpty(token))
        {
            return Unauthorized(new WebSessionResponse("InvalidRefreshToken"));
        }

        RefreshResult result = await refreshTokens.RefreshAsync(new(token), cancellationToken);
        switch (result.Code)
        {
            case "Success":
                WriteRefreshCookie(result.RefreshToken, result.RefreshTokenExpiresAt);
                return Ok(new WebSessionResponse(result.Code, result.AccessToken, result.TokenType,
                    result.ExpiresIn, result.RefreshTokenExpiresAt));
            case "InvalidRefreshToken":
                Response.Cookies.Delete(CookieName, CookieSettings());
                return Unauthorized(new WebSessionResponse(result.Code));
            default:
                throw new InvalidOperationException("Unexpected refresh result.");
        }
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken) =>
        EndSession(await sessions.LogoutAsync(UserId(), SessionId(), cancellationToken));

    [HttpPost("logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll(CancellationToken cancellationToken) =>
        EndSession(await sessions.LogoutAllAsync(UserId(), SessionId(), cancellationToken));

    private Guid UserId() => Guid.Parse(User.FindFirst("sub")!.Value);
    private Guid SessionId() => Guid.Parse(User.FindFirst("sid")!.Value);

    private IActionResult EndSession(SessionOperationResult result)
    {
        switch (result)
        {
            case SessionOperationResult.Success:
                Response.Cookies.Delete(CookieName, CookieSettings());
                return NoContent();
            case SessionOperationResult.Unauthorized:
                return Unauthorized();
            default:
                throw new InvalidOperationException("Unexpected logout result.");
        }
    }

    private void WriteRefreshCookie(string? token, DateTimeOffset? expiresAt)
    {
        if (string.IsNullOrEmpty(token) || expiresAt is null)
        {
            throw new InvalidOperationException("A successful web session requires a refresh token and expiration.");
        }

        CookieOptions cookie = CookieSettings();
        cookie.Expires = expiresAt;
        Response.Cookies.Append(CookieName, token, cookie);
    }

    private static CookieOptions CookieSettings() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = CookiePath
    };

    private sealed record WebSessionResponse(string Code, string? AccessToken = null, string? TokenType = null,
        long? ExpiresIn = null, DateTimeOffset? RefreshTokenExpiresAt = null, ConfirmationMetadata? EmailConfirmation = null);
}
