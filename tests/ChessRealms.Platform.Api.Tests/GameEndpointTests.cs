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

        GameResponse legacyFirst = await CreateGameAsync(white, blackId);
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
        Game firstGame = new(creator, opponent, position, DateTimeOffset.UtcNow);
        Game secondGame = new(creator, opponent, position, DateTimeOffset.UtcNow);
        IGameStore firstStore = first.ServiceProvider.GetRequiredService<IGameStore>();
        IGameStore secondStore = second.ServiceProvider.GetRequiredService<IGameStore>();

        Game[] saved = await Task.WhenAll(firstStore.CreateOrGetAsync(firstGame, requestId, Cancellation),
            secondStore.CreateOrGetAsync(secondGame, requestId, Cancellation));

        Assert.Equal(saved[0].Id, saved[1].Id);
        Assert.Contains(saved[0].Id, new[] { firstGame.Id, secondGame.Id });
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
        DateTimeOffset createdAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
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
        (Guid whiteId, string whiteToken) = await RegisterAndSignInAsync("white");
        (Guid blackId, string blackToken) = await RegisterAndSignInAsync("black");
        using HttpClient white = CreateClient(whiteToken);
        using HttpClient black = CreateClient(blackToken);
        GameResponse pending = await CreateGameAsync(white, blackId);
        string gameUrl = $"/api/games/{pending.Id}";
        await AcceptGameAsync(black, gameUrl);

        await using AsyncServiceScope first = app!.Services.CreateAsyncScope();
        await using AsyncServiceScope second = app.Services.CreateAsyncScope();
        IGameStore firstStore = first.ServiceProvider.GetRequiredService<IGameStore>();
        IGameStore secondStore = second.ServiceProvider.GetRequiredService<IGameStore>();
        IGameRules rules = first.ServiceProvider.GetRequiredService<IGameRules>();
        Game firstGame = Assert.IsType<Game>(await firstStore.FindAsync(pending.Id, Cancellation));
        Game secondGame = Assert.IsType<Game>(await secondStore.FindAsync(pending.Id, Cancellation));
        Assert.Equal(GameError.None, firstGame.Play(whiteId, 1, record => rules.Play(record, "e2e4")));
        GameError secondAction = resign
            ? secondGame.Resign(blackId, 1, rules.HasInsufficientMatingMaterial)
            : secondGame.Play(whiteId, 1, record => rules.Play(record, "d2d4"));
        Assert.Equal(GameError.None, secondAction);

        bool[] saved = await Task.WhenAll(firstStore.TrySaveChangesAsync(Cancellation), secondStore.TrySaveChangesAsync(Cancellation));
        Assert.Single(saved, success => success);
        GameResponse persisted = Assert.IsType<GameResponse>(await white.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));
        Assert.Equal(2, persisted.Revision);
        Assert.Equal(saved[0] ? firstGame.Fen : secondGame.Fen, persisted.Fen);
        Assert.Equal(resign && saved[1] ? "Resignation" : null, persisted.EndReason);
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
        IGameStore store = scope.ServiceProvider.GetRequiredService<IGameStore>();
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
            await store.CreateOrGetAsync(game, null, Cancellation);
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
            await scope.ServiceProvider.GetRequiredService<IGameStore>().CreateOrGetAsync(game, null, Cancellation);
        }
        string url = $"/api/games/{game.Id}";
        GameResponse active = await AcceptGameAsync(black, url);
        Assert.Equal(["FiftyMoveRule"], (await ReadBoardAsync(white, url)).DrawClaims);
        using HttpResponseMessage claimed = await white.PostAsJsonAsync($"{url}/draw/claim",
            new ClaimGameDrawRequest(active.Revision, "FiftyMoveRule", null), Cancellation);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        GameBoardResponse finished = await ReadBoardAsync(black, url);
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
        builder.Services.AddScoped<GameService>();
        builder.Services.AddIdentityAuthentication(builder.Configuration);
        builder.Services.AddControllers().AddApplicationPart(typeof(GamesController).Assembly);
        builder.Services.AddSignalR();
        builder.Services.AddProblemDetails();
        WebApplication server = builder.Build();
        server.UseExceptionHandler();
        server.UseWebSockets();
        server.UseAuthentication();
        server.UseAuthorization();
        server.MapControllers();
        server.MapHub<GameHub>(GameHub.Path, options => options.Transports = HttpTransportType.WebSockets);
        return server;
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
