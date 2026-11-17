namespace ChessRealms.Platform.Application.Identity;

public sealed record RegisterRequest(string Email, string Username, string Password);
public sealed record RegistrationError(string Code, string Description);
public sealed record ConfirmationMetadata(string? SessionToken, DateTimeOffset ExpiresAt,
    string DeliveryStatus, DateTimeOffset? ResendAvailableAt, string? FailureCode);
public sealed record RegistrationResult(Guid? UserId, ConfirmationMetadata? EmailConfirmation,
    IReadOnlyList<RegistrationError> Errors);
public sealed record ConfirmationSessionResult(string Code, ConfirmationMetadata? EmailConfirmation);

public interface IRegistrationService
{
    Task<RegistrationResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task<ConfirmationSessionResult> GetStatusAsync(string token, CancellationToken cancellationToken);
    Task<ConfirmationSessionResult> ResendAsync(string token, CancellationToken cancellationToken);
    Task<string> ConfirmAsync(Guid userId, string token, CancellationToken cancellationToken);
}
