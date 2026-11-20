namespace ChessRealms.Platform.Infrastructure.Identity.EmailConfirmation;

public sealed class EmailConfirmationOptions
{
    public bool AutoConfirmOnRegistration { get; set; }
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan DeliveryWindow { get; set; } = TimeSpan.FromHours(1);
    public int MaxDeliveriesPerWindow { get; set; } = 5;
    public string ConfirmationUrl { get; set; } = "https://localhost:7000/identity/confirm-email";
}
