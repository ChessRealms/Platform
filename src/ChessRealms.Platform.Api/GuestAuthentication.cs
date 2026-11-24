using System.Security.Claims;
using System.Text.Encodings.Web;

using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ChessRealms.Platform.Api;

internal sealed class GuestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IGuestSessions sessions, TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string GuestScheme = "GuestSession";
    internal const string GamePolicy = "GameParticipant";
    internal const string CookieName = "__Host-ChessRealms.Guest";

    internal static GameParticipant Participant(ClaimsPrincipal identity) =>
        new(Guid.Parse(identity.FindFirst("sub")!.Value), identity.Identity?.AuthenticationType == GuestScheme);

    internal static CookieOptions CookieSettings(DateTimeOffset? expiresAt = null) => new()
    {
        Expires = expiresAt,
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/"
    };

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(CookieName, out string? token))
        {
            return AuthenticateResult.NoResult();
        }

        bool webSocket = Context.WebSockets.IsWebSocketRequest;
        DateTimeOffset? proofExpires = webSocket
            ? GuestHubProof.Validate(Request.Query["access_token"], token, clock.GetUtcNow()) : null;
        if (webSocket ? proofExpires is null : !RequireSameOriginWebRequestAttribute.Matches(Request))
        {
            return AuthenticateResult.Fail("Guest sessions require a same-origin browser request.");
        }

        GuestSession? session = await sessions.FindAsync(token, Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.Fail("The guest session is unavailable.");
        }
        if (session.RenewCookie)
        {
            Response.Cookies.Append(CookieName, token, CookieSettings(session.ExpiresAt));
        }

        ClaimsIdentity identity = new([new Claim("sub", session.PlayerId.ToString()),
            new Claim(ClaimTypes.Name, session.DisplayName)], GuestScheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            ExpiresUtc = proofExpires is { } expiry && expiry < session.ExpiresAt ? expiry : session.ExpiresAt
        }, GuestScheme));
    }
}
