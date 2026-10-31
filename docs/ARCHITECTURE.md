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
Use object/file storage for uploaded documents and images. Store metadata and secure references in the database rather than binary files in ordinary relational records unless there is a documented reason. Since B-15 content goes through `IFileStore` (local disk until X-05 chooses object storage; ADR-015).

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
    `clinic_id` explicitly; use the shared, tested helper `AuditLogQueries.ForCurrentClinic` (added in B-07). Clinic-less rows (unknown-email sign-in attempts)
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

### ADR-007: Patient history read model (decided in B-07)
- **Scope** (confirmed with the user): build the timeline API now and let each later milestone add entry types,
  rather than wait for the clinical tables. Readable with `patients.read`. The only entry type today is
  `PatientRegistered`; owner transfers, status and weight changes are deliberately not shown yet.
- **Contract:** `GET /api/patients/{id}/history`, standard offset paging, newest first (ties: id, then type),
  entries
  `{ id, type, occurredAt, actor, details }` with a fixed `details` shape per `type`. Clients must ignore unknown
  types, so adding a type is not a breaking change (API.md section 14). `openapi.yaml` lists known types without a
  `discriminator`, because some generators throw on unknown discriminator values.
- **Auditing reads:** not yet, while the only type is registration. Clinical types (B-11 onward) make reading the
  history an audited event (SECURITY.md section 10).
- **No new table.** Entries are read from their source records. Registration comes from the existing
  `patient.created` audit entries (served by the `(entity_type, entity_id)` index), so patients created in B-06 already
  have history. Clinical milestones add their own tables as sources.
- **Growth path:** each source becomes a query projected to `(type, id, occurred_at)`; sources are combined with
  `UNION ALL` in SQL, ordered and paged there, then the page's rows are loaded per type. Paging never happens in memory.
  If offset paging over the union gets slow (X-04), switch this endpoint to cursor paging on `(occurred_at, type, id)`.
- **Permissions per source:** a source the caller may not read (later: clinical entries without `clinical.read`) is
  left out of the union, so `total` and paging never reveal hidden entries.
- **Tenancy:** `audit_logs` has no query filter (ADR-004), so it is read only through the shared helper
  `AuditLogQueries.ForCurrentClinic`, which fails closed. Actor and owner names are resolved through the clinic-filtered
  `users` and `clients`, so a row that somehow names another clinic's user or client shows `null`, never that name.
- **Names are current, not historical:** the actor and owner names shown are today's names. The audit log stores ids,
  never names (ADR-004), so a renamed client shows the new name on old entries.
- **Staff names** (actor) are visible to every role with `patients.read`, as on a paper record; `users.read` still
  governs the staff list itself.

### ADR-008: Clinic time zone and veterinarian schedules (decided in B-08)
- **Time zone now, not in B-26** (confirmed): `clinics.time_zone` holds an IANA id (default `UTC`), set by
  `create-admin --timezone` and `PATCH /api/clinic` (`settings.manage`). Working hours are local wall-clock times;
  instants (exceptions, later appointments) are stored in UTC. Windows zone ids are refused so data stays portable.
- **Who has a schedule** (confirmed): users with the `Veterinarian` role. Blocks and exceptions reference them through
  a composite foreign key on `(clinic_id, user_id)`; `users` gained the unique `(clinic_id, id)` key for it.
- **Permissions** (confirmed): `schedules.read` for Admin, Veterinarian, Nurse, Receptionist; `schedules.manage`
  (anyone's, closures) for Admin; `schedules.manage_own` for Veterinarian. Write endpoints use
  `[RequireAnyPermission(manage, manage_own)]` at the door and the service checks "own"; a role change applies on the
  next request, without the user-management lock (no cross-row invariant here).
- **SRS Q-3** (confirmed): 5-minute grid everywhere; no buffers (a per-clinic buffer can be a B-26 setting); B-09 lets
  `appointments.manage` holders override a conflict with a required reason, audited.
- **Weekly hours as versioned blocks:** day of week, start/end minute (1440 = `24:00`), effective dates. Overnight
  shifts are two blocks. Overlaps are refused in the service under a row lock on the veterinarian (no exclusion
  constraint yet; B-09 introduces `btree_gist` for appointments and can add one here).
- **Exceptions** (confirmed): per veterinarian `Leave`, `Sickness`, `Training`, `Other`; clinic-wide `Closure` with no
  veterinarian. Instants on the grid. Exceptions and blocks can be deleted: they are planning data, not clinical or
  financial history, and every change is audited.
- **Staff privacy:** an absence type and reason can be health information. Only `schedules.manage` holders and the
  veterinarian concerned see them; others see `Unavailable` with no reason. Audit metadata never includes the reason,
  and records the type only for closures, because a future audit reader may not hold `schedules.manage`.
- **Bounds:** dates and instants must fall in 2000–2100 (keeps calendar arithmetic away from DateTime limits; year
  0001 would be stored as `-infinity`); at most 200 weekly-hours blocks per veterinarian; the subtraction of exceptions
  is a single linear pass, so overlapping exceptions cannot make the computation quadratic.
- **Locking:** weekly-hours writes take `FOR NO KEY UPDATE` on the veterinarian's user row (it serialises schedule
  writers but does not block foreign-key checks from other tables), only after the clinic-filtered lookup, and re-read
  the account status under the lock. Concurrent update/delete of the same row maps to `409 CONFLICT` globally
  (`DbUpdateConcurrencyException`).
- **Computed working hours** (confirmed): a pure calculator expands blocks for each local date, converts to instants
  in the clinic zone, merges, and subtracts the veterinarian's exceptions and all closures. Daylight saving: skipped
  local times move forward to the next valid time; repeated local times are read as standard time. Responses carry
  the local offset. B-09 reuses the calculator for booking validation and subtracts appointments for free slots.
