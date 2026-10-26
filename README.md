# ChessRealms Platform

An open-source chess platform focused on playing chess against other players or the computer.

## Scope and status

The first milestone is the core experience of playing a complete chess game against another player or a computer opponent. Additional features will be considered after this core functionality is in place.

The repository currently contains shared configuration and the solution structure. Backend projects and the Angular frontend will be added separately. There is no runnable application yet.

## Technology

- C# and .NET for the backend.
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

Build, test, and frontend setup instructions will be added with the corresponding projects. Documentation is maintained in English.

## Contributing

Contribution guidelines are maintained at the organization level. Consult those guidelines before submitting a contribution.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
