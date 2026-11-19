using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;
using ChessRealms.Platform.Infrastructure.Identity.Sessions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ChessRealms.Platform.Infrastructure.Identity;

public sealed class LoginService(IdentityDbContext database, UserManager<PlatformUser> users,
    EmailConfirmationService confirmation, RefreshTokenService refreshTokens, DummyPasswordVerifier dummyPasswordVerifier) : ILoginService
{
    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 256
            || string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024)
        {
            return new("InvalidCredentials");
        }

        string? normalizedEmail = users.NormalizeEmail(request.Email.Trim());
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        PlatformUser? user = await database.Users.FromSqlInterpolated(
            $"SELECT * FROM identity.\"AspNetUsers\" WHERE \"NormalizedEmail\" = {normalizedEmail} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null || await users.IsLockedOutAsync(user))
        {
            // Unknown and locked accounts still perform password hashing work. This
            // avoids the obvious fast path without claiming constant-time HTTP responses.
            dummyPasswordVerifier.Verify(request.Password);
            return new("InvalidCredentials");
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            EnsureSucceeded(await users.AccessFailedAsync(user));
            await transaction.CommitAsync(cancellationToken);
            return new("InvalidCredentials");
        }

        EnsureSucceeded(await users.ResetAccessFailedCountAsync(user));
        if (!user.EmailConfirmed)
        {
            ConfirmationMetadata metadata = await confirmation.CreateSessionAsync(user.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new("EmailNotConfirmed", EmailConfirmation: metadata);
        }

        RefreshResult tokens = await refreshTokens.CreateSessionAsync(user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new("Success", tokens.AccessToken, tokens.TokenType, tokens.ExpiresIn,
            RefreshToken: tokens.RefreshToken, RefreshTokenExpiresAt: tokens.RefreshTokenExpiresAt);
    }

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Could not update login attempt state.");
        }
    }
}
