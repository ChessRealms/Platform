# ChessRealms web app

Standard Angular CLI application with standalone components, routing, CSS, and Vitest.

From this directory:

```shell
npm ci
npm start
npm run build
npm test -- --watch=false
```

The standalone development server is available at http://localhost:4200.
To run the full platform, start the Aspire AppHost from the repository root;
it installs npm dependencies and starts this application as the `web` resource.
Open the endpoint shown in the Aspire dashboard.

## API connection

Browser requests use same-origin `/api/*` URLs. During development, the
Angular server proxies those requests to the API without changing the path.
AppHost supplies the API endpoint to `web` through its resource reference, so its assigned port is not baked into
the frontend. Run the full platform through AppHost to use this automatically.

For a standalone Angular server, start the API separately and set
`PLATFORM_API_TARGET` to its origin before running `npm start`. For example, if the
API uses the repository's HTTPS launch profile:

```powershell
$env:PLATFORM_API_TARGET = 'https://localhost:7011'
npm start
```

The target must be an HTTP or HTTPS origin, without a path or credentials. A
development certificate can be used by the local proxy. No browser-facing API
port or CORS policy is required. Restart `ng serve` after changing the target.
When publishing the static frontend, its host must route `/api/*` to the API
without changing the path. The development proxy does not run in a production build.

`IdentityApiClient` contains typed request/response contracts for the existing
identity endpoints. Its protected methods take an access token explicitly; the
confirmation status/resend methods take their separate opaque session token.
The client does not store tokens or retry refresh requests. Callers can use
`classifyIdentityApiError` to distinguish a network failure from an HTTP response.

## Registration and sign in

The `/register` and `/login` pages open `/check-email` when the API requires
confirmation. That page checks status and offers resend when the server permits it.
The confirmation session is held in memory. Sign in again if you reload the page
or the session expires. A link from the email opens `/confirm-email` in any browser
and can be used independently of the waiting page.

AppHost enables registration auto-confirmation by default in Development.
Set `Mailpit__Enabled=true` to enable local delivery and start the Angular app over
HTTPS. For setup and verification, see [local Mailpit](../../docs/registration.md#local-mailpit).

Successful sign in opens a guarded `/home` placeholder. The current session is
held only in memory: refreshing the page loses the frontend session. Token
rotation, restoration after refresh, logout, and the full application shell are
separate follow-up work.
