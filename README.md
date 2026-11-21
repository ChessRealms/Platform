# ChessRealms Platform

An open-source chess platform focused on playing chess against other players or the computer.

## Scope and status

The first milestone is the core experience of playing a complete chess game against another player or a computer opponent. Additional features will be considered after this core functionality is in place.

The repository contains a .NET 10 backend scaffold, an Angular frontend, and Aspire orchestration with PostgreSQL and an EF Core migration worker.

## Technology

- C# and .NET 10 for the backend, EF Core with PostgreSQL for persistence.
- Aspire for local orchestration and observability.
- xUnit v3 for tests.
- Angular for the frontend.

## Repository layout

- `src/` — backend and frontend application source code.
- `src/cr-platform-web-app/` — Angular frontend.
- `tests/` — automated tests.
- `docs/` — project documentation.
- `orchestration/` — application orchestration configuration.
- `scripts/` — repository development and maintenance scripts.
- `.github/` — GitHub configuration and CI workflows.

Empty directories may not appear in a fresh clone until files are added.

## Development setup

Install the .NET SDK specified in `global.json`. The repository allows newer patches within the same SDK feature band.

AppHost uses `DnxPinned` to run the Aspire CLI version paired with its `Aspire.AppHost.Sdk` through .NET 10's `dnx`. A global Aspire CLI installation is not required. The first use can download the pinned CLI and prepare its Dashboard/orchestration bundle; `--no-restore` only skips the project's NuGet restore, not CLI bundle setup.

