using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Application.Games;

public sealed record GameBoardSnapshot(
    Guid Id,
    GameParticipant WhitePlayer,
    GameParticipant BlackPlayer,
    GameStatus Status,
    long Revision,
    string Fen,
    PlayerColor SideToMove,
    PlayerColor? Winner,
    GameEndReason? EndReason,
    PlayerColor? DrawOfferedBy,
    DateTimeOffset? InvitationExpiresAt,
    DateTimeOffset? FinishedAt,
    PlayerColor? RematchOfferedBy,
    Guid? RematchGameId,
    GameTimeControl? TimeControl,
    TimeSpan? WhiteTimeRemaining,
    TimeSpan? BlackTimeRemaining,
    DateTimeOffset? TurnDeadline,
    string? RulesRecord,
    string? WhiteGuestName,
    string? BlackGuestName)
{
    public string? WhiteDisplayName { get; init; }
    public string? BlackDisplayName { get; init; }
}