- **Strict instants:** every `DateTimeOffset` in a JSON body must carry an explicit offset (`StrictInstantConverter`);
  otherwise .NET would assume the server's zone. Query-string instants use the same rule (`StrictInstant`).
- **Accepted for now:**
  - Changing the clinic time zone shifts all future working periods (they are local times); exceptions, being
    instants, do not move. Documented in API.md section 4.1.
  - Disabling a veterinarian keeps their blocks; computed hours still show them. B-09 must also refuse booking a
    disabled veterinarian.
  - The working-hours endpoint loads all blocks of the veterinarian that overlap the range and computes in memory;
    bounded by the 62-day limit and the block cap. Exceptions are not capped (years of leave are legitimate); the
    range query uses the `(clinic_id, veterinarian_id, ends_at)` index.
  - `ix_users_clinic_id` is now redundant with the `(clinic_id, id)` key; dropping it (and the same pair on clients and
    patients) is left to a later clean-up migration.

### ADR-009: Appointments and conflict validation (decided in B-09)
- **Confirmed with the user:** `appointments.read` / `appointments.manage` for Admin, Veterinarian, Nurse,
  Receptionist (manage includes the override); fixed types plus `Other`; no start before today (local midnight, so
  walk-ins earlier today can be recorded); patient clashes are conflicts like veterinarian clashes; 5 minutes to 12
  hours; active patients and active veterinarians only; one override (flag + reason) accepts every conflict found;
  status changes wait for B-10.
- **Client derived, not sent:** the appointment stores the patient's owner at booking (or when moved to another
  patient). Keeping it fixed afterwards preserves who was responsible at the time; invoices (B-16) will bill it.
- **Conflict model:** `OutsideWorkingHours` (not fully inside one computed working period, reusing
  `ScheduleService.WorkingIntervalsAsync`, i.e. weekly hours minus exceptions and closures), `VeterinarianDoubleBooked`
  and `PatientDoubleBooked` (overlap with `Scheduled`/`CheckedIn` appointments, half-open ranges). All conflicts are
  reported together in one `409` whose detail `field` names the request field; messages start with the conflict name.
- **Overrides are explicit and recorded:** `allowConflicts` + `overrideReason` stores the reason and the exact list of
  accepted conflicts on the appointment (CHECK keeps them consistent) and in the audit entry. A later placement that
  is clean clears it; non-placement edits keep it. An override flag without conflicts records nothing.
- **Race safety, two layers:** every placement locks rows in one fixed order: the appointment being edited (PATCH
  only), then the veterinarian's user row, then the patient's row (`FOR NO KEY UPDATE`, each only after its
  clinic-filtered lookup, and the appointment, role and status are re-read under the lock). Checks and writes are thus
  serialised per appointment, per veterinarian and per patient, and two concurrent edits of one appointment cannot
  combine into an unchecked placement. Conflict queries carry a redundant `start_at > start - 12 h` bound (the duration
  CHECK guarantees it) so the `(…, start_at)` indexes bound both sides while the locks are held. In the database, the exclusion constraint
  `ex_appointments_veterinarian_no_overlap` (`btree_gist`) forbids overlapping un-overridden active bookings of one
  veterinarian outright; its violation is mapped to the same `409`. Overridden rows are exempt by design.
- **Times:** stored UTC; responses use the clinic's local offset, matching the computed working hours.
- **Editing:** only `Scheduled` appointments (B-10 adds transitions and their own rules). No delete.
- **Accepted for now:**
  - Patient clashes are enforced only by the service under the patient lock (no second exclusion constraint), because
    a constraint per patient would also block legitimate overrides in a way that is hard to express.
  - The patient's owner may change after booking; existing appointments keep the earlier client.
  - Disabling a veterinarian does not touch their existing appointments; B-10 / the frontend should surface them.
  - `btree_gist` must be installable by the migration role (it is a trusted extension on PostgreSQL 13+; RUNBOOK).
  - Overridden rows stay outside the exclusion constraint for as long as they carry an override, even if the clash
    later disappears (e.g. the other booking is cancelled in B-10); the service check still covers them. B-10 should
    re-run the placement where a transition makes that meaningful.
  - The constraint's status list is raw SQL; `Appointment.ActiveStatuses` must stay in step (a test compares them).