Install a Node.js version supported by Angular 22: Node.js 22.22.3+, 24.15.0+, or 26+ (see the frontend's `package.json` engines). npm is used for frontend dependencies; `package-lock.json` is checked in for reproducible installs.

Shared configuration includes:

- `.editorconfig` for formatting and C# style conventions.
- `.gitattributes` for Git line ending normalization.
- `Directory.Build.props` for shared build settings and NuGet lock file generation.
- `Directory.Packages.props` for central NuGet package version management.
- `NuGet.Config` for package sources.
- `.config/dotnet-tools.json` for repository-local .NET tools.

### Backend structure

All projects use the `ChessRealms.Platform` prefix:

- `src/ChessRealms.Platform.Domain` — domain model; no project dependencies.
- `src/ChessRealms.Platform.Application` — application layer; references Domain.
- `src/ChessRealms.Platform.Infrastructure` — persistence and external integrations; references Application.
- `src/ChessRealms.Platform.Api` — HTTP entry point; references Application, Infrastructure, and ServiceDefaults.
- `src/ChessRealms.Platform.MigrationWorker` — console host that applies migrations and exits; references Infrastructure and ServiceDefaults.
- `orchestration/ChessRealms.Platform.AppHost` — PostgreSQL, migration worker, API, and Angular frontend orchestration.
- `orchestration/ChessRealms.Platform.ServiceDefaults` — shared telemetry, health checks, service discovery, and HTTP resilience.
- `tests/` — xUnit v3 projects referencing Api, Application, Domain, and Infrastructure respectively.

`Infrastructure` leaves room for integrations beyond persistence. `PlatformDbContext` currently has no entities. An empty `InitialCreate` migration establishes the migration pipeline without adding business tables.

The isolated `Infrastructure/Identity` module provides ASP.NET Core Identity stores
in a separate `IdentityDbContext`, using the `identity` schema in the same PostgreSQL
database. See [architecture decisions](docs/architecture.md) for boundaries and scope.

Registration, confirmation sessions, status/resend, SMTP delivery, and confirmation links
are described in [registration and email confirmation](docs/registration.md). Without
SMTP configuration, email delivery reports `ProviderNotConfigured`.
For local UI development, AppHost enables registration auto-confirmation in
Development by default; the API option itself defaults to false. See the
[frontend registration flow](src/cr-platform-web-app/README.md#registration-and-sign-in).

Email/password login issues RS256 JWT access tokens for confirmed users. See
[login and JWT authentication](docs/login.md) for the `/api/identity` routes and required
signing-key setup. [Rotating refresh tokens](docs/refresh-tokens.md) use persistent
login sessions with an absolute 30-day lifetime and strict reuse detection.

### Build and test

Run from the repository root:

```shell
dotnet tool restore
dotnet restore ChessRealms.Platform.slnx --locked-mode
dotnet build ChessRealms.Platform.slnx --no-restore
dotnet test ChessRealms.Platform.slnx --no-build --no-restore
```

Infrastructure tests require Docker with Linux containers. Testcontainers starts
temporary PostgreSQL 18.3 containers for Identity persistence tests; the SMTP pipeline
test also starts a temporary Mailpit container. Tests use neither the development
database nor AppHost. API tests exercise HTTP contracts with a test service.
Application and Domain test projects are currently empty. Package versions are centralized
and each project has a NuGet lock file.

For the frontend:

```shell
cd src/cr-platform-web-app
npm ci
npm run build
npm test -- --watch=false
```

The Angular app stays outside the .NET solution; Aspire runs it directly from its directory.
The separate [Identity browser smoke test](docs/identity-e2e.md) uses Aspire and
Chromium to verify registration, email confirmation, sign-in and session restore.
To run `npm start` independently at http://localhost:4200, set the API target as
described in the [frontend setup](src/cr-platform-web-app/README.md#api-connection).

### Continuous integration

The [CI workflow](.github/workflows/ci.yml) runs on every push and pull request with separate backend and frontend jobs on Ubuntu. The backend uses the SDK from `global.json`, restores with `--locked-mode`, builds, and runs tests without restoring or rebuilding. Infrastructure tests use the runner's Docker daemon for temporary PostgreSQL and Mailpit containers. The frontend uses Node.js 26 and the npm version from `package.json`, installs with `npm ci`, builds the development configuration, and runs Vitest without watch mode. CI does not start AppHost.

### Run locally

Provision the API's JWT signing key using the development setup in
[login configuration](docs/login.md#configuration). Missing or invalid JWT configuration
prevents the API from starting; migrations do not require the key.

Start Docker Desktop with Linux containers, then run:

```shell
dotnet dev-certs https --trust
dotnet run --project orchestration/ChessRealms.Platform.AppHost
```

Open the dashboard URL printed in the terminal. AppHost starts PostgreSQL with a persistent Docker volume, waits for database readiness, runs `migrations`, then starts `api` only if the worker exits successfully. Connection strings are supplied by Aspire; no database password is stored in source control. The API exposes `/openapi/v1.json`, `/health` (including database connectivity), and `/alive` in Development.

AppHost also installs the frontend's npm dependencies and starts the Angular development server as `web`. Open its HTTPS endpoint from the dashboard. The frontend can start independently of the backend.

To test real confirmation emails locally, enable the optional Mailpit resource before
starting AppHost with `$env:Mailpit__Enabled = 'true'`. Its local Web UI appears in the
dashboard. AppHost then disables registration auto-confirmation and configures SMTP
and a working HTTPS confirmation link. See [local Mailpit](docs/registration.md#local-mailpit).

The worker has a two-minute timeout, configurable through `Migration__Timeout` (for example, `00:05:00`). Failure or cancellation returns a nonzero exit code and prevents the API from starting. Restart AppHost to run migrations again after changing the model.

### Add a migration

```shell
dotnet ef migrations add MigrationName --context PlatformDbContext --project src/ChessRealms.Platform.Infrastructure --output-dir Persistence/Migrations
```

For Identity migrations, use `--context IdentityDbContext --output-dir Identity/Persistence/Migrations` instead.

The design-time factories allow scaffolding without starting PostgreSQL or AppHost. For EF commands that connect to a database, set `ConnectionStrings__platform` to the connection string shown in the Aspire dashboard. Apply migrations through MigrationWorker, which migrates both contexts; the API does not migrate the database on startup.

Documentation is maintained in English.

## Working with coding agents

[AGENTS.md](AGENTS.md) contains concise project conventions and verification commands.
Codex uses your app settings; no project-specific permission profile or command rules are required.

## Contributing

Contribution guidelines are maintained at the organization level. Consult those guidelines before submitting a contribution.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
