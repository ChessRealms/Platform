namespace ChessRealms.Platform.Application.Identity;

public interface IEmailConfirmationService
{
    Task<ConfirmationSessionResult> GetStatusAsync(string token, CancellationToken cancellationToken);
    Task<ConfirmationSessionResult> ResendAsync(string token, CancellationToken cancellationToken);
    Task<string> ConfirmAsync(Guid userId, string token, CancellationToken cancellationToken);
}
