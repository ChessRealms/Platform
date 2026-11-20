using ChessRealms.Platform.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Xunit;

namespace ChessRealms.Platform.Infrastructure.Tests;

public sealed class SmtpConfigurationTests
{
    [Theory]
    [InlineData("Development", "None", "0", null, null)]
    [InlineData("Development", "None", "1025", "user", "password")]
    [InlineData("Production", "None", "1025", null, null)]
    public async Task InvalidSmtpSettingsFailAtStartup(string environment, string security, string port,
        string? username, string? password)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:platform"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["Identity:EmailConfirmation:Smtp:Host"] = "localhost",
            ["Identity:EmailConfirmation:Smtp:Port"] = port,
            ["Identity:EmailConfirmation:Smtp:FromAddress"] = "no-reply@chessrealms.test",
            ["Identity:EmailConfirmation:Smtp:Security"] = security,
            ["Identity:EmailConfirmation:Smtp:Username"] = username,
            ["Identity:EmailConfirmation:Smtp:Password"] = password
        });
        builder.AddInfrastructure();
        using IHost host = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }
}