- **Lock compatibility (fixed in B-09):** user management's clinic lock is now `FOR NO KEY UPDATE`. As `FOR UPDATE`
  it blocked every foreign-key check on the clinic row (each appointment insert) and could deadlock with a booking
  that held a veterinarian lock. Deadlocks and serialization failures that still occur are `409 CONFLICT` ("try
  again"), not `500`.

### ADR-010: Appointment status lifecycle (decided in B-10)
- **Transitions** (confirmed with the user): `Scheduled → CheckedIn → Completed`; `Scheduled` or `CheckedIn →
  Cancelled`; `Scheduled → NoShow`; plus two same-day undos, `CheckedIn → Scheduled` and `NoShow → Scheduled`.
  `Completed` and `Cancelled` are final. A table of explicit actions in the service, one endpoint per action, so each
  rule and its error are visible; no generic "set status".
- **Timing** (confirmed), always in the clinic time zone: check-in only on the appointment's local day; no-show only
  after the start; complete only after check-in; undo only on the day of the step being undone; cancel any time
  before completion. Violations and disallowed transitions are `409`.
- **Cancellation** (confirmed): required `cancelledBy` (`Client` / `Clinic`) for late-cancellation and no-show
  reporting, optional reason. The reason never goes to the audit log or the patient history.
- **Permissions** (confirmed): every transition needs `appointments.manage`. The actor of the latest change is stored
  (`status_changed_by_user_id`); the full trail is in the audit log.
- **Consistency in the database:** a CHECK ties every status to exactly its timestamps, so no code path can leave a
  `Completed` appointment without a completion time or a `Scheduled` one with a cancellation; another CHECK keeps
  completion and cancellation from predating check-in.
- **Concurrency:** every transition locks the appointment row (after the clinic-filtered lookup) and re-reads it,
  the same first lock as PATCH; `undo-no-show` additionally locks veterinarian then patient and refuses every
  veterinarian or patient clash (a no-show frees the slot, which may have been re-booked; an override from booking
  time covered other appointments, so it never excuses a new one). Working hours are not re-judged on undo.
- **Overrides on other bookings** (confirmed): left as recorded when a clash partner is cancelled or marked no-show;
  they clear on that booking's next re-placement.
- **Patient history:** `AppointmentCompleted`, `AppointmentCancelled`, `AppointmentNoShow` come from the appointments
  table itself, the second history source. As ADR-007 planned, both sources are projected to (type, id, occurred_at)
  and combined with `UNION ALL`, ordered and paged in SQL; the page's rows are then loaded per type. Readable with
  `patients.read`; this relies on every `patients.read` role also holding `appointments.read` (true today; otherwise
  gate this source). When clinical entry types arrive they are gated per source. The query reads all of one patient's
  finished appointments and sorts them (cheap at per-patient sizes; a partial index on finished statuses can follow).
- **Accepted for now:** timestamps of lifecycle steps come from the application clock (like `occurred_at` in the
  audit log); there is no separate "arrived" vs "seen" time beyond check-in and completion.

### ADR-011: Consultations (decided in B-11)
- **Confirmed with the user:** `clinical.read` for Admin, Veterinarian, Nurse; `clinical.write` for Veterinarian and
  Nurse; core vitals (weight, temperature, heart and respiratory rate, body condition, pain); completed consultations
  are frozen and corrected only by append-only addenda; an optional link to a `CheckedIn` appointment of the same
  patient (completing the consultation does not touch the appointment); every clinical read is audited; history shows
  `ConsultationCompleted` with the assessment to `clinical.read` holders only; one open consultation per patient;
  in-progress consultations can be discarded with a reason.
- **Sign-off:** completion needs `consultations.complete`, held only by veterinarians, because completing is the
  clinical sign-off; a nurse can open and document on behalf of the veterinarian of record.
- **Append-only and frozen in the database:** `consultation_addenda` has triggers that reject UPDATE, DELETE and
  TRUNCATE, and an INSERT unless the consultation is `Completed`; `consultations` has a trigger that rejects any UPDATE
  or DELETE once the status is no longer `InProgress` (same pattern and owner caveat as `audit_logs`, X-05). The
  service enforces the same rules first, so these only ever fire on a bug or a raw write. The lifecycle CHECK keeps
  status and timestamps consistent. At most 50 addenda per consultation, because they can never be removed.
- **Invariants in the database:** partial unique indexes for "one open per patient" and "one per appointment"
  (discarded ones excluded), so concurrent openings resolve to one `201` and `409`s. Vitals ranges are CHECKs too.
- **Weight:** completing with a weight sets the patient's current weight in the same transaction (patient row locked
  after the consultation row) and records a `patient.updated` audit entry naming the source consultation. A dated
  weight history can be read from completed consultations later.
- **Read auditing:** the service writes an audit entry on every clinical read before returning; a history page is a
  clinical read only when it contains clinical entries. Audit volume grows with use; retention is an X-05 topic.
- **History:** completed consultations are the third `UNION ALL` source (ADR-007), added only when the caller holds
  `clinical.read`; in-progress and discarded consultations never appear.
- **Timestamps** are truncated to microseconds before they are stored, so a write's response equals a later read.
- **Accepted for now:**
  - The four SOAP notes are plain text (no templates or rich text).
  - List rows carry the full object (notes and addenda), up to 100 per page; a summary shape can be added if lists
    get heavy.
  - Diagnoses, treatments and prescriptions attach to consultations in B-12/B-13 via the (clinic_id, id) key. If they
    carry `patient_id`, add a unique (clinic_id, patient_id, id) key on consultations so the database also guarantees
    they belong to the consultation's patient; keeping them off non-completed consultations stays a service rule
    under the consultation row lock.
  - Reads of the appointment list are not clinical and not audited.
  - Concurrent openings for the same appointment always hit the one-open-per-patient index first (same patient);
    the per-appointment index guards the sequential case.

### ADR-012: Diagnoses and treatments (decided in B-12)
- **Confirmed with the user:** editable while the consultation is in progress; after completion only a diagnosis's
  status may change (Provisional → Confirmed / RuledOut), by veterinarians only; free-text name plus an optional,
  unvalidated code and code system; statuses Provisional, Confirmed, RuledOut with one primary diagnosis per
  consultation; structured treatments (type, dose, route, frequency, duration as free text, notes) with an optional
  link to a diagnosis of the same consultation; a read-only, audited patient problem list; the diagnoses shown inside
  the existing `ConsultationCompleted` history entry.
- **Nested resources:** diagnoses and treatments only exist inside a consultation; every change is a sub-resource
  call that returns the whole consultation, under the same row lock and audit as other consultation writes.
- **Patient consistency in the database:** consultations gained a unique `(clinic_id, patient_id, id)` key and the
  children reference it, so the database guarantees a diagnosis or treatment belongs to its consultation's patient
  (and clinic). A treatment's diagnosis link uses `(clinic_id, consultation_id, diagnosis_id)`, so it cannot point at
  another consultation's diagnosis. Removing a diagnosis unlinks its treatments first.
- **Freeze in the database:** triggers on both tables follow the parent consultation's status, read `FOR SHARE` so
  a concurrent completion is waited for (anything while in progress; for diagnoses of completed consultations only
  `status`/`updated_at` may differ, compared as whole rows so future columns are covered too; nothing for discarded
  ones). `TRUNCATE` is refused on both tables. The service applies the
  same rules first and adds the veterinarian check, which the database cannot know.
