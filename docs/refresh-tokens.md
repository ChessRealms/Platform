# Refresh tokens and login sessions

Confirmed email/password login creates a new persistent login session and returns
`accessToken`, `tokenType`, `expiresIn`, `refreshToken`, and `refreshTokenExpiresAt`.
Each login creates an independent session. Unconfirmed users receive only the
existing email-confirmation workflow; failed logins create no session or refresh token.

## Refresh endpoint

```http
POST /api/identity/refresh
Content-Type: application/json
```

```json
{
  "refreshToken": "<opaque-token>"
}
```

A current access JWT is not required, including when the previous JWT has expired.
The refresh token in the JSON body is the credential. HTTP 200 returns:

```json
{
  "code": "Success",
  "accessToken": "<new-signed-jwt>",
  "tokenType": "Bearer",
  "expiresIn": 600,
  "refreshToken": "<new-opaque-token>",
  "refreshTokenExpiresAt": "2026-12-18T12:00:00Z"
}
```

Unknown, expired, revoked, or already-used refresh tokens return HTTP 401 with
`code: InvalidRefreshToken` and null token fields. Missing/empty tokens and bodies
with a token longer than 1024 characters fail HTTP model validation with 400.
Storage/signing failures use the API's generic 500 Problem Details response.
Responses disable caching. Never log request/response token values.

Tokens use JSON transport; cookies and browser storage are not introduced here.
Frontend storage and cross-tab refresh coordination remain separate work.

## Rotation and strict reuse detection

A refresh transaction locks the user followed by the session, rereads the token,
validates eligibility, marks it used, stores a successor, and issues a new access JWT.
The response is returned only after commit. The current session's absolute expiration
does not move. A database uniqueness constraint allows at most one unused token per
session. Failures before commit roll back token consumption and successor insertion.
Refresh transactions are not automatically retried.

Reusing any consumed token revokes its entire session, including all newer descendants.
Revocation is committed even though the response is 401. Other login sessions remain
valid. Two concurrent requests with the same token produce at most one successful
rotation; the losing request revokes that session, so the winning response's refresh
token is also unusable. There is no grace period or idempotent retry response.

If the server commits but its response is lost, retrying the old token revokes the
session and requires login again. Clients must coordinate refresh across concurrent
requests and tabs and must not blindly retry refresh requests. This is intentional
strict reuse detection, not an assertion that every reuse is malicious.

## Persistence and eligibility

`identity.LoginSessions` stores the user, SecurityStamp snapshot, creation/expiration
times, and revocation time/reason. `identity.RefreshTokens` stores SHA-256 hashes,
session IDs, creation times, and consumption times. Tokens contain 32 cryptographically
random bytes encoded as base64url. Plaintext refresh tokens are never persisted.
The session is also the token family; consumed hashes remain available to recognize
reuse of any ancestor until the session's absolute expiration.

Refresh checks that the user exists, has confirmed email, and has the same Identity
SecurityStamp. A changed stamp or unconfirmed email revokes the session on refresh.
Deleting a user cascades deletion of their sessions and refresh tokens. Temporary
login lockout does not invalidate existing sessions.

## Session management

All four endpoints require `Authorization: Bearer <accessToken>`. The validated
`sub` and `sid` claims identify the user and current session; callers cannot supply
a user ID. No refresh token or password is required. Responses disable caching.

| Endpoint | Result |
| --- | --- |
| `GET /api/identity/sessions` | 200 with the caller's usable sessions, ordered by creation time then ID; each item has `id`, `createdAt`, `expiresAt`, `isCurrent` |
| `POST /api/identity/logout` | 204; revokes only the current session, including idempotent repeats and an already cleaned-up session |
| `DELETE /api/identity/sessions/{sessionId}` | 204 for an own session, including an already revoked one; 404 for unknown or foreign IDs |
| `POST /api/identity/logout-all` | 204; revokes all existing sessions for the caller |

Listing, revoking another session, and logout-all return 401 if the caller's
session is revoked or expired, its SecurityStamp no longer matches, or the user
is missing or unconfirmed. Self-logout can still return 204 for an already revoked
or expired own session. A new login after logout-all commits creates a new session.
Revocation keeps refresh-token history until normal cleanup. Refresh and session
management use the same user-then-session lock order, so a concurrent refresh
cannot leave a usable refresh token after logout commits.

Access JWTs include `sid`, the stable login-session GUID. Each issuance still
gets a new `jti`. JWT authentication requires valid, nonempty `sub` and `sid`
GUIDs. No per-request session lookup is added to JWT authentication: an
already-issued JWT remains usable for ordinary APIs until its own expiration
plus clock skew, even after session revocation or expiration. Refresh and
session-management access stop after revocation.

## Configuration, migrations, and cleanup

`Identity:RefreshTokens:SessionLifetime` defaults to `30.00:00:00` (30 days).
It accepts whole seconds from one second to 365 days and is validated at API startup.
Environment variable: `Identity__RefreshTokens__SessionLifetime`.
The value is captured when a session is created; changing configuration affects new
sessions only. Expiration uses UTC with millisecond precision to remain identical
across JSON responses and PostgreSQL round trips. Access JWT settings remain separate.

Apply the `LoginSessionsAndRefreshTokens` Identity migration using MigrationWorker
before starting the updated API. It does not require JWT keys or create user sessions.
No new NuGet packages are needed for refresh support.

The API's login-session cleanup worker runs at startup and hourly, deleting expired
sessions in batches of up to 1000. Deletion cascades to their complete token history.
Revoked but unexpired sessions are retained, preserving replay history until expiration.
Cleanup failures are retried on the next poll and do not extend token validity.

Integration tests use temporary PostgreSQL containers and generated in-memory RSA
keys. They cover rotation, hashing, expiry, independent sessions, ancestor reuse,
concurrent requests, SecurityStamp/email changes, lockout, cleanup, and rollback after
database writes when signing fails. HTTP tests verify JSON contracts and auth boundaries.
