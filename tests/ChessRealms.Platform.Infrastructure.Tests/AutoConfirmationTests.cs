using System.Security.Cryptography;

using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure;
using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Testcontainers.PostgreSql;

using Xunit;

namespace ChessRealms.Platform.Infrastructure.Tests;

public sealed class AutoConfirmationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3").Build();
    private IHost _host = null!;
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(Cancellation);
        using RSA rsa = RSA.Create(2048);
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:platform"] = _postgres.GetConnectionString(),
            ["Identity:EmailConfirmation:AutoConfirmOnRegistration"] = "true",
            ["Identity:Jwt:SigningKeyId"] = "auto-confirm-test",
            ["Identity:Jwt:PrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem()
        });
        builder.AddInfrastructure();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddIdentityLogin(builder.Configuration);
        _host = builder.Build();
        await _host.StartAsync(Cancellation);
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync(Cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(Cancellation);
            _host.Dispose();
        }
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task AutoConfirmedRegistrationHasNoConfirmationWorkflowAndCanLoginAndRefresh()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        RegistrationResult registration = await scope.ServiceProvider.GetRequiredService<IRegistrationService>()
            .RegisterAsync(new("player@example.test", "player", "Strong-password9!"), Cancellation);
        Assert.Empty(registration.Errors);
        Assert.NotNull(registration.UserId);
        Assert.Null(registration.EmailConfirmation);
        IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.True((await database.Users.SingleAsync(Cancellation)).EmailConfirmed);
        Assert.Empty(await database.EmailConfirmationSessions.ToListAsync(Cancellation));
        Assert.Empty(await database.EmailConfirmationDeliveries.ToListAsync(Cancellation));

        LoginResult login = await scope.ServiceProvider.GetRequiredService<ILoginService>()
            .LoginAsync(new("player@example.test", "Strong-password9!"), Cancellation);
        Assert.Equal("Success", login.Code);
        Assert.NotNull(login.AccessToken);
        Assert.NotNull(login.RefreshToken);
        RefreshResult refresh = await scope.ServiceProvider.GetRequiredService<IRefreshTokenService>()
            .RefreshAsync(new(login.RefreshToken!), Cancellation);
        Assert.Equal("Success", refresh.Code);
    }

    [Fact]
    public async Task AutoConfirmationDoesNotConfirmExistingUsersOnLogin()
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        UserManager<PlatformUser> users = scope.ServiceProvider.GetRequiredService<UserManager<PlatformUser>>();
        Assert.True((await users.CreateAsync(new()
        {
            Email = "old@example.test", UserName = "old", EmailConfirmed = false
        }, "Strong-password9!")).Succeeded);
        LoginResult login = await scope.ServiceProvider.GetRequiredService<ILoginService>()
            .LoginAsync(new("old@example.test", "Strong-password9!"), Cancellation);
        Assert.Equal("EmailNotConfirmed", login.Code);
        Assert.Null(login.AccessToken);
        Assert.False((await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Users
            .SingleAsync(user => user.Email == "old@example.test", Cancellation)).EmailConfirmed);
    }

    [Fact]
    public async Task DisablingAutoConfirmationDoesNotResetConfirmedAccounts()
    {
        await using (AsyncServiceScope scope = _host.Services.CreateAsyncScope())
        {
            RegistrationResult registered = await scope.ServiceProvider.GetRequiredService<IRegistrationService>()
                .RegisterAsync(new("confirmed@example.test", "confirmed", "Strong-password9!"), Cancellation);
            Assert.Empty(registered.Errors);
        }

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing"
        });
        builder.Configuration["ConnectionStrings:platform"] = _postgres.GetConnectionString();
        builder.AddInfrastructure();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        using IHost disabledHost = builder.Build();
        await disabledHost.StartAsync(Cancellation);
        await using (AsyncServiceScope scope = disabledHost.Services.CreateAsyncScope())
        {
            RegistrationResult registered = await scope.ServiceProvider.GetRequiredService<IRegistrationService>()
                .RegisterAsync(new("new@example.test", "new", "Strong-password9!"), Cancellation);
            Assert.NotNull(registered.EmailConfirmation);
            IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            Assert.True((await database.Users.SingleAsync(user => user.Email == "confirmed@example.test", Cancellation)).EmailConfirmed);
            Assert.False((await database.Users.SingleAsync(user => user.Email == "new@example.test", Cancellation)).EmailConfirmed);
        }
        await disabledHost.StopAsync(Cancellation);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task AutoConfirmationIsRejectedOutsideDevelopmentAndTesting(string environment)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:platform"] = _postgres.GetConnectionString(),
            ["Identity:EmailConfirmation:AutoConfirmOnRegistration"] = "true"
        });
        builder.AddInfrastructure();
        using IHost host = builder.Build();
        OptionsValidationException exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Cancellation));
        Assert.Contains("AutoConfirmOnRegistration", exception.Message);
    }
}
