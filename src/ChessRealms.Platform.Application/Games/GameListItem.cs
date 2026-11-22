using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed record GameListItem(Guid Id, Guid OpponentId, PlayerColor Color, GameStatus Status,
    DateTimeOffset? CreatedAt, bool IsYourTurn, PlayerColor? Winner, GameEndReason? EndReason)
{
    public string? OpponentName { get; init; }
}
