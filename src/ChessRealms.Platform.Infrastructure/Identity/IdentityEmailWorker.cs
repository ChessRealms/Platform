using ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;
using ChessRealms.Platform.Infrastructure.Identity.PasswordReset;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChessRealms.Platform.Infrastructure.Identity;

public sealed class IdentityEmailWorker(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<IdentityEmailWorker> logger) : BackgroundService
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
                bool dispatchedConfirmation = await dispatcher.DispatchNextAsync(timeout.Token);
                var passwordResetDispatcher = scope.ServiceProvider.GetRequiredService<PasswordResetEmailDispatcher>();
                bool dispatchedReset = await passwordResetDispatcher.DispatchNextAsync(timeout.Token);
                if (dispatchedConfirmation || dispatchedReset)
                {
                    continue;
                }
                IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                DateTimeOffset now = clock.GetUtcNow();
                await database.EmailConfirmationSessions.Where(session => session.ExpiresAt <= now)
                    .ExecuteDeleteAsync(timeout.Token);
                DateTimeOffset retention = now - TimeSpan.FromDays(7);
                await database.EmailConfirmationDeliveries.Where(delivery => delivery.CreatedAt < retention && delivery.Status != "Queued")
                    .ExecuteDeleteAsync(timeout.Token);
                await database.PasswordResetEmailDeliveries.Where(delivery => delivery.CreatedAt < retention && delivery.Status != "Queued")
                    .ExecuteDeleteAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Log the exception category only: database/provider messages may contain secrets.
                logger.LogWarning("Identity email processing failed ({ExceptionType}); retrying on the next poll.",
                    exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken);
        }
    }
}
