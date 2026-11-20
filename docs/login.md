# Login and JWT authentication

## HTTP contract

`POST /identity/login` accepts JSON with `email` and `password`. Email lookup is
case-insensitive and trims surrounding whitespace; passwords are not trimmed.

For a confirmed user with valid credentials, the response is HTTP 200:

```json
{
  "code": "Success",
  "accessToken": "<signed-jwt>",
  "tokenType": "Bearer",
  "expiresIn": 600,
  "emailConfirmation": null,
  "refreshToken": "<opaque-token>",
  "refreshTokenExpiresAt": "2026-12-18T12:00:00Z"
}
```

`expiresIn` is in seconds. Each successful login creates a separate 30-day session
and returns an opaque refresh token. See [refresh tokens and sessions](refresh-tokens.md)
for rotation, strict reuse detection, session management, and configuration.

Unknown accounts, incorrect passwords, and locked accounts all return HTTP 401 with
`code: InvalidCredentials` and null token/confirmation fields. Missing required JSON
fields return HTTP 400 through API model validation. Five failed password attempts
lock the account for five minutes, using existing Identity fields. Attempts are
serialized with a PostgreSQL user-row lock so concurrent failures cannot lose updates.
A valid password resets the failure counter, including for unconfirmed users.
Unknown and locked accounts perform password hashing work to avoid an obvious fast
path; HTTP timing is not guaranteed to be constant. HTTP/IP rate limiting is separate
from this per-account lockout and the email delivery limits.

A valid password with an unconfirmed email returns HTTP 403:

```json
{
  "code": "EmailNotConfirmed",
  "accessToken": null,
  "tokenType": null,
  "expiresIn": null,
  "refreshToken": null,
  "refreshTokenExpiresAt": null,
  "emailConfirmation": {
    "sessionToken": "<opaque-secret>",
    "expiresAt": "2026-11-18T12:30:00Z",
    "deliveryStatus": "Queued",
    "resendAvailableAt": "2026-11-18T12:01:00Z",
    "failureCode": null
  }
}
```

This uses the same confirmation workflow as registration. Every eligible login can
create a new short-lived confirmation session, but all sessions share the user's
delivery cooldown and rolling limit. Login during a delivery cooldown still returns
a usable session and the latest delivery metadata; it does not queue another email.
The email provider remains unimplemented: the placeholder reports
`Failed / ProviderNotConfigured`. Provider delivery runs independently of the login
response. See [registration and confirmation](registration.md).

`GET /identity/me` requires `Authorization: Bearer <accessToken>` and returns the
authenticated `userId`. It reads the validated JWT subject, not a database profile.
Confirmation session tokens cannot authorize this endpoint. Conversely, a login JWT
does not grant access to status/resend: those endpoints validate their opaque session.
All identity controller responses disable caching.

## JWT format and validation

`Microsoft.IdentityModel.JsonWebTokens` issues tokens with `JsonWebTokenHandler`.
`Microsoft.AspNetCore.Authentication.JwtBearer` validates incoming tokens. IdentityModel
versions match the dependency line used by the selected JwtBearer package.

Tokens use RS256, with headers `typ: at+jwt` and `kid: <SigningKeyId>`. Claims are
`sub` (user GUID), `sid` (login-session GUID), `iss`, `aud`, `iat`, `nbf`, `exp`, and
`jti` (unique token ID). There are no email, username, role, or permission claims. JWT payloads
are signed, not encrypted. Claim mapping is disabled, so `sub` stays `sub`.

Validation requires a signature from the configured key, RS256, the access-token
type, the configured issuer and audience, a valid lifetime with expiration, and a
nonempty GUID `sub` and `sid` claims. Unsigned tokens and tokens of other types/algorithms are
rejected. Validation errors are not included in bearer challenge details.

There is no per-request database token lookup. Changing a password, locking an
account, or deleting a user does not immediately revoke an already issued JWT.
It remains valid until expiration plus clock skew. `jti` alone is not a revocation
mechanism. Session revocation blocks refresh and session management but does not
invalidate existing JWTs on ordinary APIs.

## Configuration

Section: `Identity:Jwt`. Environment variables use `Identity__Jwt__...`.

| Setting | Default / requirement |
| --- | --- |
| `Issuer` | `chessrealms-platform`; must be nonempty |
| `Audience` | `chessrealms-platform-api`; must be nonempty |
| `AccessTokenLifetime` | `00:10:00`; whole seconds from one second to one hour |
| `ClockSkew` | `00:00:30`; zero to one minute |
| `SigningKeyId` | Required, nonempty key identifier |
| `PrivateKeyPem` | Required, unencrypted RSA private PEM key of at least 2048 bits |

RS256 and required validation checks are fixed in code. The API fails during startup
when settings or key material are missing/invalid, including public-only or undersized
keys. It never generates an ephemeral signing key on startup. MigrationWorker uses
only the Identity persistence registration and does not require JWT settings.

Keep private keys out of committed JSON, source control, logs, and documentation.
For development, provision the API project's user-secrets once. For example, the
following PowerShell generates a development-only key and pipes it to the secret
store without printing it (running it again replaces the key and invalidates tokens):

```powershell
$jwtRsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
    @{
        'Identity:Jwt:SigningKeyId' = 'local-key-1'
        'Identity:Jwt:PrivateKeyPem' = $jwtRsa.ExportPkcs8PrivateKeyPem()
    } | ConvertTo-Json | dotnet user-secrets set --project src/ChessRealms.Platform.Api
}
finally {
    $jwtRsa.Dispose()
}
```

Use deployment secret configuration for production. All replicas in an environment
must use the same key/identifier and token settings; different environments must use
different keys. The current implementation loads one key for the process lifetime.
Changing it requires restart and invalidates tokens signed with the previous key.
Overlapping verification keys for seamless rotation are not implemented yet.

## Route migration and implementation boundaries

The controller is now `IdentityController` under `/identity`. The former `/auth`
routes are removed. Update clients and `Identity:EmailConfirmation:ConfirmationUrl`
overrides, including externally generated confirmation links. Old emailed `/auth`
links require a deployment redirect if any were issued before this change.

Application contains request/result contracts and service interfaces, with one public
contract per file. Infrastructure implements registration, login, shared confirmation,
and JWT issuance. API owns bearer middleware configuration and HTTP status mapping.
The email sender remains a placeholder. Login sessions and refresh-token history
require the Identity migration described in [refresh tokens](refresh-tokens.md).
