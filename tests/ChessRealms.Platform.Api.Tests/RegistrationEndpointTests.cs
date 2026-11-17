using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Application.Identity;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
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
        builder.Services.AddSingleton<IRegistrationService>(service);
        builder.Services.AddControllers().AddApplicationPart(typeof(RegistrationController).Assembly);
        await using WebApplication app = builder.Build();
        app.MapControllers();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await app.StartAsync(cancellationToken);
        using HttpClient client = new() { BaseAddress = new Uri(Assert.Single(app.Urls)) };

        using HttpResponseMessage invalid = await client.PostAsJsonAsync("/auth/register", new { email = "test@example.test" }, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(0, service.Registrations);

        using HttpResponseMessage created = await client.PostAsJsonAsync("/auth/register",
            new RegisterRequest("test@example.test", "player", "Password9!"), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(created.Headers.CacheControl!.NoStore);
        RegistrationResult? registration = await created.Content.ReadFromJsonAsync<RegistrationResult>(cancellationToken);
        Assert.Equal("session-token", registration!.EmailConfirmation!.SessionToken);

        using HttpResponseMessage missingToken = await client.GetAsync("/auth/email-confirmation", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, missingToken.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "session-token");
        using HttpResponseMessage limited = await client.PostAsync("/auth/email-confirmation/resend", null, cancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("session-token", service.LastToken);

        using HttpResponseMessage confirmed = await client.GetAsync($"/auth/confirm-email?userId={Guid.NewGuid()}&token=link-token", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("link-token", service.LastToken);
        Assert.Equal("no-referrer", Assert.Single(confirmed.Headers.GetValues("Referrer-Policy")));
        await app.StopAsync(cancellationToken);
    }

    private sealed class StubRegistration : IRegistrationService
    {
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
