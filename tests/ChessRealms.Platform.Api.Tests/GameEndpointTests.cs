using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using ChessRealms.Platform.Api;
using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Api.Realtime;
using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure;
using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Testcontainers.PostgreSql;

using Xunit;

namespace ChessRealms.Platform.Api.Tests;

public sealed class GameEndpointTests : IAsyncLifetime
{
    private const string Password = "Synthetic-player-password9!";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.3").Build();
    private readonly RSA signingKey = RSA.Create(2048);
    private readonly TestClock clock = new();
    private WebApplication? app;
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync(Cancellation);
        app = BuildApp();
        await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync(Cancellation);
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync(Cancellation);
        }
        await app.StartAsync(Cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.DisposeAsync();
        }
        signingKey.Dispose();
        await postgres.DisposeAsync();
    }

    [Fact]
    public async Task SimultaneousLobbyJoinsCreateOneGameAndTheWinnerCanRetryAfterExpiry()
    {
        (Guid hostId, string hostToken) = await RegisterAndSignInAsync("host");
        (Guid guestId, string guestToken) = await RegisterAndSignInAsync("guest");
        (Guid otherId, string otherToken) = await RegisterAndSignInAsync("other");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient guest = CreateClient(guestToken);
        using HttpClient other = CreateClient(otherToken);
        using HttpClient anonymous = CreateClient();
        LobbyResponse lobby = await CreateLobbyAsync(host);
        string url = $"/api/lobbies/{lobby.Id}";

        using HttpResponseMessage anonymousRead = await anonymous.GetAsync(url, Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
        LobbyResponse preview = Assert.IsType<LobbyResponse>(await guest.GetFromJsonAsync<LobbyResponse>(url, Cancellation));
        Assert.Equal("host", preview.HostName);
        Assert.False(preview.IsOwner);
        Assert.Equal("Open", preview.Status);
        Assert.Null(preview.GameId);
        Assert.Empty((await ListGamesAsync(guest)).Items);
        Assert.Empty((await ListGamesAsync(host, "?status=Active")).Items);
        GameListItemResponse invitation = Assert.Single((await ListGamesAsync(host)).Items);
        Assert.Equal(lobby.Id, invitation.Id);
        Assert.True(invitation.IsLobby);
        Assert.Null(invitation.OpponentId);
        using HttpResponseMessage selfJoin = await host.PostAsJsonAsync($"{url}/join", new GameRevisionRequest(0), Cancellation);
        await AssertErrorAsync(selfJoin, HttpStatusCode.Forbidden, "Forbidden");
        using HttpResponseMessage guestCancel = await guest.PostAsJsonAsync($"{url}/cancel", new GameRevisionRequest(0), Cancellation);
        await AssertErrorAsync(guestCancel, HttpStatusCode.Forbidden, "Forbidden");

        GameRevisionRequest revision = new(lobby.Revision);
        Task<HttpResponseMessage> first = guest.PostAsJsonAsync($"{url}/join", revision, Cancellation);
        Task<HttpResponseMessage> second = other.PostAsJsonAsync($"{url}/join", revision, Cancellation);
        using HttpResponseMessage guestResponse = await first;
        using HttpResponseMessage otherResponse = await second;
        HttpResponseMessage[] responses = [guestResponse, otherResponse];
        HttpResponseMessage success = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        await AssertErrorAsync(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict, "LobbyAlreadyJoined");
        LobbyResponse joined = Assert.IsType<LobbyResponse>(await success.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        Assert.Equal("Joined", joined.Status);
        Assert.Equal(1, joined.Revision);
        Guid gameId = Assert.IsType<Guid>(joined.GameId);
        HttpClient winner = success == guestResponse ? guest : other;
        HttpClient loser = success == guestResponse ? other : guest;

        clock.Advance(TimeSpan.FromMinutes(5));
        using HttpResponseMessage retry = await winner.PostAsJsonAsync($"{url}/join", revision, Cancellation);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(joined, await retry.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        LobbyResponse hostView = Assert.IsType<LobbyResponse>(await host.GetFromJsonAsync<LobbyResponse>(url, Cancellation));
        Assert.Equal(gameId, hostView.GameId);
        using HttpResponseMessage loserRead = await loser.GetAsync(url, Cancellation);
        await AssertErrorAsync(loserRead, HttpStatusCode.Conflict, "LobbyAlreadyJoined");
        using HttpResponseMessage loserBoard = await loser.GetAsync($"/api/games/{gameId}/board", Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, loserBoard.StatusCode);

        GameResponse active = (await ReadBoardAsync(winner, $"/api/games/{gameId}")).Game;
        Assert.Equal((hostId, success == guestResponse ? guestId : otherId), (active.WhitePlayerId, active.BlackPlayerId));
        Assert.Equal("Active", active.Status);
        Assert.Equal(0, active.Revision);
        Assert.Equal(gameId, Assert.Single((await ListGamesAsync(host, "?status=Active")).Items).Id);
        Assert.Empty((await ListGamesAsync(host)).Items);
        Assert.Empty((await ListGamesAsync(host, "?status=ClosedInvitations")).Items);
        GameResponse moved = await MakeMoveAsync(host, $"/api/games/{gameId}", "e2e4", active.Revision);
        Assert.Equal(moved, (await ReadBoardAsync(winner, $"/api/games/{gameId}")).Game);
    }

    [Fact]
    public async Task JoiningAndCancellingTheSameLobbyCommitOnlyOneOutcome()
    {
        (_, string hostToken) = await RegisterAndSignInAsync("host");
        (_, string guestToken) = await RegisterAndSignInAsync("guest");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient guest = CreateClient(guestToken);
        LobbyResponse lobby = await CreateLobbyAsync(host);
        string url = $"/api/lobbies/{lobby.Id}";
        GameRevisionRequest revision = new(lobby.Revision);

        Task<HttpResponseMessage> join = guest.PostAsJsonAsync($"{url}/join", revision, Cancellation);
        Task<HttpResponseMessage> cancel = host.PostAsJsonAsync($"{url}/cancel", revision, Cancellation);
        using HttpResponseMessage joinResponse = await join;
        using HttpResponseMessage cancelResponse = await cancel;
        HttpResponseMessage[] responses = [joinResponse, cancelResponse];
        HttpResponseMessage success = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        await AssertErrorAsync(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict, "RevisionConflict");
        LobbyResponse committed = Assert.IsType<LobbyResponse>(await success.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        Assert.Equal(1, committed.Revision);
        Assert.Equal(success == joinResponse ? "Joined" : "Cancelled", committed.Status);
        Assert.Equal(committed with { IsOwner = true }, await host.GetFromJsonAsync<LobbyResponse>(url, Cancellation));
        Assert.Equal(committed with { IsOwner = false }, await guest.GetFromJsonAsync<LobbyResponse>(url, Cancellation));
        Assert.Equal(success == joinResponse ? 1 : 0, (await ListGamesAsync(host, "?status=Active")).Items.Count);
    }

    [Fact]
    public async Task LobbiesShareTheOutgoingQuotaAndExpireBeforeJoinOrCancel()
    {
        (_, string hostToken) = await RegisterAndSignInAsync("host");
        (Guid guestId, string guestToken) = await RegisterAndSignInAsync("guest");
        (Guid otherId, _) = await RegisterAndSignInAsync("other");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient guest = CreateClient(guestToken);
        GameResponse direct = await CreateGameAsync(host, guestId);
        CreateLobbyRequest request = new(Guid.NewGuid());
        LobbyResponse first = await CreateLobbyAsync(host, request);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), first.ExpiresAt);

        Task<HttpResponseMessage> nextLink = host.PostAsJsonAsync("/api/lobbies", new CreateLobbyRequest(Guid.NewGuid()), Cancellation);
        Task<HttpResponseMessage> nextDirect = host.PostAsJsonAsync("/api/games", new CreateGameRequest(otherId), Cancellation);
        using HttpResponseMessage next = await nextLink;
        using HttpResponseMessage another = await nextDirect;
        HttpResponseMessage[] responses = [next, another];
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        await AssertErrorAsync(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict, "InvitationLimitReached");
        GameListResponse invitations = await ListGamesAsync(host);
        Assert.Equal(3, invitations.Items.Count);
        Assert.Contains(invitations.Items, item => !item.IsLobby && item.Id == direct.Id);
        using HttpResponseMessage replay = await host.PostAsJsonAsync("/api/lobbies", request, Cancellation);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(first, await replay.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));

        clock.Advance(TimeSpan.FromMinutes(5));
        string url = $"/api/lobbies/{first.Id}";
        using HttpResponseMessage join = await guest.PostAsJsonAsync($"{url}/join", new GameRevisionRequest(0), Cancellation);
        await AssertErrorAsync(join, HttpStatusCode.Gone, "InvitationExpired");
        using HttpResponseMessage cancel = await host.PostAsJsonAsync($"{url}/cancel", new GameRevisionRequest(0), Cancellation);
        await AssertErrorAsync(cancel, HttpStatusCode.Gone, "InvitationExpired");
        LobbyResponse expired = Assert.IsType<LobbyResponse>(await guest.GetFromJsonAsync<LobbyResponse>(url, Cancellation));
        Assert.Equal("Expired", expired.Status);
        Assert.Null(expired.GameId);
        Assert.Empty((await ListGamesAsync(host)).Items);
        Assert.Equal(3, (await ListGamesAsync(host, "?status=ClosedInvitations")).Items.Count);
        await CreateLobbyAsync(host);
    }

    [Fact]
    public async Task JoinedLobbiesDoNotConsumeTheDirectInvitationWindow()
    {
        (_, string hostToken) = await RegisterAndSignInAsync("host");
        (Guid guestId, string guestToken) = await RegisterAndSignInAsync("guest");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient guest = CreateClient(guestToken);

        for (int i = 0; i < 5; i++)
        {
            LobbyResponse lobby = await CreateLobbyAsync(host);
            using HttpResponseMessage join = await guest.PostAsJsonAsync($"/api/lobbies/{lobby.Id}/join",
                new GameRevisionRequest(lobby.Revision), Cancellation);
            Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        }

        Assert.Equal(5, (await ListGamesAsync(host, "?status=Active")).Items.Count);
        await CreateGameAsync(host, guestId);
    }

    [Fact]
    public async Task MyGamesPaginatesAcrossALobbyCursorThatHasClosed()
    {
        (Guid hostId, string hostToken) = await RegisterAndSignInAsync("host");
        (Guid guestId, _) = await RegisterAndSignInAsync("guest");
        using HttpClient host = CreateClient(hostToken);
        await using AsyncServiceScope scope = app!.Services.CreateAsyncScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        DateTimeOffset now = clock.GetUtcNow();
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
    public async Task GameCreationReplaysAnInvitationForTheSameCreatorAndRequest()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (Guid otherId, string otherToken) = await RegisterAndSignInAsync("other");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        using HttpClient other = CreateClient(otherToken);
        CreateGameRequest request = new(blackId, Guid.NewGuid());

        Task<HttpResponseMessage> first = white.PostAsJsonAsync("/api/games", request, Cancellation);
        Task<HttpResponseMessage> second = white.PostAsJsonAsync("/api/games", request, Cancellation);
        using HttpResponseMessage firstResponse = await first;
        using HttpResponseMessage secondResponse = await second;
        HttpResponseMessage[] responses = [firstResponse, secondResponse];
        HttpResponseMessage created = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        HttpResponseMessage replayed = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        GameResponse pending = Assert.IsType<GameResponse>(await created.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(pending, await replayed.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        string url = $"/api/games/{pending.Id}";
        Assert.Equal(url, created.Headers.Location?.AbsolutePath);
        Assert.Equal(pending.Id, Assert.Single((await ListGamesAsync(white)).Items).Id);
        Assert.Equal(pending.Id, Assert.Single((await ListGamesAsync(black)).Items).Id);

        GameResponse active = await AcceptGameAsync(black, url);
        using HttpResponseMessage progressed = await white.PostAsJsonAsync("/api/games", request, Cancellation);
        Assert.Equal(HttpStatusCode.OK, progressed.StatusCode);
        Assert.Equal(active, await progressed.Content.ReadFromJsonAsync<GameResponse>(Cancellation));

        using HttpResponseMessage conflict = await white.PostAsJsonAsync("/api/games", request with { OpponentId = otherId }, Cancellation);
        await AssertErrorAsync(conflict, HttpStatusCode.Conflict, "CreationRequestConflict");
        using HttpResponseMessage invalid = await white.PostAsJsonAsync("/api/games", request with { RequestId = Guid.Empty }, Cancellation);
        await AssertErrorAsync(invalid, HttpStatusCode.BadRequest, "InvalidRequestId");

        using HttpResponseMessage newRequest = await white.PostAsJsonAsync("/api/games", request with { RequestId = Guid.NewGuid() }, Cancellation);
        Assert.Equal(HttpStatusCode.Created, newRequest.StatusCode);
        GameResponse another = Assert.IsType<GameResponse>(await newRequest.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.NotEqual(pending.Id, another.Id);
        using HttpResponseMessage otherCreator = await other.PostAsJsonAsync("/api/games", request, Cancellation);
        Assert.Equal(HttpStatusCode.Created, otherCreator.StatusCode);
        GameResponse independent = Assert.IsType<GameResponse>(await otherCreator.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(otherId, independent.WhitePlayerId);
        Assert.NotEqual(pending.Id, independent.Id);

        await AcceptGameAsync(black, $"/api/games/{another.Id}");
        GameResponse legacyFirst = await CreateGameAsync(white, blackId);
        await AcceptGameAsync(black, $"/api/games/{legacyFirst.Id}");
        GameResponse legacySecond = await CreateGameAsync(white, blackId);
        Assert.NotEqual(legacyFirst.Id, legacySecond.Id);
        Assert.Equal(whiteId, legacySecond.WhitePlayerId);
    }

    [Fact]
    public async Task InvitationsByUsernameResolveTheConfirmedAccountAndReplayAcrossSelectors()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("WhiteKnight");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("BlackKnight");
        (Guid otherId, _) = await RegisterAndSignInAsync("OtherKnight");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        Guid requestId = Guid.NewGuid();
        CreateGameRequest request = new(RequestId: requestId, OpponentUsername: " blackknight ");

        Task<HttpResponseMessage> first = white.PostAsJsonAsync("/api/games", request, Cancellation);
        Task<HttpResponseMessage> second = white.PostAsJsonAsync("/api/games", request with
        {
            OpponentUsername = "BLACKKNIGHT"
        }, Cancellation);
        using HttpResponseMessage firstResponse = await first;
        using HttpResponseMessage secondResponse = await second;
        HttpResponseMessage[] responses = [firstResponse, secondResponse];
        HttpResponseMessage created = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        HttpResponseMessage replayed = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        GameResponse pending = Assert.IsType<GameResponse>(await created.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(pending, await replayed.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(whiteId, pending.WhitePlayerId);
        Assert.Equal(blackId, pending.BlackPlayerId);
        Assert.Equal(GameStatus.Pending.ToString(), pending.Status);
        Assert.Equal(0, pending.Revision);
        string url = $"/api/games/{pending.Id}";
        Assert.Equal(url, created.Headers.Location?.AbsolutePath);
        GameListItemResponse invitation = Assert.Single((await ListGamesAsync(black)).Items);
        Assert.Equal(pending.Id, invitation.Id);
        Assert.Equal("WhiteKnight", invitation.OpponentName);
        Assert.Equal(pending.Id, Assert.Single((await ListGamesAsync(white)).Items).Id);

        GameResponse active = await AcceptGameAsync(black, url);
        using HttpResponseMessage replayById = await white.PostAsJsonAsync("/api/games", new CreateGameRequest(blackId, requestId), Cancellation);
        Assert.Equal(HttpStatusCode.OK, replayById.StatusCode);
        Assert.Equal(active, await replayById.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        using HttpResponseMessage replayByUsername = await white.PostAsJsonAsync("/api/games", request, Cancellation);
        Assert.Equal(HttpStatusCode.OK, replayByUsername.StatusCode);
        Assert.Equal(active, await replayByUsername.Content.ReadFromJsonAsync<GameResponse>(Cancellation));

        using HttpResponseMessage conflict = await white.PostAsJsonAsync("/api/games", request with { OpponentUsername = "OtherKnight" }, Cancellation);
        await AssertErrorAsync(conflict, HttpStatusCode.Conflict, "CreationRequestConflict");
        GameResponse legacy = await CreateGameAsync(white, otherId);
        Assert.NotEqual(pending.Id, legacy.Id);
    }

    [Fact]
    public async Task InvitationsByUsernameRejectUnavailableAndInvalidOpponents()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("WhiteKnight");
        await using (AsyncServiceScope scope = app!.Services.CreateAsyncScope())
        {
            IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            database.Users.Add(new PlatformUser { UserName = "UnconfirmedKnight", NormalizedUserName = "UNCONFIRMEDKNIGHT" });
            await database.SaveChangesAsync(Cancellation);
        }

        using HttpClient white = CreateClient(whiteToken);
        (CreateGameRequest Request, string Code)[] invalidRequests =
        [
            (new(OpponentUsername: "White"), "OpponentUnavailable"),
            (new(OpponentUsername: "UnknownKnight"), "OpponentUnavailable"),
            (new(OpponentUsername: "UnconfirmedKnight"), "OpponentUnavailable"),
            (new(OpponentUsername: "whiteknight"), "InvalidPlayers"),
            (new(OpponentUsername: "  "), "InvalidOpponentUsername"),
            (new(OpponentUsername: new string('a', 257)), "InvalidOpponentUsername"),
            (new(OpponentId: whiteId, OpponentUsername: "WhiteKnight"), "InvalidPlayers"),
            (new(), "InvalidPlayers"),
            (new(RequestId: Guid.Empty, OpponentUsername: "WhiteKnight"), "InvalidRequestId")
        ];
        foreach ((CreateGameRequest request, string code) in invalidRequests)
        {
            using HttpResponseMessage response = await white.PostAsJsonAsync("/api/games", request, Cancellation);
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, code);
        }

        Assert.Empty((await ListGamesAsync(white)).Items);
    }

    [Fact]
    public async Task ConcurrentInsertsWithTheSameCreationRequestReturnTheWinningGame()
    {
        Guid creator = Guid.NewGuid();
        Guid opponent = Guid.NewGuid();
        Guid requestId = Guid.NewGuid();
        await using AsyncServiceScope first = app!.Services.CreateAsyncScope();
        await using AsyncServiceScope second = app.Services.CreateAsyncScope();
        GamePosition position = first.ServiceProvider.GetRequiredService<IGameRules>().StartingPosition();
        IGameStore firstStore = first.ServiceProvider.GetRequiredService<IGameStore>();
        IGameStore secondStore = second.ServiceProvider.GetRequiredService<IGameStore>();

        CreateGameResult[] saved = await Task.WhenAll(firstStore.CreateAsync(creator, opponent, position, requestId, Cancellation),
            secondStore.CreateAsync(creator, opponent, position, requestId, Cancellation));

        Assert.Single(saved, result => result.Created);
        Assert.Equal(Assert.IsType<Game>(saved[0].Game).Id, Assert.IsType<Game>(saved[1].Game).Id);
        PlatformDbContext database = first.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Equal(1, await database.Games.CountAsync(game => game.WhitePlayerId == creator, Cancellation));
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
        DateTimeOffset createdAt = clock.GetUtcNow();
        Game[] dated;
        Game[] legacy;
        await using (AsyncServiceScope scope = app!.Services.CreateAsyncScope())
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

    [Fact]
    public async Task InvitationExpiryMigrationBackfillsPendingRows()
    {
        await using AsyncServiceScope scope = app!.Services.CreateAsyncScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        GamePosition position = scope.ServiceProvider.GetRequiredService<IGameRules>().StartingPosition();
        DateTimeOffset now = clock.GetUtcNow();
        Game dated = new(Guid.NewGuid(), Guid.NewGuid(), position, now.AddMinutes(-20));
        Game undated = new(Guid.NewGuid(), Guid.NewGuid(), position, now);
        Game active = new(Guid.NewGuid(), Guid.NewGuid(), position, now);
        Assert.Equal(GameError.None, active.Accept(active.BlackPlayerId, 0, now));
        database.Games.AddRange(dated, undated, active);
        database.Entry(undated).Property(game => game.CreatedAt).CurrentValue = null;
        await database.SaveChangesAsync(Cancellation);

        await database.Database.MigrateAsync("20261123105735_IdempotentGameCreation", Cancellation);
        DateTimeOffset before = await database.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT statement_timestamp() AS "Value" """).SingleAsync(Cancellation);
        await database.Database.MigrateAsync(Cancellation);
        DateTimeOffset after = await database.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT statement_timestamp() AS "Value" """).SingleAsync(Cancellation);
        Dictionary<Guid, Game> migrated = await database.Games.AsNoTracking().ToDictionaryAsync(game => game.Id, Cancellation);

        Assert.Equal(dated.CreatedAt?.AddMinutes(5), migrated[dated.Id].InvitationExpiresAt);
        Assert.Null(migrated[undated.Id].CreatedAt);
        Assert.InRange(Assert.IsType<DateTimeOffset>(migrated[undated.Id].InvitationExpiresAt),
            before.AddMinutes(5), after.AddMinutes(5));
        Assert.Equal(GameStatus.Active, migrated[active.Id].Status);
        Assert.Null(migrated[active.Id].InvitationExpiresAt);
    }

    [Fact]
    public async Task InvitationExpiresAtTheDeadlineInEveryReadAndAction()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        CreateGameRequest request = new(blackId, Guid.NewGuid());
        using HttpResponseMessage created = await white.PostAsJsonAsync("/api/games", request, Cancellation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        GameResponse pending = Assert.IsType<GameResponse>(await created.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), pending.InvitationExpiresAt);
        string url = $"/api/games/{pending.Id}";
        string boardUrl = $"{url}/board?afterRevision={pending.Revision}";

        clock.Advance(TimeSpan.FromMilliseconds(299999));
        using HttpResponseMessage unchanged = await black.GetAsync(boardUrl, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);
        Assert.Single((await ListGamesAsync(black)).Items);
        Assert.Empty((await ListGamesAsync(black, "?status=ClosedInvitations")).Items);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        using HttpResponseMessage changed = await black.GetAsync(boardUrl, Cancellation);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        GameBoardResponse board = Assert.IsType<GameBoardResponse>(await changed.Content.ReadFromJsonAsync<GameBoardResponse>(Cancellation));
        GameResponse expired = pending with { Status = "Expired", Revision = pending.Revision + 1 };
        Assert.Equal(expired, board.Game);
        Assert.Empty(board.LegalMoves);
        using HttpResponseMessage upToDate = await black.GetAsync($"{url}/board?afterRevision={expired.Revision}", Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, upToDate.StatusCode);

        foreach (HttpClient participant in new[] { white, black })
        {
            Assert.Empty((await ListGamesAsync(participant)).Items);
            GameListItemResponse item = Assert.Single((await ListGamesAsync(participant, "?status=ClosedInvitations")).Items);
            Assert.Equal((expired.Id, expired.Status, expired.InvitationExpiresAt), (item.Id, item.Status, item.InvitationExpiresAt));
            Assert.Equal(expired, await participant.GetFromJsonAsync<GameResponse>(url, Cancellation));
        }

        foreach ((HttpClient actor, string action) in new[] { (black, "accept"), (black, "decline"), (white, "cancel") })
        {
            using HttpResponseMessage late = await actor.PostAsJsonAsync($"{url}/{action}", new GameRevisionRequest(pending.Revision), Cancellation);
            await AssertErrorAsync(late, HttpStatusCode.Gone, "InvitationExpired");
        }

        using HttpResponseMessage replay = await white.PostAsJsonAsync("/api/games", request, Cancellation);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(expired, await replay.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        GameResponse fresh = await CreateGameAsync(white, blackId);
        Assert.NotEqual(expired.Id, fresh.Id);

        clock.Advance(TimeSpan.FromMilliseconds(299999));
        GameResponse accepted = await AcceptGameAsync(black, $"/api/games/{fresh.Id}");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(accepted, await black.GetFromJsonAsync<GameResponse>($"/api/games/{fresh.Id}", Cancellation));
    }

    [Fact]
    public async Task ConcurrentCreationCannotExceedTheOpenLimitAndExpiryFreesCapacity()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (Guid secondId, _) = await RegisterAndSignInAsync("second");
        (Guid thirdId, _) = await RegisterAndSignInAsync("third");
        (Guid fourthId, _) = await RegisterAndSignInAsync("fourth");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        await CreateGameAsync(black, whiteId);

        Guid[] opponents = [blackId, secondId, thirdId, fourthId];
        HttpResponseMessage[] responses = await Task.WhenAll(opponents.Select(opponent =>
            white.PostAsJsonAsync("/api/games", new CreateGameRequest(opponent), Cancellation)));
        try
        {
            Assert.Equal(3, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
            HttpResponseMessage limited = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            await AssertErrorAsync(limited, HttpStatusCode.Conflict, "InvitationLimitReached");
            Assert.Equal(3, (await ListGamesAsync(white)).Items.Count(item => item.Color == "White"));

            clock.Advance(TimeSpan.FromMinutes(5));
            Guid rejectedOpponent = opponents[Array.IndexOf(responses, limited)];
            GameResponse fresh = await CreateGameAsync(white, rejectedOpponent);
            Assert.Equal(fresh.Id, Assert.Single((await ListGamesAsync(white)).Items).Id);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task PairLimitsUseResolvedAccountsAndCountClosedInvitationsInASlidingWindow()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("BlackKnight");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        CreateGameRequest byId = new(blackId);
        CreateGameRequest byName = new(OpponentUsername: " BLACKKNIGHT ");
        HttpResponseMessage[] concurrent = await Task.WhenAll(white.PostAsJsonAsync("/api/games", byId, Cancellation),
            white.PostAsJsonAsync("/api/games", byName, Cancellation));
        using HttpResponseMessage firstResponse = concurrent[0];
        using HttpResponseMessage secondResponse = concurrent[1];
        HttpResponseMessage created = Assert.Single(concurrent, response => response.StatusCode == HttpStatusCode.Created);
        HttpResponseMessage duplicate = Assert.Single(concurrent, response => response.StatusCode == HttpStatusCode.Conflict);
        GameResponse first = Assert.IsType<GameResponse>(await created.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        JsonElement conflict = await duplicate.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        Assert.Equal("InvitationAlreadyPending", conflict.GetProperty("code").GetString());
        Assert.Equal(first.Id, conflict.GetProperty("gameId").GetGuid());
        await ApplyActionAsync(black, $"/api/games/{first.Id}", "decline", first.Revision);

        clock.Advance(TimeSpan.FromMinutes(1));
        CreateGameRequest keyed = byId with { RequestId = Guid.NewGuid() };
        using HttpResponseMessage second = await white.PostAsJsonAsync("/api/games", keyed, Cancellation);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        GameResponse secondGame = Assert.IsType<GameResponse>(await second.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        GameResponse cancelled = await ApplyActionAsync(white, $"/api/games/{secondGame.Id}", "cancel", secondGame.Revision);
        for (int i = 0; i < 3; i++)
        {
            GameResponse pending = await CreateGameAsync(white, blackId);
            await ApplyActionAsync(white, $"/api/games/{pending.Id}", "cancel", pending.Revision);
        }

        using HttpResponseMessage limited = await white.PostAsJsonAsync("/api/games", byName, Cancellation);
        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "InvitationRateLimited");
        Assert.Equal(TimeSpan.FromMinutes(4), limited.Headers.RetryAfter?.Delta);
        using HttpResponseMessage replay = await white.PostAsJsonAsync("/api/games", keyed, Cancellation);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(cancelled, await replay.Content.ReadFromJsonAsync<GameResponse>(Cancellation));

        clock.Advance(TimeSpan.FromMinutes(4));
        await CreateGameAsync(white, blackId);
    }

    [Theory]
    [InlineData("decline", "Declined")]
    [InlineData("cancel", "Cancelled")]
    public async Task OnlyTheAllowedParticipantCanCloseAPendingInvitation(string action, string status)
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (_, string outsiderToken) = await RegisterAndSignInAsync("outsider");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        using HttpClient outsider = CreateClient(outsiderToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string gameUrl = $"/api/games/{pending.Id}";
        string actionUrl = $"{gameUrl}/{action}";
        GameRevisionRequest revision = new(pending.Revision);
        HttpClient allowed = action == "decline" ? black : white;
        HttpClient wrongRole = action == "decline" ? white : black;

        using HttpResponseMessage unauthorizedRole = await wrongRole.PostAsJsonAsync(actionUrl, revision, Cancellation);
        await AssertErrorAsync(unauthorizedRole, HttpStatusCode.Forbidden, "Forbidden");
        using HttpResponseMessage unavailable = await outsider.PostAsJsonAsync(actionUrl, revision, Cancellation);
        await AssertErrorAsync(unavailable, HttpStatusCode.NotFound, "NotFound");
        Assert.Equal(pending, await allowed.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));

        using HttpResponseMessage response = await allowed.PostAsJsonAsync(actionUrl, revision, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GameResponse closed = Assert.IsType<GameResponse>(await response.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(pending with { Status = status, Revision = pending.Revision + 1 }, closed);
        foreach (HttpClient participant in new[] { white, black })
        {
            GameBoardResponse board = await ReadBoardAsync(participant, gameUrl);
            Assert.Equal(closed, board.Game);
            Assert.Empty(board.Moves);
            Assert.Empty(board.LegalMoves);
            Assert.Empty((await ListGamesAsync(participant)).Items);
            Assert.Empty((await ListGamesAsync(participant, "?status=Finished")).Items);
            GameListItemResponse item = Assert.Single((await ListGamesAsync(participant, "?status=ClosedInvitations")).Items);
            Assert.Equal((closed.Id, status), (item.Id, item.Status));
        }
        Assert.Empty((await ListGamesAsync(outsider, "?status=ClosedInvitations")).Items);

        using HttpResponseMessage stale = await allowed.PostAsJsonAsync(actionUrl, revision, Cancellation);
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, "RevisionConflict");
        using HttpResponseMessage accept = await black.PostAsJsonAsync($"{gameUrl}/accept", new GameRevisionRequest(closed.Revision), Cancellation);
        await AssertErrorAsync(accept, HttpStatusCode.Conflict, "GameNotPending");
        using HttpResponseMessage play = await PlayAsync(white, gameUrl, "e2e4", closed.Revision);
        await AssertErrorAsync(play, HttpStatusCode.Conflict, "GameNotActive");

        GameResponse another = await CreateGameAsync(white, blackId);
        string anotherUrl = $"/api/games/{another.Id}";
        GameResponse active = await AcceptGameAsync(black, anotherUrl);
        using HttpResponseMessage afterAcceptance = await allowed.PostAsJsonAsync($"{anotherUrl}/{action}",
            new GameRevisionRequest(active.Revision), Cancellation);
        await AssertErrorAsync(afterAcceptance, HttpStatusCode.Conflict, "GameNotPending");
    }

    [Fact]
    public async Task ConcurrentAcceptanceAndCancellationCommitOneInvitationOutcome()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string gameUrl = $"/api/games/{pending.Id}";
        GameRevisionRequest revision = new(pending.Revision);

        Task<HttpResponseMessage> accept = black.PostAsJsonAsync($"{gameUrl}/accept", revision, Cancellation);
        Task<HttpResponseMessage> cancel = white.PostAsJsonAsync($"{gameUrl}/cancel", revision, Cancellation);
        using HttpResponseMessage accepted = await accept;
        using HttpResponseMessage cancelled = await cancel;
        HttpResponseMessage[] responses = [accepted, cancelled];
        HttpResponseMessage winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        HttpResponseMessage loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        await AssertErrorAsync(loser, HttpStatusCode.Conflict, "RevisionConflict");
        GameResponse committed = Assert.IsType<GameResponse>(await winner.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(pending.Revision + 1, committed.Revision);
        Assert.Equal(winner == accepted ? "Active" : "Cancelled", committed.Status);
        Assert.Equal(committed, (await ReadBoardAsync(white, gameUrl)).Game);
        Assert.Equal(committed, (await ReadBoardAsync(black, gameUrl)).Game);
    }

    [Fact]
    public async Task LiveUpdatesRequireMembershipAndReportOnlyCommittedRevisions()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (_, string outsiderToken) = await RegisterAndSignInAsync("outsider");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse game = await CreateGameAsync(white, blackId);
        string gameUrl = $"/api/games/{game.Id}";

        using ClientWebSocket anonymous = new();
        anonymous.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => anonymous.ConnectAsync(LiveUri(), Cancellation));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.HttpStatusCode);

        using ClientWebSocket participant = new();
        using ClientWebSocket outsider = new();
        await ConnectLiveAsync(participant, whiteToken);
        await ConnectLiveAsync(outsider, outsiderToken);

        JsonElement privateGame = await SubscribeAsync(outsider, game.Id);
        JsonElement unknownGame = await SubscribeAsync(outsider, Guid.NewGuid());
        string? denial = privateGame.GetProperty("error").GetString();
        Assert.Contains("This game is unavailable.", denial);
        Assert.Equal(denial, unknownGame.GetProperty("error").GetString());
        Assert.False((await SubscribeAsync(participant, game.Id)).TryGetProperty("error", out _));

        using HttpResponseMessage rejected = await PlayAsync(white, gameUrl, "e2e4", 0);
        await AssertErrorAsync(rejected, HttpStatusCode.Conflict, "GameNotActive");
        GameResponse active = await AcceptGameAsync(black, gameUrl);
        JsonElement notified = await ReadLiveMessageAsync(participant);
        Assert.Equal(GameHub.ChangeEvent, notified.GetProperty("target").GetString());
        Assert.Equal(game.Id, notified.GetProperty("arguments")[0].GetGuid());
        Assert.Equal(active.Revision, notified.GetProperty("arguments")[1].GetInt64());

        // A completion on the same socket also proves no update was queued for the outsider.
        Assert.Equal(denial, (await SubscribeAsync(outsider, game.Id)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task PrivateGameSurvivesAnApiRestartAndFinishesAtCheckmate()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (_, string outsiderToken) = await RegisterAndSignInAsync("outsider");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        using HttpClient outsider = CreateClient(outsiderToken);
        using HttpClient anonymous = CreateClient();

        using HttpResponseMessage self = await white.PostAsJsonAsync("/api/games", new CreateGameRequest(whiteId), Cancellation);
        await AssertErrorAsync(self, HttpStatusCode.BadRequest, "InvalidPlayers");
        using HttpResponseMessage unknown = await white.PostAsJsonAsync("/api/games", new CreateGameRequest(Guid.NewGuid()), Cancellation);
        await AssertErrorAsync(unknown, HttpStatusCode.BadRequest, "OpponentUnavailable");

        GameResponse pending = await CreateGameAsync(white, blackId);
        string gameUrl = $"/api/games/{pending.Id}";
        Assert.Equal("Pending", pending.Status);
        Assert.Equal(0, pending.Revision);
        Assert.Equal(whiteId, pending.WhitePlayerId);
        Assert.Equal(blackId, pending.BlackPlayerId);
        Assert.Equal(pending, await black.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));
        using HttpResponseMessage unauthenticated = await anonymous.GetAsync(gameUrl, Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using HttpResponseMessage hidden = await outsider.GetAsync(gameUrl, Cancellation);
        await AssertErrorAsync(hidden, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage hiddenBoard = await outsider.GetAsync($"{gameUrl}/board?afterRevision=0", Cancellation);
        await AssertErrorAsync(hiddenBoard, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage hiddenMutation = await PlayAsync(outsider, gameUrl, "e2e4", 0);
        await AssertErrorAsync(hiddenMutation, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage pendingMove = await PlayAsync(white, gameUrl, "e2e4", 0);
        await AssertErrorAsync(pendingMove, HttpStatusCode.Conflict, "GameNotActive");
        using HttpResponseMessage selfAccept = await white.PostAsJsonAsync($"{gameUrl}/accept", new GameRevisionRequest(0), Cancellation);
        await AssertErrorAsync(selfAccept, HttpStatusCode.Forbidden, "Forbidden");
        using HttpResponseMessage missingRevision = await black.PostAsJsonAsync($"{gameUrl}/accept", new { }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, missingRevision.StatusCode);

        GameResponse active = await AcceptGameAsync(black, gameUrl);
        Assert.Equal("Active", active.Status);
        Assert.Equal("White", active.SideToMove);
        Assert.Equal(1, active.Revision);
        GameBoardResponse firstBoard = Assert.IsType<GameBoardResponse>(
            await white.GetFromJsonAsync<GameBoardResponse>($"{gameUrl}/board", Cancellation));
        Assert.Equal(active, firstBoard.Game);
        Assert.Empty(firstBoard.Moves);
        Assert.Equal(20, firstBoard.LegalMoves.Count);
        Assert.Contains("e2e4", firstBoard.LegalMoves);
        using HttpResponseMessage unchanged = await white.GetAsync($"{gameUrl}/board?afterRevision=1", Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);
        GameBoardResponse futureRevision = Assert.IsType<GameBoardResponse>(
            await white.GetFromJsonAsync<GameBoardResponse>($"{gameUrl}/board?afterRevision=99", Cancellation));
        Assert.Equal(active, futureRevision.Game);
        Assert.Contains("e2e4", futureRevision.LegalMoves);
        using HttpResponseMessage wrongTurn = await PlayAsync(black, gameUrl, "e7e5", 1);
        await AssertErrorAsync(wrongTurn, HttpStatusCode.Conflict, "NotYourTurn");
        using HttpResponseMessage illegal = await PlayAsync(white, gameUrl, "e2e5", 1);
        await AssertErrorAsync(illegal, HttpStatusCode.UnprocessableEntity, "IllegalMove");
        Assert.Equal(active, await white.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));

        GameResponse whiteMove = await MakeMoveAsync(white, gameUrl, "e2e4", 1);
        Assert.Equal("Black", whiteMove.SideToMove);
        Assert.Equal(whiteMove, await black.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));
        GameBoardResponse changedBoard = Assert.IsType<GameBoardResponse>(
            await black.GetFromJsonAsync<GameBoardResponse>($"{gameUrl}/board?afterRevision=1", Cancellation));
        Assert.Equal(whiteMove, changedBoard.Game);
        Assert.Equal(["e2e4"], changedBoard.Moves);
        Assert.Contains("e7e5", changedBoard.LegalMoves);
        GameBoardResponse waitingBoard = Assert.IsType<GameBoardResponse>(
            await white.GetFromJsonAsync<GameBoardResponse>($"{gameUrl}/board", Cancellation));
        Assert.Empty(waitingBoard.LegalMoves);
        GameResponse blackMove = await MakeMoveAsync(black, gameUrl, "e7e5", 2);
        Assert.StartsWith("rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w ", blackMove.Fen);

        await app!.StopAsync(Cancellation);
        await app.DisposeAsync();
        app = BuildApp();
        await app.StartAsync(Cancellation);
        using HttpClient restoredWhite = CreateClient(whiteToken);
        using HttpClient restoredBlack = CreateClient(blackToken);
        Assert.Equal(blackMove, await restoredWhite.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));

        await MakeMoveAsync(restoredWhite, gameUrl, "f1c4", 3);
        await MakeMoveAsync(restoredBlack, gameUrl, "b8c6", 4);
        await MakeMoveAsync(restoredWhite, gameUrl, "d1h5", 5);
        await MakeMoveAsync(restoredBlack, gameUrl, "g8f6", 6);
        GameResponse finished = await MakeMoveAsync(restoredWhite, gameUrl, "h5f7", 7);
        Assert.Equal("Finished", finished.Status);
        Assert.Equal("White", finished.Winner);
        Assert.Equal("Checkmate", finished.EndReason);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), finished.RematchExpiresAt);
        Assert.Null(finished.SideToMove);
        using HttpResponseMessage afterFinish = await PlayAsync(restoredBlack, gameUrl, "e8f7", 8);
        await AssertErrorAsync(afterFinish, HttpStatusCode.Conflict, "GameNotActive");
        Assert.Equal(finished, await restoredBlack.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConcurrentMoveOrResignationCanOnlyWinOneWrite(bool resign)
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string gameUrl = $"/api/games/{pending.Id}";
        await AcceptGameAsync(black, gameUrl);

        HttpResponseMessage[] responses = await Task.WhenAll(PlayAsync(white, gameUrl, "e2e4", 1),
            resign ? SendActionAsync(black, gameUrl, "resign", 1) : PlayAsync(white, gameUrl, "d2d4", 1));
        using HttpResponseMessage winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        using HttpResponseMessage loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        await AssertErrorAsync(loser, HttpStatusCode.Conflict, "RevisionConflict");
        GameResponse updated = Assert.IsType<GameResponse>(await winner.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        GameResponse persisted = Assert.IsType<GameResponse>(await white.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));
        Assert.Equal(2, persisted.Revision);
        Assert.Equal(updated, persisted);
        Assert.Equal(resign && ReferenceEquals(responses[1], winner) ? "Resignation" : null, persisted.EndReason);
        await using AsyncServiceScope scope = app!.Services.CreateAsyncScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        List<GameMoveTiming> timings = await database.GameMoveTimings.Where(timing => timing.GameId == pending.Id)
            .ToListAsync(Cancellation);
        if (persisted.Status == "Finished")
        {
            Assert.Empty(timings);
        }
        else
        {
            Assert.Equal(1, Assert.Single(timings).Ply);
        }
        using HttpResponseMessage stale = await PlayAsync(white, gameUrl, "e2e4", 1);
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, "RevisionConflict");
    }

    [Fact]
    public async Task DrawOfferCanBeDeclinedOrAcceptedOnlyByItsRecipient()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (_, string outsiderToken) = await RegisterAndSignInAsync("outsider");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        using HttpClient outsider = CreateClient(outsiderToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string url = $"/api/games/{pending.Id}";
        using HttpResponseMessage premature = await SendActionAsync(white, url, "draw/offer", 0);
        await AssertErrorAsync(premature, HttpStatusCode.Conflict, "GameNotActive");
        GameResponse game = await AcceptGameAsync(black, url);
        using HttpResponseMessage hidden = await SendActionAsync(outsider, url, "resign", game.Revision);
        await AssertErrorAsync(hidden, HttpStatusCode.NotFound, "NotFound");

        GameResponse offered = await ApplyActionAsync(white, url, "draw/offer", game.Revision);
        Assert.Equal("White", offered.DrawOfferedBy);
        Assert.Equal(game.Fen, offered.Fen);
        Assert.Equal(offered, await black.GetFromJsonAsync<GameResponse>(url, Cancellation));
        using HttpResponseMessage ownOffer = await SendActionAsync(white, url, "draw/accept", offered.Revision);
        await AssertErrorAsync(ownOffer, HttpStatusCode.Forbidden, "Forbidden");
        using HttpResponseMessage counterOffer = await SendActionAsync(black, url, "draw/offer", offered.Revision);
        await AssertErrorAsync(counterOffer, HttpStatusCode.Conflict, "DrawOfferPending");
        using HttpResponseMessage stale = await SendActionAsync(black, url, "draw/accept", game.Revision);
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, "RevisionConflict");
        GameResponse declined = await ApplyActionAsync(black, url, "draw/decline", offered.Revision);
        Assert.Null(declined.DrawOfferedBy);
        using HttpResponseMessage alreadyDeclined = await SendActionAsync(black, url, "draw/accept", declined.Revision);
        await AssertErrorAsync(alreadyDeclined, HttpStatusCode.Conflict, "NoDrawOffer");

        offered = await ApplyActionAsync(white, url, "draw/offer", declined.Revision);
        GameResponse whiteMove = await MakeMoveAsync(white, url, "e2e4", offered.Revision);
        Assert.Equal("White", whiteMove.DrawOfferedBy);
        GameResponse blackMove = await MakeMoveAsync(black, url, "e7e5", whiteMove.Revision);
        Assert.Null(blackMove.DrawOfferedBy);

        offered = await ApplyActionAsync(black, url, "draw/offer", blackMove.Revision);
        GameResponse finished = await ApplyActionAsync(white, url, "draw/accept", offered.Revision);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), finished.RematchExpiresAt);
        Assert.Equal("Finished", finished.Status);
        Assert.Equal("DrawAgreement", finished.EndReason);
        Assert.Equal(blackMove.Fen, finished.Fen);
        Assert.Null(finished.Winner);
        Assert.Null(finished.DrawOfferedBy);

        await app!.StopAsync(Cancellation);
        await app.DisposeAsync();
        app = BuildApp();
        await app.StartAsync(Cancellation);
        using HttpClient restoredWhite = CreateClient(whiteToken);
        GameBoardResponse restored = await ReadBoardAsync(restoredWhite, url);
        Assert.Equal(finished, restored.Game);
        Assert.Equal(["e2e4", "e7e5"], restored.Moves);
        Assert.Empty(restored.LegalMoves);
        using HttpResponseMessage closed = await PlayAsync(restoredWhite, url, "g1f3", finished.Revision);
        await AssertErrorAsync(closed, HttpStatusCode.Conflict, "GameNotActive");
    }

    [Fact]
    public async Task BothPlayersCanPlayOneRematchAndTheRecipientCanRetryAfterExpiry()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (_, string outsiderToken) = await RegisterAndSignInAsync("outsider");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        using HttpClient outsider = CreateClient(outsiderToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string url = $"/api/games/{pending.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        using HttpResponseMessage premature = await SendActionAsync(white, url, "rematch/offer", active.Revision);
        await AssertErrorAsync(premature, HttpStatusCode.Conflict, "RematchUnavailable");

        clock.Advance(TimeSpan.FromMinutes(2));
        GameResponse finished = await ApplyActionAsync(white, url, "resign", active.Revision);
        DateTimeOffset deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(5);
        Assert.Equal(deadline, finished.RematchExpiresAt);
        clock.Advance(TimeSpan.FromMilliseconds(299999));

        GameResponse offered = await ApplyActionAsync(white, url, "rematch/offer", finished.Revision);
        Assert.Equal(deadline, offered.RematchExpiresAt);
        Assert.Equal("White", offered.RematchOfferedBy);
        Assert.Equal(offered, (await ReadBoardAsync(black, url)).Game);
        using HttpResponseMessage competingOffer = await SendActionAsync(black, url, "rematch/offer", finished.Revision);
        await AssertErrorAsync(competingOffer, HttpStatusCode.Conflict, "RematchOfferPending");
        using HttpResponseMessage selfAccept = await SendActionAsync(white, url, "rematch/accept", offered.Revision);
        await AssertErrorAsync(selfAccept, HttpStatusCode.Forbidden, "Forbidden");
        using HttpResponseMessage outsiderAccept = await SendActionAsync(outsider, url, "rematch/accept", offered.Revision);
        await AssertErrorAsync(outsiderAccept, HttpStatusCode.NotFound, "NotFound");

        HttpResponseMessage[] responses = await Task.WhenAll(
            SendActionAsync(black, url, "rematch/accept", offered.Revision),
            SendActionAsync(black, url, "rematch/accept", offered.Revision));
        GameResponse accepted;
        using (responses[0])
        using (responses[1])
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            accepted = Assert.IsType<GameResponse>(await responses[0].Content.ReadFromJsonAsync<GameResponse>(Cancellation));
            Assert.Equal(accepted, await responses[1].Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        }
        Guid rematchId = Assert.IsType<Guid>(accepted.RematchGameId);
        Assert.Equal(finished with { Revision = offered.Revision + 1, RematchOfferedBy = "White", RematchGameId = rematchId }, accepted);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        using HttpResponseMessage replay = await SendActionAsync(black, url, "rematch/accept", offered.Revision);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(accepted, await replay.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(accepted, (await ReadBoardAsync(white, url)).Game);
        Assert.Equal(rematchId, Assert.Single((await ListGamesAsync(white, "?status=Active")).Items).Id);

        string rematchUrl = $"/api/games/{rematchId}";
        GameResponse rematch = (await ReadBoardAsync(black, rematchUrl)).Game;
        Assert.Equal((blackId, whiteId, "Active", "White", 0L),
            (rematch.WhitePlayerId, rematch.BlackPlayerId, rematch.Status, rematch.SideToMove, rematch.Revision));
        await MakeMoveAsync(black, rematchUrl, "e2e4", rematch.Revision);
        Assert.Equal(["e2e4"], (await ReadBoardAsync(white, rematchUrl)).Moves);
    }

    [Fact]
    public async Task RematchOfferAndAcceptanceExpireAtTheDeadline()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string url = $"/api/games/{pending.Id}";
        await AcceptGameAsync(black, url);
        GameResponse unoffered = await CreateGameAsync(black, whiteId);
        string secondUrl = $"/api/games/{unoffered.Id}";
        await AcceptGameAsync(white, secondUrl);
        GameResponse finished = await ApplyActionAsync(black, url, "resign", 1);
        GameResponse secondFinished = await ApplyActionAsync(white, secondUrl, "resign", 1);
        GameResponse offered = await ApplyActionAsync(black, url, "rematch/offer", finished.Revision);

        clock.Advance(TimeSpan.FromMinutes(5));
        using HttpResponseMessage lateAccept = await SendActionAsync(white, url, "rematch/accept", offered.Revision);
        await AssertErrorAsync(lateAccept, HttpStatusCode.Gone, "RematchExpired");
        using HttpResponseMessage lateOffer = await SendActionAsync(black, secondUrl, "rematch/offer", secondFinished.Revision);
        await AssertErrorAsync(lateOffer, HttpStatusCode.Gone, "RematchExpired");
        Assert.Equal(offered, (await ReadBoardAsync(white, url)).Game);
        Assert.Empty((await ListGamesAsync(white, "?status=Active")).Items);
    }

    [Fact]
    public async Task BlitzClocksStartAtAcceptanceAndALateMoveCannotBeatTheDeadline()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        CreateGameRequest request = new(blackId, Guid.NewGuid(), TimeControl: "Blitz5");
        using HttpResponseMessage created = await white.PostAsJsonAsync("/api/games", request, Cancellation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        GameResponse pending = Assert.IsType<GameResponse>(await created.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(300000, pending.Clock!.WhiteMilliseconds);
        Assert.Null(pending.Clock.TurnDeadline);
        using HttpResponseMessage mismatch = await white.PostAsJsonAsync("/api/games", request with { TimeControl = null }, Cancellation);
        await AssertErrorAsync(mismatch, HttpStatusCode.Conflict, "CreationRequestConflict");

        clock.Advance(TimeSpan.FromSeconds(20));
        string url = $"/api/games/{pending.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        DateTimeOffset whiteDeadline = clock.GetUtcNow().AddMinutes(5);
        Assert.Equal(whiteDeadline, active.Clock!.TurnDeadline);
        clock.Advance(TimeSpan.FromMilliseconds(299999));
        GameResponse moved = await MakeMoveAsync(white, url, "e2e4", active.Revision);
        Assert.Equal(1, moved.Clock!.WhiteMilliseconds);
        Assert.Equal(300000, moved.Clock.BlackMilliseconds);
        DateTimeOffset blackDeadline = clock.GetUtcNow().AddMinutes(5);
        Assert.Equal(blackDeadline, moved.Clock.TurnDeadline);
        clock.Advance(TimeSpan.FromMinutes(5));

        Task<HttpResponseMessage> lateMove = PlayAsync(black, url, "e7e5", moved.Revision);
        Task<GameBoardResponse> boardRead = ReadBoardAsync(white, url);
        using HttpResponseMessage late = await lateMove;
        Assert.Contains(late.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        GameBoardResponse finished = await boardRead;
        Assert.Equal("Finished", finished.Game.Status);
        Assert.Equal("White", finished.Game.Winner);
        Assert.Equal("Timeout", finished.Game.EndReason);
        Assert.Equal(blackDeadline.AddMinutes(5), finished.Game.RematchExpiresAt);
        Assert.Equal(moved.Revision + 1, finished.Game.Revision);
        Assert.Equal(["e2e4"], finished.Moves);
        Assert.Null(finished.Game.Clock!.TurnDeadline);
        Assert.Equal(1, finished.Game.Clock.WhiteMilliseconds);
        Assert.Equal(0, finished.Game.Clock.BlackMilliseconds);

        GameReplayResponse replay = await ReadReplayAsync(white, url);
        Assert.Equal("recorded", replay.TimingMode);
        Assert.Equal(300000, replay.InitialClock!.WhiteMilliseconds);
        GameReplayMoveResponse frame = Assert.Single(replay.Moves);
        Assert.Equal(299999L, frame.AtMs);
        Assert.Equal("e4", frame.San);
        Assert.Equal(finished.Game.Fen, frame.Fen);
        Assert.Equal(1, frame.Clock!.WhiteMilliseconds);
        Assert.Equal(300000, frame.Clock.BlackMilliseconds);
        Assert.Equal(599999L, replay.Finish.AtMs);
        Assert.Equal("Timeout", replay.Finish.EndReason);
        Assert.Equal(0, replay.Finish.Clock!.BlackMilliseconds);

        GameResponse offered = await ApplyActionAsync(white, url, "rematch/offer", finished.Game.Revision);
        GameResponse accepted = await ApplyActionAsync(black, url, "rematch/accept", offered.Revision);
        GameResponse rematch = (await ReadBoardAsync(black, $"/api/games/{accepted.RematchGameId}")).Game;
        Assert.Equal(blackId, rematch.WhitePlayerId);
        Assert.Equal("Blitz5", rematch.Clock!.TimeControl);
        Assert.Equal(300000, rematch.Clock.WhiteMilliseconds);
        Assert.Equal(300000, rematch.Clock.BlackMilliseconds);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), rematch.Clock.TurnDeadline);
    }

    [Fact]
    public async Task ReadingMyGamesSettlesATimeoutAsADrawIfTheOpponentCannotMate()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        await using AsyncServiceScope scope = app!.Services.CreateAsyncScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        const string fen = "7k/8/8/8/8/8/R7/K7 w - - 0 1";
        Game game = new(whiteId, blackId, new(PositionRecord(fen), fen, PlayerColor.White), clock.GetUtcNow(), GameTimeControl.Blitz5);
        database.Games.Add(game);
        await database.SaveChangesAsync(Cancellation);
        string url = $"/api/games/{game.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        clock.Advance(TimeSpan.FromMinutes(6));

        GameListItemResponse item = Assert.Single((await ListGamesAsync(white, "?status=Finished")).Items);
        Assert.Equal("TimeoutNoPossibleMate", item.EndReason);
        Assert.Null(item.Winner);
        Assert.Equal("Blitz5", item.TimeControl);
        Assert.Empty((await ListGamesAsync(black, "?status=Active")).Items);
        using HttpResponseMessage changed = await black.GetAsync($"{url}/board?afterRevision={active.Revision}", Cancellation);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        GameBoardResponse finished = Assert.IsType<GameBoardResponse>(
            await changed.Content.ReadFromJsonAsync<GameBoardResponse>(Cancellation));
        Assert.Equal(active.Clock!.TurnDeadline?.AddMinutes(5), finished.Game.RematchExpiresAt);
        Assert.Equal(0, finished.Game.Clock!.WhiteMilliseconds);
        Assert.Equal(300000, finished.Game.Clock.BlackMilliseconds);
    }

    [Fact]
    public async Task APrivateLinkKeepsItsTimeControlUntilJoin()
    {
        (_, string hostToken) = await RegisterAndSignInAsync("host");
        (_, string guestToken) = await RegisterAndSignInAsync("guest");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient guest = CreateClient(guestToken);
        CreateLobbyRequest request = new(Guid.NewGuid(), "Blitz5");
        LobbyResponse lobby = await CreateLobbyAsync(host, request);
        Assert.Equal("Blitz5", lobby.TimeControl);
        using HttpResponseMessage mismatch = await host.PostAsJsonAsync("/api/lobbies", request with { TimeControl = null }, Cancellation);
        await AssertErrorAsync(mismatch, HttpStatusCode.Conflict, "CreationRequestConflict");
        clock.Advance(TimeSpan.FromMinutes(1));

        string url = $"/api/lobbies/{lobby.Id}";
        Assert.Equal("Blitz5", (await guest.GetFromJsonAsync<LobbyResponse>(url, Cancellation))?.TimeControl);
        using HttpResponseMessage joined = await SendActionAsync(guest, url, "join", lobby.Revision);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        LobbyResponse result = Assert.IsType<LobbyResponse>(await joined.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        GameResponse active = (await ReadBoardAsync(guest, $"/api/games/{result.GameId}")).Game;
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), active.Clock!.TurnDeadline);
    }

    [Fact]
    public async Task ReplayIsPrivateAndResignationWithoutMovesHasItsOwnTime()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        (_, string outsiderToken) = await RegisterAndSignInAsync("outsider");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        using HttpClient outsider = CreateClient(outsiderToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string url = $"/api/games/{pending.Id}";
        clock.Advance(TimeSpan.FromSeconds(30));
        GameResponse active = await AcceptGameAsync(black, url);
        using HttpResponseMessage tooEarly = await white.GetAsync($"{url}/replay", Cancellation);
        await AssertErrorAsync(tooEarly, HttpStatusCode.Conflict, "GameNotFinished");
        clock.Advance(TimeSpan.FromSeconds(7));
        GameResponse finished = await ApplyActionAsync(black, url, "resign", active.Revision);
        using HttpResponseMessage inaccessible = await outsider.GetAsync($"{url}/replay", Cancellation);
        await AssertErrorAsync(inaccessible, HttpStatusCode.NotFound, "NotFound");

        GameReplayResponse replay = await ReadReplayAsync(black, url);
        Assert.Equal("Black", replay.ViewerColor);
        Assert.Equal("recorded", replay.TimingMode);
        Assert.Equal(finished.Fen, replay.InitialFen);
        Assert.Empty(replay.Moves);
        Assert.Equal("White", replay.Finish.Winner);
        Assert.Equal("Resignation", replay.Finish.EndReason);
        Assert.Equal(7000L, replay.Finish.AtMs);
    }

    [Theory]
    [InlineData("7k/8/8/3pP3/8/8/8/7K w - d6 0 2", "e5d6", "exd6", "7k/8/3P4/8/8/8/8/7K", "White", "2", false)]
    [InlineData("7k/P7/8/8/8/8/8/7K w - - 0 1", "a7a8q", "a8=Q+", "Q6k/8/8/8/8/8/8/7K", "White", "1", false)]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R b KQkq - 0 37", "e8c8", "O-O-O", "2kr3r/8/8/8/8/8/8/R3K2R", "Black", "37", true)]
    public async Task ReplayUsesTheRecordedInitialPositionAndHandlesSpecialMoves(string fen, string coordinate, string san,
        string board, string color, string moveNumber, bool legacy)
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        await using AsyncServiceScope scope = app!.Services.CreateAsyncScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        PlayerColor startingColor = Enum.Parse<PlayerColor>(color);
        Game game = new(whiteId, blackId, new(PositionRecord(fen), fen, startingColor), clock.GetUtcNow());
        database.Games.Add(game);
        await database.SaveChangesAsync(Cancellation);
        string url = $"/api/games/{game.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        if (legacy)
        {
            await database.Games.Where(row => row.Id == game.Id).ExecuteUpdateAsync(
                updates => updates.SetProperty(row => row.StartedAt, (DateTimeOffset?)null), Cancellation);
        }

        clock.Advance(TimeSpan.FromSeconds(3));
        GameResponse moved = await MakeMoveAsync(startingColor == PlayerColor.White ? white : black, url, coordinate, active.Revision);
        clock.Advance(TimeSpan.FromSeconds(8));
        await ApplyActionAsync(white, url, "resign", moved.Revision);
        GameReplayResponse replay = await ReadReplayAsync(black, url);
        GameReplayMoveResponse frame = Assert.Single(replay.Moves);
        Assert.Equal(fen, replay.InitialFen);
        Assert.Equal(san, frame.San);
        Assert.Equal(moveNumber, frame.MoveNumber);
        Assert.Equal(color, frame.Color);
        Assert.Equal(board, frame.Fen.Split(' ')[0]);
        Assert.Equal(legacy ? "unavailable" : "recorded", replay.TimingMode);
        Assert.Equal(legacy ? null : 3000L, frame.AtMs);
        Assert.Equal(legacy ? null : 11000L, replay.Finish.AtMs);
    }

    [Fact]
    public async Task APlayerCanResignOutsideTheirTurn()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string url = $"/api/games/{pending.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        GameResponse result = await ApplyActionAsync(black, url, "resign", active.Revision);

        Assert.Equal("Finished", result.Status);
        Assert.Equal("White", result.Winner);
        Assert.Equal("Resignation", result.EndReason);
        Assert.Equal(active.Fen, result.Fen);
        Assert.Equal(result, await white.GetFromJsonAsync<GameResponse>(url, Cancellation));
    }

    [Fact]
    public async Task ResignationUsesTheOpponentsMatingMaterialAndPersistsTheResult()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        await using AsyncServiceScope scope = app!.Services.CreateAsyncScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        IGameRules rules = scope.ServiceProvider.GetRequiredService<IGameRules>();

        (string Fen, HttpClient Resigning, string? Winner)[] positions =
        [
            ("7k/8/8/8/8/8/R7/K7 w - - 0 1", white, null),
            ("k7/r7/8/8/8/8/8/7K w - - 0 1", black, null),
            ("7k/6n1/8/8/8/8/Q7/K7 w - - 0 1", white, null),
            ("7k/5b2/8/8/8/8/R7/K7 w - - 0 1", white, null),
            ("7k/8/8/8/8/8/NN6/K7 w - - 0 1", black, "White"),
            ("k1K5/p7/4N3/8/8/8/8/8 w - - 0 1", black, "White")
        ];

        // The defending pawn blocks its own king, so one knight is enough in this position.
        string matingRecord = PositionRecord(positions[^1].Fen);
        Assert.Equal(GameEndReason.Checkmate, rules.Play(matingRecord, "e6c7")?.EndReason);

        foreach ((string fen, HttpClient resigning, string? winner) in positions)
        {
            Game game = new(whiteId, blackId, new(PositionRecord(fen), fen, PlayerColor.White), DateTimeOffset.UtcNow);
            database.Games.Add(game);
            await database.SaveChangesAsync(Cancellation);
            string url = $"/api/games/{game.Id}";
            GameResponse active = await AcceptGameAsync(black, url);
            GameResponse offered = await ApplyActionAsync(resigning, url, "draw/offer", active.Revision);
            GameResponse result = await ApplyActionAsync(resigning, url, "resign", offered.Revision);

            string reason = winner is null ? "ResignationNoPossibleMate" : "Resignation";
            Assert.Equal("Finished", result.Status);
            Assert.Equal(offered.Revision + 1, result.Revision);
            Assert.Equal(winner, result.Winner);
            Assert.Equal(reason, result.EndReason);
            Assert.Equal(fen, result.Fen);
            Assert.Null(result.DrawOfferedBy);

            HttpClient opponent = resigning == white ? black : white;
            GameBoardResponse board = await ReadBoardAsync(opponent, url);
            Assert.Equal(result, board.Game);
            Assert.Empty(board.Moves);
            GameListItemResponse listed = Assert.Single((await ListGamesAsync(opponent, "?status=Finished")).Items,
                item => item.Id == game.Id);
            Assert.Equal(winner, listed.Winner);
            Assert.Equal(reason, listed.EndReason);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThreefoldClaimKeepsThePositionAndHistory(bool useIntendedMove)
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string url = $"/api/games/{pending.Id}";
        GameResponse game = await AcceptGameAsync(black, url);
        using HttpResponseMessage unavailable = await white.PostAsJsonAsync($"{url}/draw/claim",
            new ClaimGameDrawRequest(game.Revision, "FiftyMoveRule", null), Cancellation);
        await AssertErrorAsync(unavailable, HttpStatusCode.UnprocessableEntity, "DrawClaimUnavailable");

        (HttpClient Player, string Move)[] cycle = [(white, "g1f3"), (black, "g8f6"), (white, "f3g1"), (black, "f6g8")];
        int plies = useIntendedMove ? 7 : 8;
        for (int i = 0; i < plies; i++)
        {
            (HttpClient player, string move) = cycle[i % cycle.Length];
            game = await MakeMoveAsync(player, url, move, game.Revision);
        }
        HttpClient claimant = useIntendedMove ? black : white;
        HttpClient waiting = useIntendedMove ? white : black;
        Assert.Empty((await ReadBoardAsync(waiting, url)).DrawClaims);
        GameBoardResponse before = await ReadBoardAsync(claimant, url);
        if (useIntendedMove)
        {
            Assert.Empty(before.DrawClaims);
        }
        else
        {
            Assert.Equal(["ThreefoldRepetition"], before.DrawClaims);
        }
        using HttpResponseMessage wrongTurn = await waiting.PostAsJsonAsync($"{url}/draw/claim",
            new ClaimGameDrawRequest(game.Revision, "ThreefoldRepetition", null), Cancellation);
        await AssertErrorAsync(wrongTurn, HttpStatusCode.Conflict, "NotYourTurn");

        string? intendedMove = useIntendedMove ? "f6g8" : null;
        using HttpResponseMessage claimed = await claimant.PostAsJsonAsync($"{url}/draw/claim",
            new ClaimGameDrawRequest(game.Revision, "ThreefoldRepetition", intendedMove), Cancellation);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        GameBoardResponse after = await ReadBoardAsync(waiting, url);
        Assert.Equal(game.Revision + 1, after.Game.Revision);
        Assert.Equal(game.Fen, after.Game.Fen);
        Assert.Equal("Finished", after.Game.Status);
        Assert.Equal("ThreefoldRepetition", after.Game.EndReason);
        Assert.Equal(before.Moves, after.Moves);
        Assert.Empty(after.DrawClaims);
    }

    [Fact]
    public async Task FiftyMoveClaimUsesThePersistedEnginePosition()
    {
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);

        const string fen = "7k/8/8/8/8/8/R7/K7 w - - 100 60";
        string record = PositionRecord(fen);
        Game game = new(whiteId, blackId, new(record, fen, PlayerColor.White), DateTimeOffset.UtcNow);
        await using (AsyncServiceScope scope = app!.Services.CreateAsyncScope())
        {
            PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            database.Games.Add(game);
            await database.SaveChangesAsync(Cancellation);
        }
        string url = $"/api/games/{game.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        Assert.Equal(["FiftyMoveRule"], (await ReadBoardAsync(white, url)).DrawClaims);
        using HttpResponseMessage claimed = await white.PostAsJsonAsync($"{url}/draw/claim",
            new ClaimGameDrawRequest(active.Revision, "FiftyMoveRule", null), Cancellation);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        GameBoardResponse finished = await ReadBoardAsync(black, url);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), finished.Game.RematchExpiresAt);
        Assert.Equal("Finished", finished.Game.Status);
        Assert.Equal("FiftyMoveRule", finished.Game.EndReason);
        Assert.Equal(fen, finished.Game.Fen);
        Assert.Null(finished.Game.Winner);
        Assert.Empty(finished.Moves);
    }

    [Fact]
    public async Task PersistedHistoryTriggersAutomaticFivefoldRepetition()
    {
        (_, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string gameUrl = $"/api/games/{pending.Id}";
        GameResponse game = await AcceptGameAsync(black, gameUrl);
        (HttpClient Player, string Move)[] cycle =
            [(white, "g1f3"), (black, "g8f6"), (white, "f3g1"), (black, "f6g8")];
        for (int repetition = 0; repetition < 4; repetition++)
        {
            Assert.Equal("Active", game.Status);
            foreach ((HttpClient player, string move) in cycle)
            {
                game = await MakeMoveAsync(player, gameUrl, move, game.Revision);
            }
        }
        Assert.Equal("Finished", game.Status);
        Assert.Equal("FivefoldRepetition", game.EndReason);
        Assert.Null(game.Winner);
    }

    [Fact]
    public async Task GuestSessionOwnsTheSeatAndCanRecoverAfterTheLinkExpires()
    {
        (_, string hostToken) = await RegisterAndSignInAsync("host");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient guest = CreateGuestClient();
        using HttpClient stranger = CreateGuestClient();
        LobbyResponse lobby = await CreateLobbyAsync(host, new(Guid.NewGuid(), "Blitz5"));
        string link = $"/api/lobbies/{lobby.Id}";

        LobbyResponse preview = Assert.IsType<LobbyResponse>(await guest.GetFromJsonAsync<LobbyResponse>($"{link}/guest", Cancellation));
        Assert.Equal("host", preview.HostName);
        Assert.Equal("Blitz5", preview.TimeControl);
        Assert.Null(preview.GameId);
        using HttpResponseMessage unauthenticatedJoin = await SendActionAsync(guest, link, "join", preview.Revision);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedJoin.StatusCode);
        using HttpResponseMessage invalidName = await guest.PostAsJsonAsync("/api/guests/session",
            new CreateGuestSessionRequest(lobby.Id, "  "), Cancellation);
        await AssertErrorAsync(invalidName, HttpStatusCode.BadRequest, "InvalidGuestName");

        GuestProfile profile = await StartGuestAsync(guest, lobby.Id, "  Guest Knight  ");
        Assert.Equal("Guest Knight", profile.Username);
        await StartGuestAsync(stranger, lobby.Id, profile.Username);
        using HttpResponseMessage join = await SendActionAsync(guest, link, "join", preview.Revision);
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        LobbyResponse joined = Assert.IsType<LobbyResponse>(await join.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        Guid gameId = Assert.IsType<Guid>(joined.GameId);
        string game = $"/api/games/{gameId}";
        GameBoardResponse aboard = await ReadBoardAsync(guest, game);
        Assert.Equal(profile.UserId, aboard.Game.BlackPlayerId);
        Assert.Equal(profile.Username, aboard.Game.BlackGuestName);
        Assert.Equal("Blitz5", aboard.Game.Clock?.TimeControl);
        Assert.Equal(profile.Username, Assert.Single((await ListGamesAsync(host, "?status=Active")).Items).OpponentName);

        JsonElement liveGrant = await guest.GetFromJsonAsync<JsonElement>("/api/guests/session/live", Cancellation);
        string proof = liveGrant.GetProperty("accessToken").GetString()!;
        using (ClientWebSocket otherSocket = new())
        {
            otherSocket.Options.SetRequestHeader("Cookie", stranger.DefaultRequestHeaders.GetValues("Cookie").Single());
            await Assert.ThrowsAsync<WebSocketException>(() => otherSocket.ConnectAsync(LiveUri(proof), Cancellation));
        }
        using (ClientWebSocket guestSocket = new())
        {
            guestSocket.Options.SetRequestHeader("Cookie", guest.DefaultRequestHeaders.GetValues("Cookie").Single());
            await ConnectLiveAsync(guestSocket, proof);
            JsonElement subscribed = await SubscribeAsync(guestSocket, gameId);
            Assert.False(subscribed.TryGetProperty("error", out _));
        }

        using HttpResponseMessage otherPreview = await stranger.GetAsync($"{link}/guest", Cancellation);
        await AssertErrorAsync(otherPreview, HttpStatusCode.Conflict, "LobbyAlreadyJoined");
        using HttpResponseMessage otherBoard = await stranger.GetAsync($"{game}/board", Cancellation);
        await AssertErrorAsync(otherBoard, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage otherMove = await PlayAsync(stranger, game, "e7e5", 1);
        await AssertErrorAsync(otherMove, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage createLink = await guest.PostAsJsonAsync("/api/lobbies", new CreateLobbyRequest(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, createLink.StatusCode);
        using HttpResponseMessage guestList = await guest.GetAsync("/api/games?status=Active", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, guestList.StatusCode);
        using HttpResponseMessage guestQuickPlay = await guest.PostAsJsonAsync("/api/quick-play",
            new JoinQuickPlayRequest(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, guestQuickPlay.StatusCode);

        GameResponse whiteMove = await MakeMoveAsync(host, game, "e2e4", aboard.Game.Revision);
        using (HttpRequestMessage crossOrigin = new(HttpMethod.Post, $"{game}/moves")
        {
            Content = JsonContent.Create(new PlayGameMoveRequest(whiteMove.Revision, "e7e5"))
        })
        {
            crossOrigin.Headers.Add("Sec-Fetch-Site", "same-site");
            using HttpResponseMessage denied = await guest.SendAsync(crossOrigin, Cancellation);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        GameResponse blackMove = await MakeMoveAsync(guest, game, "e7e5", whiteMove.Revision);
        GameResponse finished = await ApplyActionAsync(guest, game, "resign", blackMove.Revision);
        GameReplayResponse replay = await ReadReplayAsync(guest, game);
        Assert.Equal("Black", replay.ViewerColor);
        Assert.Equal(["e2e4", "e7e5"], replay.Moves.Select(move => move.Move));
        GameResponse offer = await ApplyActionAsync(host, game, "rematch/offer", finished.Revision);
        GameResponse rematched = await ApplyActionAsync(guest, game, "rematch/accept", offer.Revision);
        GameResponse rematch = (await ReadBoardAsync(guest, $"/api/games/{rematched.RematchGameId}")).Game;
        Assert.Equal(profile.UserId, rematch.WhitePlayerId);
        Assert.Equal(profile.Username, rematch.WhiteGuestName);

        clock.Advance(TimeSpan.FromMinutes(6));
        using HttpResponseMessage repeated = await SendActionAsync(guest, link, "join", preview.Revision);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(gameId, (await repeated.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation))?.GameId);

        clock.Advance(TimeSpan.FromDays(29));
        using HttpResponseMessage renewed = await guest.GetAsync("/api/guests/session", Cancellation);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.True(renewed.Headers.Contains("Set-Cookie"));
        Assert.Equal(profile, await renewed.Content.ReadFromJsonAsync<GuestProfile>(Cancellation));
        clock.Advance(TimeSpan.FromDays(1));
        using HttpResponseMessage expired = await stranger.GetAsync("/api/guests/session", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        using HttpResponseMessage revoke = await guest.DeleteAsync("/api/guests/session", Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using HttpResponseMessage revokedBoard = await guest.GetAsync($"{game}/board", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedBoard.StatusCode);
    }

    [Fact]
    public async Task GuestAndAccountJoiningTogetherCanCreateOnlyOneGame()
    {
        (_, string hostToken) = await RegisterAndSignInAsync("host");
        (_, string memberToken) = await RegisterAndSignInAsync("member");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient member = CreateClient(memberToken);
        using HttpClient guest = CreateGuestClient();
        LobbyResponse lobby = await CreateLobbyAsync(host);
        await StartGuestAsync(guest, lobby.Id, "Guest");
        string url = $"/api/lobbies/{lobby.Id}";

        HttpResponseMessage[] responses = await Task.WhenAll(
            SendActionAsync(guest, url, "join", lobby.Revision),
            SendActionAsync(member, url, "join", lobby.Revision));
        using HttpResponseMessage winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        using HttpResponseMessage loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        await AssertErrorAsync(loser, HttpStatusCode.Conflict, "LobbyAlreadyJoined");
        LobbyResponse joined = Assert.IsType<LobbyResponse>(await winner.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        Assert.Equal(joined.GameId, Assert.Single((await ListGamesAsync(host, "?status=Active")).Items).Id);
    }

    private HttpClient CreateGuestClient()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add("X-ChessRealms-Web", "1");
        return client;
    }

    private async Task<GuestProfile> StartGuestAsync(HttpClient client, Guid lobbyId, string name)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/guests/session",
            new CreateGuestSessionRequest(lobbyId, name), Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Equal("__Host-ChessRealms.Guest", cookie.Split('=', 2)[0]);
        Assert.True(cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.True(cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.True(cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie.Split(';')[0]);
        return Assert.IsType<GuestProfile>(await response.Content.ReadFromJsonAsync<GuestProfile>(Cancellation));
    }

    [Fact]
    public async Task FourConcurrentSearchesCreateTwoDistinctBlitzGames()
    {
        var registered = new List<(Guid Id, string Token)>();
        for (int index = 0; index < 4; index++)
        {
            registered.Add(await RegisterAndSignInAsync($"player{index}"));
        }
        using HttpClient first = CreateClient(registered[0].Token);
        using HttpClient second = CreateClient(registered[1].Token);
        using HttpClient third = CreateClient(registered[2].Token);
        using HttpClient fourth = CreateClient(registered[3].Token);
        HttpClient[] clients = [first, second, third, fourth];
        Guid[] requests = clients.Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(clients.Select((client, index) => JoinQuickPlayAsync(client, requests[index])));
        QuickPlayResponse[] tickets = await Task.WhenAll(clients.Select((client, index) =>
            ReadQuickPlayAsync(client, $"/api/quick-play/{requests[index]}")));
        Assert.All(tickets, ticket => Assert.Equal("Matched", ticket.Status));
        Assert.Equal(2, tickets.Select(ticket => ticket.GameId).Distinct().Count());

        foreach (var pair in tickets.Select((ticket, index) => (ticket, index)).GroupBy(item => item.ticket.GameId))
        {
            var matched = pair.ToArray();
            Assert.Equal(2, matched.Length);
            string url = $"/api/games/{pair.Key}";
            GameResponse game = (await ReadBoardAsync(clients[matched[0].index], url)).Game;
            Assert.Equal(game, (await ReadBoardAsync(clients[matched[1].index], url)).Game);
            Assert.Equal("Active", game.Status);
            Assert.Equal(0, game.Revision);
            Assert.Equal("Blitz5", game.Clock?.TimeControl);
            Assert.Equal(clock.GetUtcNow().AddMinutes(5), game.Clock?.TurnDeadline);
            Assert.Equal(matched.Select(item => registered[item.index].Id).Order(), new[] { game.WhitePlayerId, game.BlackPlayerId }.Order());
        }
    }

    [Fact]
    public async Task QuickPlayRecoversMatchesAcrossTabsAndKeepsThemOutOfTheInvitationLimit()
    {
        (Guid firstId, string firstToken) = await RegisterAndSignInAsync("first");
        (Guid secondId, string secondToken) = await RegisterAndSignInAsync("second");
        using HttpClient first = CreateClient(firstToken);
        using HttpClient second = CreateClient(secondToken);
        using HttpClient anonymous = CreateClient();
        using HttpResponseMessage rejected = await anonymous.PostAsJsonAsync("/api/quick-play",
            new JoinQuickPlayRequest(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        Guid key = Guid.NewGuid();
        Guid otherTab = Guid.NewGuid();
        QuickPlayResponse waiting = await JoinQuickPlayAsync(first, key);
        Assert.Equal("Waiting", waiting.Status);
        Assert.Equal(waiting, await JoinQuickPlayAsync(first, otherTab));
        using HttpResponseMessage hidden = await second.GetAsync($"/api/quick-play/{key}", Cancellation);
        await AssertErrorAsync(hidden, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage hiddenCancel = await SendActionAsync(second, $"/api/quick-play/{key}", "cancel", 0);
        await AssertErrorAsync(hiddenCancel, HttpStatusCode.NotFound, "NotFound");

        Guid secondKey = Guid.NewGuid();
        QuickPlayResponse opponent = await JoinQuickPlayAsync(second, secondKey);
        QuickPlayResponse matched = await ReadQuickPlayAsync(first);
        Assert.Equal("Matched", matched.Status);
        Assert.Equal(opponent.GameId, matched.GameId);
        Assert.Equal(matched, await JoinQuickPlayAsync(first, key));
        Assert.Equal(matched, await JoinQuickPlayAsync(first, otherTab));
        Assert.Equal(opponent, await JoinQuickPlayAsync(second, secondKey));
        Assert.Equal(matched, await ChangeQuickPlayAsync(first, key, "cancel"));
        Assert.Single((await ListGamesAsync(first, "?status=Active")).Items);

        GameResponse game = (await ReadBoardAsync(first, $"/api/games/{matched.GameId}")).Game;
        HttpClient creator = game.WhitePlayerId == firstId ? first : second;
        Guid invitee = game.WhitePlayerId == firstId ? secondId : firstId;
        for (int index = 0; index < 5; index++)
        {
            GameResponse invite = await CreateGameAsync(creator, invitee);
            await ApplyActionAsync(creator, $"/api/games/{invite.Id}", "cancel", invite.Revision);
        }
        using HttpResponseMessage limited = await creator.PostAsJsonAsync("/api/games", new CreateGameRequest(invitee), Cancellation);
        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "InvitationRateLimited");
    }

    [Fact]
    public async Task OnlyHeartbeatsExtendQuickPlayAndAnExpiredRequestNeverRejoins()
    {
        (_, string firstToken) = await RegisterAndSignInAsync("first");
        (_, string secondToken) = await RegisterAndSignInAsync("second");
        using HttpClient first = CreateClient(firstToken);
        using HttpClient second = CreateClient(secondToken);
        Guid key = Guid.NewGuid();
        QuickPlayResponse waiting = await JoinQuickPlayAsync(first, key);
        Assert.Equal(clock.GetUtcNow().AddSeconds(45), waiting.ExpiresAt);
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(waiting, await ReadQuickPlayAsync(first));
        Assert.Equal(waiting, await JoinQuickPlayAsync(first, key));

        QuickPlayResponse renewed = await ChangeQuickPlayAsync(first, key, "heartbeat");
        Assert.Equal(clock.GetUtcNow().AddSeconds(45), renewed.ExpiresAt);
        Assert.Equal(waiting.EnqueuedAt, renewed.EnqueuedAt);
        clock.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal("Expired", (await ReadQuickPlayAsync(first)).Status);
        Assert.Equal("Expired", (await ChangeQuickPlayAsync(first, key, "heartbeat")).Status);

        QuickPlayResponse outsider = await JoinQuickPlayAsync(second, Guid.NewGuid());
        Assert.Equal("Waiting", outsider.Status);
        Assert.Equal("Expired", (await JoinQuickPlayAsync(first, key)).Status);
        QuickPlayResponse matched = await JoinQuickPlayAsync(first, Guid.NewGuid(), previous: key);
        Assert.Equal("Matched", matched.Status);
        Assert.Equal(matched.GameId, (await ReadQuickPlayAsync(second)).GameId);
    }

    [Fact]
    public async Task ConcurrentQuickPlayCancellationAndMatchingHaveOneOutcome()
    {
        (_, string firstToken) = await RegisterAndSignInAsync("first");
        (_, string secondToken) = await RegisterAndSignInAsync("second");
        using HttpClient first = CreateClient(firstToken);
        using HttpClient second = CreateClient(secondToken);
        Guid key = Guid.NewGuid();
        await JoinQuickPlayAsync(first, key);

        QuickPlayResponse[] results = await Task.WhenAll(
            ChangeQuickPlayAsync(first, key, "cancel"), JoinQuickPlayAsync(second, Guid.NewGuid()));
        if (results[0].Status == "Cancelled")
        {
            Assert.Equal("Waiting", results[1].Status);
            Assert.Null(results[1].GameId);
            Assert.Empty((await ListGamesAsync(first, "?status=Active")).Items);
        }
        else
        {
            Assert.Equal("Matched", results[0].Status);
            Assert.Equal("Matched", results[1].Status);
            Assert.Equal(Assert.IsType<Guid>(results[0].GameId), results[1].GameId);
        }
    }

    private async Task<QuickPlayResponse> JoinQuickPlayAsync(HttpClient client, Guid requestId, Guid? previous = null)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/quick-play",
            new JoinQuickPlayRequest(requestId, previous), Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<QuickPlayResponse>(await response.Content.ReadFromJsonAsync<QuickPlayResponse>(Cancellation));
    }

    private async Task<QuickPlayResponse> ReadQuickPlayAsync(HttpClient client, string url = "/api/quick-play") =>
        Assert.IsType<QuickPlayResponse>(await client.GetFromJsonAsync<QuickPlayResponse>(url, Cancellation));

    private async Task<QuickPlayResponse> ChangeQuickPlayAsync(HttpClient client, Guid id, string action)
    {
        using HttpResponseMessage response = await client.PostAsync($"/api/quick-play/{id}/{action}", null, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<QuickPlayResponse>(await response.Content.ReadFromJsonAsync<QuickPlayResponse>(Cancellation));
    }

    private WebApplication BuildApp()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            Args = []
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:platform"] = postgres.GetConnectionString(),
            ["Identity:EmailConfirmation:AutoConfirmOnRegistration"] = "true",
            ["Identity:Jwt:SigningKeyId"] = "game-http-test",
            ["Identity:Jwt:PrivateKeyPem"] = signingKey.ExportPkcs8PrivateKeyPem()
        });
        builder.AddInfrastructure();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddScoped<GameService>();
        builder.Services.AddScoped<LobbyService>();
        builder.Services.AddScoped<QuickPlayService>();
        builder.Services.AddIdentityAuthentication(builder.Configuration);
        builder.Services.AddControllers().AddApplicationPart(typeof(GamesController).Assembly);
        builder.Services.AddSignalR();
        builder.Services.AddProblemDetails();
        WebApplication server = builder.Build();
        server.UseExceptionHandler();
        server.UseRateLimiter();
        server.UseWebSockets();
        server.UseAuthentication();
        server.UseAuthorization();
        server.MapControllers();
        server.MapHub<GameHub>(GameHub.Path, options => options.Transports = HttpTransportType.WebSockets);
        return server;
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }

    private Uri LiveUri(string? token = null) => new UriBuilder(Assert.Single(app!.Urls))
    {
        Scheme = "ws",
        Path = GameHub.Path,
        Query = token is null ? string.Empty : $"access_token={Uri.EscapeDataString(token)}"
    }.Uri;

    private async Task ConnectLiveAsync(ClientWebSocket socket, string token)
    {
        await socket.ConnectAsync(LiveUri(token), Cancellation);
        await SendLiveMessageAsync(socket, new { protocol = "json", version = 1 });
        Assert.False((await ReadLiveMessageAsync(socket)).TryGetProperty("error", out _));
    }

    private async Task<JsonElement> SubscribeAsync(ClientWebSocket socket, Guid gameId)
    {
        await SendLiveMessageAsync(socket, new { type = 1, invocationId = "join", target = "Subscribe", arguments = new[] { gameId } });
        JsonElement reply = await ReadLiveMessageAsync(socket);
        Assert.Equal("join", reply.GetProperty("invocationId").GetString());
        return reply;
    }

    private Task SendLiveMessageAsync(ClientWebSocket socket, object message) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + '\u001e')),
            WebSocketMessageType.Text, true, Cancellation);

    private async Task<JsonElement> ReadLiveMessageAsync(ClientWebSocket socket)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        byte[] buffer = new byte[4096];
        while (true)
        {
            WebSocketReceiveResult received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            Assert.True(received.EndOfMessage && buffer[received.Count - 1] == 0x1e);
            JsonElement message = JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan(0, received.Count - 1));
            if (!message.TryGetProperty("type", out JsonElement type) || type.GetInt32() != 6)
            {
                return message;
            }
        }
    }

    private HttpClient CreateClient(string? token = null)
    {
        HttpClient client = new() { BaseAddress = new Uri(Assert.Single(app!.Urls)) };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    private async Task<GameListResponse> ListGamesAsync(HttpClient client, string query = "") =>
        Assert.IsType<GameListResponse>(await client.GetFromJsonAsync<GameListResponse>("/api/games" + query, Cancellation));

    private static string PositionRecord(string fen) =>
        JsonSerializer.Serialize(new { schemaVersion = 1, initialFen = fen, moves = Array.Empty<string>() });

    private async Task<(Guid Id, string Token)> RegisterAndSignInAsync(string name)
    {
        using HttpClient client = CreateClient();
        string email = $"{name}@example.test";
        using HttpResponseMessage registered = await client.PostAsJsonAsync("/api/identity/register",
            new RegisterRequest(email, name, Password), Cancellation);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var registration = await registered.Content.ReadFromJsonAsync<RegistrationResult>(Cancellation);
        using HttpResponseMessage signedIn = await client.PostAsJsonAsync("/api/identity/login",
            new LoginRequest(email, Password), Cancellation);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var login = await signedIn.Content.ReadFromJsonAsync<LoginResult>(Cancellation);
        return (Assert.IsType<Guid>(registration?.UserId), Assert.IsType<string>(login?.AccessToken));
    }

    private async Task<GameResponse> CreateGameAsync(HttpClient client, Guid opponentId)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/games", new CreateGameRequest(opponentId), Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        GameResponse game = Assert.IsType<GameResponse>(await response.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal($"/api/games/{game.Id}", response.Headers.Location?.AbsolutePath);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return game;
    }

    private async Task<LobbyResponse> CreateLobbyAsync(HttpClient client, CreateLobbyRequest? request = null)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/lobbies",
            request ?? new CreateLobbyRequest(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        LobbyResponse lobby = Assert.IsType<LobbyResponse>(await response.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        Assert.Equal($"/api/lobbies/{lobby.Id}", response.Headers.Location?.AbsolutePath);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return lobby;
    }

    private async Task<GameResponse> AcceptGameAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"{url}/accept", new GameRevisionRequest(0), Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<GameResponse>(await response.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
    }

    private Task<HttpResponseMessage> SendActionAsync(HttpClient client, string url, string action, long revision) =>
        client.PostAsJsonAsync($"{url}/{action}", new GameRevisionRequest(revision), Cancellation);

    private async Task<GameResponse> ApplyActionAsync(HttpClient client, string url, string action, long revision)
    {
        using HttpResponseMessage response = await SendActionAsync(client, url, action, revision);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GameResponse game = Assert.IsType<GameResponse>(await response.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(revision + 1, game.Revision);
        return game;
    }

    private async Task<GameBoardResponse> ReadBoardAsync(HttpClient client, string url) =>
        Assert.IsType<GameBoardResponse>(await client.GetFromJsonAsync<GameBoardResponse>($"{url}/board", Cancellation));

    private async Task<GameReplayResponse> ReadReplayAsync(HttpClient client, string url) =>
        Assert.IsType<GameReplayResponse>(await client.GetFromJsonAsync<GameReplayResponse>($"{url}/replay", Cancellation));

    private Task<HttpResponseMessage> PlayAsync(HttpClient client, string url, string move, long revision) =>
        client.PostAsJsonAsync($"{url}/moves", new PlayGameMoveRequest(revision, move), Cancellation);

    private async Task<GameResponse> MakeMoveAsync(HttpClient client, string url, string move, long revision)
    {
        using HttpResponseMessage response = await PlayAsync(client, url, move, revision);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GameResponse game = Assert.IsType<GameResponse>(await response.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(revision + 1, game.Revision);
        return game;
    }

    private async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        Assert.Equal(code, body.GetProperty("code").GetString());
    }
}
