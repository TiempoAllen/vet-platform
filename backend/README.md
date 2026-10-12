# Backend

ASP.NET Core (.NET 10, controllers) modular monolith. Contract and rules live in `../docs/`
(`API.md`, `ARCHITECTURE.md`, `DATABASE.md`, `SECURITY.md`).

## Prerequisites

- .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). `global.json` pins the SDK to 10.0.x
  (`rollForward: latestFeature`), so builds fail fast on a missing or older SDK instead of silently using another one.
- PostgreSQL for running the API and the database tests: Docker (recommended), or any PostgreSQL 16+ server.
- `dotnet tool restore` once, to install the pinned `dotnet-ef` tool from `dotnet-tools.json`.
- `dotnet dev-certs https --trust` once: the API only serves HTTPS, because session and CSRF cookies are Secure-only
  (antiforgery refuses plain-HTTP requests with a 500).

## Layout

```
backend/
  global.json                   SDK pin
  dotnet-tools.json             pinned dotnet-ef
  compose.yaml                  local PostgreSQL (development only)
  VetPlatform.slnx
  src/VetPlatform.Api/          the web API
    Common/Errors/              error envelope, ApiException, global exception handler
    Common/Http/                request-id and request-logging middleware
    Common/Persistence/         AppDbContext, Entity base, timestamps, database health check, Migrations/
    Common/Security/            CSRF (antiforgery) protection
    Features/Identity/          Clinic, User, UserSession entities
    Features/Auth/              login/logout/me/csrf/password endpoints, session validation, sign-in rules
    Features/Users/             user management endpoints, create-admin command
    Common/Authorization/       roles, permissions, [RequirePermission]
    Common/Tenancy/             current clinic, clinic write guard (query filters live in AppDbContext)
    Common/Auditing/            audit log entity and AuditTrail
    Program.cs                  composition root and middleware order
  tests/VetPlatform.Api.Tests/  xunit integration tests (in-memory host)
```

Further feature code (clients, patients, ...) goes under `src/VetPlatform.Api/Features/<Name>/` in later
milestones, following the layering in `docs/ARCHITECTURE.md`.

## Commands

Run from `backend/`.

```bash
dotnet build VetPlatform.slnx
dotnet test  VetPlatform.slnx                                # all tests; database tests need PostgreSQL (below)
dotnet test  VetPlatform.slnx --filter "Category!=Database"  # no PostgreSQL needed
dotnet run --project src/VetPlatform.Api                     # https://localhost:7096, Development
curl https://localhost:7096/api/health                       # {"status":"Healthy"} once the database is reachable
```

## Database (PostgreSQL + EF Core)

Decisions and schema conventions: `docs/ARCHITECTURE.md` ADR-002.

First-time local setup:

```bash
echo POSTGRES_PASSWORD=<choose-a-local-password> > .env     # git-ignored
docker compose up -d
dotnet user-secrets --project src/VetPlatform.Api set ConnectionStrings:Default   "Host=localhost;Port=5432;Database=vetplatform;Username=vetplatform;Password=<same-password>"
dotnet tool restore
dotnet ef database update --project src/VetPlatform.Api
```

Changing the schema:

```bash
dotnet ef migrations add <Name> --project src/VetPlatform.Api --output-dir Common/Persistence/Migrations
dotnet ef migrations script --idempotent --project src/VetPlatform.Api   # review the SQL before committing
```

- The app never migrates on startup. Other environments apply migrations as a deployment step, e.g. with an
  idempotent script or `dotnet ef migrations bundle`.
- `PersistenceConfigurationTests` fails if the model changed without a migration.
- Database tests (`[Trait("Category", "Database")]`) start a disposable PostgreSQL container through Testcontainers.
  Without Docker, set `VETPLATFORM_TEST_POSTGRES` to a connection string for a server where the user may create
  databases; each test class gets its own database, dropped afterwards.

## Configuration

Precedence (later wins): `appsettings.json` < `appsettings.{Environment}.json` < environment variables <
command-line. Environments: `Development`, `Testing` (automated tests), `Staging`, `Production`.

- Set values with environment variables using `__` for nesting, e.g. `Logging__LogLevel__Default=Warning`.
- Secrets (connection strings, keys) come from environment variables or a secret store. Never commit them.
  The API refuses to start without `ConnectionStrings:Default` (env var `ConnectionStrings__Default`).
  Local-only overrides may go in `appsettings.Development.local.json` style files, which are git-ignored.
- Logging: structured JSON on the console in every environment except Development, which uses readable text.

## Authentication (B-03)

Cookie sessions validated against the database on every request; contract in `docs/API.md` section 3, design in
`docs/ARCHITECTURE.md` ADR-003. Settings (`Auth` section, all optional): `IdleTimeout`, `AbsoluteLifetime`,
`MaxFailedLogins`, `LockoutDuration`, `LoginAttemptsPerMinute`.

- Controllers require a signed-in user by default. Mark anonymous endpoints `[AllowAnonymous]` deliberately and
  document them in `docs/API.md`.
- Endpoints that change state must be controller actions: CSRF protection is an MVC filter and does not cover
  minimal APIs (`MapPost` etc.). `CsrfAndAccessTests` fails if one appears.
- Unsafe requests need the CSRF header. In tests use `factory.CreateClientWithCsrfAsync()` and, after login or logout,
  `client.UseCsrfTokenFrom(response)`; `DatabaseApiFactory.CreateUserAsync` seeds a signed-in-able user.
- Read the time from the injected `TimeProvider`, never `DateTime.UtcNow`; tests advance `factory.Clock`.

## Users, permissions and tenancy (B-04)

Create a clinic and its first administrator (prompts for the password without echoing it; or pipe it on stdin):

```bash
dotnet run --project src/VetPlatform.Api -- create-admin --clinic "Happy Paws" --name "Jo Owner" --email jo@happypaws.example
```

Then sign in as that administrator and create the other staff through `POST /api/users`.

Rules for new code (ADR-004):
- Protect every endpoint with `[RequirePermission(Permissions.X)]` unless any signed-in user may call it. Add new
  permissions to `Common/Authorization/Permissions.cs` and grant them there.
- Clinic-owned entities implement `IClinicOwned`; they are then filtered to the current clinic and write-guarded
  automatically. Never pass a clinic id from the request; it comes from the session.
- `IgnoreQueryFilters()` only for genuinely cross-clinic code, with a comment saying why.
- Record an audit entry (`AuditTrail.Record`) for security-relevant and business changes, before `SaveChanges`.
- Tests: `factory.SignedInAsync(Role.Admin)` gives a signed-in client in a new clinic; `CreateColleagueAsync` adds
  users to the same clinic. Direct database setup in tests has no current clinic, so use `IgnoreQueryFilters()`.

## Conventions established in B-01

- Every failure returns the `docs/API.md` error envelope. Throw `ApiException` for expected, client-safe
  errors; anything else becomes a generic 500 and is logged server-side only.
- Every request has a correlation id (`X-Request-Id` header, `requestId` in error bodies, `CorrelationId`
  log scope).
- Request logs contain method, path, status and duration only. Never log query strings, headers or bodies.
