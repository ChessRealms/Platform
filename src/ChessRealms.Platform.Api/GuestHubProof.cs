using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Primitives;

namespace ChessRealms.Platform.Api;

internal static class GuestHubProof
{
    private const string Prefix = "guest.";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    internal static bool IsGuest(StringValues values) =>
        values.Count == 1 && values[0]?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    internal static string Create(string cookie, DateTimeOffset now)
    {
        string expires = (now + Lifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return Prefix + expires + "." + Sign(cookie, expires);
    }

    internal static DateTimeOffset? Validate(StringValues values, string cookie, DateTimeOffset now)
    {
        if (!IsGuest(values) || values[0] is not { Length: <= 92 } proof)
        {
            return null;
        }

        string[] parts = proof.Split('.');
        if (parts.Length != 3
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long expires)
            || expires <= now.ToUnixTimeSeconds() || expires > (now + Lifetime).ToUnixTimeSeconds())
        {
            return null;
        }

        byte[] actual = Encoding.ASCII.GetBytes(parts[2]);
        byte[] expected = Encoding.ASCII.GetBytes(Sign(cookie, parts[1]));
        return CryptographicOperations.FixedTimeEquals(actual, expected) ? DateTimeOffset.FromUnixTimeSeconds(expires) : null;
    }

    private static string Sign(string cookie, string expires) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.ASCII.GetBytes(cookie),
            Encoding.ASCII.GetBytes("ChessRealms.GuestGameHub/v1:" + expires)));
}
