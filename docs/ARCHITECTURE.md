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
  - The filter is a named filter (`AppDbContext.ClinicFilter`), so entities can add other filters (e.g. soft delete)
    without replacing it.
  - Child tables that are not `IClinicOwned` (user roles, later invoice items, ...) are not exposed as `DbSet`s and
    are reached only through their clinic-owned parent. `UserSessions` is the exception, used only by authentication
    code that is keyed by session or user id.
  - Sign-in clears the request's identity before running, so a browser already signed in to clinic A can sign in to
    a clinic B account, and that sign-in is never attributed to the clinic A user.
- A clinic always keeps at least one active administrator: disabling or demoting the last one is refused, and such
  changes take a row lock on the clinic so two administrators cannot demote each other at the same moment. Under that
  lock every user-management change re-checks from the database that the caller still holds `users.manage`, so a
  request authorised just before the caller was demoted cannot complete.
- Disabling a user revokes all their sessions (re-enabling does not revive them).
- Audit log (`audit_logs`):
  - Append-only: database triggers reject UPDATE, DELETE and TRUNCATE, and stamp `recorded_at` with the database
    clock on insert, so the stored timeline cannot be back- or forward-dated by the application. `occurred_at` is
    the application's clock (the fake clock in tests).
  - `clinic_id` and `actor_user_id` are foreign keys (RESTRICT): entries never dangle, and clinics and users with
    history cannot be deleted. Rolling back the B-04 migration refuses to run while the table holds entries.
  - Entries join the same unit of work as the change they describe, so both commit or neither does.
    `AuditTrail.Record` takes actor and clinic from the signed-in caller; `RecordFor` takes them explicitly and is
    used for sign-in events (no actor: whoever tries is not authenticated as the account) and the CLI.
  - Metadata is JSON and never holds secrets, passwords or email addresses; failed sign-ins for unknown emails are
    recorded without the email.
  - Recorded so far: sign-in success and failure (with reason), lockout, logout, password change and failed password
    change, sessions revoked, user created/updated/roles changed. Refused requests (403s, 409s) are application logs
    only.
  - Protection limit: the triggers stop the application, not the table owner, who can disable or drop them. Production
    must run migrations as a schema-owner role and the API as a separate role with only the privileges it needs
    (INSERT and SELECT on `audit_logs`). Recorded as an X-05 requirement.
  - For B-27: `audit_logs` is not `IClinicOwned` (rows can lack a clinic), so tenant-scoped audit queries must filter
    `clinic_id` explicitly; use one shared, tested query helper. Clinic-less rows (unknown-email sign-in attempts)
    need a separately authorised, platform-level view. Add indexes for the query shapes B-27 defines (e.g. actor and
    action).
  - Retention: no automatic purge. When one is needed (X-05), it must be an owner-only, audited operation, never a
    weakening of the triggers. If the table is later partitioned by time, remember that dropping or detaching a
    partition bypasses row triggers.
- Bootstrap: `create-admin` (CLI, password on stdin) creates a clinic and its first administrator. There is no public
  sign-up endpoint.
- Passwords: 12 to 1024 characters and not the email address (NIST SP 800-63B style, no composition rules).
  Self-service change via `POST /api/auth/password` signs out the user's other sessions.
- Wrong current passwords on `POST /api/auth/password` count towards the account lockout and are audited; the attempt
  that locks the account also signs out all its sessions, so a stolen session cannot be used to guess the password.
  Password change and sign-in serialise on a row lock on the user, so a sign-in with the old password cannot slip
  past the "sign out other sessions" step.
- Enums cross the API boundary only as exact names (`StrictEnumConverterFactory`, `StrictEnum`).
- Upgrade note: B-04 grants no roles to users created before it, so a pre-B-04 database would have no administrators.
  No deployment predates B-04; if one ever must be upgraded, grant `Admin` to the intended users in the same release.
- Not yet done: administrator-initiated password reset and "must change password at next sign-in". Users currently
  change the initial password themselves; email-based reset needs notifications (B-25).

