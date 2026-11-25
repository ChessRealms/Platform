using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Api.Controllers;

public sealed record GameListResponse(IReadOnlyList<GameListItemResponse> Items, Guid? NextCursor, DateTimeOffset ServerTime)
{
    internal static GameListResponse From(GameListPage page, DateTimeOffset now) =>
        new(page.Items.Select(GameListItemResponse.From).ToArray(), page.NextCursor, now);
}

public sealed record GameListItemResponse(Guid Id, Guid? OpponentId, string? OpponentName, string Color, string Status,
    DateTimeOffset? CreatedAt, DateTimeOffset? InvitationExpiresAt, bool IsYourTurn, string? Winner, string? EndReason, bool IsLobby,
    string? TimeControl, bool OpponentIsGuest)
{
    internal static GameListItemResponse From(GameListItem item) => new(item.Id, item.OpponentId, item.OpponentName,
        item.Color.ToString(), item.Status.ToString(), item.CreatedAt, item.InvitationExpiresAt, item.IsYourTurn, item.Winner?.ToString(),
        item.EndReason?.ToString(), item.IsLobby, item.TimeControl?.ToString(), item.OpponentKind == GameParticipantKind.Guest);
}
