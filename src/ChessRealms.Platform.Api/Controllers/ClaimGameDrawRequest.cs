using System.ComponentModel.DataAnnotations;

namespace ChessRealms.Platform.Api.Controllers;

public sealed record ClaimGameDrawRequest(
    [Required, Range(0, long.MaxValue)] long? ExpectedRevision,
    [Required] string Reason,
    [StringLength(5, MinimumLength = 4)] string? IntendedMove);
