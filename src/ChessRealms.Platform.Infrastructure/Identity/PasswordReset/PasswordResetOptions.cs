namespace ChessRealms.Platform.Infrastructure.Identity.PasswordReset;

public sealed class PasswordResetOptions
{
    internal static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(30);
    public string? ResetUrl { get; set; }
}
