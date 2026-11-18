namespace ChessRealms.Platform.Infrastructure.Identity.Jwt;

public sealed class JwtOptions
{
    public const string SectionName = "Identity:Jwt";

    public string Issuer { get; set; } = "chessrealms-platform";
    public string Audience { get; set; } = "chessrealms-platform-api";
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
    public string SigningKeyId { get; set; } = "";
    public string PrivateKeyPem { get; set; } = "";
}
