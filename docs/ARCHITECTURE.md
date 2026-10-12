# Architecture

## 1. Architectural goal
Use a modular architecture that allows the frontend and backend to evolve independently while sharing a precise API contract.

## 2. Logical components
### Frontend
Responsible for:
- routing
- page composition
- UI components
- client-side interaction state
- form handling
- API consumption
- user-facing validation
- loading/error/empty states

### Backend
Responsible for:
- authentication
- authorization
- business rules
- validation
- persistence
- transactions
- reporting queries
- audit logging
- integration boundaries

### Database
Relational database recommended for transactional consistency and rich relationships.

### File storage
Use object/file storage for uploaded documents and images. Store metadata and secure references in the database rather than binary files in ordinary relational records unless there is a documented reason.

## 3. Layering
Backend should generally separate:
- API/controller layer
- validation/schema layer
- service/business-logic layer
- data-access/repository layer
- domain models/entities
- infrastructure/integration layer

Frontend should generally separate:
- routes/pages
- feature components
- shared UI components
- API/data-access layer
- client-side state
- validation schemas
- utility functions

## 4. Cross-cutting concerns
Authentication, authorization, logging, error handling, configuration, observability, and audit events should use consistent shared patterns.

## 5. API principle
The API contract in `docs/API.md` is the boundary between frontend and backend. Backend implementation details must not leak into the frontend.

## 6. Environment separation
Support development, testing, staging, and production configuration without committing secrets.

## 7. Data integrity
Important operations such as appointment booking, payment recording, stock movements, and other multi-record updates should use appropriate transaction boundaries.

## 8. Scalability
Start with a modular monolith unless a concrete requirement justifies distributed services. Keep domain boundaries clean enough that components can be separated later.

## 9. Observability
Production systems should provide structured logs, error tracking, health checks, and useful operational metrics without logging secrets or unnecessary personal/medical information.

## 10. Architecture decision rule
Choose the simplest solution that satisfies documented requirements, security, maintainability, and expected scale. Document significant architectural decisions.

## 11. Decisions record
### ADR-001: Backend stack (decided in B-01)
- Runtime/framework: .NET 10 (LTS), ASP.NET Core Web API with controllers.
- Shape: modular monolith in a single `VetPlatform.Api` project, organised by feature folders under
  `Features/` with shared infrastructure under `Common/`. Split into more projects only when a real boundary requires it.
- Tests: xunit with `WebApplicationFactory` integration tests in `VetPlatform.Api.Tests`.
- Errors, request correlation and logging: see `API.md` section 2 and `backend/README.md`. Logging uses the built-in
  `Microsoft.Extensions.Logging` JSON console formatter (no third-party logging dependency); the log sink is the
  platform's stdout collector.
- Still open, to be decided and recorded in the milestone that needs them: file storage provider (B-15).
  Database: see ADR-002. Authentication: see ADR-003.

### ADR-002: Database and data access (decided in B-02)
- Engine: PostgreSQL (18 for local development and tests). Data access: EF Core 10 with the Npgsql provider,
  through a single `AppDbContext` in `Common/Persistence/`. Feature modules add entities via
  `IEntityTypeConfiguration<T>` classes in their own feature folder; the context picks them up automatically.
  Raw SQL only through EF's parameterised APIs (`FromSql`/`SqlQuery` with interpolation), never string concatenation.
- Schema conventions, applied by every later milestone:
  - table and column names are snake_case (`EFCore.NamingConventions`), matching `DATABASE.md`;
  - primary keys are `uuid`, generated in the application with `Guid.CreateVersion7()` (time-ordered, index-friendly);
  - timestamps are `timestamptz`, stored in UTC and mapped to `DateTimeOffset`;
  - money is `numeric(19,4)` mapped to `decimal`, never floating point;
  - clinic-owned tables carry a non-null `clinic_id` foreign key with an index that starts with `clinic_id`.
