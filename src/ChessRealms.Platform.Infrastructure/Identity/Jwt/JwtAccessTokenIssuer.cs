using ChessRealms.Platform.Application.Identity;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ChessRealms.Platform.Infrastructure.Identity.Jwt;

public sealed class JwtAccessTokenIssuer(JwtSigningKey key, IOptions<JwtOptions> options, TimeProvider clock) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public AccessToken Issue(Guid userId, Guid sessionId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(sessionId, Guid.Empty);
        JwtOptions settings = options.Value;
        DateTime now = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds()).UtcDateTime;
        string token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + settings.AccessTokenLifetime,
            TokenType = "at+jwt",
            SigningCredentials = key.SigningCredentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                ["sid"] = sessionId.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString()
            }
        });
        return new(token, (long)settings.AccessTokenLifetime.TotalSeconds);
    }
}
