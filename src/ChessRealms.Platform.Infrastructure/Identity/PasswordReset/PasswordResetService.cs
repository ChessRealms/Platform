using System.Text;

using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace ChessRealms.Platform.Infrastructure.Identity.PasswordReset;

internal sealed class PasswordResetService(
    IdentityDbContext database,
    UserManager<PlatformUser> users,
    TimeProvider clock,
    IOptions<PasswordResetOptions> options) : IPasswordResetService
{
    public async Task RequestAsync(string email, CancellationToken cancellationToken)
    {
        if (options.Value.ResetUrl is null)
        {
            return;
        }
        string? normalizedEmail = users.NormalizeEmail(email.Trim());
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // Serialize requests for an address with each other and with password changes.
        PlatformUser? user = await database.Users.FromSqlInterpolated(
            $"SELECT * FROM identity.\"AspNetUsers\" WHERE \"NormalizedEmail\" = {normalizedEmail} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (user is not { EmailConfirmed: true, SecurityStamp: not null })
        {
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset window = now - TimeSpan.FromHours(1);
        DateTimeOffset cooldown = now - TimeSpan.FromMinutes(1);
        List<DateTimeOffset> requests = await database.PasswordResetEmailDeliveries
            .Where(delivery => delivery.UserId == user.Id && delivery.CreatedAt > window)
            .Select(delivery => delivery.CreatedAt).Take(5).ToListAsync(cancellationToken);
        if (requests.Count >= 5 || requests.Any(createdAt => createdAt > cooldown))
        {
            return;
        }

        database.PasswordResetEmailDeliveries.Add(new()
        {
            UserId = user.Id,
            SecurityStamp = user.SecurityStamp,
            CreatedAt = now
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PasswordResetResult> ResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (request.UserId == Guid.Empty || string.IsNullOrEmpty(request.Token) || request.Token.Length > 2048)
        {
            return new("InvalidResetToken", []);
        }
        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token));
        }
        catch (FormatException)
        {
            return new("InvalidResetToken", []);
        }
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length > 1024)
        {
            return new("InvalidPassword", [new("InvalidPassword", "Enter a password of at most 1024 characters.")]);
        }

        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // Use the same user-first lock order as login, refresh and session management.
        PlatformUser? user = await database.Users.FromSqlInterpolated(
            $"SELECT * FROM identity.\"AspNetUsers\" WHERE \"Id\" = {request.UserId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (user is not { EmailConfirmed: true })
        {
            return new("InvalidResetToken", []);
        }

        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        IdentityResult result = await users.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.InvalidToken)))
            {
                return new("InvalidResetToken", []);
            }
            if (result.Errors.All(error => error.Code.StartsWith("Password", StringComparison.Ordinal)))
            {
                return new("InvalidPassword", result.Errors
                    .Select(error => new RegistrationError(error.Code, error.Description)).ToArray());
            }
            throw new InvalidOperationException("Unexpected password reset failure.");
        }

        DateTimeOffset now = clock.GetUtcNow();
        await database.LoginSessions.Where(session => session.UserId == user.Id && session.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAt, now)
                .SetProperty(session => session.RevocationReason, "PasswordReset"), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new("PasswordReset", []);
    }
}
