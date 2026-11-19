namespace ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;

// Only application-owned codes may leave the provider adapter. Never expose provider messages.
public enum EmailSendFailure
{
    ProviderNotConfigured,
    ProviderFailure
}
