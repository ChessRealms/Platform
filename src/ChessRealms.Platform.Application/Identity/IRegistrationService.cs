namespace ChessRealms.Platform.Application.Identity;

public interface IRegistrationService
{
    Task<RegistrationResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
}