- **Primary diagnosis:** a partial unique index allows one primary per consultation. Making another one primary clears
  the old flag in a first save inside the same transaction, because PostgreSQL checks the index row by row. All
  validation runs before that save, and the swap is the last step of the change.
- **For B-13:** if prescriptions link to a diagnosis, removing a diagnosis must unlink them too, and prescriptions
  need the same follow-the-consultation trigger; if they can exist without a consultation, give them their own
  (clinic_id, patient_id) foreign key to patients, because a composite key with a null part is not checked.
- **History shows current status:** the history entry lists the consultation's diagnoses as they are now, so a later
  confirmation is visible there; the audit log keeps when it changed.
- **Accepted for now:** no terminology validation or picklists (codes are free text); diagnosis status changes after
  sign-off do not add an addendum automatically (the audit entry records who and when); treatments do not touch
  inventory until B-19+.

### ADR-013: Prescriptions (decided in B-13)
- **Confirmed with the user:** only veterinarians write prescriptions (new permission `prescriptions.issue`, granted
  to Veterinarian only; nurses and administrators read them through `clinical.read`); prescriptions exist only inside
  a consultation (nested like treatments; repeats without a visit can come later); editable while the consultation is
  in progress, then frozen, with a veterinarian able to cancel one with a required reason (`Active` → `Cancelled`);
  numeric quantity with a free-text unit, a refills count that is recorded only, and an optional link to a diagnosis
  of the same consultation; a read-only, audited patient medication list; prescriptions shown with their current
  status inside the existing `ConsultationCompleted` history entry (no new entry type); no controlled-drug flag until
  B-20 answers SRS Q-5.
- **Required fields:** medication name, dose, frequency, quantity and unit, because a prescription without them cannot
  be dispensed; route, duration and instructions are optional. Medication names are free text until the inventory
  product catalogue (B-19) can be referenced.
- **Prescriber:** `prescribed_by_user_id` is the veterinarian who added the prescription; it does not change when
  another veterinarian edits it while the consultation is open (the audit log records every editor). The consultation's
  veterinarian of record is shown beside it in the medication list.
- **Patient and diagnosis consistency in the database:** the same composite keys as treatments (ADR-012): the
  prescription references `consultations (clinic_id, patient_id, id)` and its diagnosis link uses
  `(clinic_id, consultation_id, diagnosis_id)`. Removing a diagnosis unlinks its treatments and prescriptions in the
  same save, and the audit entry lists both.
- **Freeze in the database:** a trigger follows the consultation's status (read `FOR SHARE`): anything while it is in
  progress except a `Cancelled` row (cancelling is only meaningful after sign-off; in progress a prescription is edited
  or removed); after completion only the one-way step `Active` → `Cancelled`, which may change nothing but the status,
  the cancellation fields and `updated_at` (compared as whole rows, so future columns are covered); nothing on a
  discarded consultation. `TRUNCATE` is refused. A lifecycle CHECK ties the cancellation fields to the status. The
  service applies the same rules first and they only fire on a bug or a raw write.
- **Rows never move between consultations** (found in the B-13 review): the triggers judge an UPDATE by the new parent
  only, so moving a row from a completed consultation to an open one of the same patient passed the composite key and
  the trigger. The prescriptions trigger now refuses any change of `clinic_id`, `patient_id` or `consultation_id`, and
  the same migration re-creates the B-12 diagnoses and treatments trigger functions with that check (its rollback
  restores the B-12 versions). The API never changes these columns.
- **Concurrency:** every change runs under the consultation's row lock, like diagnoses and treatments, so a
  cancellation and a concurrent completion or a second cancellation are serialised.
- **Audit:** `prescription.added`, `.updated` (field names), `.removed`, `.cancelled` and the clinical read
  `prescription.listed`; metadata never holds medication names, instructions or the cancellation reason.
- **Statuses:** `Active` means "not cancelled", not "course still running"; an end date can be derived later from the
  duration if it becomes structured.
- **Accepted for now:** the medication list reads all of a patient's prescriptions of completed consultations through
  the consultation foreign key's index and pages in SQL (cheap at per-patient sizes); refills are not tracked against
  dispensing; prescriptions do not touch inventory or billing until B-16/B-19+; a prescription cannot be re-activated
  after cancellation (a new consultation prescribes again); a prescription printout or label is a frontend/reporting
  concern.

### ADR-014: Vaccinations (decided in B-14)
- **Confirmed with the user:** veterinarians and nurses record vaccinations (`clinical.write`; administrators read
  through `clinical.read`); a vaccination is a patient-level record with an optional link to an in-progress or completed
  consultation of the same patient, and its rules do not depend on that consultation; records are never edited, and a
  wrong one is voided with a reason and recorded again; doses given in the past or elsewhere can be recorded, naming
  the place instead of a veterinarian; an optional next due date feeds a clinic-wide due list readable by reception
  (new permission `vaccinations.read_due` for Admin, Veterinarian, Nurse, Receptionist); doses appear in the patient
  history as a new clinical entry type `VaccinationGiven`; the vaccine name is free text until B-19.
- **Dates, not instants:** `administeredOn`, `nextDueOn` and `lotExpiresOn` are clinic-local calendar days, because a
  certificate states a day and historical doses rarely have a time. "Today" (the latest allowed administered day) is
  the clinic's local date. The history needs an instant to order by, so each row stores the start of its administered
  day in the clinic's time zone when recorded (`administered_day_starts_at`); a later time-zone change does not move
  existing entries (accepted, like exceptions in ADR-008).
- **Given here vs elsewhere:** exactly one of `veterinarian_id` and `administered_elsewhere` (CHECK). Doses given here
  name an active veterinarian of the clinic, who may differ from the nurse who records it (`recorded_by_user_id`).
