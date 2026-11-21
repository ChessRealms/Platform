# Identity browser smoke test

The smoke test runs headless Chromium against the Aspire AppHost over HTTPS. It
registers a synthetic account through Angular, reads the confirmation link from
the run's Mailpit inbox, confirms in a new tab, signs in, and reloads the protected
`/home` page. The reload must call the real web refresh endpoint and keep the user
signed in. Existing backend and Angular tests cover their own error cases.

## Run locally

Start Docker with Linux containers. Install the .NET SDK from `global.json` and
the ASP.NET Core 10 runtime. The test also needs the Node.js and npm versions from
the frontend setup and PowerShell 7. Run these commands from the repository root:

```powershell
npm ci --prefix src/cr-platform-web-app
$project = 'tests/ChessRealms.Platform.Identity.E2E.Tests/ChessRealms.Platform.Identity.E2E.Tests.csproj'
dotnet restore $project --locked-mode
dotnet build $project --no-restore
pwsh tests/ChessRealms.Platform.Identity.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install --only-shell chromium
dotnet dev-certs https
pwsh -File scripts/test-identity-e2e.ps1
```

On Linux, use `install --with-deps --only-shell chromium` to install Chromium's
system dependencies. Aspire may download its pinned CLI bundle on the first build.
The browser ignores certificate errors in its isolated loopback context so it can
use the local development certificate. The test still requires an HTTPS origin.

## Isolation and cleanup

Each run starts its own AppHost with dynamically allocated endpoints, an ephemeral
PostgreSQL container, Mailpit, and an in-memory JWT signing key. The AppHost uses
the frontend dependencies installed by `npm ci` and runs the regular migrations
before the API. No developer database or external mail provider is required.

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
