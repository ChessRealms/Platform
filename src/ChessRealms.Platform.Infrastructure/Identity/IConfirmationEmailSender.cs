namespace ChessRealms.Platform.Infrastructure.Identity;

public sealed record ConfirmationEmail(Guid DeliveryId, string Recipient, string ConfirmationLink);
public sealed record EmailSendResult(bool Accepted, string? FailureCode = null);

public interface IConfirmationEmailSender
{
    Task<EmailSendResult> SendAsync(ConfirmationEmail email, CancellationToken cancellationToken);
}

// Replace this adapter when a provider is selected. Never report a simulated send as Sent.
public sealed class UnconfiguredConfirmationEmailSender : IConfirmationEmailSender
{
    public Task<EmailSendResult> SendAsync(ConfirmationEmail email, CancellationToken cancellationToken) =>
        Task.FromResult(new EmailSendResult(false, "ProviderNotConfigured"));
}
