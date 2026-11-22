# Identity browser E2E tests

The suite runs headless Chromium against the Aspire AppHost over HTTPS. The smoke
test registers a synthetic account through Angular, opens its Mailpit
confirmation link, signs in, and verifies that a reload gets HTTP 200 from the
refresh endpoint and keeps the user signed in.

The session lifecycle tests also verify:

- Closing Chromium and relaunching it with the same profile restores the session
  on `/home` through a real refresh.
- Two tabs sharing cookies queue concurrent refreshes through Web Locks. A
  third tab holds the lock until both reloads are queued. The test releases
  the lock and verifies that both refresh requests succeed and both tabs stay
  signed in.
- Sign-out ends the active session and updates another tab of the same browser
  context. A separate browser context signed in as the same user can still
  refresh successfully.
- Sign-out everywhere causes the other context's next refresh to return HTTP
  401 and redirects it to sign-in. Already issued access tokens can still be
  valid until they expire.
- Password recovery from the sign-in page through Mailpit removes the token from
  the address bar, accepts the new password, and rejects the previous session's
  next refresh.

The suite also covers a private game between two confirmed accounts. White plays
with the keyboard and Black plays on a touch-enabled mobile viewport. Each
browser sees the other player's move through polling, and reloading restores
the saved board and move list.

## Run locally

Start Docker with Linux containers. Install the .NET SDK from `global.json` and
the ASP.NET Core 10 runtime. The test also needs the Node.js and npm versions from
the frontend setup and PowerShell 7. Run these commands from the repository root
on Windows:

```powershell
npm ci --prefix src/cr-platform-web-app
$project = 'tests/ChessRealms.Platform.Identity.E2E.Tests/ChessRealms.Platform.Identity.E2E.Tests.csproj'
dotnet restore $project --locked-mode
dotnet build $project --no-restore
pwsh tests/ChessRealms.Platform.Identity.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install --no-shell chromium
dotnet dev-certs https --trust
pwsh -File scripts/test-identity-e2e.ps1
```

The suite uses Playwright's full Chromium build in headless mode for browser
profile persistence. On Linux, use `install --with-deps --no-shell chromium`
to install Chromium's system dependencies. For development certificate trust, follow
the [.NET instructions](https://learn.microsoft.com/aspnet/core/security/enforcing-ssl#linux-specific-considerations).
The CI job exports only the public certificate and adds it to its OpenSSL
certificate search path for Aspire's HTTPS health checks.

Aspire may download its pinned CLI bundle. Chromium uses an isolated loopback
context that accepts the local certificate, and the test requires HTTPS.

## Isolation and cleanup

The tests share one AppHost per run with dynamically allocated endpoints, an
ephemeral PostgreSQL container, Mailpit, and an in-memory JWT signing key. Each
test uses a unique account and fresh browser contexts. The restart test uses a
temporary profile directory and deletes it after closing Chromium. PostgreSQL
uses Docker's published loopback port for the test's database health checks and
connections. The AppHost uses the frontend dependencies installed by `npm ci` and
runs the regular migrations before the API. No developer database or external
mail provider is required.

Aspire owns the normal shutdown of its application resources. The runner also
checks Docker by the unique run label and removes any leftover containers for
that run. PostgreSQL uses tmpfs in this mode to avoid retaining a test volume.
If a local run is forcibly interrupted, use the ID printed at startup:

```powershell
$env:CHESSREALMS_IDENTITY_E2E_RUN_ID = '<printed-run-id>'
pwsh -File scripts/test-identity-e2e.ps1 -CleanupOnly
```

The separate CI job runs the same script and checks cleanup again after failure or
cancellation when the runner is available. Browser traces and mailbox dumps are not
uploaded because they can include confirmation links and session credentials.
