# Backend

ASP.NET Core (.NET 10, controllers) modular monolith. Contract and rules live in `../docs/`
(`API.md`, `ARCHITECTURE.md`, `DATABASE.md`, `SECURITY.md`).

## Prerequisites

- .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). `global.json` pins the SDK to 10.0.x
  (`rollForward: latestFeature`), so builds fail fast on a missing or older SDK instead of silently using another one.
- PostgreSQL for running the API and the database tests: Docker (recommended), or any PostgreSQL 16+ server.
- `dotnet tool restore` once, to install the pinned `dotnet-ef` tool from `dotnet-tools.json`.

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
    Common/Persistence/         AppDbContext, database health check, Migrations/
    Program.cs                  composition root and middleware order
  tests/VetPlatform.Api.Tests/  xunit integration tests (in-memory host)
```

Feature code (clients, patients, ...) will be added under `src/VetPlatform.Api/Features/<Name>/` in later
milestones, following the layering in `docs/ARCHITECTURE.md`.

## Commands

Run from `backend/`.

```bash
dotnet build VetPlatform.slnx
dotnet test  VetPlatform.slnx                                # all tests; database tests need PostgreSQL (below)
dotnet test  VetPlatform.slnx --filter "Category!=Database"  # no PostgreSQL needed
dotnet run --project src/VetPlatform.Api                     # http://localhost:5096, Development
curl http://localhost:5096/api/health                        # {"status":"Healthy"} once the database is reachable
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

## Conventions established in B-01

- Every failure returns the `docs/API.md` error envelope. Throw `ApiException` for expected, client-safe
  errors; anything else becomes a generic 500 and is logged server-side only.
- Every request has a correlation id (`X-Request-Id` header, `requestId` in error bodies, `CorrelationId`
  log scope).
- Request logs contain method, path, status and duration only. Never log query strings, headers or bodies.
