# Platform development

- Use the existing code and repository configuration as the source of truth.
- Keep documentation in English and follow `.editorconfig`.
- Preserve Domain -> no project dependencies, Application -> Domain,
  Infrastructure -> Application. Api composes application and infrastructure.
- Match existing C# nullable, cancellation, async/disposal and error-handling conventions.
- Frontend uses Angular 22 standalone components and Vitest; Angular 22 requires
  TypeScript 6.0.x. Keep Angular framework/compiler versions aligned.
- NuGet versions live in `Directory.Packages.props`; retain per-project
  `packages.lock.json` files and the frontend's `package-lock.json`.
- Keep changes focused; add regression tests for meaningful behavior changes.
- Never read or expose credentials, user-secrets contents or production data.
- Preserve existing user changes and report verification failures honestly.

## Build and test

From the repository root, with dependencies restored:

```powershell
dotnet build ChessRealms.Platform.slnx --no-restore
dotnet test ChessRealms.Platform.slnx --no-build --no-restore
```

From `src/cr-platform-web-app`, with dependencies installed:

```powershell
node node_modules/@angular/cli/bin/ng.js build --configuration development
node node_modules/@angular/cli/bin/ng.js test --watch=false
```

There is no lint script. Backend test projects currently contain no tests.
AppHost starts containers, installs frontend dependencies and applies migrations;
do not use it as a substitute for build/test verification. See README.md for setup.

Codex permissions and approvals use the user's app settings; this repository
does not provide custom permission profiles, command rules or reviewer agents.
