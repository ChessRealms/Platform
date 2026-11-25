namespace ChessRealms.Platform.Api.Controllers;

public sealed record GameBoardResponse(GameResponse Game, IReadOnlyList<string> Moves, IReadOnlyList<string> LegalMoves,
    IReadOnlyList<string> DrawClaims, GameBoardPlayerResponse WhitePlayer, GameBoardPlayerResponse BlackPlayer);

public sealed record GameBoardPlayerResponse(string? DisplayName, bool IsGuest);