- Migrations: EF Core migrations in `Common/Persistence/Migrations/`, history table `__ef_migrations_history`.
  The app never migrates on startup; migrations are applied as an explicit deployment step (see `backend/README.md`).
  B-02 ships an empty `Baseline` migration; the first tables arrive with the identity milestones (B-03/B-04).
- Connection string: `ConnectionStrings:Default`, from environment variables (`ConnectionStrings__Default`) or a
  secret store; `dotnet user-secrets` in local development. The app refuses to start without one.
- Not decided yet: tenant-scoping mechanism (e.g. EF global query filters on `clinic_id`) belongs to B-04, when
  the authenticated clinic is known; connection-retry policy is deferred to X-05 because EF's retrying execution
  strategy changes how explicit transactions must be written.
- Tests: integration tests run against real PostgreSQL (Testcontainers, or `VETPLATFORM_TEST_POSTGRES`), each test
  class in its own database. No in-memory provider: it hides constraint, transaction and SQL behaviour.

### ADR-003: Authentication with server-side cookie sessions (decided in B-03)
- Mechanism: ASP.NET Core cookie authentication. The cookie (`__Host-vet_session`: HttpOnly, Secure, SameSite=Strict,
  browser-session lifetime) carries the user id, clinic id and a session id. Every request re-validates the session
  row in `user_sessions` (`SessionCookieEvents`), so logout, idle timeout (2 h), absolute lifetime (12 h), disabling a
  user and suspending a clinic take effect on the next request. The check also requires the cookie's user and clinic
  claims to match the session row, so after it the claims are trustworthy: B-04 tenant scoping may rely on them. Chosen over JWTs because browser sessions need
  immediate revocation and HttpOnly storage; a future mobile client can add a token scheme alongside.
- Cost: one indexed database read per authenticated request, plus at most one `last_seen_at` write per session per
  minute. Acceptable at clinic scale; revisit in X-04 if needed.
- Passwords: ASP.NET Core Identity's `PasswordHasher<T>` (PBKDF2) from the shared framework, with automatic rehash
  when the algorithm parameters are upgraded. ASP.NET Core Identity's stores and tables are not used; the schema
  follows `DATABASE.md`.
- Credential abuse: a fixed-window login rate limit per client (10/min; IPv4 address or IPv6 /64) and a temporary
  per-account lockout (5 consecutive failures, 15 min). Failures are counted with one atomic UPDATE, so parallel
  guesses cannot slip past the lock. All sign-in failures return the same 401 after the same work (one password hash
  and one failed-attempt UPDATE, which matches no row when nothing should be counted), so neither responses nor
  timing distinguish unknown emails from real ones.
- Accepted risk (reviewed in B-03): anyone who knows a staff email can keep that account locked by sending 5 wrong
  passwords every 15 minutes, and the lock alone still allows about 480 guesses per account per day. Mitigations to
  consider when they become necessary: escalating lock durations, a per-account limit across clients, alerting on
  lockouts, MFA (future milestone).
- CSRF: ASP.NET Core antiforgery. A global MVC filter requires a valid `X-XSRF-TOKEN` header on every unsafe request;
  `GET /api/auth/csrf` issues the readable `XSRF-TOKEN` cookie. Tokens are bound to the sign-in session
  (`SessionBoundAntiforgeryData`), not only the user. SameSite=Strict cookies are a second layer. Because the filter
  only runs for MVC controllers, unsafe endpoints must be controller actions; a test fails if a minimal-API endpoint
  accepts unsafe methods.
- Secure default: `MapControllers().RequireAuthorization()`, so every controller endpoint needs a signed-in user unless
  marked `[AllowAnonymous]`. Applied per endpoint, not as a fallback policy, so unmatched routes still return 404/405.
- Identity model: email is unique across the platform (one account, one clinic), so sign-in needs no clinic selector.
  The database enforces it case-insensitively (ICU nondeterministic collation `case_insensitive` on `users.email`),
  whoever writes the row; the application also stores emails trimmed and lower-cased.
  Revisit if one person must work for several clinics (multi-location is a future candidate in `ROADMAP.md`).
