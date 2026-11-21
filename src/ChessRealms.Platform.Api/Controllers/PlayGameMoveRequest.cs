using System.ComponentModel.DataAnnotations;

namespace ChessRealms.Platform.Api.Controllers;

public sealed record PlayGameMoveRequest(
    [Required, Range(0, long.MaxValue)] long? ExpectedRevision,
    [Required, StringLength(5, MinimumLength = 4)] string Move);
