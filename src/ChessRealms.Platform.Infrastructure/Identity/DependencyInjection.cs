using System.Net.Mail;

using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;
using ChessRealms.Platform.Infrastructure.Identity.PasswordReset;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
            options.Tokens.PasswordResetTokenProvider = "ChessRealms.PasswordReset";
        }).AddEntityFrameworkStores<IdentityDbContext>().AddDefaultTokenProviders()
            .AddTokenProvider<PasswordResetTokenProvider>("ChessRealms.PasswordReset");
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
            .Validate(options => !options.AutoConfirmOnRegistration || builder.Environment.IsDevelopment()
                || builder.Environment.IsEnvironment("Testing"),
                "Identity:EmailConfirmation:AutoConfirmOnRegistration is allowed only in Development or Testing.")
            .ValidateOnStart();
        builder.Services.AddOptions<PasswordResetOptions>()
            .Bind(builder.Configuration.GetSection("Identity:PasswordReset"))
            .Validate(options => options.ResetUrl is null || Uri.TryCreate(options.ResetUrl, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
                && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment),
                "ResetUrl must be an absolute HTTPS URL without credentials, a query, or a fragment.")
            .ValidateOnStart();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IRegistrationService, RegistrationService>();
        builder.Services.AddScoped<IPasswordResetService, PasswordResetService>();
        builder.Services.AddScoped<PasswordResetEmailDispatcher>();
        builder.Services.AddScoped<EmailConfirmationService>();
        builder.Services.AddScoped<IEmailConfirmationService>(provider => provider.GetRequiredService<EmailConfirmationService>());
        builder.Services.AddScoped<ConfirmationEmailDispatcher>();
        IConfigurationSection smtpConfiguration = builder.Configuration.GetSection("Identity:EmailConfirmation:Smtp");
        if (smtpConfiguration.Exists())
        {
            builder.Services.AddOptions<SmtpEmailOptions>()
                .Bind(smtpConfiguration)
                .Validate(options => !string.IsNullOrWhiteSpace(options.Host)
                    && options.Port is > 0 and <= 65535
                    && MailAddress.TryCreate(options.FromAddress, out var sender)
                    && sender.Address == options.FromAddress,
                    "SMTP host, port, and sender address are required when SMTP is configured.")
                .Validate(options => options.Security is SmtpTransportSecurity.None or SmtpTransportSecurity.StartTls
                    or SmtpTransportSecurity.SslOnConnect,
                    "SMTP security mode is invalid.")
                .Validate(options => string.IsNullOrWhiteSpace(options.Username) == string.IsNullOrWhiteSpace(options.Password)
                    && (string.IsNullOrWhiteSpace(options.Username) || options.Security != SmtpTransportSecurity.None),
                    "SMTP credentials must be paired and require TLS.")
                .Validate(options => builder.Environment.IsDevelopment()
                    || builder.Environment.IsEnvironment("Testing")
                    || options.Security != SmtpTransportSecurity.None,
                    "SMTP requires TLS outside Development and Testing.")
                .ValidateOnStart();
            builder.Services.AddSingleton<SmtpConfirmationEmailSender>();
            builder.Services.AddSingleton<IConfirmationEmailSender>(provider => provider.GetRequiredService<SmtpConfirmationEmailSender>());
            builder.Services.AddSingleton<IPasswordResetEmailSender>(provider => provider.GetRequiredService<SmtpConfirmationEmailSender>());
        }
        else
        {
            builder.Services.AddSingleton<IConfirmationEmailSender, UnconfiguredConfirmationEmailSender>();
            builder.Services.AddSingleton<IPasswordResetEmailSender, UnconfiguredConfirmationEmailSender>();
        }

        return builder;
    }

    public static IServiceCollection AddConfirmationEmailWorker(this IServiceCollection services) =>
        services.AddHostedService<ConfirmationEmailWorker>();
}
