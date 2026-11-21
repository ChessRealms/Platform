namespace ChessRealms.Platform.Application.Identity;

public interface IPasswordResetService
{
    Task RequestAsync(string email, CancellationToken cancellationToken);
    Task<PasswordResetResult> ResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken);
}
