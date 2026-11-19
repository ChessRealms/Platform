namespace ChessRealms.Platform.Application.Identity;

public interface IRefreshTokenService
{
    Task<RefreshResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken);
}
