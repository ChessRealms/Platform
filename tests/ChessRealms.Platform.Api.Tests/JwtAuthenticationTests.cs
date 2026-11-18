using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

using ChessRealms.Platform.Api;
using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Jwt;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Xunit;

namespace ChessRealms.Platform.Api.Tests;

public sealed class JwtAuthenticationTests
{
    [Fact]
    public async Task BearerAuthenticationAcceptsIssuedTokenAndRejectsInvalidTokens()
    {
        using RSA rsa = RSA.Create(2048);
        WebApplicationBuilder builder = CreateBuilder(rsa.ExportPkcs8PrivateKeyPem());
        await using WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/protected", (System.Security.Claims.ClaimsPrincipal user) => user.FindFirst("sub")!.Value).RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = new() { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        Guid userId = Guid.NewGuid();
        AccessToken issued = app.Services.GetRequiredService<IAccessTokenIssuer>().Issue(userId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Value);
        using HttpResponseMessage accepted = await client.GetAsync("/protected", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(userId.ToString(), await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        JsonWebTokenHandler handler = new();
        SigningCredentials credentials = new(new RsaSecurityKey(rsa) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256);
        SecurityTokenDescriptor Descriptor() => new()
        {
            Issuer = "chessrealms-platform",
            Audience = "chessrealms-platform-api",
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(10),
            TokenType = "at+jwt",
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object> { ["sub"] = userId.ToString(), ["jti"] = Guid.NewGuid().ToString() }
        };
        List<string> invalidTokens = ["opaque-confirmation-session", "not.a.jwt"];
        foreach (Action<SecurityTokenDescriptor> mutate in new Action<SecurityTokenDescriptor>[]
        {
            descriptor => descriptor.Issuer = "another-issuer",
            descriptor => descriptor.Audience = "another-api",
            descriptor => descriptor.TokenType = "JWT",
            descriptor => descriptor.Expires = DateTime.UtcNow.AddSeconds(-40),
            descriptor => descriptor.NotBefore = DateTime.UtcNow.AddMinutes(5),
            descriptor => descriptor.Claims["sub"] = "not-a-guid",
            descriptor => descriptor.Claims.Remove("sub"),
            descriptor => descriptor.SigningCredentials = new SigningCredentials(credentials.Key, SecurityAlgorithms.RsaSha512),
            descriptor => descriptor.SigningCredentials = null
        })
        {
            SecurityTokenDescriptor descriptor = Descriptor();
            mutate(descriptor);
            invalidTokens.Add(handler.CreateToken(descriptor));
        }
        using RSA wrongKey = RSA.Create(2048);
        SecurityTokenDescriptor wrongSignature = Descriptor();
        wrongSignature.SigningCredentials = new(new RsaSecurityKey(wrongKey) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256);
        invalidTokens.Add(handler.CreateToken(wrongSignature));
        SecurityTokenDescriptor noExpiration = Descriptor();
        noExpiration.Expires = null;
        invalidTokens.Add(new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(noExpiration));
        foreach (string token in invalidTokens)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage rejected = await client.GetAsync("/protected", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = null;
        using HttpResponseMessage missing = await client.GetAsync("/protected", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("public")]
    [InlineData("weak")]
    [InlineData("lifetime")]
    [InlineData("issuer")]
    [InlineData("clockSkew")]
    public async Task InvalidConfigurationFailsStartup(string scenario)
    {
        using RSA rsa = RSA.Create(scenario == "weak" ? 1024 : 2048);
        string pem = scenario switch
        {
            "missing" => "",
            "malformed" => "invalid-key",
            "public" => rsa.ExportSubjectPublicKeyInfoPem(),
            _ => rsa.ExportPkcs8PrivateKeyPem()
        };
        WebApplicationBuilder builder = CreateBuilder(pem);
        if (scenario == "lifetime")
        {
            builder.Configuration["Identity:Jwt:AccessTokenLifetime"] = "00:00:00";
        }
        if (scenario == "issuer")
        {
            builder.Configuration["Identity:Jwt:Issuer"] = "";
        }
        if (scenario == "clockSkew")
        {
            builder.Configuration["Identity:Jwt:ClockSkew"] = "00:05:00";
        }
        await using WebApplication app = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync(TestContext.Current.CancellationToken));
    }

    private static WebApplicationBuilder CreateBuilder(string pem)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            Args = []
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:Jwt:SigningKeyId"] = "test-key",
            ["Identity:Jwt:PrivateKeyPem"] = pem
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddIdentityAuthentication(builder.Configuration);
        return builder;
    }
}
