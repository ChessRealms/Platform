# Browser E2E tests for Identity and Games

The suite runs headless Chromium against the Aspire AppHost over HTTPS.
`IdentityBrowserTests` covers accounts and sessions. Its registration test
registers a synthetic account through Angular, opens its Mailpit
confirmation link, signs in, and verifies that a reload gets HTTP 200 from the
refresh endpoint and keeps the user signed in.

The session lifecycle tests also verify:

- Closing Chromium and relaunching it with the same profile restores the session
  on `/home` through a real refresh.
- Two tabs sharing cookies queue concurrent refreshes through Web Locks. A
  third page on the same origin holds the lock until both reloads are queued.
  The test observes both refresh requests before verifying that both tabs
  stay signed in.
- Closing a tab with a pending refresh makes the next tab ask for sign-in
  without retrying the refresh. Signing in clears the pending journal and
  a subsequent reload restores the new session.
- Sign-out ends the active session and updates another tab of the same browser
  context. A separate browser context signed in as the same user can still
  refresh successfully.
- Sign-out everywhere causes the other context's next refresh to return HTTP
  401 and redirects it to sign-in. Already issued access tokens can still be
  valid until they expire.
- Password recovery from the sign-in page through Mailpit removes the token from
  the address bar, accepts the new password, and rejects the previous session's
  next refresh.

`GameBrowserTests` verifies that two players can share a username, send and accept
an invitation through My games, and see White's first move in both browser contexts.
The 5+0 case checks that both clocks start full, the running clock follows the
turn, and reloading preserves the time saved for the waiting player.
It also verifies that both players see declined and cancelled invitations on an
open board and can reopen them from **Closed invitations** in My games.
The private-link scenario copies a link, opens it while signed out in a separate
browser context, returns to the invitation after sign-in, and plays the first move.
The guest scenario joins a 5+0 game without signing in, verifies that the guest
can subscribe to live updates, and plays both sides across a guest reload.
A third browser sees the used invitation and cannot open the guest's board.
In the direct-link scenario, Black opens the invitation while signed out,
signs in, returns to the same game, and accepts.
The test reopens the game from Invitations, Active, and Finished in My games.
White plays with the keyboard and Black plays on a touch-enabled mobile viewport.
Each browser sees the other player's move. The test observes a real SignalR
notification, drops a move notification, and verifies that the browser
resubscribes and recovers the missed move after disconnect. Reloading restores
the saved board and move list. The two players can then agree to a draw and
both browsers show the persisted result.
The rematch scenario starts with a resignation, reloads the pending offer, and
accepts in a separate session. Both browsers open the new game with reversed
colors and see its first move. The original result still links to the rematch.

The game tests also verify that an open board and My games switch to the new
player when another tab signs in as the opponent. Signing out removes both
private views. Signing in from the board reopens the original invitation.

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

Both classes belong to `BrowserTestCollection`, which shares one AppHost per
run with dynamically allocated endpoints, an
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
