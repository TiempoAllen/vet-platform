# Runbook

Veterinary Management Platform. Version 1.0, 2026-10-13, written at the B-04 merge.

Operating procedures for the API and its database. Commands assume `backend/` as the working directory unless
stated. Hosting is not chosen yet (X-05): sections marked **[X-05]** describe the required procedure and must be
completed with concrete commands when the platform is selected.

## 1. Service overview

| Item | Value |
|---|---|
| Component | `VetPlatform.Api` (ASP.NET Core, .NET 10), one stateless process |
| Database | PostgreSQL 16+ (18 in development), database `vetplatform` |
| Health | `GET /api/health` → `200 {"status":"Healthy"}` or `503 {"status":"Unhealthy"}` (database check, 5 s timeout) |
| Logs | JSON lines on stdout; every line in a request carries `CorrelationId` = `X-Request-Id` |
| Dev URL | `https://localhost:7096` (HTTPS only) |
| Admin CLI | `dotnet run --project src/VetPlatform.Api -- create-admin ...` (or `VetPlatform.Api create-admin ...` from a published build) |
| Design | `SDD.md`; decisions `ARCHITECTURE.md` ADR-001..004 |

## 2. Configuration reference

| Key (env var form) | Required | Default | Notes |
|---|---|---|---|
| `ConnectionStrings__Default` | Yes | — | Startup fails without it. Secret: env var or secret store only. |
| `ASPNETCORE_ENVIRONMENT` | Yes in prod | `Production` | `Development`, `Testing`, `Staging`, `Production`. HSTS is on outside Development. |
| `Auth__IdleTimeout` | No | `02:00:00` | Session idle timeout. |
| `Auth__AbsoluteLifetime` | No | `12:00:00` | Session hard limit. |
| `Auth__MaxFailedLogins` | No | `5` | Wrong passwords before lockout. |
| `Auth__LockoutDuration` | No | `00:15:00` | Lockout length. |
| `Auth__LoginAttemptsPerMinute` | No | `10` | Per client address (IPv6 /64). |
| `Logging__LogLevel__Default` | No | `Information` | Raise to `Debug` only temporarily. |
| `FileStorage__LocalRoot` | Yes | — (Development: `App_Data/files`) | Directory for uploaded files (B-15); absolute, or relative to the content root. Must be private (never served by a web server), writable by the API, and backed up with the database. Startup fails without it. |
| `FileStorage__Provider` | No | `Local` | The only provider until X-05 adds object storage. |

Precedence: `appsettings.json` < `appsettings.{Environment}.json` < environment variables < command line. Invalid
`Auth` values and a missing `FileStorage__LocalRoot` stop the app at startup.

## 3. Local environment setup
Prerequisites: .NET 10 SDK, Docker Desktop (needs WSL 2 on Windows), Git.

```bash
dotnet dev-certs https --trust
echo POSTGRES_PASSWORD=<choose-a-local-password> > .env
docker compose up -d
dotnet user-secrets --project src/VetPlatform.Api set ConnectionStrings:Default "Host=localhost;Port=5432;Database=vetplatform;Username=vetplatform;Password=<same-password>"
dotnet tool restore
dotnet ef database update --project src/VetPlatform.Api
dotnet run --project src/VetPlatform.Api
```

Check: `curl https://localhost:7096/api/health` returns `{"status":"Healthy"}`.

## 4. Routine procedures

### 4.1 Provision a new clinic
1. Confirm the clinic name, administrator name and email with the requester (out of band).
2. Run, and type the password when prompted (it is not echoed; never pass it as an argument):

   ```bash
   dotnet run --project src/VetPlatform.Api -- create-admin --clinic "Happy Paws" --name "Jo Owner" --email jo@happypaws.example --timezone Europe/London
   ```

   In a non-interactive shell, pipe the password from a secret store on stdin. `--timezone` takes an IANA id
   (default `UTC`); the administrator can change it later with `PATCH /api/clinic`. Windows zone ids are refused.
3. Expected output: `Created clinic <id> and administrator <id>.` Exit codes: `0` ok, `1` email already exists,
   `2` invalid arguments or password (12–1024 characters, not the email).
4. Send the administrator the password through a separate channel and ask them to change it at first sign-in
   (`POST /api/auth/password`; the UI will offer this from F-03).
5. Before the clinic invoices, the administrator sets the currency (`PATCH /api/clinic` with an ISO 4217 code; it is
   fixed once the first invoice exists) and creates the tax rates (`POST /api/tax-rates`, e.g. a standard and a zero
   rate). Confirm the currency with the clinic: a wrong one can only be fixed by hand in the database before any
   invoice exists.

### 4.2 Apply database migrations (deployment step)

From B-09 the migrations create the `btree_gist` extension (a trusted extension on PostgreSQL 13+, so the database
owner can create it without superuser). If the migration role cannot, have a superuser run
`CREATE EXTENSION IF NOT EXISTS btree_gist;` in the database first.

