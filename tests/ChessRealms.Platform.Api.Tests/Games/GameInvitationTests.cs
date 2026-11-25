using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace ChessRealms.Platform.Api.Tests.Games;

public sealed class GameInvitationTests : GameApiTestBase
{
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
        await using (AsyncServiceScope scope = CreateScope())
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
        await using AsyncServiceScope first = CreateScope();
        await using AsyncServiceScope second = CreateScope();
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
    public async Task InvitationExpiryMigrationBackfillsPendingRows()
    {
        await using AsyncServiceScope scope = CreateScope();
        PlatformDbContext database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        GamePosition position = scope.ServiceProvider.GetRequiredService<IGameRules>().StartingPosition();
        DateTimeOffset now = Clock.GetUtcNow();
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
        Assert.Equal(Clock.GetUtcNow().AddMinutes(5), pending.InvitationExpiresAt);
        string url = $"/api/games/{pending.Id}";
        string boardUrl = $"{url}/board?afterRevision={pending.Revision}";

        Clock.Advance(TimeSpan.FromMilliseconds(299999));
        using HttpResponseMessage unchanged = await black.GetAsync(boardUrl, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);
        GameListResponse beforeExpiry = await ListGamesAsync(black);
        Assert.Single(beforeExpiry.Items);
        Assert.Equal(pending.InvitationExpiresAt!.Value.AddMilliseconds(-1), beforeExpiry.ServerTime);
        Assert.Empty((await ListGamesAsync(black, "?status=ClosedInvitations")).Items);

        Clock.Advance(TimeSpan.FromMilliseconds(1));
        using HttpResponseMessage changed = await black.GetAsync(boardUrl, Cancellation);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        GameBoardResponse board = Assert.IsType<GameBoardResponse>(await changed.Content.ReadFromJsonAsync<GameBoardResponse>(Cancellation));
        GameResponse expired = pending with { Status = "Expired", Revision = pending.Revision + 1, ServerTime = Clock.GetUtcNow() };
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

        Clock.Advance(TimeSpan.FromMilliseconds(299999));
        GameResponse accepted = await AcceptGameAsync(black, $"/api/games/{fresh.Id}");
        Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(accepted with { ServerTime = Clock.GetUtcNow() },
            await black.GetFromJsonAsync<GameResponse>($"/api/games/{fresh.Id}", Cancellation));
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

            Clock.Advance(TimeSpan.FromMinutes(5));
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

        Clock.Advance(TimeSpan.FromMinutes(1));
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

        Clock.Advance(TimeSpan.FromMinutes(4));
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
}
