using ChessRealms.Platform.Infrastructure.Identity.PasswordReset;

namespace ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;

// Replace this adapter when a provider is selected. Never report a simulated send as Sent.
public sealed class UnconfiguredConfirmationEmailSender : IConfirmationEmailSender, IPasswordResetEmailSender
{
    public Task<EmailSendResult> SendAsync(ConfirmationEmail email, CancellationToken cancellationToken) =>
        UnconfiguredAsync(cancellationToken);

    Task<EmailSendResult> IPasswordResetEmailSender.SendPasswordResetAsync(string recipient, string link,
        CancellationToken cancellationToken) => UnconfiguredAsync(cancellationToken);

    private static Task<EmailSendResult> UnconfiguredAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new EmailSendResult(false, EmailSendFailure.ProviderNotConfigured));
    }
}