The app never migrates on startup.

```bash
dotnet ef migrations list --project src/VetPlatform.Api                  # what is applied / pending
dotnet ef migrations script --idempotent --project src/VetPlatform.Api -o migrate.sql
```

1. Review `migrate.sql`: no unintended `DROP`, no destructive change to clinical, financial or audit tables.
2. Take a backup (section 5.1) or confirm a recent one.
3. Apply `migrate.sql` as the schema-owner role (`psql -v ON_ERROR_STOP=1 -f migrate.sql`), or use a migration
   bundle (`dotnet ef migrations bundle`).
4. Deploy the matching application version, then check health (section 4.4).

Order rule: migrations must be backward compatible with the running version (add before use, remove after the code
stops using it), so deploy = migrate, then roll out code.

### 4.3 Deploy a new version **[X-05]**
1. Confirm the release passed the exit criteria in `TEST_PLAN.md` section 8.
2. Build: `dotnet publish src/VetPlatform.Api -c Release -o out` (or the container image build).
3. Apply migrations (4.2).
4. Roll out the new version; instances must share the persisted Data Protection key ring, otherwise every user is
   signed out and in-flight CSRF tokens fail.
5. Smoke test: health `200`; sign in with the monitoring account; `GET /api/auth/me` `200`.
6. Watch error rate and `5xx` logs for 15 minutes.

### 4.4 Health and smoke checks

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://<host>/api/health
```

`200` healthy; `503` means the database is unreachable from the API (section 6.1).

### 4.5 Rotate the database password
1. Create the new password in PostgreSQL (`ALTER ROLE ... PASSWORD ...`) keeping the old one valid if using two
   roles, or schedule a short window.
2. Update `ConnectionStrings__Default` in the secret store.
3. Restart the API; check health.

### 4.6 Rotate Data Protection keys **[X-05]**
Keys encrypt session and antiforgery cookies. Rotation via the key ring keeps old keys for decryption, so users stay
signed in. Revoking keys (after a suspected leak) signs everyone out: announce it to clinics first.

### 4.7 File store housekeeping
Files are written to a temporary name and then moved into place, and the row is saved after the file (ADR-015). A
crash in between can leave:
- `*.tmp` files older than a day under `FileStorage__LocalRoot`: delete them;
- files whose name (the storage key) has no row: compare the store with
  `SELECT storage_key FROM attachments` and move unmatched files older than a day to a quarantine folder (never delete
  in the same step; a backup restore may still need them).

Watch the volume's free space; there is no upload quota yet (X-05). Keep `Include Error Detail` off in production
connection strings.

## 5. Backup and restore **[X-05]**
Targets until X-05 sets final values: RPO ≤ 24 h, RTO ≤ 4 h (`SRS.md` NFR-REL-4).

### 5.1 Backup
- Managed PostgreSQL: enable automated daily backups and point-in-time recovery; retain at least 30 days.
- Self-hosted: nightly logical backup plus WAL archiving:

  ```bash
  pg_dump --format=custom --no-owner --file=vetplatform-$(date +%F).dump "$DATABASE_URL"
  ```

- Store backups encrypted, in a different account/region from the database. They contain personal, clinical and
  financial data.
- Back up the Data Protection key ring with the database (without it, a restored system signs everyone out, which
  is acceptable but must be expected).
- Back up the file store (`FileStorage__LocalRoot`) with the database, after it: rows reference files by key, so a
  file backup older than the database backup can miss recent uploads (their downloads then fail with 500 and are
  logged). Files are never deleted by the application (withdrawn files stay), so restoring a newer file backup is
  harmless.

### 5.2 Restore
1. Restore into a **new** database, never over the live one:

   ```bash
   createdb vetplatform_restore
   pg_restore --no-owner --dbname=vetplatform_restore vetplatform-YYYY-MM-DD.dump
   ```

2. Verify: `dotnet ef migrations list` against it shows no pending migrations; row counts of `clinics`, `users`,
   `audit_logs` are plausible; the audit triggers exist (`\dS audit_logs`).
3. Point the API at it (connection string), restart, run smoke checks.
4. Record the incident and data-loss window.

### 5.3 Restore drill
Quarterly on staging: restore the latest backup, time it against the RTO, run the smoke checks, record the result.

## 6. Incident procedures

Use the request id from the user's error report (`requestId` in the error body, `X-Request-Id` header) to find the
request in the logs: filter on `CorrelationId`.

### 6.1 Health returns 503 / users see 500 errors
1. Check database reachability from the API host (`pg_isready -h <host> -p 5432`).
2. Check the database: running, disk space, connection count (`select count(*) from pg_stat_activity;`).
3. Check the API logs for `Npgsql` exceptions (authentication failure → section 4.5; timeouts → database load).
4. If a deployment just happened, consider rollback (6.6).

### 6.2 All requests that change data fail with 500, reads work
Likely cause: antiforgery cannot set Secure cookies because the app believes the request is HTTP. Behind a proxy,
forwarded headers are not configured or the proxy does not send `X-Forwarded-Proto` **[X-05]**. Locally: the client
used `http://`; use `https://localhost:7096`.

