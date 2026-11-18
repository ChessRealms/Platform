namespace ChessRealms.Platform.Application.Identity;

public interface ILoginService
{
    Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
}