### ADR-005: Clients and the pattern for clinic-owned records (decided in B-05)
- **Shape** (confirmed with the user): first and last name; structured optional address (`address_line1`,
  `address_line2`, `city`, `region`, `postal_code`, `country`); optional `preferred_contact_method`
  (`Email`, `Phone`, `Sms`, null = no preference); `Active`/`Inactive` status. Email is optional and **not**
  unique, because members of one household often share one.
- **Permissions** (confirmed): `clients.read` for Admin, Veterinarian, Nurse and Receptionist; `clients.manage` for
  Admin and Receptionist. Client changes have no cross-row invariant, so unlike user management they take no clinic
  lock and rely on the permission check at the door; concurrent edits are last-write-wins.
- **No deletion** (FR-CLI-3): there is no `DELETE`; deactivate instead. Later references use RESTRICT.
- **Cross-clinic references are refused by the database.** Clinic-owned parents get a unique `(clinic_id, id)` key and
  children reference them with a composite foreign key on `(clinic_id, <parent>_id)`. The query filter and write
  guard (ADR-004) remain the first line; this makes a missed check in a future service impossible to persist.
- **PATCH null semantics:** text fields use `""` to clear. Where `null` itself must be expressible
  (`preferredContactMethod`), the request uses `Optional<T>`, which tells an omitted property from an explicit null.
- **Audit:** `client.created`, `client.updated`; metadata lists changed field names, never values (personal data).
- **Timestamps** set by `TimestampsInterceptor` are truncated to microseconds (PostgreSQL precision), so a create
  response equals a later read.
- **Text PostgreSQL cannot store** (NUL characters) is a `400 BAD_REQUEST` from the global exception handler, for every
  endpoint, instead of a logged 500.
- **Accepted for now:**
  - Substring search (`ILIKE '%term%'` over names, email and phone) cannot use an index and scans the clinic's
    clients. Fine at single-clinic sizes; add `pg_trgm` GIN indexes if X-04 measures a need.
  - Other application timestamps (`audit_logs.occurred_at`, `users.last_login_at`, `user_roles.created_at`, session
    times) keep 100 ns precision in memory. None is returned straight from memory after a save today; revisit if one
    is.
  - Phone search matches the stored text as typed ("5550100" does not find "555 0100"). A normalised digits column can
    be added if users need it.

### ADR-006: Patients (decided in B-06)
- **Shape** (confirmed with the user): species from a fixed list plus `Other` (breed stays free text); `sex` and
  `reproductiveStatus` as two fields with `Unknown`; `dateOfBirth` with a `dateOfBirthEstimated` flag; current
  `weightKg` only (history arrives with consultation vitals, B-11); statuses `Active`, `Inactive`, `Deceased` with an
  optional `dateOfDeath` that exists only while deceased.
- **Permissions** (confirmed): `patients.read` and `patients.manage` for Admin, Veterinarian, Nurse and Receptionist.
  Every one of these roles also has `clients.read`, so embedding the owner summary in patient responses discloses
  nothing new. If a role ever gets `patients.read` without `clients.read`, revisit the embedded summary.
- **Ownership:** one primary owner (`client_id`) through the ADR-005 composite foreign key. New patients and transfers
  need an **active** client; keeping a now-inactive current owner is allowed so old records stay editable. Unknown,
  inactive and other clinics' clients get one identical `422` so nothing about other clinics leaks. Transfers are
  audited with both owner ids. Additional owners later: a `patient_owners` table; `client_id` stays primary.
- **Microchips** are normalised (separators removed, upper-cased) and unique per clinic, not platform-wide: a
  platform-wide check would tell one clinic that another treats the animal.
- **Validation needing the clock or the database** (owner, microchip, weight, dates) runs in the service and reports
  all problems in one `422`. "Today" is the UTC date plus one day of grace until clinics have a time zone (B-26).
- **Responses equal later reads:** `weightKg` drops trailing zeros, because PostgreSQL returns `numeric(7,3)` with three
  decimals.
- **Accepted for now:**
  - A client can be deactivated while it still owns active patients; the patients stay valid. Whether deactivation
    should be refused or cascade is a product decision for later.
  - A transfer racing a concurrent deactivation of the target client can still complete (no lock). The composite key
    still guarantees the owner is of the same clinic.
  - Search over owner names and microchip substrings scans the clinic's patients (as for clients, ADR-005).
