using ChessRealms.Platform.Application.Games;
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
    string? DrawOfferedBy,
    DateTimeOffset? InvitationExpiresAt)
{
    internal static GameResponse From(Game game, DateTimeOffset now)
    {
        bool expired = game.IsInvitationExpired(now);
        return From(game.Id, game.WhitePlayerId, game.BlackPlayerId, expired ? GameStatus.Expired : game.Status,
            expired ? game.Revision + 1 : game.Revision, game.Fen, game.SideToMove, game.Winner, game.EndReason,
            game.DrawOfferedBy, game.InvitationExpiresAt);
    }

    internal static GameResponse From(GameBoardSnapshot board) =>
        From(board.Id, board.WhitePlayerId, board.BlackPlayerId, board.Status, board.Revision, board.Fen, board.SideToMove,
            board.Winner, board.EndReason, board.DrawOfferedBy, board.InvitationExpiresAt);

    private static GameResponse From(Guid id, Guid whitePlayerId, Guid blackPlayerId, GameStatus status, long revision,
        string fen, PlayerColor sideToMove, PlayerColor? winner, GameEndReason? endReason, PlayerColor? drawOfferedBy,
        DateTimeOffset? invitationExpiresAt) =>
        new(id, whitePlayerId, blackPlayerId, status.ToString(), revision, fen,
            status == GameStatus.Active ? sideToMove.ToString() : null, winner?.ToString(), endReason?.ToString(),
            drawOfferedBy?.ToString(), invitationExpiresAt);
}
