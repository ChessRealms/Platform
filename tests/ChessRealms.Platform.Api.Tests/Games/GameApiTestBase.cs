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
using ChessRealms.Platform.Infrastructure;
using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Testcontainers.PostgreSql;

using Xunit;

namespace ChessRealms.Platform.Api.Tests.Games;

// Keep PostgreSQL integration tests in one collection to limit concurrent containers.
[Collection("Games API")]
public abstract class GameApiTestBase : IAsyncLifetime
{
    private const string Password = "Synthetic-player-password9!";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.3").Build();
    private readonly RSA signingKey = RSA.Create(2048);
    private WebApplication? app;

    protected TestClock Clock { get; } = new();
    protected CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync(Cancellation);
        app = BuildApp();
        await using (AsyncServiceScope scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync(Cancellation);
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync(Cancellation);
        }
        await app.StartAsync(Cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }
        finally
        {
            signingKey.Dispose();
            await postgres.DisposeAsync();
        }
    }

    protected AsyncServiceScope CreateScope() => app!.Services.CreateAsyncScope();

    protected async Task RestartApiAsync()
    {
        await app!.StopAsync(Cancellation);
        await app.DisposeAsync();
        app = null;

        app = BuildApp();
        await app.StartAsync(Cancellation);
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
        builder.Services.AddSingleton<TimeProvider>(Clock);
        builder.Services.AddScoped<GameService>();
        builder.Services.AddScoped<LobbyService>();
        builder.Services.AddScoped<QuickPlayService>();
        builder.Services.AddIdentityAuthentication(builder.Configuration);
        builder.Services.AddControllers().AddApplicationPart(typeof(GamesController).Assembly);
        builder.Services.AddGameRealtime();
        builder.Services.AddProblemDetails();
        WebApplication server = builder.Build();
        server.UseExceptionHandler();
        server.UseRateLimiter();
        server.UseWebSockets();
        server.UseAuthentication();
        server.UseAuthorization();
        server.MapControllers();
        server.MapGameHub();
        return server;
    }

    protected HttpClient CreateClient(string? token = null)
    {
        HttpClient client = new() { BaseAddress = new Uri(Assert.Single(app!.Urls)) };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    protected async Task<GameListResponse> ListGamesAsync(HttpClient client, string query = "") =>
        Assert.IsType<GameListResponse>(await client.GetFromJsonAsync<GameListResponse>("/api/games" + query, Cancellation));

    protected static string PositionRecord(string fen) =>
        JsonSerializer.Serialize(new { schemaVersion = 1, initialFen = fen, moves = Array.Empty<string>() });

    protected async Task<(Guid Id, string Token)> RegisterAndSignInAsync(string name)
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

    protected async Task<GameResponse> CreateGameAsync(HttpClient client, Guid opponentId)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/games", new CreateGameRequest(opponentId), Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        GameResponse game = Assert.IsType<GameResponse>(await response.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal($"/api/games/{game.Id}", response.Headers.Location?.AbsolutePath);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return game;
    }

    protected async Task<LobbyResponse> CreateLobbyAsync(HttpClient client, CreateLobbyRequest? request = null)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/lobbies",
            request ?? new CreateLobbyRequest(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        LobbyResponse lobby = Assert.IsType<LobbyResponse>(await response.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        Assert.Equal($"/api/lobbies/{lobby.Id}", response.Headers.Location?.AbsolutePath);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return lobby;
    }

    protected async Task<GameResponse> AcceptGameAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"{url}/accept", new GameRevisionRequest(0), Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<GameResponse>(await response.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
    }

    protected Task<HttpResponseMessage> SendActionAsync(HttpClient client, string url, string action, long revision) =>
        client.PostAsJsonAsync($"{url}/{action}", new GameRevisionRequest(revision), Cancellation);

    protected async Task<GameResponse> ApplyActionAsync(HttpClient client, string url, string action, long revision)
    {
        using HttpResponseMessage response = await SendActionAsync(client, url, action, revision);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GameResponse game = Assert.IsType<GameResponse>(await response.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(revision + 1, game.Revision);
        return game;
    }

    protected async Task<GameBoardResponse> ReadBoardAsync(HttpClient client, string url) =>
        Assert.IsType<GameBoardResponse>(await client.GetFromJsonAsync<GameBoardResponse>($"{url}/board", Cancellation));

    protected async Task<GameReplayResponse> ReadReplayAsync(HttpClient client, string url) =>
        Assert.IsType<GameReplayResponse>(await client.GetFromJsonAsync<GameReplayResponse>($"{url}/replay", Cancellation));

    protected async Task<string> DownloadPgnAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync($"{url}/pgn", Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-chess-pgn", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal($"chessrealms-{url.Split('/')[^1]}.pgn", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return await response.Content.ReadAsStringAsync(Cancellation);
    }

    protected Task<HttpResponseMessage> PlayAsync(HttpClient client, string url, string move, long revision) =>
        client.PostAsJsonAsync($"{url}/moves", new PlayGameMoveRequest(revision, move), Cancellation);

    protected async Task<GameResponse> MakeMoveAsync(HttpClient client, string url, string move, long revision)
    {
        using HttpResponseMessage response = await PlayAsync(client, url, move, revision);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GameResponse game = Assert.IsType<GameResponse>(await response.Content.ReadFromJsonAsync<GameResponse>(Cancellation));
        Assert.Equal(revision + 1, game.Revision);
        return game;
    }

    protected async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        Assert.Equal(code, body.GetProperty("code").GetString());
    }

    protected Uri LiveUri(string? token = null) => new UriBuilder(Assert.Single(app!.Urls))
    {
        Scheme = "ws",
        Path = GameHub.Path,
        Query = token is null ? string.Empty : $"access_token={Uri.EscapeDataString(token)}"
    }.Uri;

    protected async Task ConnectLiveAsync(ClientWebSocket socket, string token)
    {
        await socket.ConnectAsync(LiveUri(token), Cancellation);
        await SendLiveMessageAsync(socket, new { protocol = "json", version = 1 });
        Assert.False((await ReadLiveMessageAsync(socket)).TryGetProperty("error", out _));
    }

    protected Task<JsonElement> SubscribeAsync(ClientWebSocket socket, Guid gameId) =>
        InvokeLiveAsync(socket, "Subscribe", [gameId]);

    protected Task<JsonElement> SubscribeMyGamesAsync(ClientWebSocket socket) =>
        InvokeLiveAsync(socket, "SubscribeMyGames", []);

    private async Task<JsonElement> InvokeLiveAsync(ClientWebSocket socket, string target, object[] arguments)
    {
        await SendLiveMessageAsync(socket, new { type = 1, invocationId = "join", target, arguments });
        JsonElement reply = await ReadLiveMessageAsync(socket);
        Assert.Equal("join", reply.GetProperty("invocationId").GetString());
        return reply;
    }

    private Task SendLiveMessageAsync(ClientWebSocket socket, object message) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + '\u001e')),
            WebSocketMessageType.Text, true, Cancellation);

    protected async Task<JsonElement> ReadLiveMessageAsync(ClientWebSocket socket)
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

    protected sealed class TestClock : TimeProvider
    {
        private long ticks = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }
}