- **Immutable in the database:** trigger `vaccinations_guard` allows INSERT of `Active` rows only (and no link to a
  discarded consultation, whose row it reads `FOR SHARE`), the one-way UPDATE `Active` → `Voided` touching nothing but
  the status, the void columns and `updated_at` (whole-row comparison, so future columns are covered, and moving a row
  to another patient or consultation is refused too), and no DELETE or TRUNCATE. A lifecycle CHECK ties the void
  fields to the status. Voiding locks the row (`FOR NO KEY UPDATE`, after the clinic-filtered lookup), so two voids are
  serialised into one `200` and one `409`. Recording with a consultation link takes `FOR SHARE` on the consultation
  after its clinic-filtered lookup and re-checks it, so a concurrent discard cannot slip in between. The trigger reads
  the consultation's status `FOR SHARE` and only then tests it: a status filter inside the locking query (as first
  written, found in the B-14 review) would skip a row that is being discarded instead of waiting for it.
- **A later discard keeps the link** (confirmed design, made explicit in the review): the "not discarded" rule holds
  when the dose is recorded; discarding the consultation afterwards does not touch the vaccination, whose rules do not
  depend on the consultation. The dose was still given; void it if it was not.
- **Due list:** for each patient and vaccine (lower-cased name) the latest active dose (`DISTINCT ON`, by
  administered day, then recording time, then id) is due when its `next_due_on` falls in the range; without `from`,
  overdue doses are included. A newer dose without a due date ends the series, and voiding the newest dose makes the
  previous one count again. Only active patients are listed. Each item carries patient, owner contact, vaccine name
  and dates, and no lot, notes or other clinical detail, because reception reads it; every read is audited
  (`vaccination.due_listed`, with the range and ids).
- **History:** `VaccinationGiven` is the fourth `UNION ALL` source (ADR-007), included only for `clinical.read`
  holders; voided doses stay with their current status, so the record shows what was entered and withdrawn. A page
  with clinical entries is an audited read listing consultation and vaccination ids.
- **Audit:** `vaccination.recorded`, `.voided`, and the reads `.viewed`, `.listed`, `.due_listed`; metadata never holds
  vaccine names, notes or the void reason.
- **Accepted for now:** the due list scans the clinic's active vaccinations to find the latest dose per patient and
  vaccine (fine at clinic sizes; a partial index or a materialised "current dose" can follow if X-04 measures a need);
  vaccine names are grouped by exact text ignoring case, so "Rabies" and "Rabies 3yr" are separate series until B-19
  products exist; due dates are entered, not computed from a protocol; doses do not touch inventory (lot numbers are
  not checked against stock) until B-19+; reminders are sent by people from the due list until B-25 notifications;
  the patient-active and veterinarian-active checks on recording take no lock, so a deactivation at the same moment
  can still let one dose through (locking those rows here would cross the appointment -> veterinarian -> patient lock
  order; the composite keys still keep everything in the clinic, as in ADR-006); the due list projects only the owner's
  contact fields, so client notes and address are never loaded for reception.

### ADR-015: Attachments, file storage and clinical notes (decided in B-15)
- **SRS Q-2 answered (confirmed with the user):** content is stored through an `IFileStore` abstraction with a
  local-disk provider now (`FileStorage:LocalRoot`, a private directory outside any web root); an S3-compatible
  provider follows when hosting is chosen (X-05), so no storage dependency is added before then. At most **20 MB** per
  file; only **JPEG, PNG, WebP and PDF**, recognised from the leading bytes (the name and the client's Content-Type
  are ignored).
- **Confirmed with the user:** a file has exactly one owner (patient, consultation or client); access follows the
  owner (client files `clients.read` / `clients.manage`, so reception can attach consent forms; patient and
  consultation files `clinical.read` / `clinical.write`); no hard delete, a mistaken upload is withdrawn with a reason
  (row and stored content kept); files may be attached to in-progress and completed consultations but not discarded
  ones; clinical notes are append-only patient notes with an optional consultation link, written by veterinarians and
  nurses (`clinical.write`); both appear in the patient history (`ClinicalNoteAdded`, `AttachmentAdded`) for
  `clinical.read` holders, client files excluded.
- **One route per owner for uploads and lists**, so the permission is checked at the door before the body is read;
  `/api/attachments/{id}` (metadata, content, withdraw) takes either permission at the door and the service checks
  the one the file's owner needs (`403`). Other clinics' files and owners are `404`, as everywhere.
- **Upload handling:** the controller reads the multipart form itself (MVC's form value providers are disabled on the
  upload actions so an oversized form is a `413`, not a generic `400`). Order of checks: permission (`403`); owner (`404` / `409`, checked before the body is read); the request itself (`415` if it is not `multipart/form-data`, `413` if the body exceeds 20 MB plus framing, `400` if the multipart body is malformed); form fields (`422`); file type (`415`); file size (`413`). The
  owner is resolved before the body is read (found in the B-15 review), so an unknown or discarded owner never makes the
  server buffer a large body; a consultation owner is checked again under its lock at insert. The content's SHA-256 is
  recorded. Content is written to the store before the row; if the row cannot be saved the content is removed again,
  except when the commit itself failed with an unknown outcome: then the content is kept, because an orphan file is
  harmless and a row without content is not (best effort; a crash in between leaves an orphan, swept per RUNBOOK 4.7).
- **Storage keys** are 32 random lowercase hex characters (`RandomNumberGenerator`), validated before any path is
  built, and laid out as `root/xx/key`; the uploader's name never touches the file system. Writes go to a temporary
  name and are moved into place without overwriting.
- **Download names** (found in the B-15 review): the signature check reads only the first bytes, so a polyglot
  (a valid JPEG header followed by HTA or batch lines) could otherwise be saved by a colleague's browser under an
  executable name such as `xray.hta`, or one disguised with a right-to-left override. The stored name therefore keeps
  only the uploader's base name, drops control, format (bidirectional) and separator characters and quotes, and takes
  the extension of the detected type.
