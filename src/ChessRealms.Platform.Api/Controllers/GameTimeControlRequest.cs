using ChessRealms.Platform.Domain.Games;

namespace ChessRealms.Platform.Api.Controllers;

internal static class GameTimeControlRequest
{
    internal static bool TryParse(string? value, out GameTimeControl? control)
    {
        control = value == nameof(GameTimeControl.Blitz5) ? GameTimeControl.Blitz5 : null;
        return value is null || control.HasValue;
    }
}
