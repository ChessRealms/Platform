namespace ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;

public interface IConfirmationEmailSender
{
    Task<EmailSendResult> SendAsync(ConfirmationEmail email, CancellationToken cancellationToken);
}

