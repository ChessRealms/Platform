using System.Text;

using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;

public sealed class ConfirmationEmailDispatcher(IdentityDbContext database, UserManager<PlatformUser> users,
    IConfirmationEmailSender sender, IOptions<EmailConfirmationOptions> options,
    ILogger<ConfirmationEmailDispatcher> logger)
{
    public async Task<bool> DispatchNextAsync(CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // The lock allows multiple API instances to process different deliveries safely.
        ConfirmationEmailDelivery? delivery = await database.EmailConfirmationDeliveries.FromSqlRaw(
            """
            SELECT * FROM identity."EmailConfirmationDeliveries"
            WHERE "Status" = 'Queued' ORDER BY "CreatedAt"
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(cancellationToken);
        if (delivery is null)
        {
            return false;
        }
        PlatformUser user = await database.Users.SingleAsync(item => item.Id == delivery.UserId, cancellationToken);
        if (user.EmailConfirmed)
        {
            delivery.Status = "Cancelled";
        }
        else
        {
            string token = await users.GenerateEmailConfirmationTokenAsync(user);
            string link = QueryHelpers.AddQueryString(options.Value.ConfirmationUrl, new Dictionary<string, string?>
            {
                ["userId"] = user.Id.ToString(),
                ["token"] = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token))
            });
            EmailSendResult result;
            try
            {
                result = await sender.SendAsync(new(delivery.Id, user.Email!, link), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Provider exception details may contain credentials or recipient data.
                logger.LogWarning("Confirmation email provider failed ({ExceptionType}).", exception.GetType().Name);
                result = new(false, EmailSendFailure.ProviderFailure);
            }
            delivery.Status = result.Accepted ? "Sent" : "Failed";
            delivery.FailureCode = result.Accepted ? null : GetFailureCode(result.Failure);
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static string GetFailureCode(EmailSendFailure? failure) => failure switch
    {
        EmailSendFailure.ProviderNotConfigured => nameof(EmailSendFailure.ProviderNotConfigured),
        _ => nameof(EmailSendFailure.ProviderFailure)
    };
}
