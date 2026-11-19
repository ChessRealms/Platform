using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.WebUtilities;

namespace ChessRealms.Platform.Infrastructure.Identity.Tokens;

// Purpose is enforced by the separate confirmation-session and refresh-token stores.
internal static class OpaqueToken
{
    public const int EncodedLength = 43;

    public static string Generate() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
