namespace ChessRealms.Platform.Application.Games;

public interface IGamePlayerDirectory
{
    Task<bool> IsEligibleAsync(Guid playerId, CancellationToken cancellationToken);
    Task<bool> AreEligibleAsync(Guid whitePlayerId, Guid blackPlayerId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken);
}
