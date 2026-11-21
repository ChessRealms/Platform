using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChessRealms.Platform.Infrastructure.Identity.PasswordReset;

internal sealed class PasswordResetTokenProvider(
    IDataProtectionProvider dataProtection,
    IOptions<PasswordResetTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<PlatformUser>> logger)
    : DataProtectorTokenProvider<PlatformUser>(dataProtection, options, logger);

internal sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public PasswordResetTokenProviderOptions()
    {
        Name = "ChessRealms.PasswordReset";
        TokenLifespan = PasswordResetOptions.LinkLifetime;
    }
}
