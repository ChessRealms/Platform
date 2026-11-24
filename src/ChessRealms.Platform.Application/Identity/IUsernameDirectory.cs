namespace ChessRealms.Platform.Application.Identity;

public interface IUsernameDirectory
{
    Task<Guid?> FindIdAsync(string username, CancellationToken cancellationToken);
    Task<string?> FindUsernameAsync(Guid userId, CancellationToken cancellationToken);
}