- **Downloads** only through the API: `Content-Disposition: attachment` with that name, `X-Content-Type-Options: nosniff`,
  `Content-Security-Policy: default-src 'none'; sandbox`, `Cache-Control: private, no-store`. Every download is
  audited. A row whose content is missing is a logged `500` (an operational fault, see RUNBOOK backups).
- **Immutable in the database:** `attachments_guard` allows INSERT of `Active` rows only, the one-way UPDATE `Active` →
  `Withdrawn` (whole-row comparison, so moving a file to another owner is refused too), and no DELETE or TRUNCATE;
  `clinical_notes_guard` allows INSERT only. Both refuse a link to a discarded consultation through a shared function
  that reads the consultation's status `FOR SHARE` before testing it (the B-14 lesson). CHECKs pin the owner rule,
  the content types, the size range, and the key and hash formats.
- **Notes vs other text:** SOAP notes belong to one consultation and freeze with it; addenda correct a completed
  consultation; clinical notes are the patient's running log between and beyond consultations.
- **New error code** `PAYLOAD_TOO_LARGE` (413), also used for any framework 413.
- **Accepted for now:** no malware scanning (files are never opened server-side beyond the first 12 bytes and are
  only offered as downloads; scanning belongs with object storage in X-05); content is not checked beyond its
  signature (a PDF with a valid header but broken body is stored as given); withdrawn content is kept until a
  retention policy exists (X-05, Q-8); no thumbnails or previews (frontend topic); the local provider assumes a single
  API instance or a shared volume; orphan files from crashes are swept by hand (RUNBOOK 4.7) until X-05 automates it;
  the request-size limit is enforced by Kestrel in production (the in-memory test server does not, so tests rely on
  the form limit; check it on the real host, and IIS `maxAllowedContentLength` if IIS is used); there is no per-user
  upload rate limit or per-clinic storage quota yet, so a compromised staff account could fill the volume (X-05: rate
  limiting on the upload routes, a quota, volume alerts); `Include Error Detail` must stay off in production
  connection strings so constraint errors never log row values (file names, descriptions); a frontend that shows
  content through blob URLs loses the API's CSP and must not render PDFs inline in its own origin.

### ADR-016: Invoices, tax rates and money (decided in B-16)
- **SRS Q-4 answered (confirmed with the user):** prices **exclude tax** and each line has its own rate; rates come from
  a clinic **tax-rate table** managed by administrators (`settings.manage`), and a line copies the rate's name and
  percentage, so later changes never alter an invoice; the clinic gets an **ISO 4217 currency** now (set with
  `PATCH /api/clinic`, fixed once an invoice exists); each line's net, discount and tax are rounded **half away from
  zero to the currency's minor units**, and the invoice totals are the sums of the lines.
- **Confirmed with the user:** lifecycle Draft → Issued → Void (drafts editable, deletable and unnumbered; issuing
  assigns a gapless number and freezes; an issued invoice is only voided, with a reason); permissions as proposed in the
  SDD (`billing.read` Admin, Veterinarian, Receptionist; `billing.manage` Admin, Receptionist); per-line discounts as a
  percentage or an amount (no invoice-level discount); free-text lines with an optional link to a consultation of the
  invoice's patient until the product catalogue (B-19); an `InvoiceIssued` patient-history entry for `billing.read`
  holders.
- **Arithmetic** lives in one pure class, `InvoiceCalculator`, unit-tested apart from the database. An amount discount
  above the line's net is refused (`422`), never clipped. `decimal` throughout; JSON amounts are decimals in their
  shortest form. Minor units come from a built-in ISO 4217 table rather than the operating system's culture data,
  which differs between platforms and is absent in invariant-globalization containers.
- **Numbers:** a per-clinic `invoice_number_sequences` row is created on first issue and incremented with
  `UPDATE ... RETURNING` inside the issuing transaction (lock order: invoice, then sequence), so numbers are unique and
  gapless even under concurrent issues (tested), and deleted drafts never consume one. Numbers are plain integers; a
  display prefix can be a B-26 setting.
- **Currency lock:** changing the clinic currency locks the clinic row (`FOR NO KEY UPDATE`) and refuses when any
  invoice exists; creating an invoice reads the clinic row `FOR SHARE`, so the two cannot interleave.
- **Freeze in the database:** `invoices_guard` and `invoice_items_follow_invoice` (see DATABASE.md) mirror the service
  rules; issuing is refused unless the stored totals equal the sums of the lines, so no code path can issue an
  invoice whose total disagrees with its lines. CHECKs keep each line and each invoice arithmetically consistent and
  the lifecycle fields tied to the status.
- **Parties:** the client must be active when the invoice is created or changed; the patient (optional) must currently
  belong to the client, in any status (final bills for deceased animals are normal), and still does when the invoice
  is issued (a transfer in between is a `409`). Changing the patient while lines bill its consultations is refused.
- **Found in the B-16 review:** amounts are capped at 1,000,000,000,000 per line total and per invoice total (`422`
  in the service, CHECKs in the database), because the per-field limits multiplied by 200 lines and a 100% rate
  could overflow `numeric(19,4)` into a `500`; a billed consultation is checked with the shared `ConsultationLinks`
  rule (locked `FOR SHARE`) and by the line trigger (non-discarded, same patient); issuing compares the stored amounts
  with a recomputation and refuses (`409`) instead of rewriting them inside the issue; `tax_rates_guard` makes a
  rate's percentage immutable and rates undeletable in the database too.
- **History:** `InvoiceIssued` is a separate permission-gated source (`billing.read`), like clinical sources
  (ADR-007); the history service takes a billing flag next to the clinical one.
- **Audit:** every change, with ids, field names, numbers, totals and currency; never descriptions, notes or reasons.
  Invoice reads are not audited (billing is not a clinical record; SECURITY.md asks for audited changes).
