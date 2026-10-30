using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChessRealms.Platform.MigrationWorker;

public sealed class MigrationService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<MigrationService> logger) : BackgroundService
{
    // A cancelled or interrupted run must not unblock dependent services.
    public int ExitCode { get; private set; } = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            TimeSpan timeout = configuration.GetValue("Migration:Timeout", TimeSpan.FromMinutes(2));
            if (timeout <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("Migration timeout must be positive.");
            }

            deadline.CancelAfter(timeout);
            deadline.Token.ThrowIfCancellationRequested();
            logger.LogInformation("Applying PostgreSQL schema migrations.");
            await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
            {
                PlatformDbContext dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await dbContext.Database.MigrateAsync(deadline.Token);
            }

            deadline.Token.ThrowIfCancellationRequested();
            logger.LogInformation("Schema migrations completed successfully.");
            ExitCode = 0;
        }
        catch (Exception error)
        {
            logger.LogError(error, "Schema migration failed.");
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}
