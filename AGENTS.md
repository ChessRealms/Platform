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

## Git workflow

These defaults apply unless the user explicitly requests a different workflow.
Read-only tasks leave branches and files untouched.

- Before editing, inspect `git status --short --branch`, the current branch, and
  its upstream. Reuse an existing branch for the same task. For new work, create
  a dedicated branch from the current remote default branch. Use
  `git switch --no-track -c <branch> origin/main` when `origin/main` is the
  intended base. Preserve unrelated local changes when switching branches.
- Generate a descriptive branch name automatically using
  `<type>/<short-kebab-case-description>`, for example
  `feat/persistent-game-core` or `test/concurrent-tab-refresh`. Common types are
  `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `ci`, `build`, and
  `chore`. Platform uses `feat/` for the organization guide's feature branch
  type. Use the user's branch name when provided. Do not add a `codex/` prefix.
- Keep the base branch separate from the push upstream. A new local task branch
  has no upstream until its first authorized push. Before any push, verify the
  current branch, its upstream, the remote, and the destination ref. Resolve an
  upstream that points to the default branch before pushing. Push explicitly to
  the named task branch with `git push --set-upstream origin HEAD:refs/heads/<branch>`
  when `origin` is the intended remote. Never use a bare `git push` or push to
  the default branch unless the user explicitly names it as the destination.
- Commit, push, and create a pull request only when the user authorizes each
  action. A request for a title does not authorize the action.
- For every completed change, include a recommended commit title and a
  recommended PR title in the final response without waiting for a prompt.
  Follow the [ChessRealms contribution guide](https://github.com/ChessRealms/.github/blob/main/CONTRIBUTING.md)
  and use Conventional Commit style for both titles, with an imperative summary
  and a scope where useful, for example `fix(games): reject stale moves`.

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

There is no lint script. Infrastructure tests require Docker with Linux containers
and use a temporary PostgreSQL container; other backend test projects are empty.
AppHost starts containers, installs frontend dependencies and applies migrations;
do not use it as a substitute for build/test verification. See README.md for setup.

Codex permissions and approvals use the user's app settings; this repository
does not provide custom permission profiles, command rules or reviewer agents.
