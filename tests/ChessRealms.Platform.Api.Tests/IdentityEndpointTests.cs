using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using ChessRealms.Platform.Api;
using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Application.Identity;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using SetCookieHeaderValue = Microsoft.Net.Http.Headers.SetCookieHeaderValue;

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
        builder.Services.AddSingleton<IUsernameDirectory>(_services);
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
        JsonElement identity = await me.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        Assert.Equal(userId, identity.GetProperty("userId").GetGuid());
        Assert.Equal("Player", identity.GetProperty("username").GetString());
        Assert.Equal(userId, _services.LastUserId);
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

    [Fact]
    public async Task WebLoginAndRefreshUseOnlyTheCookieForRefreshCredentials()
    {
        DateTimeOffset expires = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
        LoginRequest login = new("player@example.test", "Password9!");
        _services.NextLogin = new("EmailNotConfirmed", EmailConfirmation: _services.Metadata);
        using HttpResponseMessage unconfirmed = await WebPostAsync("login", body: login);
        Assert.Equal(HttpStatusCode.Forbidden, unconfirmed.StatusCode);
        Assert.False(unconfirmed.Headers.Contains("Set-Cookie"));
        Assert.Equal("session-token", (await unconfirmed.Content.ReadFromJsonAsync<LoginResult>(Cancellation))!.EmailConfirmation!.SessionToken);

        _services.NextLogin = new("Success", "access-token", "Bearer", 600,
            RefreshToken: "initial-refresh-token", RefreshTokenExpiresAt: expires);
        using HttpResponseMessage loginResponse = await WebPostAsync("login", body: login);
        await AssertWebCredentialsAsync(loginResponse, "initial-refresh-token", expires);

        using HttpResponseMessage jsonOnly = await WebPostAsync("refresh", body: new RefreshRequest("initial-refresh-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, jsonOnly.StatusCode);
        Assert.Null(_services.LastRefreshToken);

        _services.NextRefresh = new("Success", "access-token", "Bearer", 600, "rotated-refresh-token", expires);
        using HttpResponseMessage refresh = await WebPostAsync("refresh", cookie: "initial-refresh-token");
        Assert.Equal("initial-refresh-token", _services.LastRefreshToken);
        await AssertWebCredentialsAsync(refresh, "rotated-refresh-token", expires);

        _services.NextRefresh = new("InvalidRefreshToken");
        using HttpResponseMessage denied = await WebPostAsync("refresh", cookie: "rotated-refresh-token");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        AssertClearedCookie(denied);
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("logout-all")]
    public async Task WebLogoutRequiresBearerAndClearsCookieAfterRevocation(string route)
    {
        using HttpResponseMessage unauthenticated = await WebPostAsync(route, cookie: "refresh-token");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.False(unauthenticated.Headers.Contains("Set-Cookie"));

        Guid userId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        AccessToken token = _app.Services.GetRequiredService<IAccessTokenIssuer>().Issue(userId, sessionId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        using HttpResponseMessage logout = await WebPostAsync(route, cookie: "refresh-token");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal((userId, sessionId, route), (_services.LastUserId, _services.LastCurrentSessionId, _services.LastLogout));
        AssertClearedCookie(logout);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "cross-site")]
    [InlineData(true, "same-site")]
    public async Task WebRefreshRejectsCrossOriginAndSimpleRequests(bool sendHeader, string? site)
    {
        using HttpResponseMessage response = await WebPostAsync("refresh", cookie: "refresh-token",
            sendHeader: sendHeader, site: site);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(_services.LastRefreshToken);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private async Task<HttpResponseMessage> WebPostAsync(string route, object? body = null, string? cookie = null,
        bool sendHeader = true, string? site = "same-origin")
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/identity/web/" + route);
        if (sendHeader)
        {
            request.Headers.Add("X-ChessRealms-Web", "1");
        }
        if (site is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", site);
        }
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", "__Secure-ChessRealms.Refresh=" + cookie);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await _client.SendAsync(request, Cancellation);
    }

    private async Task AssertWebCredentialsAsync(HttpResponseMessage response, string expectedCookieValue,
        DateTimeOffset expires)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        SetCookieHeaderValue cookie = ReadWebCookie(response);
        Assert.Equal(expectedCookieValue, cookie.Value.Value);
        Assert.Equal(expires, cookie.Expires);
        string json = await response.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain("\"refreshToken\"", json);
        Assert.DoesNotContain(expectedCookieValue, json);
        Assert.Equal("access-token", (await response.Content.ReadFromJsonAsync<RefreshResult>(Cancellation))!.AccessToken);
    }

    private static SetCookieHeaderValue ReadWebCookie(HttpResponseMessage response)
    {
        SetCookieHeaderValue cookie = SetCookieHeaderValue.Parse(Assert.Single(response.Headers.GetValues("Set-Cookie")));
        Assert.Equal("__Secure-ChessRealms.Refresh", cookie.Name.Value);
        Assert.Equal("/api/identity/web", cookie.Path.Value);
        Assert.False(cookie.Domain.HasValue);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, cookie.SameSite);
        return cookie;
    }

    private static void AssertClearedCookie(HttpResponseMessage response)
    {
        SetCookieHeaderValue cookie = ReadWebCookie(response);
        Assert.Equal(0, cookie.Value.Length);
        Assert.True(cookie.Expires < DateTimeOffset.UtcNow);
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
        IRefreshTokenService, ILoginSessionService, IUsernameDirectory
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
        public string? LastLogout { get; private set; }
        public string? LastToken { get; private set; }
        public Task<Guid?> FindIdAsync(string username, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> FindUsernameAsync(Guid userId, CancellationToken cancellationToken)
        {
            LastUserId = userId;
            return Task.FromResult<string?>("Player");
        }

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
            RecordLogout(userId, currentSessionId, "logout");
        public Task<SessionOperationResult> RevokeAsync(Guid userId, Guid currentSessionId, Guid sessionId,
            CancellationToken cancellationToken) => Task.FromResult(SessionResult);
        public Task<SessionOperationResult> LogoutAllAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken) =>
            RecordLogout(userId, currentSessionId, "logout-all");

        private Task<SessionOperationResult> RecordLogout(Guid userId, Guid sessionId, string operation)
        {
            LastUserId = userId;
            LastCurrentSessionId = sessionId;
            LastLogout = operation;
            return Task.FromResult(SessionResult);
        }
    }
}
