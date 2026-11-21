using System.Text;

using ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChessRealms.Platform.Infrastructure.Identity.PasswordReset;

internal sealed class PasswordResetEmailDispatcher(IdentityDbContext database, UserManager<PlatformUser> users,
    IPasswordResetEmailSender sender, IOptions<PasswordResetOptions> options, TimeProvider clock,
    ILogger<PasswordResetEmailDispatcher> logger)
{
    public async Task<bool> DispatchNextAsync(CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        PasswordResetEmailDelivery? delivery = await database.PasswordResetEmailDeliveries.FromSqlRaw(
            """
            SELECT * FROM identity."PasswordResetEmailDeliveries"
            WHERE "Status" = 'Queued' ORDER BY "CreatedAt"
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(cancellationToken);
        if (delivery is null)
        {
            return false;
        }

        PlatformUser user = await database.Users.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.UserId, cancellationToken);
        if (options.Value.ResetUrl is not { } resetUrl || !user.EmailConfirmed || string.IsNullOrEmpty(user.Email)
            || user.SecurityStamp != delivery.SecurityStamp
            || delivery.CreatedAt <= clock.GetUtcNow() - PasswordResetOptions.LinkLifetime)
        {
            delivery.Status = "Cancelled";
        }
        else
        {
            string token = await users.GeneratePasswordResetTokenAsync(user);
            string encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            // The fragment stays in the browser. The API receives the token only in the POST body.
            string link = $"{resetUrl}#userId={user.Id:D}&token={encodedToken}";
            try
            {
                EmailSendResult result = await sender.SendPasswordResetAsync(user.Email, link, cancellationToken);
                delivery.Status = result.Accepted ? "Sent" : "Failed";
                if (!result.Accepted)
                {
                    logger.LogWarning("Password reset email was not accepted ({Failure}).", result.Failure);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Password reset email provider failed ({ExceptionType}).", exception.GetType().Name);
                delivery.Status = "Failed";
            }
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