- **Accepted for now:** `balance_due` and payments wait for B-17 (an issued invoice shows its total only); no credit
  notes (a void plus a new invoice corrects an error); due dates are entered, not computed from payment terms; the
  clinic currency cannot be corrected once invoices exist (RUNBOOK 4.1 asks to confirm it); tax-rate names are unique
  only among active rates; the client and patient names shown are current, not historical (a printed invoice for
  legal use may need a snapshot of the billing address, a B-18 receipts topic); the database checks that each line
  and invoice add up and that issuing matches the lines, but not that a line's net equals its quantity × unit price
  (the rounding scale depends on the currency), so only the service guarantees how a line was priced.

### ADR-017: Payments, refunds and balances (decided in B-17)
- **Confirmed with the user:** overpayment is refused (a payment is at most the balance due; cash change is the
  frontend's arithmetic); payments are never edited, voided or deleted, and every correction is a **refund** against a
  payment (money returned or a mistaken entry), one mechanism and a clean ledger; methods are a fixed list (Cash, Card,
  BankTransfer, Cheque, Other) with an optional reference that may never hold a card number; issued invoices gain a
  derived payment state (`amountPaid`, `balanceDue`, `paymentStatus`) instead of more lifecycle statuses, and an
  invoice holding money cannot be voided until refunded; `payments.record` for Admin and Receptionist and
  `payments.refund` for Admin only; payments may be backdated (not before the issue, never in the future) while refunds
  are dated now; outstanding balances are found with `paymentStatus` and `overdue` filters on the invoice list (a client
  statement can come with reports); the patient-history invoice entry shows the current payment state instead of
  separate payment entries.
- **Balance kept, not summed on read:** `invoices.amount_paid` is updated in the same transaction as each payment or
  refund, under the invoice's row lock (after the clinic-filtered lookup), so concurrent payments are serialised and
  the balance can never go negative (tested with six simultaneous payments). The row is saved first, then the invoice,
  and `invoices_guard` refuses any amount_paid that differs from the payments minus refunds. Found in the B-17
  review: that check alone ran only when the invoice row changed, so deferred constraint triggers on payments and
  refunds now check the same equality at commit; together they keep the ledger and amount_paid in step for every
  committed transaction, including raw SQL. Issuing requires amount_paid 0 and voiding requires the rows (not only
  amount_paid) to net to 0. CHECKs keep amount_paid between 0 and the total, and 0 unless issued.
- **Immutable ledger in the database:** `payments_guard` (INSERT only against an issued invoice) and `refunds_guard`
  (INSERT only within the payment's amount, locking the payment `FOR NO KEY UPDATE` so concurrent refunds of one
  payment queue instead of each missing the other), no UPDATE, DELETE or TRUNCATE. A refund's composite key
  (clinic_id, invoice_id, payment_id) guarantees it names its payment's invoice.
- **Card data:** a payment reference or refund reason containing 13 to 19 digits, in one group or consecutive groups
  joined by single spaces, dashes, dots or slashes, that pass the Luhn check is refused, so a pasted card number is
  never stored (SECURITY.md section 7; widened in the B-17 review from a whole-field check, which "Visa 4111..." or
  dotted numbers slipped past). References and refund reasons never reach the audit log, and production connection
  strings keep Npgsql's `Include Error Detail` off so a failing row is never logged with its values (ADR-015).
- **Responses:** recording and refunding return the payment with its refunds and the invoice's new payment state, so
  the screen updates without a reload; no single-payment `GET` exists (the list is short and part of the invoice).
- **Accepted for now:** no client credit or deposits (overpayment refused); no payment allocation across invoices (a
  payment belongs to one invoice); refund methods are free to differ from the payment's (e.g. a card payment refunded
  in cash is recorded as such); no gateway integration (payments are recorded, not processed); other free-text fields
  (invoice notes, line descriptions) are not scanned for card numbers; receipts and a printable statement are B-18.

### ADR-018: Receipts, frozen bill-to and invoice activity (decided in B-18)
- **Confirmed with the user:** receipts are structured JSON documents the frontend renders and prints (no server PDF,
  so no PDF dependency or licence); every payment and every refund gets its own receipt with a gapless per-clinic number
  and everything printed on it frozen at that moment, so a reprint is identical; issuing an invoice freezes who it is
  addressed to (`billTo`: the client's name and address and the patient's name), closing the gap ADR-016 left; a
  per-invoice activity endpoint lists the audit entries of the invoice, its payments and receipts, and opening a receipt
  is audited. The clinic-wide audit search stays in B-27.
- **Made here (recorded for review):** receipts are read with `billing.read`, like invoices; there is no separate
  receipt permission. Payment and refund receipts share one number sequence (`receipt_number_sequences`, updated with
  `UPDATE ... RETURNING` in the payment or refund transaction; lock order invoice, then receipt sequence), so receipt
  numbers never collide with or depend on invoice numbers. A receipt does not copy the address: it shows the invoice's
  own frozen `billTo`, which cannot change after issue (the invoice guard compares whole rows). It does copy what can
  change: the clinic name, the cashier's name, the invoice number and total, and the amount paid and balance after
  the change. Refund receipts do not print the refund reason (free text, possibly sensitive) or a reference. No
  receipts were backfilled for payments recorded before B-18 (none exist in any deployment; `receipt` is `null` for
  them); invoices issued before B-18 were backfilled with the clients' then-current details, the best available.
- **Database:** `receipts_guard` refuses UPDATE, DELETE and TRUNCATE, and (widened in the B-18 review) a receipt whose
  amount, method, reference or time differs from its payment or refund, whose invoice number, currency or total
  differs from its invoice, or whose number was not handed out by the clinic's sequence; unique indexes allow one receipt per payment and one per refund; CHECKs keep the frozen
  amounts consistent (balance after = total − paid after). The receipt references its payment through
  (clinic_id, invoice_id, payment_id) and its refund through (clinic_id, payment_id, refund_id), so it can never name
  another invoice's money.
