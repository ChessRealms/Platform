# ChessRealms Platform

An open-source chess platform focused on playing chess against other players or the computer.

## Scope and status

The first milestone is the core experience of playing a complete chess game against another player or a computer opponent. Additional features will be considered after this core functionality is in place.

The repository contains a .NET 10 backend scaffold with Aspire orchestration, PostgreSQL, and an EF Core migration worker. The frontend and CI will be added separately.

## Technology

- C# and .NET 10 for the backend, EF Core with PostgreSQL for persistence.
- Aspire for local orchestration and observability.
- xUnit v3 for tests.
- Angular for the frontend.

## Repository layout

- `src/` — application source code, including the future backend and frontend.
- `tests/` — automated tests.
- `docs/` — project documentation.
- `orchestration/` — application orchestration configuration.
- `scripts/` — repository development and maintenance scripts.
- `.github/` — GitHub configuration and future workflows.

Empty directories may not appear in a fresh clone until files are added.

## Development setup

Install the .NET SDK specified in `global.json`. The repository allows newer patches within the same SDK feature band.

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
- `orchestration/ChessRealms.Platform.AppHost` — PostgreSQL, migration worker, and API orchestration.
- `orchestration/ChessRealms.Platform.ServiceDefaults` — shared telemetry, health checks, service discovery, and HTTP resilience.
- `tests/` — separate empty xUnit v3 projects referencing Api, Application, Domain, and Infrastructure respectively.

`Infrastructure` leaves room for integrations beyond persistence. `PlatformDbContext` currently has no entities. An empty `InitialCreate` migration establishes the migration pipeline without adding business tables.

### Build and test

Run from the repository root:

```shell
dotnet tool restore
dotnet restore ChessRealms.Platform.slnx --locked-mode
dotnet build ChessRealms.Platform.slnx --no-restore
dotnet test ChessRealms.Platform.slnx --no-build --no-restore
```

The test projects intentionally contain no tests, so the runner currently reports that no tests were found. Package versions are centralized and each project has a NuGet lock file.

### Run locally

Start Docker Desktop with Linux containers, then run:

```shell
dotnet dev-certs https --trust
dotnet run --project orchestration/ChessRealms.Platform.AppHost
```

Open the dashboard URL printed in the terminal. AppHost starts PostgreSQL with a persistent Docker volume, waits for database readiness, runs `migrations`, then starts `api` only if the worker exits successfully. Connection strings are supplied by Aspire; no database password is stored in source control. The API exposes `/openapi/v1.json`, `/health` (including database connectivity), and `/alive` in Development.

The worker has a two-minute timeout, configurable through `Migration__Timeout` (for example, `00:05:00`). Failure or cancellation returns a nonzero exit code and prevents the API from starting. Restart AppHost to run migrations again after changing the model.

### Add a migration

```shell
dotnet ef migrations add MigrationName --project src/ChessRealms.Platform.Infrastructure --output-dir Persistence/Migrations
```

The design-time factory allows scaffolding without starting PostgreSQL or AppHost. For EF commands that connect to a database, set `ConnectionStrings__platform` to the connection string shown in the Aspire dashboard. Apply migrations through MigrationWorker; the API does not migrate the database on startup.

Documentation is maintained in English.

## Contributing

Contribution guidelines are maintained at the organization level. Consult those guidelines before submitting a contribution.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
