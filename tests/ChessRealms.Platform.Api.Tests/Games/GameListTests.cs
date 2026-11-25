using System.Net;
using System.Net.Http.Json;

using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace ChessRealms.Platform.Api.Tests.Games;

public sealed class GameListTests : GameApiTestBase
{
    [Fact]
    public async Task MyGamesPaginatesAcrossALobbyCursorThatHasClosed()
    {
        (Guid hostId, string hostToken) = await RegisterAndSignInAsync("host");
        (Guid guestId, _) = await RegisterAndSignInAsync("guest");
        using HttpClient host = CreateClient(hostToken);
        await using AsyncServiceScope scope = CreateScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        DateTimeOffset now = Clock.GetUtcNow();
        Game direct = new(hostId, guestId, scope.ServiceProvider.GetRequiredService<IGameRules>().StartingPosition(), now.AddSeconds(-1));
        database.Games.Add(direct);
        database.PrivateLobbies.AddRange(Enumerable.Range(0, 20).Select(_ => new PrivateLobby(hostId, Guid.NewGuid(), now)));
        await database.SaveChangesAsync(Cancellation);

        GameListResponse first = await ListGamesAsync(host);
        Assert.Equal(20, first.Items.Count);
        Assert.All(first.Items, item => Assert.True(item.IsLobby));
        Guid cursor = Assert.IsType<Guid>(first.NextCursor);
        using HttpResponseMessage cancel = await host.PostAsJsonAsync($"/api/lobbies/{cursor}/cancel",
            new GameRevisionRequest(0), Cancellation);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        GameListResponse next = await ListGamesAsync(host, $"?before={cursor}");
        Assert.Equal(direct.Id, Assert.Single(next.Items).Id);
        Assert.Null(next.NextCursor);
        GameListItemResponse closed = Assert.Single((await ListGamesAsync(host, "?status=ClosedInvitations")).Items);
        Assert.Equal(cursor, closed.Id);
        Assert.Equal("Cancelled", closed.Status);
        Assert.True(closed.IsLobby);
    }

    [Fact]
    public async Task MyGamesListsOwnInvitationsActiveGamesAndResults()
    {
        (Guid playerId, string playerToken) = await RegisterAndSignInAsync("listing-player");
        (Guid opponentId, string opponentToken) = await RegisterAndSignInAsync("listing-opponent");
        (_, string outsiderToken) = await RegisterAndSignInAsync("listing-outsider");
        using HttpClient player = CreateClient(playerToken);
        using HttpClient opponent = CreateClient(opponentToken);
        using HttpClient outsider = CreateClient(outsiderToken);
        using HttpClient anonymous = CreateClient();

        GameResponse incoming = await CreateGameAsync(opponent, playerId);
        GameResponse outgoing = await CreateGameAsync(player, opponentId);
        GameResponse unrelated = await CreateGameAsync(outsider, opponentId);

        using HttpResponseMessage unauthorized = await anonymous.GetAsync("/api/games", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using HttpResponseMessage invalid = await player.GetAsync("/api/games?status=99", Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty((await ListGamesAsync(player, $"?before={unrelated.Id}")).Items);
        Assert.Equal(unrelated.Id, Assert.Single((await ListGamesAsync(outsider)).Items).Id);

        GameListResponse pending = await ListGamesAsync(player);
        Assert.Null(pending.NextCursor);
        Assert.Equal(2, pending.Items.Count);
        Assert.Contains(pending.Items, item => item.Id == incoming.Id && item.Color == "Black");
        Assert.Contains(pending.Items, item => item.Id == outgoing.Id && item.Color == "White");
        Assert.All(pending.Items, item =>
        {
            Assert.Equal(opponentId, item.OpponentId);
            Assert.Equal("listing-opponent", item.OpponentName);
            Assert.Equal("Pending", item.Status);
            Assert.NotNull(item.CreatedAt);
            Assert.False(item.IsYourTurn);
        });

        string gameUrl = $"/api/games/{incoming.Id}";
        GameResponse active = await AcceptGameAsync(player, gameUrl);
        Assert.Equal(outgoing.Id, Assert.Single((await ListGamesAsync(player)).Items).Id);
        GameListItemResponse item = Assert.Single((await ListGamesAsync(player, "?status=Active")).Items);
        Assert.Equal(active.Id, item.Id);
        Assert.Equal("Active", item.Status);
        Assert.False(item.IsYourTurn);
        Assert.True(Assert.Single((await ListGamesAsync(opponent, "?status=Active")).Items).IsYourTurn);

        using HttpResponseMessage resigned = await player.PostAsJsonAsync($"{gameUrl}/resign",
            new GameRevisionRequest(active.Revision), Cancellation);
        Assert.Equal(HttpStatusCode.OK, resigned.StatusCode);
        Assert.Empty((await ListGamesAsync(player, "?status=Active")).Items);
        GameListItemResponse finished = Assert.Single((await ListGamesAsync(player, "?status=Finished")).Items);
        Assert.Equal(incoming.Id, finished.Id);
        Assert.Equal("Finished", finished.Status);
        Assert.Equal("White", finished.Winner);
        Assert.Equal("Resignation", finished.EndReason);
    }

    [Fact]
    public async Task MyGamesPaginatesTiedDatesAndLegacyGamesAfterTheCursorChangesStatus()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("listing-white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("listing-black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        DateTimeOffset createdAt = Clock.GetUtcNow();
        Game[] dated;
        Game[] legacy;
        await using (AsyncServiceScope scope = CreateScope())
        {
            GamePosition startingPosition = scope.ServiceProvider.GetRequiredService<IGameRules>().StartingPosition();
            PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            dated = Enumerable.Range(0, 21).Select(_ => new Game(whiteId, blackId, startingPosition, createdAt)).ToArray();
            legacy = Enumerable.Range(0, 20).Select(_ => new Game(whiteId, blackId, startingPosition, createdAt)).ToArray();
            database.Games.AddRange(dated);
            database.Games.AddRange(legacy);
            foreach (Game game in legacy)
            {
                database.Entry(game).Property(item => item.CreatedAt).CurrentValue = null;
            }
            await database.SaveChangesAsync(Cancellation);
        }

        GameListResponse first = await ListGamesAsync(white);
        Assert.Equal(20, first.Items.Count);
        Assert.Equal(first.Items[^1].Id, first.NextCursor);
        await AcceptGameAsync(black, $"/api/games/{first.NextCursor}");

        GameListResponse second = await ListGamesAsync(white, $"?before={first.NextCursor}");
        Assert.Equal(20, second.Items.Count);
        Assert.Equal(second.Items[^1].Id, second.NextCursor);
        GameListResponse third = await ListGamesAsync(white, $"?before={second.NextCursor}");
        Assert.Null(third.NextCursor);
        Assert.Null(Assert.Single(third.Items).CreatedAt);

        GameListItemResponse[] all = [.. first.Items, .. second.Items, .. third.Items];
        Assert.Equal(dated.OrderByDescending(game => game.Id).Select(game => game.Id)
            .Concat(legacy.OrderByDescending(game => game.Id).Select(game => game.Id)), all.Select(game => game.Id));
        Assert.All(all.Take(21), game => Assert.Equal(createdAt, game.CreatedAt));
        Assert.All(all.Skip(21), game => Assert.Null(game.CreatedAt));
    }
}
