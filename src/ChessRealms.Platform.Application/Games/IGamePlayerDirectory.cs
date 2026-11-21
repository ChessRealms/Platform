namespace ChessRealms.Platform.Application.Games;

public interface IGamePlayerDirectory
{
    Task<bool> AreEligibleAsync(Guid whitePlayerId, Guid blackPlayerId, CancellationToken cancellationToken);
}