- Settings live in the `Auth` configuration section (`AuthOptions`) and are validated at startup.
- The clock is the DI `TimeProvider` everywhere (sessions, lockout, timestamps), so tests use a fake clock.
- Frontend constraint: same-site deployment, no CORS (see `API.md` section 3).
- HTTPS everywhere, including local development (`https://localhost:7096`): antiforgery refuses to issue or check
  Secure cookies on plain-HTTP requests. Behind a TLS-terminating proxy, forwarded headers must be configured (X-05)
  or every CSRF-protected request fails.
- HSTS is sent outside Development. HTTPS redirection is deliberately not used for the API: plain-HTTP API calls
  should fail, not be redirected (a redirect can replay a request body over HTTPS after it was already sent in clear).
- Audit events for authentication (sign-in, failure, lockout, logout) are required by `SECURITY.md` section 11 but
  need the `AuditLog` table, which no milestone owned. B-04 now owns it (see `ROADMAP.md`); until then sign-in events
  are application logs only. Sessions store no IP address or user agent (data minimisation); revisit with B-04.
- Deferred to X-05 (deployment): persisting and encrypting the ASP.NET Core Data Protection key ring (it encrypts the
  session and antiforgery cookies; keys currently live in the default per-machine location, so a new container signs
  everyone out), and forwarded-headers configuration so the app sees the original scheme (HTTPS) and client address
  behind a proxy.

### ADR-004: Roles, permissions, tenant isolation and audit logging (decided in B-04)
- Roles are a fixed enum (`Admin`, `Veterinarian`, `Nurse`, `Receptionist`, `Inventory`), stored per user in
  `user_roles`. Permissions are string constants granted to roles in code (`Common/Authorization/Permissions.cs`).
  Endpoints declare `[RequirePermission(Permissions.X)]`; a policy handler checks it. Chosen over per-clinic
  configurable roles for now: simpler, reviewable in code, and enough for the documented users. Revisit if clinics
  need custom roles. This replaces the Role, Permission and RolePermission tables sketched in `DATABASE.md`.
- Roles are loaded from the database during session validation on every request and added as claims for that request
  only; the cookie never carries them. Role and status changes therefore apply to the next request.
- Tenant isolation is enforced twice:
  - Read side: every `IClinicOwned` entity has an EF Core global query filter on the current clinic. With no current
    clinic the filter matches nothing (fail closed). `ExecuteUpdate`/`ExecuteDelete` honour it too.
  - Write side: `ClinicScopeInterceptor` defaults new rows to the current clinic and throws when saving a row of, or
    moving a row to, another clinic.
  - The current clinic comes from the validated session (ADR-003), via `ITenantContext`.
  - Opting out (`IgnoreQueryFilters()`) is reserved for code that must work across clinics: sign-in, session
    validation, platform-wide email uniqueness checks and the CLI. Each use carries a comment explaining why.
  - Other clinics' records are reported as `404`, so their existence is not revealed.
- A clinic always keeps at least one active administrator: disabling or demoting the last one is refused, and such
  changes take a row lock on the clinic so two administrators cannot demote each other at the same moment.
- Audit log (`audit_logs`): an append-only table (a database trigger rejects UPDATE, DELETE and TRUNCATE). Entries are
  added to the same unit of work as the change they describe, so both commit or neither does. Actor, clinic and request
  id default to the current request. Metadata is JSON and never holds secrets, passwords or email addresses; failed
  sign-ins for unknown emails are recorded without the email. Recorded so far: sign-in success, failure (with
  reason), lockout, logout, password change, user created/updated/roles changed. Querying is B-27.
- Bootstrap: `create-admin` (CLI, password on stdin) creates a clinic and its first administrator. There is no public
  sign-up endpoint.
- Passwords: 12 to 1024 characters and not the email address (NIST SP 800-63B style, no composition rules).
  Self-service change via `POST /api/auth/password` signs out the user's other sessions.
- Not yet done: administrator-initiated password reset and "must change password at next sign-in". Users currently
  change the initial password themselves; email-based reset needs notifications (B-25).
