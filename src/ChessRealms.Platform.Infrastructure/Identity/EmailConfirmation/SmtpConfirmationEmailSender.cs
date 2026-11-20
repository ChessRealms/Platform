using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Options;

using MimeKit;

namespace ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;

public sealed class SmtpConfirmationEmailSender(IOptions<SmtpEmailOptions> options) : IConfirmationEmailSender
{
    public async Task<EmailSendResult> SendAsync(ConfirmationEmail email, CancellationToken cancellationToken)
    {
        SmtpEmailOptions settings = options.Value;
        MimeMessage message = new();
        message.From.Add(new MailboxAddress("ChessRealms", settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(email.Recipient));
        message.Subject = "Confirm your ChessRealms email";
        message.Body = new TextPart("plain")
        {
            Text = $"Confirm your email address by opening this link:\n{email.ConfirmationLink}\n\nIf you did not create a ChessRealms account, ignore this message."
        };

        using SmtpClient client = new();
        SecureSocketOptions security = settings.Security switch
        {
            SmtpTransportSecurity.None => SecureSocketOptions.None,
            SmtpTransportSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpTransportSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => throw new InvalidOperationException("SMTP security mode is invalid.")
        };
        await client.ConnectAsync(settings.Host, settings.Port, security, cancellationToken);
        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password!, cancellationToken);
        }
        SendResult result = await client.SendAsync(FormatOptions.Default, new SmtpSendRequest(message), cancellationToken);
        bool accepted = result.AcceptedRecipients.Count == 1 && result.RejectedRecipients.Count == 0;
        return accepted ? new(true) : new(false, EmailSendFailure.ProviderFailure);
    }
}
