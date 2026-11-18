using System.Security.Cryptography;
using System.Text;

using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace ChessRealms.Platform.Infrastructure.Identity;

public sealed class EmailConfirmationService(IdentityDbContext database, UserManager<PlatformUser> users,
    IOptions<EmailConfirmationOptions> options, TimeProvider clock) : IEmailConfirmationService
{
    private readonly EmailConfirmationOptions _options = options.Value;

    // Only call for a newly registered user or after validating login credentials. The caller owns
    // the transaction and must hold the user row lock (registration owns a new row).
    internal async Task<ConfirmationMetadata> CreateSessionAsync(Guid userId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        string token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        EmailConfirmationSession session = new()
        {
            TokenHash = Hash(token),
            UserId = userId,
            ExpiresAt = now + _options.SessionLifetime
        };
        database.EmailConfirmationSessions.Add(session);
        if (await GetResendAvailableAtAsync(userId, now, cancellationToken) <= now)
        {
            database.EmailConfirmationDeliveries.Add(new() { UserId = userId, CreatedAt = now });
        }
        await database.SaveChangesAsync(cancellationToken);
        return await MetadataAsync(session, token, cancellationToken);
    }

    public async Task<ConfirmationSessionResult> GetStatusAsync(string token, CancellationToken cancellationToken)
    {
        EmailConfirmationSession? session = await FindSessionAsync(token, cancellationToken);
        if (session is null)
        {
            return new("InvalidSession", null);
        }
        bool confirmed = await database.Users.AnyAsync(user => user.Id == session.UserId && user.EmailConfirmed, cancellationToken);
        return new(confirmed ? "EmailConfirmed" : "EmailNotConfirmed", await MetadataAsync(session, null, cancellationToken));
    }

    public async Task<ConfirmationSessionResult> ResendAsync(string token, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        EmailConfirmationSession? session = await FindSessionAsync(token, cancellationToken);
        if (session is null)
        {
            return new("InvalidSession", null);
        }
        PlatformUser user = await LockUserAsync(session.UserId, cancellationToken);
        // Recheck after waiting for the lock: a session may expire while another request runs.
        DateTimeOffset now = clock.GetUtcNow();
        if (session.ExpiresAt <= now)
        {
            return new("InvalidSession", null);
        }
        if (user.EmailConfirmed)
        {
            return new("EmailConfirmed", await MetadataAsync(session, null, cancellationToken));
        }
        if (await GetResendAvailableAtAsync(user.Id, now, cancellationToken) > now)
        {
            return new("RateLimited", await MetadataAsync(session, null, cancellationToken));
        }
        database.EmailConfirmationDeliveries.Add(new() { UserId = user.Id, CreatedAt = now });
        await database.SaveChangesAsync(cancellationToken);
        ConfirmationMetadata metadata = await MetadataAsync(session, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new("Queued", metadata);
    }

    public async Task<string> ConfirmAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096)
        {
            return "InvalidConfirmationToken";
        }
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (FormatException)
        {
            return "InvalidConfirmationToken";
        }
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        PlatformUser? user = await database.Users.FromSqlInterpolated(
            $"SELECT * FROM identity.\"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null || !await users.VerifyUserTokenAsync(user, users.Options.Tokens.EmailConfirmationTokenProvider,
            UserManager<PlatformUser>.ConfirmEmailTokenPurpose, decoded))
        {
            return "InvalidConfirmationToken";
        }
        if (user.EmailConfirmed)
        {
            return "AlreadyConfirmed";
        }
        IdentityResult result = await users.ConfirmEmailAsync(user, decoded);
        if (!result.Succeeded)
        {
            return "InvalidConfirmationToken";
        }
        await transaction.CommitAsync(cancellationToken);
        return "EmailConfirmed";
    }

    private async Task<EmailConfirmationSession?> FindSessionAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 43)
        {
            return null;
        }
        string hash = Hash(token);
        DateTimeOffset now = clock.GetUtcNow();
        return await database.EmailConfirmationSessions.AsNoTracking()
            .SingleOrDefaultAsync(session => session.TokenHash == hash && session.ExpiresAt > now, cancellationToken);
    }

    private Task<PlatformUser> LockUserAsync(Guid userId, CancellationToken cancellationToken) =>
        database.Users.FromSqlInterpolated(
            $"SELECT * FROM identity.\"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE")
            .SingleAsync(cancellationToken);

    private async Task<DateTimeOffset> GetResendAvailableAtAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = now - _options.DeliveryWindow;
        List<DateTimeOffset> attempts = await database.EmailConfirmationDeliveries
            .Where(delivery => delivery.UserId == userId && delivery.CreatedAt > cutoff)
            .OrderByDescending(delivery => delivery.CreatedAt).Select(delivery => delivery.CreatedAt)
            .Take(_options.MaxDeliveriesPerWindow).ToListAsync(cancellationToken);
        if (attempts.Count == 0)
        {
            return now;
        }
        DateTimeOffset available = attempts[0] + _options.ResendCooldown;
        if (attempts.Count == _options.MaxDeliveriesPerWindow)
        {
            DateTimeOffset windowEnd = attempts[^1] + _options.DeliveryWindow;
            available = windowEnd > available ? windowEnd : available;
        }
        return available > now ? available : now;
    }

    private async Task<ConfirmationMetadata> MetadataAsync(EmailConfirmationSession session, string? token, CancellationToken cancellationToken)
    {
        ConfirmationEmailDelivery? delivery = await database.EmailConfirmationDeliveries.AsNoTracking()
            .Where(item => item.UserId == session.UserId).OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        bool confirmed = await database.Users.AnyAsync(user => user.Id == session.UserId && user.EmailConfirmed, cancellationToken);
        return new(token, session.ExpiresAt, delivery?.Status ?? "Queued",
            confirmed ? null : await GetResendAvailableAtAsync(session.UserId, clock.GetUtcNow(), cancellationToken), delivery?.FailureCode);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
