namespace ChessRealms.Platform.Application.Identity;

public interface ILoginSessionService
{
    Task<SessionListResult> ListAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken);
    Task<SessionOperationResult> LogoutAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken);
    Task<SessionOperationResult> RevokeAsync(Guid userId, Guid currentSessionId, Guid sessionId, CancellationToken cancellationToken);
    Task<SessionOperationResult> LogoutAllAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken);
}
