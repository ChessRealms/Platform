namespace ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;

// Replace this adapter when a provider is selected. Never report a simulated send as Sent.
public sealed class UnconfiguredConfirmationEmailSender : IConfirmationEmailSender
{
    public Task<EmailSendResult> SendAsync(ConfirmationEmail email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new EmailSendResult(false, EmailSendFailure.ProviderNotConfigured));
    }
}
