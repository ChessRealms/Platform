namespace ChessRealms.Platform.Infrastructure.Identity.Sessions;

public sealed class RefreshTokenOptions
{
    public const string SectionName = "Identity:RefreshTokens";

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);
}
