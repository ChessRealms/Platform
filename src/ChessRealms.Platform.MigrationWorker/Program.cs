using ChessRealms.Platform.Infrastructure;
using ChessRealms.Platform.MigrationWorker;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddInfrastructure();
builder.Services.AddSingleton<MigrationService>();
builder.Services.AddHostedService(services => services.GetRequiredService<MigrationService>());

using IHost host = builder.Build();
MigrationService migrationService = host.Services.GetRequiredService<MigrationService>();
await host.RunAsync();
return migrationService.ExitCode;
