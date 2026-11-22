# Platform architecture decisions

## Boundaries

The platform remains a modular monolith. Domain has no project dependencies;
Application references Domain, Infrastructure references Application, and Api
composes the application and infrastructure. PostgreSQL and EF Core provide persistence.

Identity is an isolated module under `Infrastructure/Identity`, not a microservice.
It owns `PlatformUser`, its EF stores, DI registration, and migrations. Its separate
`IdentityDbContext` uses the same database as `PlatformDbContext`, but owns the
`identity` schema and its own migration history. MigrationWorker applies platform
migrations followed by Identity migrations; Api never migrates on startup.
These are separate migration operations, not one cross-context transaction.

ASP.NET Core Identity types stay in Infrastructure. Application use cases access
Identity through narrow contracts and stable user IDs. The game model stores those
IDs for White and Black.

## Accounts and authentication

Accounts use GUID identifiers and ASP.NET Core Identity for password hashing,
validation, and persistence. Email must be unique (including a database constraint
on normalized email). Confirmed email is configured as a prerequisite for sign-in;
login and refresh enforce it before issuing tokens. Roles are not required yet,
so the context uses the user-only Identity model.

The chosen direction is application-issued JWT access tokens, rotating refresh
tokens, and revocation of individual or all login sessions, without a separate
OAuth authorization server. Access tokens last ten minutes by default; opaque refresh
tokens use JSON transport and PostgreSQL sessions with an absolute 30-day expiration.
Rotation is transactional; reuse revokes the whole session without a grace period.
Existing access JWTs remain valid until expiration plus clock skew.
Google login is optional and can be added later.

Registration, email confirmation, and password recovery expose HTTP endpoints backed
by application contracts and the isolated Identity implementation. Opaque confirmation
sessions and durable email requests live in PostgreSQL. The API worker sends them through
the configured SMTP adapter. Mailpit provides SMTP for local development. See
[registration and confirmation](registration.md) for contracts, limits, and deployment
requirements. Login uses email/password, Identity lockout, and RS256 JWT access tokens.
The API validates JWT signatures, issuer, audience, type, algorithm, and lifetime.
Application endpoints use the `/api` prefix; operational health and OpenAPI
endpoints retain their conventional paths.
See [login and JWT authentication](login.md) for key configuration and scope.
See [refresh tokens and sessions](refresh-tokens.md) for storage, concurrency, and cleanup.
Session management endpoints are available. Angular supports registration, email
confirmation, sign-in, session restoration, sign-out, and password recovery.
Password reset changes the security stamp and revokes login sessions in the same
transaction. See [password recovery](password-reset.md) for delivery and configuration.

## Identity code navigation and errors

Public contracts and types each have their own file. Private test doubles stay with
the tests that use them. Infrastructure is grouped by responsibility:

| Location under `Infrastructure/Identity` | Responsibility |
| --- | --- |
| Root services | Registration and login orchestration, Identity registration |
| `IdentityLoginServiceCollectionExtensions.cs` | API-only login, JWT, and session service registration |
| `EmailConfirmation/` | Confirmation sessions and dispatch, SMTP transport, identity email worker |
| `PasswordReset/` | Reset token provider, password recovery, and reset email dispatch |
| `Sessions/` | Refresh rotation, session configuration and cleanup |
| `Jwt/` | Signing keys, JWT settings and issuance |
| `Tokens/OpaqueToken.cs` | Shared random token generation and hashing; each purpose has its own store |
| `Persistence/` | Entities, context and migrations with stable entity namespaces |

Expected failures (invalid credentials/tokens, duplicate registration, throttling)
return explicit application results. The controller maps known results to HTTP
statuses; an unknown result is a programming error and becomes generic 500 Problem
Details. Unexpected database or Identity update failures also propagate to the API's
exception handler instead of being misreported as invalid credentials or tokens.

Background workers log exception types and typed email failure categories.
Cancellation rolls back an in-flight delivery so the persisted request remains
available for a later attempt. The unconfigured adapter reports
`ProviderNotConfigured`.

## Game functionality

The [games API](games.md) supports private, untimed games between two confirmed
accounts. Domain owns participation and the game lifecycle. Application checks
commands and Infrastructure applies chess rules through the ChessRealms.Engine
adapter. PostgreSQL stores a versioned game record and commits each position,
result, and revision together.

The first product milestone is a complete private game between two registered
players. The current flow creates a game for a known player ID and gives the
creator a link to share. The invited player accepts explicitly. To close the
milestone, verify one browser scenario with independent sessions: open the invite
while signed out, sign in and return to the game, accept, play to a visible result,
and restore the saved state after reloading or losing the connection. The backend
continues to enforce participant access, move legality, and revision consistency.
The Angular board uses polling for this milestone.

Realtime updates, opponent discovery, richer invitations and lobbies, guests, and
clocks are follow-ups. Private lobbies may support incoming invitation opt-out,
passwords, and spam protection. Guests would use a protected session and could
play with guests or registered users. Clock and guest-to-account conversion
requirements remain open.

Branch protection and repository rules will be configured manually later.

## ChessRealms library plans

These are candidate projects for the ChessRealms organization. Each could live in
its own repository and publish independently alongside `ChessRealms.Engine`.

| Package | Proposed responsibility |
| --- | --- |
| `ChessRealms.Engine` | Existing rules library. Owns positions, legal moves, game history, and chess outcomes. |
| `ChessRealms.Uci` | Universal Chess Interface (UCI) tooling for exchanging positions, search requests, and move responses with chess engines. Its chess-specific code can reuse `ChessRealms.Engine` for positions and move notation. The scope of client and host support is open. |
| `ChessRealms.Search` (working name, originally `ChessRealms.AI`) | Own move selection, including position evaluation and search under time or depth limits. The search library can depend on `ChessRealms.Engine`. A UCI entry point in that repository can combine it with `ChessRealms.Uci` for external clients. |

UCI is the protocol boundary for running an engine as an external program.
An in-process caller could use the search library directly. This keeps the
search reusable from Platform, a UCI executable, or tests. See the
[UCI specification](https://backscattering.de/chess/uci/) and
[Stockfish](https://github.com/official-stockfish/Stockfish).

### Future Platform integration

If Platform adds a computer opponent, Application can define a move provider
that takes a position with its history and a search budget, then returns a
coordinate move. One adapter could call Stockfish through UCI. Another could
call ChessRealms search directly or through its UCI entry point. Platform can
choose the provider per game if it offers both.

A computer-controlled seat would need an explicit participant model. Platform
would run search outside the save transaction and submit the candidate through
the same legality and revision checks used for a player's move. This lets a
stale result be discarded if the game changes during search.

## Delivery order

1. Identity persistence and integration verification.
2. Registration/email confirmation, JWT and refresh sessions, revocation, and recovery.
3. Angular authentication integration.
4. Engine integration and persistent game state.
5. Playable Angular board with polling.
6. Resignation, draw agreements, and claims for threefold repetition or the
   50-move rule.
7. Finish the private two-player milestone with end-to-end invitation, sign-in,
   gameplay, result, and recovery coverage.
8. Extend multiplayer with realtime synchronization, opponent discovery,
   invitations and lobbies, guests, and clocks as needed.
9. Consider computer opponents independently, with interchangeable move providers.
