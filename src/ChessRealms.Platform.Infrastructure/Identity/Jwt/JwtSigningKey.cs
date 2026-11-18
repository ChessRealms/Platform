using System.Security.Cryptography;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ChessRealms.Platform.Infrastructure.Identity.Jwt;

public sealed class JwtSigningKey : IDisposable
{
    private readonly RSA _rsa;

    public JwtSigningKey(IOptions<JwtOptions> options)
    {
        JwtOptions settings = options.Value;
        _rsa = RSA.Create();
        try
        {
            _rsa.ImportFromPem(settings.PrivateKeyPem);
            if (_rsa.KeySize < 2048)
            {
                throw new CryptographicException();
            }
            // Signing a probe also rejects a public-only PEM without exporting private parameters.
            _rsa.SignData([0], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            SigningCredentials = new(new RsaSecurityKey(_rsa) { KeyId = settings.SigningKeyId }, SecurityAlgorithms.RsaSha256);
            ValidationKey = new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = settings.SigningKeyId };
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            _rsa.Dispose();
            // Do not include supplied key material or parser exceptions in configuration errors.
            throw new OptionsValidationException(JwtOptions.SectionName, typeof(JwtOptions),
                ["JWT requires an unencrypted RSA private PEM key of at least 2048 bits."]);
        }
    }

    public SigningCredentials SigningCredentials { get; }
    public SecurityKey ValidationKey { get; }

    public void Dispose() => _rsa.Dispose();
}
