using System.ComponentModel.DataAnnotations;

namespace ChessRealms.Platform.Api.Controllers;

public sealed record PasswordResetEmailRequest([Required, EmailAddress, StringLength(256)] string Email);
