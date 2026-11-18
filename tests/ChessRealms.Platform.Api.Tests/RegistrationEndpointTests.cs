using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

using ChessRealms.Platform.Api;
using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Application.Identity;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace ChessRealms.Platform.Api.Tests;

public sealed class RegistrationEndpointTests
{
    [Fact]
    public async Task HttpContractsValidateBodiesAndKeepSessionTokenInAuthorizationHeader()
    {
        StubRegistration service = new();
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
        builder.Services.AddSingleton<IRegistrationService>(service);
        builder.Services.AddSingleton<IEmailConfirmationService>(service);
        builder.Services.AddSingleton<ILoginService>(service);
        builder.Services.AddControllers().AddApplicationPart(typeof(IdentityController).Assembly);
        builder.Services.AddProblemDetails();
        await using WebApplication app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await app.StartAsync(cancellationToken);
        using HttpClient client = new() { BaseAddress = new Uri(Assert.Single(app.Urls)) };

        using HttpResponseMessage invalid = await client.PostAsJsonAsync("/identity/register", new { email = "test@example.test" }, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(0, service.Registrations);

        using HttpResponseMessage created = await client.PostAsJsonAsync("/identity/register",
            new RegisterRequest("test@example.test", "player", "Password9!"), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(created.Headers.CacheControl!.NoStore);
        RegistrationResult? registration = await created.Content.ReadFromJsonAsync<RegistrationResult>(cancellationToken);
        Assert.Equal("session-token", registration!.EmailConfirmation!.SessionToken);

        using HttpResponseMessage missingToken = await client.GetAsync("/identity/email-confirmation", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, missingToken.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "session-token");
        using HttpResponseMessage limited = await client.PostAsync("/identity/email-confirmation/resend", null, cancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("session-token", service.LastToken);

        using HttpResponseMessage confirmed = await client.GetAsync($"/identity/confirm-email?userId={Guid.NewGuid()}&token=link-token", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("link-token", service.LastToken);
        Assert.Equal("no-referrer", Assert.Single(confirmed.Headers.GetValues("Referrer-Policy")));

        using HttpResponseMessage opaqueOnMe = await client.GetAsync("/identity/me", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, opaqueOnMe.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        using HttpResponseMessage invalidLogin = await client.PostAsJsonAsync("/identity/login", new { email = "player@example.test" }, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidLogin.StatusCode);
        LoginRequest login = new("player@example.test", "Password9!");
        using HttpResponseMessage deniedLogin = await client.PostAsJsonAsync("/identity/login", login, cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedLogin.StatusCode);
        service.FailLogin = true;
        using HttpResponseMessage failedLogin = await client.PostAsJsonAsync("/identity/login", login, cancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, failedLogin.StatusCode);
        Assert.Equal("application/problem+json", failedLogin.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Could not update login attempt state", await failedLogin.Content.ReadAsStringAsync(cancellationToken));
        service.FailLogin = false;
        service.NextLogin = new("EmailNotConfirmed", EmailConfirmation: registration.EmailConfirmation);
        using HttpResponseMessage unconfirmedLogin = await client.PostAsJsonAsync("/identity/login", login, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, unconfirmedLogin.StatusCode);
        Assert.Equal("session-token", (await unconfirmedLogin.Content.ReadFromJsonAsync<LoginResult>(cancellationToken))!.EmailConfirmation!.SessionToken);
        Guid userId = Guid.NewGuid();
        AccessToken token = app.Services.GetRequiredService<IAccessTokenIssuer>().Issue(userId);
        service.NextLogin = new("Success", token.Value, "Bearer", token.ExpiresIn);
        using HttpResponseMessage successfulLogin = await client.PostAsJsonAsync("/identity/login", login, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, successfulLogin.StatusCode);
        Assert.True(successfulLogin.Headers.CacheControl!.NoStore);
        LoginResult? loginResult = await successfulLogin.Content.ReadFromJsonAsync<LoginResult>(cancellationToken);
        Assert.Equal(token.Value, loginResult!.AccessToken);
        Assert.Equal(600, loginResult.ExpiresIn);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.AccessToken);
        using HttpResponseMessage me = await client.GetAsync("/identity/me", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Dictionary<string, Guid>? identity = await me.Content.ReadFromJsonAsync<Dictionary<string, Guid>>(cancellationToken);
        Assert.Equal(userId, identity!["userId"]);
        using HttpResponseMessage oldRoute = await client.PostAsJsonAsync("/auth/login", login, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, oldRoute.StatusCode);
        await app.StopAsync(cancellationToken);
    }

    private sealed class StubRegistration : IRegistrationService, IEmailConfirmationService, ILoginService
    {
        public LoginResult NextLogin { get; set; } = new("InvalidCredentials");
        public bool FailLogin { get; set; }
        public Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken) =>
            FailLogin ? throw new InvalidOperationException("Could not update login attempt state.") : Task.FromResult(NextLogin);

        public int Registrations { get; private set; }
        public string? LastToken { get; private set; }
        public Task<RegistrationResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
        {
            Registrations++;
            return Task.FromResult(new RegistrationResult(Guid.NewGuid(),
                new("session-token", DateTimeOffset.UtcNow.AddMinutes(30), "Queued", DateTimeOffset.UtcNow.AddMinutes(1), null), []));
        }
        public Task<ConfirmationSessionResult> GetStatusAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new ConfirmationSessionResult("InvalidSession", null));
        public Task<ConfirmationSessionResult> ResendAsync(string token, CancellationToken cancellationToken)
        {
            LastToken = token;
            return Task.FromResult(new ConfirmationSessionResult("RateLimited", null));
        }
        public Task<string> ConfirmAsync(Guid userId, string token, CancellationToken cancellationToken)
        {
            LastToken = token;
            return Task.FromResult("EmailConfirmed");
        }
    }
}
