using ChessRealms.Platform.Infrastructure.Identity.PasswordReset;

using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Options;

using MimeKit;

namespace ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;

public sealed class SmtpConfirmationEmailSender(IOptions<SmtpEmailOptions> options)
    : IConfirmationEmailSender, IPasswordResetEmailSender
{
    public Task<EmailSendResult> SendAsync(ConfirmationEmail email, CancellationToken cancellationToken) =>
        SendAsync(email.Recipient, "Confirm your ChessRealms email",
            $"Confirm your email address by opening this link:\n{email.ConfirmationLink}\n\nIf you did not create a ChessRealms account, ignore this message.",
            cancellationToken);

    Task<EmailSendResult> IPasswordResetEmailSender.SendPasswordResetAsync(string recipient, string link,
        CancellationToken cancellationToken) =>
        SendAsync(recipient, "Reset your ChessRealms password",
            $"Choose a new password by opening this link within 30 minutes:\n{link}\n\nIf you did not request a password reset, ignore this message.",
            cancellationToken);

    private async Task<EmailSendResult> SendAsync(string recipient, string subject, string body,
        CancellationToken cancellationToken)
    {
        SmtpEmailOptions settings = options.Value;
        MimeMessage message = new();
        message.From.Add(new MailboxAddress("ChessRealms", settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

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
