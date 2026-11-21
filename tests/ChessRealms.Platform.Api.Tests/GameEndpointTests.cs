using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using ChessRealms.Platform.Api;
using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        using HttpResponseMessage hiddenMutation = await PlayAsync(outsider, gameUrl, "e2e4", 0);
        await AssertErrorAsync(hiddenMutation, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage pendingMove = await PlayAsync(white, gameUrl, "e2e4", 0);
        await AssertErrorAsync(pendingMove, HttpStatusCode.Conflict, "GameNotActive");
        using HttpResponseMessage selfAccept = await white.PostAsJsonAsync($"{gameUrl}/accept", new AcceptGameRequest(0), Cancellation);
        await AssertErrorAsync(selfAccept, HttpStatusCode.Forbidden, "Forbidden");
        using HttpResponseMessage missingRevision = await black.PostAsJsonAsync($"{gameUrl}/accept", new { }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, missingRevision.StatusCode);

        GameResponse active = await AcceptGameAsync(black, gameUrl);
        Assert.Equal("Active", active.Status);
        Assert.Equal("White", active.SideToMove);
        Assert.Equal(1, active.Revision);
        using HttpResponseMessage wrongTurn = await PlayAsync(black, gameUrl, "e7e5", 1);
        await AssertErrorAsync(wrongTurn, HttpStatusCode.Conflict, "NotYourTurn");
        using HttpResponseMessage illegal = await PlayAsync(white, gameUrl, "e2e5", 1);
        await AssertErrorAsync(illegal, HttpStatusCode.UnprocessableEntity, "IllegalMove");
        Assert.Equal(active, await white.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));

        GameResponse whiteMove = await MakeMoveAsync(white, gameUrl, "e2e4", 1);
        Assert.Equal("Black", whiteMove.SideToMove);
        Assert.Equal(whiteMove, await black.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));
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

    [Fact]
    public async Task TwoTrackedWritesOnOneRevisionSaveOnlyOneMove()
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
        Assert.Equal(GameError.None, secondGame.Play(whiteId, 1, record => rules.Play(record, "d2d4")));

        bool[] saved = await Task.WhenAll(firstStore.TrySaveChangesAsync(Cancellation), secondStore.TrySaveChangesAsync(Cancellation));
        Assert.Single(saved, success => success);
        GameResponse persisted = Assert.IsType<GameResponse>(await white.GetFromJsonAsync<GameResponse>(gameUrl, Cancellation));
        Assert.Equal(2, persisted.Revision);
        Assert.Equal(saved[0] ? firstGame.Fen : secondGame.Fen, persisted.Fen);
        using HttpResponseMessage stale = await PlayAsync(white, gameUrl, "e2e4", 1);
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, "RevisionConflict");
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
        builder.Services.AddProblemDetails();
        WebApplication server = builder.Build();
        server.UseExceptionHandler();
        server.UseAuthentication();
        server.UseAuthorization();
        server.MapControllers();
        return server;
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
        using HttpResponseMessage response = await client.PostAsJsonAsync($"{url}/accept", new AcceptGameRequest(0), Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<GameResponse>(await response.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
    }

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
