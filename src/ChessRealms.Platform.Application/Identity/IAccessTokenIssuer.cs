namespace ChessRealms.Platform.Application.Identity;

public interface IAccessTokenIssuer
{
    AccessToken Issue(Guid userId, Guid sessionId);
}
