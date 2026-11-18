using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChessRealms.Platform.Infrastructure.Identity;

public sealed class LoginSessionCleanupWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<LoginSessionCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                int deleted = await scope.ServiceProvider.GetRequiredService<LoginSessionCleanup>().DeleteExpiredBatchAsync(timeout.Token);
                if (deleted == LoginSessionCleanup.BatchSize)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                logger.LogWarning("Expired login session cleanup failed; retrying on the next poll.");
            }
            await Task.Delay(TimeSpan.FromHours(1), clock, stoppingToken);
        }
    }
}
