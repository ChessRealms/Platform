namespace ChessRealms.Platform.Api.Controllers;

public sealed record GameBoardResponse(GameResponse Game, IReadOnlyList<string> Moves, IReadOnlyList<string> LegalMoves);
