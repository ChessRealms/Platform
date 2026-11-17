using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChessRealms.Platform.Infrastructure.Identity;

public sealed class ConfirmationEmailWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<ConfirmationEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                ConfirmationEmailDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<ConfirmationEmailDispatcher>();
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                if (await dispatcher.DispatchNextAsync(timeout.Token))
                {
                    continue;
                }
                IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                DateTimeOffset now = clock.GetUtcNow();
                await database.EmailConfirmationSessions.Where(session => session.ExpiresAt <= now)
                    .ExecuteDeleteAsync(stoppingToken);
                DateTimeOffset retention = now - TimeSpan.FromDays(7);
                await database.EmailConfirmationDeliveries.Where(delivery => delivery.CreatedAt < retention && delivery.Status != "Queued")
                    .ExecuteDeleteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                logger.LogWarning("Confirmation email processing failed; retrying on the next poll.");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken);
        }
    }
}
