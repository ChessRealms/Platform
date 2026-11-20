using System.Text.Json.Serialization;

namespace ChessRealms.Platform.Application.Identity;

public sealed record RegistrationResult(
    Guid? UserId, 
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConfirmationMetadata? EmailConfirmation,
    IReadOnlyList<RegistrationError> Errors);
