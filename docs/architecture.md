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

ASP.NET Core Identity types must not enter Domain or Application. Future application
use cases should access identity through narrow application contracts and stable
user IDs. Game participants will not require an `IdentityUser` object.

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

Registration and email confirmation expose HTTP endpoints backed by application
contracts and the isolated Identity implementation. Opaque confirmation sessions and
durable email requests live in PostgreSQL. The API worker processes requests through
a placeholder email adapter until a provider is selected. See
[registration and confirmation](registration.md) for contracts, limits, and deployment
requirements. Login uses email/password, Identity lockout, and RS256 JWT access tokens.
The API validates JWT signatures, issuer, audience, type, algorithm, and lifetime.
See [login and JWT authentication](login.md) for key configuration and scope.
See [refresh tokens and sessions](refresh-tokens.md) for storage, concurrency, and cleanup.
Logout/session management endpoints, password reset, and Angular integration remain subsequent work.

## Identity code navigation and errors

Public contracts and types each have their own file. Private test doubles stay with
the tests that use them. Infrastructure is grouped by responsibility:

| Location under `Infrastructure/Identity` | Responsibility |
| --- | --- |
| Root services | Registration and login orchestration, Identity registration |
| `IdentityLoginServiceCollectionExtensions.cs` | API-only login, JWT, and session service registration |
| `EmailConfirmation/` | Confirmation sessions, email dispatch, sender contract, placeholder, worker |
| `Sessions/` | Refresh rotation, session configuration and cleanup |
| `Jwt/` | Signing keys, JWT settings and issuance |
| `Tokens/OpaqueToken.cs` | Shared random token generation and hashing; each purpose has its own store |
| `Persistence/` | Entities, context and migrations with stable entity namespaces |

Expected failures (invalid credentials/tokens, duplicate registration, throttling)
return explicit application results. The controller maps known results to HTTP
statuses; an unknown result is a programming error and becomes generic 500 Problem
Details. Unexpected database or Identity update failures also propagate to the API's
exception handler instead of being misreported as invalid credentials or tokens.

Background workers catch processing failures to remain available and log only the
exception type, excluding potentially sensitive exception messages. Provider adapters
return typed, application-owned email failure categories; arbitrary provider text is
never exposed as an API failure code. Cancellation rolls back an in-flight delivery
so the persisted request remains available for a later attempt. The placeholder sender
continues to report `ProviderNotConfigured` and makes no external calls.

## Game functionality

Chess rules come from ChessRealms/Engine. Guests are temporary participants without
an ASP.NET Core Identity record, but require a protected guest session. They can play
with guests or registered users. Login sessions, lobbies, and chess games are distinct
concepts. Private lobbies will support invitations to registered users, an opt-out
for incoming invitations, optional passwords, and invitation spam protection.

Clock requirements and guest-to-account conversion remain open. Playing against the
computer remains part of the initial product milestone. Branch protection and
repository rules will be configured manually later.

## Delivery order

1. Identity persistence and integration verification (this foundation).
2. Registration/email confirmation, JWT and refresh sessions, revocation, and recovery.
3. Angular authentication integration.
4. Engine integration and persistent game state.
5. Online games, reconnects, guests, lobbies, and invitations.
6. Computer opponent; optional Google login and additional Identity features separately.
