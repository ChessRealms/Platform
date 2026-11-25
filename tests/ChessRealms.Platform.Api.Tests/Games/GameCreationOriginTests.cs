using System.Net;
using System.Net.Http.Json;

using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace ChessRealms.Platform.Api.Tests.Games;

public sealed class GameCreationOriginTests : GameApiTestBase
{
    [Fact]
    public async Task BackfillPreservesCreationPathsAndInvitationQuotas()
    {
        (Guid firstId, string firstToken) = await RegisterAndSignInAsync("first");
        (_, string secondToken) = await RegisterAndSignInAsync("second");
        using HttpClient first = CreateClient(firstToken);
        using HttpClient second = CreateClient(secondToken);
        GameResponse match = await MatchQuickPlayAsync(first, second);
        HttpClient white = match.WhitePlayerId == firstId ? first : second;
        HttpClient black = match.WhitePlayerId == firstId ? second : first;
        Guid whiteId = match.WhitePlayerId;
        Guid blackId = match.BlackPlayerId;

        GameResponse rematch = await StartRematchAsync(match, white, black);
        GameResponse secondRematch = await StartRematchAsync(rematch, black, white);
        Assert.Equal((whiteId, blackId), (secondRematch.WhitePlayerId, secondRematch.BlackPlayerId));
        LobbyResponse lobby = await CreateLobbyAsync(white);
        using HttpResponseMessage join = await SendActionAsync(black, $"/api/lobbies/{lobby.Id}", "join", lobby.Revision);
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        LobbyResponse joined = Assert.IsType<LobbyResponse>(await join.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        Guid lobbyGameId = Assert.IsType<Guid>(joined.GameId);

        Dictionary<Guid, GameCreationOrigin> expectedOrigins = new()
        {
            [match.Id] = GameCreationOrigin.QuickPlay,
            [rematch.Id] = GameCreationOrigin.Rematch,
            [secondRematch.Id] = GameCreationOrigin.Rematch,
            [lobbyGameId] = GameCreationOrigin.PrivateLobby
        };
        await using AsyncServiceScope scope = CreateScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        GameResponse undated = await CreateGameAsync(white, blackId);
        await ApplyActionAsync(white, $"/api/games/{undated.Id}", "cancel", undated.Revision);
        // Games from before creation dates were recorded still need an origin.
        await database.Games.Where(game => game.Id == undated.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(game => game.CreatedAt, (DateTimeOffset?)null), Cancellation);
        expectedOrigins.Add(undated.Id, GameCreationOrigin.DirectInvitation);

        GameResponse firstInvitation = await CreateGameAsync(white, blackId);
        await AcceptGameAsync(black, $"/api/games/{firstInvitation.Id}");
        expectedOrigins.Add(firstInvitation.Id, GameCreationOrigin.DirectInvitation);
        List<Guid> directInWindow = [firstInvitation.Id];
        Clock.Advance(TimeSpan.FromMinutes(1));
        for (int i = 0; i < 3; i++)
        {
            GameResponse invitation = await CreateGameAsync(white, blackId);
            await ApplyActionAsync(white, $"/api/games/{invitation.Id}", "cancel", invitation.Revision);
            expectedOrigins.Add(invitation.Id, GameCreationOrigin.DirectInvitation);
            directInWindow.Add(invitation.Id);
        }
        await CreateLobbyAsync(white);
        await CreateLobbyAsync(white);
        await AssertOriginsAsync(database, expectedOrigins);

        // Drop the origin column to measure the old quota using the legacy schema.
        DateTimeOffset now = Clock.GetUtcNow();
        DateTimeOffset cutoff = now.AddMinutes(-5);
        await database.Database.MigrateAsync("20261124205356_QuickPlay", Cancellation);
        IQueryable<Game> pair = database.Games.Where(game =>
            game.WhitePlayerId == whiteId && game.BlackPlayerId == blackId && game.CreatedAt > cutoff);
        Guid[] legacyDirect = await pair
            .Where(game => !database.PrivateLobbies.Any(link => link.GameId == game.Id))
            .Where(game => !database.Games.Any(source => source.RematchGameId == game.Id))
            .Where(game => !database.QuickPlayTickets.Any(ticket => ticket.GameId == game.Id))
            .OrderBy(game => game.Id).Select(game => game.Id).ToArrayAsync(Cancellation);
        Assert.Equal(directInWindow.Order(), legacyDirect);
        int legacyOpen = await database.Games.CountAsync(game => game.WhitePlayerId == whiteId
                && game.Status == GameStatus.Pending && game.InvitationExpiresAt > now, Cancellation)
            + await database.PrivateLobbies.CountAsync(link => link.CreatorId == whiteId
                && link.Status == LobbyStatus.Open && link.ExpiresAt > now, Cancellation);
        Assert.Equal(2, legacyOpen);

        await database.Database.MigrateAsync(Cancellation);
        await AssertOriginsAsync(database, expectedOrigins);
        Assert.Equal(rematch.Id, await database.Games.Where(game => game.Id == match.Id)
            .Select(game => game.RematchGameId).SingleAsync(Cancellation));
        Assert.Equal(secondRematch.Id, await database.Games.Where(game => game.Id == rematch.Id)
            .Select(game => game.RematchGameId).SingleAsync(Cancellation));
        Assert.Equal(legacyDirect, await pair.Where(game => game.CreationOrigin == GameCreationOrigin.DirectInvitation)
            .OrderBy(game => game.Id).Select(game => game.Id).ToArrayAsync(Cancellation));

        LobbyResponse thirdOpen = await CreateLobbyAsync(white);
        using HttpResponseMessage full = await white.PostAsJsonAsync("/api/games", new CreateGameRequest(blackId), Cancellation);
        await AssertErrorAsync(full, HttpStatusCode.Conflict, "InvitationLimitReached");
        using HttpResponseMessage cancel = await SendActionAsync(white, $"/api/lobbies/{thirdOpen.Id}", "cancel", thirdOpen.Revision);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        GameResponse fifth = await CreateGameAsync(white, blackId);
        await ApplyActionAsync(white, $"/api/games/{fifth.Id}", "cancel", fifth.Revision);
        using HttpResponseMessage limited = await white.PostAsJsonAsync("/api/games", new CreateGameRequest(blackId), Cancellation);
        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "InvitationRateLimited");
        Assert.Equal(TimeSpan.FromMinutes(4), limited.Headers.RetryAfter?.Delta);
        Clock.Advance(TimeSpan.FromMinutes(4));
        await CreateGameAsync(white, blackId);
    }

