# Registration and email confirmation

Registration accepts `email`, `username`, and `password`. ASP.NET Core Identity
validates credentials and hashes passwords. Email and username uniqueness is
case-insensitive and enforced by PostgreSQL as well as Identity validation.
Registration does not issue login access or refresh tokens.

## HTTP contract

| Endpoint | Request | Result |
| --- | --- | --- |
| `POST /api/identity/register` | JSON: `email`, `username`, `password` | 201 with `userId` and empty `errors` (plus `emailConfirmation` when confirmation is required); 400 validation errors; 409 duplicate credentials |
| `GET /api/identity/email-confirmation` | `Authorization: Bearer <sessionToken>` | 200 with session status; 401 for missing, unknown, or expired sessions |
| `POST /api/identity/email-confirmation/resend` | Same authorization header | 200 `Queued` or `EmailConfirmed`; 429 `RateLimited`; 401 invalid session |
| `GET /api/identity/confirm-email?userId=...&token=...` | Link generated for the email | 200 `EmailConfirmed` or `AlreadyConfirmed`; 400 `InvalidConfirmationToken` |

Successful registration includes:

```json
{
  "userId": "<user-guid>",
  "emailConfirmation": {
    "sessionToken": "<opaque-secret>",
    "expiresAt": "2026-11-18T12:30:00Z",
    "deliveryStatus": "Queued",
    "resendAvailableAt": "2026-11-18T12:01:00Z",
    "failureCode": null
  },
  "errors": []
}
```

Status/resend return `{ "code": "...", "emailConfirmation": { ... } }`.
They do not reissue the token (`sessionToken` is null). `deliveryStatus` describes
the latest delivery request for this user: `Queued`, `Sent`, `Failed`, or `Cancelled`.
Rate limiting is an operation result; it does not overwrite the last delivery's status.
After confirmation, `resendAvailableAt` is null and resend cannot queue another email.
All these responses disable caching. Never log passwords, session tokens, confirmation
links, or authorization headers. Configure proxy/access logs to omit confirmation query strings.

## Persistence and processing

The `identity` schema contains `EmailConfirmationSessions` and
`EmailConfirmationDeliveries`. With auto-confirmation disabled, registration commits
the user, session, and delivery request in one transaction. A session uses 32
random bytes encoded as base64url;
only its SHA-256 hash is stored. It grants status/resend access only, not application
authentication or email confirmation. Sessions expire after 30 minutes by default.

An API background worker processes persisted requests. MigrationWorker does not run
this worker. Workers claim separate delivery rows with `FOR UPDATE SKIP LOCKED` and
hold the transaction through the provider call (bounded by a 30-second cancellation
timeout). This deliberately simple implementation gives at-least-once processing:
a crash after provider acceptance but before commit can result in another send.
The future provider should use `DeliveryId` as an idempotency key where supported.

The current `UnconfiguredConfirmationEmailSender` is a placeholder: it does not send
or log email. It reports `Failed` / `ProviderNotConfigured`, never a simulated `Sent`.
Replace `IConfirmationEmailSender` when a provider is selected. Automated tests use a
recording sender; no live email service is called. `Sent` will mean provider acceptance,
not inbox delivery. Provider exceptions produce `Failed` / `ProviderFailure`; users
can resend after the cooldown. Failed deliveries are not automatically retried.
Infrastructure failures roll back processing and are retried on a later worker poll.

Resend locks the user row before checking limits and inserting the request. Limits
apply across all sessions for that user, including failed attempts: one request per
minute and at most five in a rolling hour by default. HTTP/IP abuse limiting is a
separate deployment concern. Expired sessions and completed deliveries older than
seven days are cleaned when the worker has no queued work.

The confirmation link uses a separate ASP.NET Core Identity Data Protection token,
base64url-wrapped for transport, with a 24-hour lifetime. It remains usable after the
session expires and on another device. Invalid and expired tokens share one response;
repeating a valid confirmation returns `AlreadyConfirmed`. Resending does not revoke
earlier unexpired links. Already confirmed users' pending deliveries are cancelled.

## Configuration and deployment

Configuration section: `Identity:EmailConfirmation` (environment variables use `__`).

| Setting | Default |
| --- | --- |
| `SessionLifetime` | `00:30:00` |
| `ResendCooldown` | `00:01:00` |
| `DeliveryWindow` | `01:00:00` (at most seven days; at least the cooldown) |
| `MaxDeliveriesPerWindow` | `5` |
| `ConfirmationUrl` | `https://localhost:7000/api/identity/confirm-email` |
| `AutoConfirmOnRegistration` | `false` |

`Identity:EmailConfirmation:AutoConfirmOnRegistration` can be set with standard
.NET configuration, for example
`Identity__EmailConfirmation__AutoConfirmOnRegistration=true`. It is read at
startup and may be enabled only in `Development` or `Testing`; enabling it in
another environment fails startup. When enabled, registration sets
`EmailConfirmed` on the new user and commits without a confirmation session or
email-delivery request. The 201 response omits `emailConfirmation` and does not
contain login tokens. Login remains a separate request. Existing unconfirmed
accounts are not changed or auto-confirmed on login. Disabling the setting does
not reset accounts already confirmed.

Set `ConfirmationUrl` to the externally reachable HTTPS confirmation endpoint before
connecting a real provider. It is configuration-owned and never built from an incoming
Host header. Apply the Identity migration through the existing migration worker.

Persist and protect ASP.NET Core Data Protection keys in deployment, sharing the key
ring across API replicas with application name `ChessRealms.Platform`. Otherwise a
restart or another replica may be unable to validate existing confirmation links.
Identity database command retries are disabled because the workflow owns transactions
and includes external delivery. Database failures in HTTP operations propagate as
server errors rather than replaying an ambiguously committed registration.

Login validates credentials before creating a confirmation session, reuses the
user-level delivery limits, and issues no JWT until email is confirmed. See
[login and JWT authentication](login.md) for configuration and response contracts.
Login also issues [rotating refresh tokens](refresh-tokens.md) for confirmed users.
Angular screens and an email provider remain separate work.
