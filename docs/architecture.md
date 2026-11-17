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
future login code must enforce it before issuing tokens. Roles are not required yet,
so the context uses the user-only Identity model.

The chosen direction is application-issued JWT access tokens, rotating refresh
tokens, and revocation of individual or all login sessions, without a separate
OAuth authorization server. Token lifetimes, refresh transport, concurrency handling,
and immediate versus expiry-based access-token revocation remain to be specified.
Google login is optional and can be added later.

This foundation registers Identity core services and EF stores only. It does not
enable authentication schemes, expose endpoints, issue tokens, or provide email
confirmation/reset flows. Token providers, email delivery, abuse protection, and
Angular integration belong to subsequent work.

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
