using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Jwt;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ChessRealms.Platform.Api;

public static class IdentityAuthentication
{
    public static IServiceCollection AddIdentityAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddIdentityLogin(configuration);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtSigningKey, IOptions<JwtOptions>>((bearer, key, options) =>
            {
                JwtOptions settings = options.Value;
                bearer.MapInboundClaims = false;
                bearer.IncludeErrorDetails = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key.ValidationKey,
                    RequireSignedTokens = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidTypes = ["at+jwt"],
                    TryAllIssuerSigningKeys = false,
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    IgnoreTrailingSlashWhenValidatingAudience = false,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = settings.ClockSkew,
                    NameClaimType = "sub"
                };
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        System.Security.Claims.Claim[] subjects = context.Principal?.FindAll("sub").ToArray() ?? [];
                        System.Security.Claims.Claim[] sessionIds = context.Principal?.FindAll("sid").ToArray() ?? [];
                        if (subjects.Length != 1 || !Guid.TryParse(subjects[0].Value, out Guid userId)
                            || userId == Guid.Empty)
                        {
                            context.Fail("Invalid subject.");
                        }
                        else if (sessionIds.Length != 1 || !Guid.TryParse(sessionIds[0].Value, out Guid sessionId)
                            || sessionId == Guid.Empty)
                        {
                            context.Fail("Invalid session identifier.");
                        }
                        return Task.CompletedTask;
                    }
                };
            }).ValidateOnStart();
        services.AddAuthorization();
        return services;
    }
}
