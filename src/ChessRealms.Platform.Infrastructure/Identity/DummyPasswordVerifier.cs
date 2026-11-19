using System.Security.Cryptography;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ChessRealms.Platform.Infrastructure.Identity;

public sealed class DummyPasswordVerifier
{
    private readonly PasswordHasher<PlatformUser> _hasher;
    private readonly PlatformUser _user = new();
    private readonly string _hash;

    public DummyPasswordVerifier(IOptions<PasswordHasherOptions> options)
    {
        _hasher = new(options);
        _hash = _hasher.HashPassword(_user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public void Verify(string password) => _hasher.VerifyHashedPassword(_user, _hash, password);
}
