namespace ChessRealms.Platform.Domain.Games;

public sealed record GameMoveTiming(Guid GameId, int Ply, DateTimeOffset PlayedAt,
    TimeSpan? WhiteTimeRemaining, TimeSpan? BlackTimeRemaining);