### 6.3 Every user was signed out at once
Causes: Data Protection keys changed (new container without the persisted key ring), clinic suspended, or the
database was restored. Check the deployment history first. Users can simply sign in again.

### 6.4 A user cannot sign in
The API deliberately returns the same message for every cause. Check, in order:

```sql
select u.id, u.status, u.failed_login_count, u.locked_until, c.status as clinic_status
from users u join clinics c on c.id = u.clinic_id
where u.email = 'person@clinic.example';   -- case-insensitive column
```

- No row: wrong email.
- `status = 'Disabled'`: a clinic administrator must re-enable them (`PATCH /api/users/:id`).
- `locked_until` in the future: wait (15 min), or unlock if the clinic confirms the user's identity:

  ```sql
  update users set failed_login_count = 0, locked_until = null where email = 'person@clinic.example';
  ```

- `clinic_status = 'Suspended'`: see 6.5.
- None of these: the password is wrong. There is no password reset yet (`SRS.md` FR-AUTH-10). Interim answer: the
  user must remember it; escalate to engineering if this blocks a clinic.
- Many failures from one address: the rate limiter (`429`) may be blocking a shared office IP; it resets each minute.

Record every manual change in the incident log; direct SQL bypasses the application audit trail.

### 6.5 Suspend or reactivate a clinic
Used for non-payment, contract end or a compromised clinic. There is no API for this yet (B-26).

```sql
update clinics set status = 'Suspended' where id = '<clinic-id>';   -- or 'Active'
```

Effect: every session of that clinic ends on its next request; nobody can sign in. Data is kept. Reactivating does
not restore old sessions.

### 6.6 Roll back a release
1. Redeploy the previous application version.
2. Do not roll back migrations by default: migrations are backward compatible (4.2). Roll back a migration only if
   it is destructive to the old version, after a backup, with
   `dotnet ef database update <PreviousMigration> --project src/VetPlatform.Api`. Rolling back past
   `AddRolesAndAuditLog` is refused while `audit_logs` has rows, by design; so is rolling back past
   `AddProductsAndStock` (B-19) while stock movements exist or issued invoice lines link products.

### 6.7 Suspected account compromise
1. Ask the clinic administrator to disable the user (ends all sessions immediately), or operator SQL:

   ```sql
   update user_sessions set revoked_at = now()
   where user_id = '<user-id>' and revoked_at is null;
   ```

2. Review the user's activity:

   ```sql
   select occurred_at, action, entity_type, entity_id, metadata, request_id
   from audit_logs where actor_user_id = '<user-id>' or entity_id = '<user-id>'
   order by occurred_at desc limit 200;
   ```

3. Re-enable after the user sets a new password with the administrator present.

### 6.8 Suspected cross-clinic data exposure (S1)
1. Treat as a security incident: notify the owner immediately.
2. Preserve logs and the audit table; do not modify data.
3. Identify the request ids and endpoints involved; reproduce in a test against a copy, never in production.
4. Fix with a regression test in the tenant-isolation suite (`TEST_PLAN.md` section 5, item 4), release, and assess
   notification obligations under the applicable privacy law.

### 6.9 Brute-force or credential-stuffing attack
Signs: many `auth.login_failed` and `auth.account_locked` entries, many `429` responses.

```sql
select date_trunc('minute', occurred_at) as minute, action, count(*)
from audit_logs where action like 'auth.%' and occurred_at > now() - interval '1 hour'
group by 1, 2 order by 1 desc;
```

The per-address rate limit and per-account lockout are already active. If the attack is distributed, block at the
proxy/WAF **[X-05]** and inform the affected clinics that staff may be temporarily locked out (accepted risk,
ADR-003).

## 7. Monitoring and alerts **[X-05]**
Recommended alerts:

| Signal | Threshold |
|---|---|
| `/api/health` not `200` | 2 consecutive checks, 1 min apart |
| `5xx` responses | > 1 % of requests over 5 min |
| `auth.account_locked` | > 20 per hour platform-wide |
| `429` on login | sustained > 5 min |
| Database CPU / storage | > 80 % |
| Backup job | Any failure, or no successful backup in 26 h |

## 8. Data handling rules for operators
- Never copy production data to development or staging.
- Do not paste personal, clinical or financial data into tickets or chat; reference ids and request ids instead.
- Never update or delete `audit_logs`; the database refuses it anyway.
- Do not delete clinics, users, clients, patients, invoices or stock records; suspend or disable instead.

## 9. Escalation and ownership
| Area | Owner |
|---|---|
| Application and database | Repository owner (TiempoAllen) |
| Hosting, backups, DNS/TLS | To be assigned in X-05 |
| Clinic account questions | Clinic administrator first, then the operator |

Keep this runbook current: any change to configuration, CLI, deployment or incident handling updates it in the same
change.
