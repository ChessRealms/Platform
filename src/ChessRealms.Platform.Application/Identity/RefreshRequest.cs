using System.ComponentModel.DataAnnotations;

namespace ChessRealms.Platform.Application.Identity;

public sealed record RefreshRequest([Required, MaxLength(1024)] string RefreshToken);
