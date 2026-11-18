using ChessRealms.Platform.Application.Identity;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChessRealms.Platform.Infrastructure.Identity.Jwt;

public static class DependencyInjection
{
    // API-only registration: migrations and persistence tests do not need signing secrets.
    public static IServiceCollection AddIdentityLogin(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer)
                && !string.IsNullOrWhiteSpace(options.Audience) && !string.IsNullOrWhiteSpace(options.SigningKeyId)
                && !string.IsNullOrWhiteSpace(options.PrivateKeyPem), "JWT issuer, audience, key ID and private key are required.")
            .Validate(options => options.AccessTokenLifetime >= TimeSpan.FromSeconds(1)
                && options.AccessTokenLifetime <= TimeSpan.FromHours(1)
                && options.AccessTokenLifetime.Ticks % TimeSpan.TicksPerSecond == 0,
                "JWT lifetime must be a whole number of seconds between one second and one hour.")
            .Validate(options => options.ClockSkew >= TimeSpan.Zero && options.ClockSkew <= TimeSpan.FromMinutes(1),
                "JWT clock skew must be between zero and one minute.")
            .ValidateOnStart();
        services.AddSingleton<JwtSigningKey>();
        services.AddOptions<RefreshTokenOptions>().Bind(configuration.GetSection(RefreshTokenOptions.SectionName))
            .Validate(options => options.SessionLifetime >= TimeSpan.FromSeconds(1)
                && options.SessionLifetime <= TimeSpan.FromDays(365)
                && options.SessionLifetime.Ticks % TimeSpan.TicksPerSecond == 0,
                "Login session lifetime must be whole seconds between one second and 365 days.")
            .ValidateOnStart();
        services.AddScoped<RefreshTokenService>();
        services.AddScoped<LoginSessionCleanup>();
        services.AddScoped<IRefreshTokenService>(provider => provider.GetRequiredService<RefreshTokenService>());
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddSingleton<LoginPasswordWork>();
        services.AddScoped<ILoginService, LoginService>();
        return services;
    }
}
