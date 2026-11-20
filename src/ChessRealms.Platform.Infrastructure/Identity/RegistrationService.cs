using ChessRealms.Platform.Application.Identity;
using ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;
using ChessRealms.Platform.Infrastructure.Identity.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

using Npgsql;

namespace ChessRealms.Platform.Infrastructure.Identity;

public sealed class RegistrationService(
    IdentityDbContext database,
    UserManager<PlatformUser> users,
    EmailConfirmationService confirmation,
    IOptions<EmailConfirmationOptions> options) : IRegistrationService
{
    public async Task<RegistrationResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        RegisterRequest normalizedRequest = request with
        {
            Email = request.Email?.Trim() ?? string.Empty,
            Username = request.Username?.Trim() ?? string.Empty
        };
        List<RegistrationError> validationErrors = ValidateRegisterRequest(normalizedRequest);
        if (validationErrors.Count > 0)
        {
            return new(null, null, validationErrors);
        }

        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        bool autoConfirm = options.Value.AutoConfirmOnRegistration;
        PlatformUser user = new()
        {
            Email = normalizedRequest.Email,
            UserName = normalizedRequest.Username,
            EmailConfirmed = autoConfirm
        };
        try
        {
            IdentityResult created = await users.CreateAsync(user, normalizedRequest.Password);
            if (!created.Succeeded)
            {
                return new(null, null, created.Errors
                    .Select(error => new RegistrationError(error.Code, error.Description)).ToArray());
            }

            ConfirmationMetadata? metadata = autoConfirm
                ? null : await confirmation.CreateSessionAsync(user.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(user.Id, metadata, []);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "EmailIndex" or "UserNameIndex" } postgres)
        {
            RegistrationError error = postgres.ConstraintName switch
            {
                "EmailIndex" => new("DuplicateEmail", "Email is already registered."),
                "UserNameIndex" => new("DuplicateUserName", "Username is already taken."),
                _ => throw new InvalidOperationException("Unexpected registration constraint.")
            };
            return new(null, null, [error]);
        }
    }

    private static List<RegistrationError> ValidateRegisterRequest(RegisterRequest request)
    {
        const int emailMaxLength = 256;
        const int usernameMaxLength = 256;
        const int passwordMaxLength = 1024;

        List<RegistrationError> errors = [];
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > emailMaxLength)
        {
            errors.Add(new("InvalidEmail", "Email is required and must be at most 256 characters."));
        }

        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > usernameMaxLength)
        {
            errors.Add(new("InvalidUsername", "Username is required and must be at most 256 characters."));
        }

        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > passwordMaxLength)
        {
            errors.Add(new("InvalidPassword", "Password is required and must be at most 1024 characters."));
        }

        return errors;
    }
}
