using System.Globalization;
using System.Text;

using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ChessRealms.Platform.Api.Controllers;

[ApiController]
[Route("api/guests/session")]
[RequireSameOriginWebRequest]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class GuestSessionsController(IGuestSessions sessions, LobbyService lobbies, TimeProvider clock) : ControllerBase
{
    internal const string RateLimitPolicy = "guest-session";

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Create(CreateGuestSessionRequest request, CancellationToken cancellationToken)
    {
        AuthenticateResult existing = await HttpContext.AuthenticateAsync(GuestAuthentication.GuestScheme);
        if (existing.Principal is { } principal)
        {
            return Ok(new GuestProfile(GuestAuthentication.Participant(principal).Id, principal.Identity!.Name!));
        }

        string name = request.DisplayName.Trim();
        if (name.Length is < 1 or > 32 || name.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
        {
            return BadRequest(new { code = "InvalidGuestName" });
        }

        LobbyResult lobby = await lobbies.GetAsync(request.LobbyId, null, cancellationToken);
        if (lobby.Error == GameError.NotFound)
        {
            return NotFound(new { code = "NotFound" });
        }
        if (lobby.Lobby?.StatusAt(clock.GetUtcNow()) != LobbyStatus.Open)
        {
            return Conflict(new { code = "LobbyNotOpen" });
        }

        (GuestSession session, string token) = await sessions.CreateAsync(name, cancellationToken);
        Response.Cookies.Append(GuestAuthentication.CookieName, token, GuestAuthentication.CookieSettings(session.ExpiresAt));
        return StatusCode(StatusCodes.Status201Created, new GuestProfile(session.PlayerId, session.DisplayName));
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = GuestAuthentication.GuestScheme)]
    public GuestProfile Get() => new(GuestAuthentication.Participant(User).Id, User.Identity!.Name!);

    [HttpGet("live")]
    [Authorize(AuthenticationSchemes = GuestAuthentication.GuestScheme)]
    public IActionResult Live() => Ok(new
    {
        accessToken = GuestHubProof.Create(Request.Cookies[GuestAuthentication.CookieName]!, clock.GetUtcNow())
    });

    [HttpDelete]
    public async Task<IActionResult> Revoke(CancellationToken cancellationToken)
    {
        if (Request.Cookies.TryGetValue(GuestAuthentication.CookieName, out string? token))
        {
            await sessions.RevokeAsync(token, cancellationToken);
            Response.Cookies.Delete(GuestAuthentication.CookieName, GuestAuthentication.CookieSettings());
        }
        return NoContent();
    }
}

public sealed record CreateGuestSessionRequest(Guid LobbyId, string DisplayName);

public sealed record GuestProfile(Guid UserId, string Username);