    private async Task<GameResponse> MatchQuickPlayAsync(HttpClient first, HttpClient second)
    {
        using HttpResponseMessage queued = await first.PostAsJsonAsync("/api/quick-play", new JoinQuickPlayRequest(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        QuickPlayResponse waiting = Assert.IsType<QuickPlayResponse>(await queued.Content.ReadFromJsonAsync<QuickPlayResponse>(Cancellation));
        Assert.Equal("Waiting", waiting.Status);

        using HttpResponseMessage matched = await second.PostAsJsonAsync("/api/quick-play", new JoinQuickPlayRequest(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);
        QuickPlayResponse ticket = Assert.IsType<QuickPlayResponse>(await matched.Content.ReadFromJsonAsync<QuickPlayResponse>(Cancellation));
        Guid gameId = Assert.IsType<Guid>(ticket.GameId);
        return (await ReadBoardAsync(second, $"/api/games/{gameId}")).Game;
    }

    private async Task<GameResponse> StartRematchAsync(GameResponse game, HttpClient white, HttpClient black)
    {
        string url = $"/api/games/{game.Id}";
        GameResponse finished = await ApplyActionAsync(white, url, "resign", game.Revision);
        GameResponse offered = await ApplyActionAsync(white, url, "rematch/offer", finished.Revision);
        GameResponse accepted = await ApplyActionAsync(black, url, "rematch/accept", offered.Revision);
        Guid rematchId = Assert.IsType<Guid>(accepted.RematchGameId);
        return (await ReadBoardAsync(black, $"/api/games/{rematchId}")).Game;
    }

    private async Task AssertOriginsAsync(PlatformDbContext database, Dictionary<Guid, GameCreationOrigin> expected)
    {
        Dictionary<Guid, GameCreationOrigin> origins = await database.Games
            .Select(game => new { game.Id, game.CreationOrigin })
            .ToDictionaryAsync(game => game.Id, game => game.CreationOrigin, Cancellation);
        Assert.Equal(expected.OrderBy(item => item.Key), origins.OrderBy(item => item.Key));
    }
}
