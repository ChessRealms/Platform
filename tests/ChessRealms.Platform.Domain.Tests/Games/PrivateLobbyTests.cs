using ChessRealms.Platform.Domain.Games;

using Xunit;

namespace ChessRealms.Platform.Domain.Tests.Games;

public sealed class PrivateLobbyTests
{
    [Fact]
    public void LobbyRejectsJoinAndCancelAtTheDeadline()
    {
        DateTimeOffset createdAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        PrivateLobby lobby = new(Guid.NewGuid(), Guid.NewGuid(), createdAt);
        GameParticipant joiner = new(Guid.NewGuid(), false);
        GamePosition startingPosition = new("initial record",
            "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", PlayerColor.White);

        Assert.Equal(LobbyStatus.Open, lobby.StatusAt(lobby.ExpiresAt.AddTicks(-1)));
        Assert.Equal(LobbyStatus.Expired, lobby.StatusAt(lobby.ExpiresAt));
        Assert.Equal(GameError.InvitationExpired, lobby.Join(joiner, null, 0, lobby.ExpiresAt, startingPosition, out Game? game));
        Assert.Equal(GameError.InvitationExpired, lobby.Cancel(lobby.CreatorId, 0, lobby.ExpiresAt));
        Assert.Null(game);
        Assert.Null(lobby.GameId);
        Assert.Null(lobby.JoinedPlayerId);
        Assert.Equal(0, lobby.Revision);
    }
}
