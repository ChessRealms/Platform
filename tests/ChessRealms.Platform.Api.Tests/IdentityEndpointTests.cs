using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

using ChessRealms.Platform.Api;
using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Application.Identity;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace ChessRealms.Platform.Api.Tests;

public sealed class IdentityEndpointTests : IAsyncLifetime
{
    private readonly StubIdentityServices _services = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            Args = []
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        using RSA rsa = RSA.Create(2048);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:Jwt:SigningKeyId"] = "http-test",
            ["Identity:Jwt:PrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem()
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddIdentityAuthentication(builder.Configuration);
        builder.Services.AddSingleton<IRegistrationService>(_services);
        builder.Services.AddSingleton<IEmailConfirmationService>(_services);
        builder.Services.AddSingleton<ILoginService>(_services);
        builder.Services.AddSingleton<IRefreshTokenService>(_services);
        builder.Services.AddSingleton<ILoginSessionService>(_services);
        builder.Services.AddControllers().AddApplicationPart(typeof(IdentityController).Assembly);
        builder.Services.AddProblemDetails();
        _app = builder.Build();
        _app.UseExceptionHandler();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        await _app.StartAsync(Cancellation);
        _client = new() { BaseAddress = new Uri(Assert.Single(_app.Urls)) };

    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task RegistrationValidatesBodyAndReturnsConfirmationMetadata()
    {
        using HttpResponseMessage invalid = await _client.PostAsJsonAsync("/api/identity/register", new { email = "test@example.test" }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(0, _services.Registrations);

        using HttpResponseMessage created = await _client.PostAsJsonAsync("/api/identity/register",
            new RegisterRequest("test@example.test", "player", "Password9!"), Cancellation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(created.Headers.CacheControl!.NoStore);
        RegistrationResult? registration = await created.Content.ReadFromJsonAsync<RegistrationResult>(Cancellation);
        Assert.Equal("session-token", registration!.EmailConfirmation!.SessionToken);
    }

    [Fact]
    public async Task RegistrationCanSucceedWithoutConfirmationMetadata()
    {
        _services.AutoConfirmRegistration = true;
        using HttpResponseMessage created = await _client.PostAsJsonAsync("/api/identity/register",
            new RegisterRequest("test@example.test", "player", "Password9!"), Cancellation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string json = await created.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain("emailConfirmation", json);
        Assert.NotNull((await created.Content.ReadFromJsonAsync<RegistrationResult>(Cancellation))!.UserId);
    }

    [Fact]
    public async Task SessionRoutesRequireJwtAndDoNotUseRefreshToken()
    {
        using HttpResponseMessage unauthenticated = await _client.GetAsync("/api/identity/sessions", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Guid userId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        AccessToken token = _app.Services.GetRequiredService<IAccessTokenIssuer>().Issue(userId, sessionId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        using HttpResponseMessage listing = await _client.GetAsync("/api/identity/sessions", Cancellation);
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        Assert.True(listing.Headers.CacheControl!.NoStore);
        Assert.Equal(userId, _services.LastUserId);
        Assert.Equal(sessionId, _services.LastCurrentSessionId);
        using HttpResponseMessage revoked = await _client.DeleteAsync($"/api/identity/sessions/{Guid.NewGuid()}", Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using HttpResponseMessage all = await _client.PostAsync("/api/identity/logout-all", null, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, all.StatusCode);
        using HttpResponseMessage logout = await _client.PostAsync("/api/identity/logout", null, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        _services.SessionResult = SessionOperationResult.Unauthorized;
        using HttpResponseMessage denied = await _client.GetAsync("/api/identity/sessions", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using HttpResponseMessage ordinaryApi = await _client.GetAsync("/api/identity/me", Cancellation);
        Assert.Equal(HttpStatusCode.OK, ordinaryApi.StatusCode);
    }

    [Fact]
    public async Task ConfirmationEndpointsUseOpaqueSessionToken()
    {
        using HttpResponseMessage missingToken = await _client.GetAsync("/api/identity/email-confirmation", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, missingToken.StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "session-token");
        using HttpResponseMessage limited = await _client.PostAsync("/api/identity/email-confirmation/resend", null, Cancellation);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("session-token", _services.LastToken);

        using HttpResponseMessage confirmed = await _client.GetAsync($"/api/identity/confirm-email?userId={Guid.NewGuid()}&token=link-token", Cancellation);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("link-token", _services.LastToken);
        Assert.Equal("no-referrer", Assert.Single(confirmed.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task LoginAndMeRespectAuthenticationBoundaries()
    {
        using HttpResponseMessage opaqueOnMe = await _client.GetAsync("/api/identity/me", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, opaqueOnMe.StatusCode);
        _client.DefaultRequestHeaders.Authorization = null;
        using HttpResponseMessage invalidLogin = await _client.PostAsJsonAsync("/api/identity/login", new { email = "player@example.test" }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, invalidLogin.StatusCode);
        LoginRequest login = new("player@example.test", "Password9!");
        using HttpResponseMessage deniedLogin = await _client.PostAsJsonAsync("/api/identity/login", login, Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedLogin.StatusCode);
        _services.FailLogin = true;
        using HttpResponseMessage failedLogin = await _client.PostAsJsonAsync("/api/identity/login", login, Cancellation);
        Assert.Equal(HttpStatusCode.InternalServerError, failedLogin.StatusCode);
        Assert.Equal("application/problem+json", failedLogin.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Could not update login attempt state", await failedLogin.Content.ReadAsStringAsync(Cancellation));
        _services.FailLogin = false;
        _services.NextLogin = new("EmailNotConfirmed", EmailConfirmation: _services.Metadata);
        using HttpResponseMessage unconfirmedLogin = await _client.PostAsJsonAsync("/api/identity/login", login, Cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, unconfirmedLogin.StatusCode);
        Assert.Equal("session-token", (await unconfirmedLogin.Content.ReadFromJsonAsync<LoginResult>(Cancellation))!.EmailConfirmation!.SessionToken);
        Guid userId = Guid.NewGuid();
        AccessToken token = _app.Services.GetRequiredService<IAccessTokenIssuer>().Issue(userId, Guid.NewGuid());
        DateTimeOffset refreshExpiration = DateTimeOffset.UtcNow.AddDays(30);
        _services.NextLogin = new("Success", token.Value, "Bearer", token.ExpiresIn,
            RefreshToken: "initial-refresh-token", RefreshTokenExpiresAt: refreshExpiration);
        using HttpResponseMessage successfulLogin = await _client.PostAsJsonAsync("/api/identity/login", login, Cancellation);
        Assert.Equal(HttpStatusCode.OK, successfulLogin.StatusCode);
        Assert.True(successfulLogin.Headers.CacheControl!.NoStore);
        LoginResult? loginResult = await successfulLogin.Content.ReadFromJsonAsync<LoginResult>(Cancellation);
        Assert.Equal(token.Value, loginResult!.AccessToken);
        Assert.Equal(600, loginResult.ExpiresIn);
        Assert.Equal("initial-refresh-token", loginResult.RefreshToken);
        Assert.Equal(refreshExpiration, loginResult.RefreshTokenExpiresAt);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.AccessToken);
        using HttpResponseMessage me = await _client.GetAsync("/api/identity/me", Cancellation);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Dictionary<string, Guid>? identity = await me.Content.ReadFromJsonAsync<Dictionary<string, Guid>>(Cancellation);
        Assert.Equal(userId, identity!["userId"]);
        using HttpResponseMessage oldRoute = await _client.PostAsJsonAsync("/auth/login", login, Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, oldRoute.StatusCode);
        using HttpResponseMessage oldIdentityRoute = await _client.PostAsJsonAsync("/identity/login", login, Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, oldIdentityRoute.StatusCode);
    }

    [Fact]
    public async Task RefreshUsesItsOwnCredentialAndRejectsMalformedBodies()
    {
        AccessToken token = _app.Services.GetRequiredService<IAccessTokenIssuer>().Issue(Guid.NewGuid(), Guid.NewGuid());
        DateTimeOffset refreshExpiration = DateTimeOffset.UtcNow.AddDays(30);
        // Refresh is authenticated by the request token, not by a current access JWT.
        _client.DefaultRequestHeaders.Authorization = null;
        using HttpResponseMessage missingRefresh = await _client.PostAsJsonAsync("/api/identity/refresh", new { }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, missingRefresh.StatusCode);
        using HttpResponseMessage oversizedRefresh = await _client.PostAsJsonAsync("/api/identity/refresh", new RefreshRequest(new string('x', 1025)), Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, oversizedRefresh.StatusCode);
        RefreshRequest refreshRequest = new("initial-refresh-token");
        using HttpResponseMessage deniedRefresh = await _client.PostAsJsonAsync("/api/identity/refresh", refreshRequest, Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedRefresh.StatusCode);
        Assert.Equal(new RefreshResult("InvalidRefreshToken"), await deniedRefresh.Content.ReadFromJsonAsync<RefreshResult>(Cancellation));
        _services.NextRefresh = new("Success", token.Value, "Bearer", token.ExpiresIn, "rotated-refresh-token", refreshExpiration);
        using HttpResponseMessage successfulRefresh = await _client.PostAsJsonAsync("/api/identity/refresh", refreshRequest, Cancellation);
        Assert.Equal(HttpStatusCode.OK, successfulRefresh.StatusCode);
        Assert.True(successfulRefresh.Headers.CacheControl!.NoStore);
        Assert.Equal(_services.NextRefresh, await successfulRefresh.Content.ReadFromJsonAsync<RefreshResult>(Cancellation));
        Assert.Equal(refreshRequest.RefreshToken, _services.LastRefreshToken);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "expired-or-invalid-access-token");
        using HttpResponseMessage expiredAccessRefresh = await _client.PostAsJsonAsync("/api/identity/refresh", refreshRequest, Cancellation);
        Assert.Equal(HttpStatusCode.OK, expiredAccessRefresh.StatusCode);
        _services.FailRefresh = true;
        using HttpResponseMessage failedRefresh = await _client.PostAsJsonAsync("/api/identity/refresh", refreshRequest, Cancellation);
        Assert.Equal(HttpStatusCode.InternalServerError, failedRefresh.StatusCode);
        Assert.DoesNotContain("Simulated refresh persistence failure", await failedRefresh.Content.ReadAsStringAsync(Cancellation));
    }

    [Theory]
    [InlineData("POST", "/api/identity/login")]
    [InlineData("POST", "/api/identity/refresh")]
    [InlineData("GET", "/api/identity/email-confirmation")]
    [InlineData("POST", "/api/identity/email-confirmation/resend")]
    [InlineData("GET", "/api/identity/confirm-email?userId=11111111-1111-1111-1111-111111111111&token=test")]
    public async Task UnexpectedServiceResultsAreServerErrors(string method, string path)
    {
        _services.NextLogin = new("Unexpected");
        _services.NextRefresh = new("Unexpected");
        _services.ConfirmationCode = "Unexpected";
        _services.StatusCode = "Unexpected";
        _services.ResendCode = "Unexpected";
        using HttpRequestMessage request = new(new HttpMethod(method), path);
        if (path == "/api/identity/login")
        {
            request.Content = JsonContent.Create(new LoginRequest("player@example.test", "Password9!"));
        }
        else if (path == "/api/identity/refresh")
        {
            request.Content = JsonContent.Create(new RefreshRequest("refresh-token"));
        }
        using HttpResponseMessage response = await _client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Unexpected", await response.Content.ReadAsStringAsync(Cancellation));
    }

    private sealed class StubIdentityServices : IRegistrationService, IEmailConfirmationService, ILoginService,
        IRefreshTokenService, ILoginSessionService
    {
        public ConfirmationMetadata Metadata { get; } = new("session-token", DateTimeOffset.UtcNow.AddMinutes(30), "Queued", DateTimeOffset.UtcNow.AddMinutes(1), null);
        public string ConfirmationCode { get; set; } = "EmailConfirmed";
        public string StatusCode { get; set; } = "InvalidSession";
        public string ResendCode { get; set; } = "RateLimited";
        public RefreshResult NextRefresh { get; set; } = new("InvalidRefreshToken");
        public string? LastRefreshToken { get; private set; }
        public bool FailRefresh { get; set; }
        public Task<RefreshResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
        {
            LastRefreshToken = request.RefreshToken;
            return FailRefresh ? throw new InvalidOperationException("Simulated refresh persistence failure.") : Task.FromResult(NextRefresh);
        }

        public LoginResult NextLogin { get; set; } = new("InvalidCredentials");
        public bool FailLogin { get; set; }
        public Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken) =>
            FailLogin ? throw new InvalidOperationException("Could not update login attempt state.") : Task.FromResult(NextLogin);

        public int Registrations { get; private set; }
        public bool AutoConfirmRegistration { get; set; }
        public Guid LastUserId { get; private set; }
        public Guid LastCurrentSessionId { get; private set; }
        public SessionOperationResult SessionResult { get; set; } = SessionOperationResult.Success;
        public string? LastToken { get; private set; }
        public Task<RegistrationResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
        {
            Registrations++;
            return Task.FromResult(new RegistrationResult(Guid.NewGuid(),
                AutoConfirmRegistration ? null : Metadata, []));
        }
        public Task<ConfirmationSessionResult> GetStatusAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new ConfirmationSessionResult(StatusCode, null));
        public Task<ConfirmationSessionResult> ResendAsync(string token, CancellationToken cancellationToken)
        {
            LastToken = token;
            return Task.FromResult(new ConfirmationSessionResult(ResendCode, null));
        }
        public Task<string> ConfirmAsync(Guid userId, string token, CancellationToken cancellationToken)
        {
            LastToken = token;
            return Task.FromResult(ConfirmationCode);
        }
        public Task<SessionListResult> ListAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken)
        {
            LastUserId = userId;
            LastCurrentSessionId = currentSessionId;
            return Task.FromResult(new SessionListResult(SessionResult, []));
        }
        public Task<SessionOperationResult> LogoutAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken) =>
            Task.FromResult(SessionResult);
        public Task<SessionOperationResult> RevokeAsync(Guid userId, Guid currentSessionId, Guid sessionId,
            CancellationToken cancellationToken) => Task.FromResult(SessionResult);
        public Task<SessionOperationResult> LogoutAllAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken) =>
            Task.FromResult(SessionResult);
    }
}
