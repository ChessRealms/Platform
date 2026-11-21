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

Guests are temporary participants without
an ASP.NET Core Identity record, but require a protected guest session. They can play
with guests or registered users. Login sessions, lobbies, and chess games are distinct
concepts. Private lobbies will support invitations to registered users, an opt-out
for incoming invitations, optional passwords, and invitation spam protection.

Clock requirements and guest-to-account conversion remain open. Playing against the
computer remains part of the initial product milestone. Branch protection and
repository rules will be configured manually later.

## Delivery order

1. Identity persistence and integration verification.
2. Registration/email confirmation, JWT and refresh sessions, revocation, and recovery.
3. Angular authentication integration.
4. Engine integration and persistent game state.
5. Next milestone: playable board, online synchronization, reconnects, guests, lobbies, and invitations.
6. Computer opponent; optional Google login and additional Identity features separately.
