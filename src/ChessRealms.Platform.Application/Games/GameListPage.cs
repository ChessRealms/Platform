namespace ChessRealms.Platform.Application.Games;

public sealed record GameListPage(IReadOnlyList<GameListItem> Items, Guid? NextCursor);
