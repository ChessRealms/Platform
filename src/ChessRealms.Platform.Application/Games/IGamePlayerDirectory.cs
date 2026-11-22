namespace ChessRealms.Platform.Application.Games;

public interface IGamePlayerDirectory
{
    Task<bool> AreEligibleAsync(Guid whitePlayerId, Guid blackPlayerId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken);
}
