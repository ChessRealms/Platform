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
            ? secondGame.Resign(blackId, 1)
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
        string record = JsonSerializer.Serialize(new { schemaVersion = 1, initialFen = fen, moves = Array.Empty<string>() });
        Game game = new(whiteId, blackId, new(record, fen, PlayerColor.White));
        await using (AsyncServiceScope scope = app!.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IGameStore>().CreateAsync(game, Cancellation);
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
