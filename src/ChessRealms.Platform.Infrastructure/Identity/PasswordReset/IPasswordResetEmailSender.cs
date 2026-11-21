using ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;

namespace ChessRealms.Platform.Infrastructure.Identity.PasswordReset;

internal interface IPasswordResetEmailSender
{
    Task<EmailSendResult> SendPasswordResetAsync(string recipient, string link, CancellationToken cancellationToken);
}
