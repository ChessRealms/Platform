using ChessRealms.Platform.Application.Games;

namespace ChessRealms.Platform.Api.Controllers;

public sealed record GameListResponse(IReadOnlyList<GameListItemResponse> Items, Guid? NextCursor)
{
    internal static GameListResponse From(GameListPage page) =>
        new(page.Items.Select(GameListItemResponse.From).ToArray(), page.NextCursor);
}

public sealed record GameListItemResponse(Guid Id, Guid OpponentId, string? OpponentName, string Color, string Status,
    DateTimeOffset? CreatedAt, bool IsYourTurn, string? Winner, string? EndReason)
{
    internal static GameListItemResponse From(GameListItem item) => new(item.Id, item.OpponentId, item.OpponentName,
        item.Color.ToString(), item.Status.ToString(), item.CreatedAt, item.IsYourTurn, item.Winner?.ToString(),
        item.EndReason?.ToString());
}