- **Activity:** `audit_logs` is read only through `AuditLogQueries.ForCurrentClinic` (ADR-007), selecting entries whose
  entity is the invoice, one of its payments (refunds are recorded on their payment) or one of its receipts; actors are
  resolved through the clinic-filtered users. Reading the activity is not itself audited (it shows audit data and is
  billing, not clinical); opening a receipt is (`receipt.viewed`), and so is each receipt list call
  (`receipt.listed`, added in the B-18 review because a list returns the same details as opening each receipt). The
  activity therefore shows receipt views next to changes.
- **Review fixes (B-18):** the lifecycle CHECK requires all eight bill-to columns to be empty on drafts and the patient
  name to be present exactly when the invoice has a patient; the receipt list sorts after its join; the refunds key got
  its plural name. The backfilled bill-to of invoices issued before B-18 uses the details at migration time, not at
  issue (documented in DATABASE.md).
- **Accepted for now:** no clinic address, tax number or logo on receipts until B-26 adds those settings (the
  frontend may print what it has); no credit notes; receipts are not emailed until B-25; `TRUNCATE payments` now fails
  on the receipts foreign key before its own guard (either way it is refused).

### ADR-019: Products, stock lots and stock receipts (decided in B-19)
- **Confirmed with the user:** B-19 delivers the product catalogue, stock lots and **one** movement type, `Receive`;
  outgoing movements, the negative-stock rule and controlled drugs stay in B-20 with SRS Q-5 (receiving can never make
  stock negative, so Q-5 is not needed yet). Permissions: `inventory.read` for Admin, Veterinarian, Nurse, Receptionist
  and Inventory (vets and nurses check stock, reception bills products); `inventory.manage` (products and receiving)
  for Admin and Inventory. The catalogue includes a non-stocked `Service` type so invoice lines can link to a price
  list; products carry an optional default price and tax rate. Invoice lines get an optional `productId`; the
  description and price are still sent by the client (the frontend prefills them) and billing does not touch stock.
  Vaccination and prescription product links come with dispensing in B-20. SKUs are required, unique per clinic
  ignoring case (inactive products included) and editable. Units are a fixed list; quantities have up to 3 decimals.
  Categories are free text with an exact, case-insensitive filter and a list of the categories in use. A lot is
  identified by (lot number, expiry date, location), each optional; receiving a match tops it up. Medications and
  vaccines need an expiry date. Unit cost is recorded on each movement, not on the lot.
- **Made here (recorded for review):** the lot keeps a stored `quantity` (fast stock reads) that the database checks
  **at commit** equals the sum of its movements (deferred constraint triggers on both tables, the B-17 pattern), so stock
  is derived from movements and cannot drift. Movements store a signed quantity (B-20 adds negative ones) and are
  append-only. The type and unit of a product are fixed once it has a lot (the service answers `409`, a trigger
  refuses it too); before that they can be corrected. Every receipt locks the product row `FOR NO KEY UPDATE` after the
  clinic-filtered lookup, so concurrent receipts of one product queue and a new lot is never created twice; product
  edits take the same lock (lock order: product, then lot). Expired stock (expiry before the clinic-local today) is not
  received; `occurredAt` may be backdated but not in the future. A unit cost needs the clinic currency, which the
  movement copies. `GET /api/inventory/movements` (read) was added beside the documented `POST` so the stock history is
  visible from the start. Product, stock and movement reads are not audited (not clinical or financial records);
  changes are, without names, SKUs, lot numbers, locations, references or notes.
- **Database:** `products` (SKU, category, lot number and location use the `case_insensitive` collation; the SKU CHECK
  compares under `COLLATE "C"` because PostgreSQL has no regular expressions on nondeterministic collations, and the
  name search uses ILIKE while the SKU search uses LIKE for the same reason); `stock_lots` with a unique identity
  index `NULLS NOT DISTINCT` on (clinic, product, lot number, expiry, location); `stock_movements` referencing its lot
  through (clinic_id, product_id, stock_lot_id), so a movement can never name another product's lot.
  `products_guard` refuses DELETE, moving a product and changing the type or unit while lots exist (it locks the row
  before looking, so a lot being inserted is waited for); `stock_lots_guard` locks the product `FOR SHARE` and refuses
  lots of services and medication or vaccine lots without expiry, and allows an UPDATE of nothing but the quantity;
  `stock_movements_guard` refuses UPDATE and DELETE; TRUNCATE is refused on all three. Rolling back refuses while
  stock movements exist or issued invoice lines link products.
- **Review fixes (B-19):** `stock_movements_guard` also refuses, on INSERT, a `Receive` into a product that is not
  active (it locks the product `FOR SHARE` first), so the database and the service agree; the clinic currency is now
  also fixed once a costed stock receipt or a product default price exists (those amounts are in it); the raw product
  row locks also filter by clinic; a lot-identity collision maps to `409` instead of `500`; the rollback locks its
  tables before checking for data; an index (clinic_id, occurred_at, id) serves the unfiltered movement list.
- **Accepted for now:** no supplier on a receipt until B-21 (purchase orders); no low-stock or expiry queries until
  B-22 (the reorder level is stored now); the database does not check that a linked invoice product is active (the
  service does, without a lock, so a concurrent deactivation may let one link through; deactivating a product later
  leaves its links); likewise a product's default tax rate may be archived while it is being set (cosmetic: the
  response shows the rate's status); receipts read the clinic currency before the transaction, so a concurrent
  currency change can only make a movement keep the previous code, which it stores explicitly; stock values (cost × quantity) are reports (B-24);
  vaccine names and medication names stay free text until B-20 links them.
