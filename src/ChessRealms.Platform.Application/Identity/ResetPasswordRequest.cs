namespace ChessRealms.Platform.Application.Identity;

public sealed record ResetPasswordRequest(Guid UserId, string Token, string NewPassword);
