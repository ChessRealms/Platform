using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Api.Controllers;

public sealed record GameResponse(
    Guid Id,
    Guid WhitePlayerId,
    Guid BlackPlayerId,
    string Status,
    long Revision,
    string Fen,
    string? SideToMove,
    string? Winner,
    string? EndReason,
    string? DrawOfferedBy)
{
    internal static GameResponse From(Game game) => new(
        game.Id,
        game.WhitePlayerId,
        game.BlackPlayerId,
        game.Status.ToString(),
        game.Revision,
        game.Fen,
        game.Status == GameStatus.Active ? game.SideToMove.ToString() : null,
        game.Winner?.ToString(),
        game.EndReason?.ToString(),
        game.DrawOfferedBy?.ToString());
}
