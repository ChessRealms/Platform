using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChessRealms.Platform.Infrastructure.Identity;

public static class DependencyInjection
{
    public static TBuilder AddPlatformIdentity<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.AddNpgsqlDbContext<IdentityDbContext>("platform",
            // These workflows own transactions. Retrying individual commands could replay
            // email delivery; the worker retries a rolled-back request on its next poll.
            configureSettings: settings => settings.DisableRetry = true,
            configureDbContextOptions: options => options.UseNpgsql(postgres =>
                postgres.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDbContext.Schema)));

        builder.Services.AddIdentityCore<PlatformUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedEmail = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        }).AddEntityFrameworkStores<IdentityDbContext>().AddDefaultTokenProviders();
        builder.Services.AddDataProtection().SetApplicationName("ChessRealms.Platform");
        builder.Services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(24));
        builder.Services.AddOptions<EmailConfirmationOptions>()
            .Bind(builder.Configuration.GetSection("Identity:EmailConfirmation"))
            .Validate(options => options.SessionLifetime > TimeSpan.Zero
                && options.ResendCooldown > TimeSpan.Zero && options.DeliveryWindow >= options.ResendCooldown
                && options.DeliveryWindow <= TimeSpan.FromDays(7) && options.MaxDeliveriesPerWindow > 0,
                "Email confirmation lifetimes and delivery limits must be positive and the delivery window must fit retention.")
            .Validate(options => Uri.TryCreate(options.ConfirmationUrl, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Fragment),
                "ConfirmationUrl must be an absolute HTTPS URL without a fragment.")
            .ValidateOnStart();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IRegistrationService, RegistrationService>();
        builder.Services.AddScoped<EmailConfirmationService>();
        builder.Services.AddScoped<IEmailConfirmationService>(provider => provider.GetRequiredService<EmailConfirmationService>());
        builder.Services.AddScoped<ConfirmationEmailDispatcher>();
        builder.Services.AddSingleton<IConfirmationEmailSender, UnconfiguredConfirmationEmailSender>();

        return builder;
    }

    public static IServiceCollection AddConfirmationEmailWorker(this IServiceCollection services) =>
        services.AddHostedService<ConfirmationEmailWorker>();
}
