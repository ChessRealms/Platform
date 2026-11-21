# Password recovery

| Endpoint | Expected response |
| --- | --- |
| `POST /api/identity/password-reset/request` with `{ email }` | `202` for a syntactically valid request, including unknown, unconfirmed, and account-throttled addresses. Invalid input returns `400`. |
| `POST /api/identity/password-reset/complete` with `{ userId, token, newPassword }` | `204` on success. `400 InvalidResetToken` for invalid, expired, or used links. `400 InvalidPassword` carries password policy errors. |

Both endpoints are anonymous and share a fixed window of 30 requests per minute
per remote connection IP on each API instance. The response for exceeding this
limit is `429`. PostgreSQL serializes eligible email requests per account and
allows one delivery per minute and at most five per hour across API replicas.
The generic response hides account eligibility in the HTTP status and body.
Database work can still affect response timing.

## Delivery and token handling

The Identity email worker sends reset messages through the same SMTP transport
as confirmation mail. Requests have a separate durable queue so a deployment
running an older confirmation worker can leave reset deliveries for the new
worker. Jobs older than 30 minutes or tied to a previous security stamp are
cancelled. Completed jobs are retained for seven days. SMTP acceptance and the
database update are separate operations, so an interrupted dispatch can result
in duplicate email. A provider failure marks the job failed. The user can
request another link after the cooldown.

The worker generates an ASP.NET Core Identity password reset token when dispatching
the message. It lasts 30 minutes from issuance and uses a dedicated Data Protection
purpose. The token is carried in the Angular URL fragment, which stays out of the
HTTP navigation request. Angular removes the fragment from the address bar,
keeps the token in page memory, and sends it in the completion POST body when
the user submits the form. Reloading the form requires reopening the email link.
The email and the POST body contain sensitive data. Avoid link tracking that
captures or strips fragments and redact these request bodies in any HTTP logging.

Password validation uses the existing Identity policy. A successful reset clears
the login lockout, rotates the security stamp, and revokes all existing refresh
sessions in one database transaction. Concurrent completion of the same link can
succeed only once. Already issued JWTs remain valid until expiration plus clock
skew under the existing JWT validation model.

## Configuration

For local testing, start AppHost with `Mailpit__Enabled=true`, register and confirm
a synthetic account, then follow **Forgot your password?** on the sign-in page.
AppHost sets the Angular reset URL and SMTP settings for Mailpit.

When deploying the feature, apply the Identity migration through MigrationWorker
before routing traffic to the new API. Set `Identity__PasswordReset__ResetUrl` to the
public HTTPS Angular `/reset-password` route and configure
[`Identity:EmailConfirmation:Smtp`](registration.md#configuration-and-deployment).
With the reset URL unset the request endpoint still returns the generic response
and skips enqueueing. A reset URL with SMTP unset produces failed deliveries.
Persist and share the Data Protection key ring across API replicas. Apply a
deployment-level rate limit if replicas need a shared quota or the API sees a
common reverse-proxy address as its remote IP.
