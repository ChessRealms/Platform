namespace ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;

public sealed record EmailSendResult(bool Accepted, EmailSendFailure? Failure = null);
