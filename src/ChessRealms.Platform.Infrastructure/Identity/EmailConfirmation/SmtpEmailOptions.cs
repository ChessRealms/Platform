namespace ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;

public enum SmtpTransportSecurity
{
    None,
    StartTls,
    SslOnConnect
}

public sealed class SmtpEmailOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public SmtpTransportSecurity Security { get; set; } = SmtpTransportSecurity.StartTls;
    public string? Username { get; set; }
    public string? Password { get; set; }
}
