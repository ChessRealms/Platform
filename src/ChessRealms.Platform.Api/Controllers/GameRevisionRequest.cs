using System.ComponentModel.DataAnnotations;

namespace ChessRealms.Platform.Api.Controllers;

public sealed record GameRevisionRequest([Required, Range(0, long.MaxValue)] long? ExpectedRevision);
