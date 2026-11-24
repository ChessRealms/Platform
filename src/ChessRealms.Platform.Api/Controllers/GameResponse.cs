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
    DateTimeOffset? InvitationExpiresAt,
    DateTimeOffset? RematchExpiresAt,
    string? RematchOfferedBy,
    Guid? RematchGameId,
    GameClockResponse? Clock)
{
    internal static GameResponse From(Game game, DateTimeOffset now)
    {
        bool expired = game.IsInvitationExpired(now);
        return From(game.Id, game.WhitePlayerId, game.BlackPlayerId, expired ? GameStatus.Expired : game.Status,
            expired ? game.Revision + 1 : game.Revision, game.Fen, game.SideToMove, game.Winner, game.EndReason,
            game.DrawOfferedBy, game.InvitationExpiresAt, game.FinishedAt, game.RematchOfferedBy, game.RematchGameId,
            GameClockResponse.From(game.TimeControl, game.WhiteTimeRemaining, game.BlackTimeRemaining, game.TurnDeadline, now));
    }

    internal static GameResponse From(GameBoardSnapshot board, DateTimeOffset now) =>
        From(board.Id, board.WhitePlayerId, board.BlackPlayerId, board.Status, board.Revision, board.Fen, board.SideToMove,
            board.Winner, board.EndReason, board.DrawOfferedBy, board.InvitationExpiresAt,
            board.FinishedAt, board.RematchOfferedBy, board.RematchGameId,
            GameClockResponse.From(board.TimeControl, board.WhiteTimeRemaining, board.BlackTimeRemaining, board.TurnDeadline, now));

    private static GameResponse From(Guid id, Guid whitePlayerId, Guid blackPlayerId, GameStatus status, long revision,
        string fen, PlayerColor sideToMove, PlayerColor? winner, GameEndReason? endReason, PlayerColor? drawOfferedBy,
        DateTimeOffset? invitationExpiresAt, DateTimeOffset? finishedAt, PlayerColor? rematchOfferedBy, Guid? rematchGameId,
        GameClockResponse? clock) =>
        new(id, whitePlayerId, blackPlayerId, status.ToString(), revision, fen,
            status == GameStatus.Active ? sideToMove.ToString() : null, winner?.ToString(), endReason?.ToString(),
            drawOfferedBy?.ToString(), invitationExpiresAt, finishedAt + Game.RematchWindow,
            rematchOfferedBy?.ToString(), rematchGameId, clock);
}

public sealed record GameClockResponse(string TimeControl, double WhiteMilliseconds, double BlackMilliseconds,
    DateTimeOffset? TurnDeadline, DateTimeOffset ServerTime)
{
    internal static GameClockResponse? From(GameTimeControl? control, TimeSpan? white, TimeSpan? black,
        DateTimeOffset? deadline, DateTimeOffset now) =>
        control is null ? null : new(control.Value.ToString(), white!.Value.TotalMilliseconds, black!.Value.TotalMilliseconds,
            deadline, now);
}
