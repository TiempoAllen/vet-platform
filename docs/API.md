# API Contract

Machine-readable form of the implemented endpoints: `openapi.yaml`. Change both together; this document wins if
they disagree.

## 1. Conventions
Base path: `/api`

Use JSON for ordinary requests/responses.

Use ISO-8601 timestamps with timezone information. Instants sent to the API must carry an explicit offset (`Z` or
`+01:00`); a value without one is `400 BAD_REQUEST` in a body and `422` in a query string. Calendar dates are
`YYYY-MM-DD`.

Use consistent HTTP semantics:
- `200` successful read/update
- `201` successful creation
- `204` successful deletion where appropriate
- `400` malformed request
- `401` unauthenticated
- `403` authenticated but unauthorized
- `404` resource not found or intentionally hidden
- `409` conflict
- `422` validation failure where the framework supports it
- `429` rate limited
- `500` unexpected server error

## 2. Error format
Every error response uses this shape (implemented in B-01):

{
  "error": {
    "code": "VALIDATION_ERROR",
    "message": "Human-readable message",
    "details": [{ "field": "name", "message": "The Name field is required." }]
  },
  "requestId": "..."
}

`details` is always an array (empty when there is nothing field-specific). `field` is camelCase and optional.

Error codes (stable; clients may switch on them): `BAD_REQUEST`, `UNAUTHENTICATED`, `FORBIDDEN`,
`CSRF_TOKEN_INVALID`, `NOT_FOUND`, `METHOD_NOT_ALLOWED`, `CONFLICT`, `UNSUPPORTED_MEDIA_TYPE`, `VALIDATION_ERROR`,
`RATE_LIMITED`, `INTERNAL_ERROR`.

Status mapping for request-body problems: `422 VALIDATION_ERROR` when the body parses but field values are invalid;
`400 BAD_REQUEST` when the body is missing or not valid JSON (parser details are not echoed), or when any text
(body or query string) contains a NUL character, which cannot be stored.

`details` may not list every invalid field: checks that compare or parse values (email format, enum query
parameters, blank names in a PATCH) run only after the basic required/length checks pass. Show what is returned
and expect further errors after the user corrects it.

Request correlation: every response, success or error, carries an `X-Request-Id` header equal to the `requestId`
in error bodies. A client may supply its own `X-Request-Id` (max 64 characters of `A-Z a-z 0-9 - _ .`);
otherwise the server generates one. Unsafe values are replaced, not rejected.

Do not expose stack traces or sensitive internal details in production. Unexpected errors return a generic
`500 INTERNAL_ERROR`; details are logged server-side only.

## 3. Authentication
Cookie sessions (implemented in B-03; design in `ARCHITECTURE.md` ADR-003). There is no token refresh endpoint.

**Same-site only.** The API sets `SameSite=Strict`, `Secure` cookies and has no CORS. The frontend must be served from
the same site as the API (in development, proxy `/api` from the frontend dev server). Use HTTPS, except
`http://localhost`, which browsers treat as secure. Requests must include cookies (same-origin `fetch` does by default).

In development the API listens only on `https://localhost:7096` (run `dotnet dev-certs https --trust` once). The
dev-server proxy must forward `/api` to that HTTPS address; the browser-to-dev-server leg may stay on
`http://localhost`. The API cannot process CSRF-protected requests over plain HTTP (they fail with
`500 INTERNAL_ERROR`).

**CSRF.** Every unsafe request (`POST`, `PUT`, `PATCH`, `DELETE`), including login and logout, must send the header
`X-XSRF-TOKEN` with the value of the `XSRF-TOKEN` cookie. Otherwise: `403 CSRF_TOKEN_INVALID`.
- Get the cookie with `GET /api/auth/csrf` before the first unsafe request.
- The token is bound to the current sign-in session (or to "signed out"), so login and logout responses set a new
  `XSRF-TOKEN` cookie. Read the cookie just before each request; never cache the value.
- After any `401 UNAUTHENTICATED`, and before showing the login screen, call `GET /api/auth/csrf`: a token issued
  while signed in is not valid once signed out. Requests rejected for CSRF still count toward the login rate limit.
- On `403 CSRF_TOKEN_INVALID`, call `GET /api/auth/csrf` and retry the request once.

**Error precedence** when several apply: `429` → `401` (protected endpoints) → `403 FORBIDDEN` (missing permission)
→ `403 CSRF_TOKEN_INVALID` → `400`/`422` → the endpoint's own result (including `404` for other clinics' records).

**Authorization default.** Every endpoint requires a signed-in user and returns `401 UNAUTHENTICATED` otherwise, unless
documented as anonymous. Anonymous endpoints: `GET /api/auth/csrf`, `POST /api/auth/login`, `POST /api/auth/logout`,
`GET /api/health`.

`GET /api/auth/csrf` returns `204` and sets the `XSRF-TOKEN` cookie.

`POST /api/auth/login`, rate limited to 10 attempts per minute per client address (`429 RATE_LIMITED` with
`Retry-After` when exceeded):

    { "email": "jo@clinic.example", "password": "..." }

- `200` with the current-user body (below); sets the HttpOnly session cookie. Signing in again from the same browser
  replaces the previous session.
- `401 UNAUTHENTICATED`, message `Email or password is incorrect.`, for every failure: unknown email, wrong password,
  temporarily locked account (5 consecutive wrong passwords lock it for 15 minutes), disabled user or suspended clinic.
  The response never says which.
- `422 VALIDATION_ERROR` when `email` is missing, blank or not an email address (max 254), or `password` is missing
  or blank (max 1024). `400 BAD_REQUEST` when the body is missing or not valid JSON.
- Email matching ignores case and surrounding spaces.

`POST /api/auth/logout` returns `204`. Ends the session server-side and clears the cookie; succeeds even if not signed
in (a valid CSRF token is still required; on `403 CSRF_TOKEN_INVALID` refresh it and retry, as above).

`GET /api/auth/me` returns `200` or `401 UNAUTHENTICATED`:

    {
      "id": "uuid", "name": "Jo Vet", "email": "jo@clinic.example",
      "clinic": { "id": "uuid", "name": "Happy Paws", "timeZone": "Europe/London" },
      "roles": ["Admin", "Veterinarian"],
      "permissions": ["clients.manage", "clients.read", "patients.manage", "patients.read", "users.manage", "users.read"]
    }

`email` is returned normalized (trimmed, lowercase). `roles` and `permissions` reflect the current state on every call
(a role change applies to the user's next request). Use `permissions` to decide what to show; the API enforces them
regardless. A user may have no roles, in which case both arrays are empty.

`POST /api/auth/password` (signed in; rate limited like login):

    { "currentPassword": "...", "newPassword": "..." }

- `204`: password changed; every other session of this user is signed out, this one stays.
- `422 VALIDATION_ERROR` with field `currentPassword` (incorrect) or `newPassword` (fails the password policy:
  12 to 1024 characters, not the email address).
- Wrong current passwords count towards the same lockout as failed sign-ins. The attempt that triggers the lock
  returns `401 UNAUTHENTICATED` and signs out every session of the user, this one included.

**Session lifetime.** A session ends after 2 hours without requests, 12 hours after sign-in regardless of activity, on
logout, or as soon as the user is disabled or the clinic suspended. After that every protected endpoint returns
`401 UNAUTHENTICATED`; the frontend should send the user to the login screen.

## 4. Users and access
Implemented in B-04. All endpoints act on the caller's clinic only: users of other clinics are `404 NOT_FOUND`, never
`403`. Missing permission is `403 FORBIDDEN`. Design: `ARCHITECTURE.md` ADR-004.

**Roles** (fixed): `Admin`, `Veterinarian`, `Nurse`, `Receptionist`, `Inventory`. Users created or edited through
the API hold one or more; `GET /api/auth/me` still returns empty arrays for a user with none.
**Permissions** granted so far (later milestones add more; `GET /api/auth/me` lists the caller's):

| Permission | Admin | Veterinarian | Nurse | Receptionist | Inventory |
|---|:-:|:-:|:-:|:-:|:-:|
| `users.read`, `users.manage` | ✓ | – | – | – | – |
| `clients.read` | ✓ | ✓ | ✓ | ✓ | – |
| `clients.manage` | ✓ | – | – | ✓ | – |
| `patients.read`, `patients.manage` | ✓ | ✓ | ✓ | ✓ | – |
| `schedules.read` | ✓ | ✓ | ✓ | ✓ | – |
| `schedules.manage` | ✓ | – | – | – | – |
| `schedules.manage_own` | – | ✓ | – | – | – |
| `settings.manage` | ✓ | – | – | – | – |

**User object**

    {
      "id": "uuid", "name": "Sam Nurse", "email": "sam@clinic.example", "phone": "+1 555 0100" | null,
      "status": "Active" | "Disabled", "roles": ["Nurse"],
      "lastLoginAt": "2026-10-13T08:00:00+00:00" | null,
      "createdAt": "...", "updatedAt": "..."
    }

`GET /api/users` (`users.read`): paginated list (section 13).
- `q`: case-insensitive substring match on name or email (max 100 characters; `%` and `_` match literally).
- `status`: `Active` or `Disabled`. `role`: one of the roles.
- `sort`: `name` (default), `email`, `createdAt` or `lastLoginAt`; prefix `-` for descending. Users who never signed
  in sort last ascending and first descending. Ties are broken by id, so pages are stable.
- Invalid values (unknown sort, status or role, `page < 1`, `pageSize` outside 1 to 100) are `422 VALIDATION_ERROR`
  with the parameter as `field`.

`GET /api/users/:id` (`users.read`): `200` user object, or `404`.

`POST /api/users` (`users.manage`): creates a user in the caller's clinic.

    { "name": "Sam Nurse", "email": "sam@clinic.example", "phone": null, "password": "...", "roles": ["Nurse"] }

Limits: `name` required, max 200, trimmed; `email` required, a valid address, max 254, stored trimmed and lowercased;
`phone` optional, max 32, trimmed (blank becomes `null`); `password` 12 to 1024 characters, not the email; `roles`
at least one.

- `201` with the user object and a `Location` header.
- `422` when a field is missing or invalid (password policy as for `POST /api/auth/password`; at least one role).
- `400 BAD_REQUEST` when the body is not valid JSON or contains an unknown role or status name (enum values must parse
  to be accepted; this applies to every endpoint that takes roles or statuses).
- `409 CONFLICT` (detail field `email`) when the email is already used by any user on the platform (emails are unique
  across clinics, ADR-003).
- The administrator chooses the initial password and should ask the user to change it with `POST /api/auth/password`.

`PATCH /api/users/:id` (`users.manage`): partial update. Omitted or `null` fields are unchanged; `"phone": ""` clears
the phone.

    { "name": "...", "email": "...", "phone": "...", "status": "Active" | "Disabled" }

- `200` with the updated user object.
- `422 VALIDATION_ERROR` when `name` is blank or a field is invalid or too long (same limits as create).
  `404 NOT_FOUND` for an unknown user. `400 BAD_REQUEST` for an unknown `status`.
- `409 CONFLICT` when the email is taken (detail field `email`), when disabling your own account, or when disabling
  the clinic's last active administrator. The last two have no field detail; show `error.message`.
- Disabling a user ends all their sessions immediately and blocks sign-in until re-enabled; re-enabling does not
  restore old sessions.

`POST /api/users/:id/roles` (`users.manage`): replaces the user's roles with exactly this set (duplicates ignored).

    { "roles": ["Veterinarian", "Admin"] }

- `200` with the updated user object.
- `roles` must contain at least one role; an empty or missing array is `422 VALIDATION_ERROR` (field `roles`).
  Unknown user (or another clinic's) is `404 NOT_FOUND`.
- `409 CONFLICT` when it would remove `Admin` from the clinic's last active administrator (including yourself).
- Takes effect on the user's next request.

If the caller loses `users.manage` while a request is in flight (demoted or disabled by another administrator), the
request is refused with `403 FORBIDDEN`.

Every change made through these endpoints is written to the audit log.

### 4.1 Clinic
Implemented in B-08 (the other clinic settings follow in B-26).

`GET /api/clinic` (any signed-in user): `{ "id": "uuid", "name": "Happy Paws", "timeZone": "Europe/London" }`.

`PATCH /api/clinic` (`settings.manage`): `{ "timeZone": "Europe/London" }`.
- `timeZone` is an IANA id. Windows ids ("GMT Standard Time") and unknown ids are `422` (field `timeZone`).
- New clinics get the zone from `create-admin --timezone` (default `UTC`); clinics created before B-08 start as `UTC`.
- Weekly working hours are local wall-clock times, so changing the zone shifts every future working period with it.
- The change is audited (`clinic.updated`).

## 5. Clients
Implemented in B-05. Clients are the pet owners of the caller's clinic: other clinics' clients are `404 NOT_FOUND`,
never `403`. Missing permission is `403 FORBIDDEN`. Clients are never deleted (there is no `DELETE`; it is `405`):
deactivate them with `"status": "Inactive"`.

**Client object**

    {
      "id": "uuid", "firstName": "Jane", "lastName": "Doe",
      "email": "jane@example.com" | null, "phone": "+1 555 0100" | null,
      "addressLine1": "1 Main St" | null, "addressLine2": null, "city": "Springfield" | null,
      "region": "IL" | null, "postalCode": "62701" | null, "country": "US" | null,
      "preferredContactMethod": "Email" | "Phone" | "Sms" | null,
      "notes": "..." | null, "status": "Active" | "Inactive",
      "createdAt": "...", "updatedAt": "..."
    }

Every field is always present; absent values are `null`. `region` is a state, province or county. `country` is free
text. `preferredContactMethod: null` means no stated preference.

`GET /api/clients` (`clients.read`): paginated list (section 13). Includes inactive clients unless `status` is given.
- `q`: case-insensitive substring match on first name, last name, full name ("Jane Doe"), email or phone (max 100
  characters; `%` and `_` match literally). Phone matches the stored text, so "555 0100" does not match "5550100".
- `status`: `Active` or `Inactive`.
- `sort`: `name` (default: last name, then first name), `createdAt` or `updatedAt`; prefix one `-` for descending
  (`--name` is invalid). Ties are broken by id in the same direction, so pages are stable.
- Invalid values (unknown sort or status, `page < 1`, `pageSize` outside 1 to 100) are `422 VALIDATION_ERROR` with the
  parameter as `field`.

`GET /api/clients/:id` (`clients.read`): `200` client object, or `404`.

`POST /api/clients` (`clients.manage`): creates an `Active` client in the caller's clinic. Any `clinicId` in the body
is ignored.

    { "firstName": "Jane", "lastName": "Doe", "email": "jane@example.com", "phone": "+1 555 0100",
      "addressLine1": "1 Main St", "addressLine2": null, "city": "Springfield", "region": "IL",
      "postalCode": "62701", "country": "US", "preferredContactMethod": "Email", "notes": null }

Limits: `firstName` and `lastName` required, max 100 each; `email` optional, a valid address, max 254, stored
trimmed and lowercased; `phone` max 32; `addressLine1`, `addressLine2` max 200; `city`, `region`, `country` max 100;
`postalCode` max 20; `notes` max 4000. Limits apply to the value as sent, before trimming. All text is trimmed; an optional field that is omitted, `null` or blank is
stored as `null`. Emails are **not** unique: members of one household may share one. The server's email check is
deliberately loose (text, one `@`, text, e.g. `a@b` passes); the frontend may validate more strictly.

- `201` with the client object and a `Location` header.
- `422 VALIDATION_ERROR` when a field is missing, blank, too long or not a valid email (detail `field` names it).
- `400 BAD_REQUEST` when the body is not valid JSON or `preferredContactMethod` is not exactly `Email`, `Phone` or
  `Sms`.

`PATCH /api/clients/:id` (`clients.manage`): partial update.

    { "lastName": "Smith", "phone": "", "preferredContactMethod": null, "status": "Inactive" }

- Text fields: omitted or `null` leaves the field unchanged; `""` (or only spaces) clears an optional field. `firstName` and
  `lastName` cannot be cleared (`""` is `422`).
- `preferredContactMethod`: omitted leaves it unchanged; an explicit `null` clears it.
- `status`: `Active` or `Inactive`; unknown values are `400 BAD_REQUEST`.
- `200` with the updated client object; `404` for an unknown client (or another clinic's); `422` as for create.

Creating a client and every change to one is written to the audit log (`client.created`, `client.updated`). The entry
names the changed fields but not their values.

## 6. Patients
Implemented in B-06. Patients are the animals of the caller's clinic: other clinics' patients are `404 NOT_FOUND`,
never `403`. Missing permission is `403 FORBIDDEN`. Patients are never deleted (`DELETE` is `405`): set
`"status": "Inactive"` or `"Deceased"`.

**Patient object**

    {
      "id": "uuid", "name": "Rex",
      "species": "Dog" | "Cat" | "Bird" | "Rabbit" | "Rodent" | "Reptile" | "Horse" | "Other",
      "breed": "Labrador" | null,
      "sex": "Male" | "Female" | "Unknown",
      "reproductiveStatus": "Intact" | "Neutered" | "Unknown",
      "dateOfBirth": "2020-05-01" | null, "dateOfBirthEstimated": false,
      "markings": "Black, white chest" | null,
      "microchipNumber": "985112003456789" | null,
      "weightKg": 31.25 | null,
      "status": "Active" | "Inactive" | "Deceased", "dateOfDeath": "2024-03-01" | null,
      "notes": "..." | null,
      "client": { "id": "uuid", "firstName": "Jane", "lastName": "Doe" },
      "createdAt": "...", "updatedAt": "..."
    }

Every field is always present. Dates are calendar dates (`YYYY-MM-DD`). `weightKg` is a JSON number with at most 3
decimals and no trailing zeros. `client` is the primary owner; it is always returned because every role that can read
patients can also read clients. `Neutered` covers spayed and castrated. `dateOfBirthEstimated` is `true` when the date
is approximate (always `false` without a date). `dateOfDeath` is only ever set while the status is `Deceased`.

`GET /api/patients` (`patients.read`): paginated list (section 13). Includes inactive and deceased patients unless
`status` is given.
- `q`: case-insensitive substring of the patient's name or the owner's first, last or full name ("Jane Doe"), or a
  substring of the microchip number ignoring spaces, dashes and dots ("003-456" finds "985112003456789"). Max 100
  characters; `%` and `_` match literally.
- `clientId`: only that owner's patients (an unknown id returns an empty page). Not a GUID → `422`.
- `species`, `status`: exact names as above.
- `sort`: `name` (default), `createdAt` or `updatedAt`; one leading `-` for descending. Ties are broken by id in the
  same direction.
- Invalid values are `422 VALIDATION_ERROR` with the parameter as `field`.

`GET /api/patients/:id` (`patients.read`): `200` patient object, or `404`.

`POST /api/patients` (`patients.manage`): creates an `Active` patient in the caller's clinic.

    { "clientId": "uuid", "name": "Rex", "species": "Dog", "breed": "Labrador", "sex": "Male",
      "reproductiveStatus": "Neutered", "dateOfBirth": "2020-05-01", "dateOfBirthEstimated": true,
      "markings": "Black, white chest", "microchipNumber": "985 112 003 456 789", "weightKg": 31.25, "notes": null }

- Required: `clientId`, `name` (max 100), `species`. `sex` and `reproductiveStatus` default to `Unknown`.
- `clientId` must be an **active** client of the caller's clinic. An unknown, inactive or other clinic's client is
  `422` with field `clientId` and the same message in every case.
- `breed` max 100, `markings` max 200, `notes` max 4000. Text is trimmed; blank optional text is stored as `null`.
  Length limits apply to the value as sent.
- `microchipNumber`: spaces, dashes and dots are removed and letters upper-cased; the result must be 1 to 20 letters
  or digits (input max 40 characters). Unique within the clinic: a second patient with the same chip is
  `409 CONFLICT` (detail field `microchipNumber`). Other clinics may record the same chip.
- `weightKg`: more than 0, at most 2000, at most 3 decimals.
- `dateOfBirth`: not in the future. Until clinics have a time zone (B-26), "today" is the UTC date plus one day.
- Problems found by the server (owner, microchip format, weight, dates) are reported together in one `422`.
- `400 BAD_REQUEST` for invalid JSON, a value of the wrong type or format (dates must be exactly `YYYY-MM-DD`, ids
  must be UUIDs, `weightKg` a number), or an enum value that is not an exact name (`"dog"`, `"Spayed"`).
- `201` with the patient object and a `Location` header.

`PATCH /api/patients/:id` (`patients.manage`): partial update.

    { "clientId": "uuid", "weightKg": null, "status": "Deceased", "dateOfDeath": "2024-03-01" }

- Omitted or `null` fields are unchanged, except `dateOfBirth`, `weightKg` and `dateOfDeath`, which an explicit
  `null` clears. `""` (or only spaces) clears optional text, including `microchipNumber`. `name` cannot be cleared.
- `clientId` transfers the patient to another **active** client of the clinic (rules as for create). Sending the
  current owner is always accepted, even if that client is now inactive.
- `dateOfDeath` needs `status` `Deceased` (in the same request or already), cannot be in the future or before the
  date of birth. Changing the status away from `Deceased` clears the date of death. If a new `dateOfBirth` would
  fall after the recorded date of death, the error is reported on `dateOfDeath`.
- `200` with the updated patient; `404` for an unknown patient (or another clinic's); `409` for a microchip already
  used by another patient of the clinic (only once there are no `422` problems); `422` and `400` as for create.

Creating a patient and every change is written to the audit log (`patient.created` with the owner's id,
`patient.updated` with the changed field names and, on a transfer, `fromClientId` and `toClientId`). Names, notes and
other values are not recorded.

### 6.1 Patient history
Implemented in B-07 (design: `ARCHITECTURE.md` ADR-007).

`GET /api/patients/:id/history` (`patients.read`): the patient's timeline, newest first, in the standard paginated
envelope (section 13; `page`, `pageSize` only). Unknown or another clinic's patient is `404`; missing
`patients.read` is `403 FORBIDDEN`; out-of-range `page`/`pageSize` is `422 VALIDATION_ERROR`.

    {
      "data": [
        {
          "id": "uuid",
          "type": "PatientRegistered",
          "occurredAt": "2026-10-22T08:00:00+00:00",
          "actor": { "id": "uuid", "name": "Sam Reception" } | null,
          "details": { "client": { "id": "uuid", "firstName": "Jane", "lastName": "Doe" } | null }
        }
      ],
      "pagination": { "page": 1, "pageSize": 25, "total": 1, "totalPages": 1 }
    }

- `id` is the id of the entry's source record; it is unique per `type`, so use `type` + `id` as a key.
- `actor` is the staff member who caused the entry, or `null` (created by an operator command, or not visible).
- `details` has a fixed shape per `type`. **Ignore entries whose `type` you do not know**: later milestones add types
  (consultations, diagnoses, prescriptions, vaccinations, attachments, invoices, ...) without a version change.
  Later clinical types will also require `clinical.read`; entries the caller may not see are left out entirely.
- Ties on `occurredAt` are broken by `id`, then `type`, descending, so pages are stable (ids compare like their
  lowercase hex strings).
- Generated clients must accept an unknown `type` as a plain entry rather than fail; `openapi.yaml` therefore has no
  `discriminator` and lists the known types instead.

Entry types so far:

| `type` | When | `details` |
|---|---|---|
| `PatientRegistered` | The patient was created. | `client`: the owner at registration, with that client's **current** name; `null` if it can no longer be shown. |

## 7. Appointments
`GET /api/appointments`
`GET /api/appointments/:id`
`POST /api/appointments`
`PATCH /api/appointments/:id`
`POST /api/appointments/:id/check-in`
`POST /api/appointments/:id/complete`
`POST /api/appointments/:id/cancel`

The backend must validate veterinarian availability and appointment conflicts.

### 7.1 Veterinarian schedules
Implemented in B-08 (design: `ARCHITECTURE.md` ADR-008). Scheduling uses a **5-minute grid**: every time of day
and every instant must be on a 5-minute mark.

Permissions: `schedules.read` (Admin, Veterinarian, Nurse, Receptionist) reads everything below.
`schedules.manage` (Admin) changes anyone's schedule and clinic closures. `schedules.manage_own` (Veterinarian)
changes only the caller's own hours and exceptions: for any other veterinarian id (including unknown ids) and for
closures it is `403`. Callers with neither manage permission get `403` before any body validation; the "not your own
schedule" `403` is decided by the server after the body has been read, so a malformed body from such a caller may get
`400`/`422` first.

Veterinarians are the clinic's users holding the `Veterinarian` role. An id that is not one of them (another clinic's
user, a nurse, unknown) is `404`. A disabled veterinarian's schedule can be read, edited and deleted, but nothing new
can be added for them (`422`, field `veterinarianId`).

`GET /api/veterinarians` (`schedules.read`): active veterinarians by name, paginated (section 13):
`{ "id": "uuid", "name": "Jo Vet", "status": "Active" }`. Use it for calendar columns (`users.read` is admin-only).

**Weekly hours** are the regular week in the clinic's local time:

    { "id": "uuid", "veterinarianId": "uuid", "dayOfWeek": "Monday", "startTime": "09:00", "endTime": "17:30",
      "effectiveFrom": "2026-11-01", "effectiveTo": null, "createdAt": "...", "updatedAt": "..." }

- `dayOfWeek`: `Monday` … `Sunday` (exact names; otherwise `400`).
- `startTime` / `endTime`: `HH:mm` on the 5-minute grid; `endTime` may be `24:00` (midnight at the end of the day).
  A shift past midnight is two blocks (Friday 20:00–24:00 and Saturday 00:00–02:00).
- `effectiveFrom` (default: today in the clinic's time zone) and `effectiveTo` (inclusive, `null` = open-ended) are
  dates between 2000-01-01 and 2100-12-31. To change someone's hours from a date, set `effectiveTo` on the old block
  and add a new one. A veterinarian can have at most 200 blocks in total (`409` beyond that; delete old versions).
- Several blocks per day are allowed (09:00–12:30 and 13:30–17:00). Blocks of one veterinarian on the same day may
  not overlap in time while their dates overlap: `409 CONFLICT`.

`GET /api/veterinarians/:id/weekly-hours` (`schedules.read`): `{ "data": [block, ...] }`, all blocks (past, current
and future), Monday first, then by start time. Not paginated.

`POST /api/veterinarians/:id/weekly-hours`: `{ "dayOfWeek", "startTime", "endTime", "effectiveFrom"?, "effectiveTo"? }`
→ `201` with the block and a `Location` header. `422` for a missing day or time, a time not on the grid, an end not
after the start, or `effectiveTo` before `effectiveFrom`.

`PATCH /api/veterinarians/:id/weekly-hours/:blockId`: partial update; omitted or `null` fields are unchanged, except
`effectiveTo`, which an explicit `null` clears. Same rules as create. `DELETE` the same path → `204` (schedule
blocks are not clinical history; changes are audited).

**Exceptions** are times a veterinarian is unavailable, or clinic-wide **closures** (no veterinarian):

    { "id": "uuid", "veterinarianId": "uuid" | null,
      "type": "Leave" | "Sickness" | "Training" | "Other" | "Closure" | "Unavailable",
      "startsAt": "2026-11-02T00:00:00+00:00", "endsAt": "2026-11-03T00:00:00+00:00",
      "reason": "..." | null, "createdAt": "...", "updatedAt": "..." }

- `veterinarianId` is `null` exactly when `type` is `Closure`; otherwise `422`.
- **Privacy:** staff absences can be health information. Only `schedules.manage` holders and the veterinarian
  concerned see the real `type` and `reason`. Everyone else sees `"type": "Unavailable"` and `"reason": null`.
  Closures are shown in full to everyone. `Unavailable` is never accepted as input (`400`).
- `startsAt` / `endsAt` are instants: ISO 8601 **with an explicit offset** (`Z` or `+01:00`) on the 5-minute grid,
  `endsAt` after `startsAt`, between 2000-01-01 and 2100-12-31. Without an offset the request is `400`. Responses are in UTC. A whole local day is the
  local midnight-to-midnight instants (the frontend converts using `GET /api/clinic`).
- Exceptions may overlap each other.

`GET /api/schedule-exceptions` (`schedules.read`): paginated, by `startsAt` then id.
- `veterinarianId`: that veterinarian's exceptions **and** all closures. The id is not checked: an unknown id simply
  matches only the closures.
- `from` / `to`: instants (offset required; encode `+` as `%2B` in the query string); only exceptions ending after
  `from` / starting before `to`. Invalid values or `to` not after `from` are `422`.

`GET /api/schedule-exceptions/:id` (`schedules.read`), `POST /api/schedule-exceptions`
`{ "veterinarianId"?, "type", "startsAt", "endsAt", "reason"? }` (→ `201`, `Location`), `PATCH /api/schedule-exceptions/:id`
`{ "type"?, "startsAt"?, "endsAt"?, "reason"? }` (the veterinarian cannot change; a closure cannot become a personal
exception or the reverse; `""` clears the reason) and `DELETE /api/schedule-exceptions/:id` (→ `204`). Closures need
`schedules.manage`. Another clinic's exception is `404`. `reason` max 500 characters.

**Computed working hours**

`GET /api/veterinarians/:id/working-hours?from=YYYY-MM-DD&to=YYYY-MM-DD` (`schedules.read`): the actual working
periods for the local calendar days `from` to `to` (inclusive, at most 62 days, between 2000-01-01 and 2100-12-31): weekly hours minus that
veterinarian's exceptions and all closures.

    { "veterinarianId": "uuid", "timeZone": "Europe/London", "from": "2026-06-01", "to": "2026-06-07",
      "intervals": [ { "startAt": "2026-06-01T09:00:00+01:00", "endAt": "2026-06-01T12:00:00+01:00" }, ... ] }

- Intervals are sorted, half-open (`endAt` is the first minute not worked), merged where they touch, and carry the
  clinic's local offset at that moment.
- Daylight saving: a local time skipped when clocks go forward moves to the first valid time (a block entirely
  inside the gap disappears); a repeated local time when clocks go back is read as standard time.
- Missing or malformed dates (`YYYY-MM-DD` only), `to` before `from`, or more than 62 days are `422`.
- Appointments are not subtracted yet; B-09 builds booking validation on this.

Every create, update and delete of hours and exceptions is audited (`working_hours.*`, `schedule_exception.*`).
Metadata holds the veterinarian and the changed field names; the type only for closures; never the reason.

`Location` headers (here and on every create) are URI references: they may be absolute or relative to the API.

If two people change or delete the same record at the same moment, the loser gets `409 CONFLICT` ("changed or
removed by someone else"); reload and retry.

## 8. Consultations
`GET /api/patients/:id/consultations`
`GET /api/consultations/:id`
`POST /api/consultations`
`PATCH /api/consultations/:id`
`POST /api/consultations/:id/complete`

Clinical permissions must be enforced.

## 9. Diagnoses, treatments, prescriptions, vaccinations
These resources may be nested under consultations/patients or exposed as dedicated endpoints. The final shape must be documented before frontend implementation.

## 10. Billing
`GET /api/invoices`
`GET /api/invoices/:id`
`POST /api/invoices`
`PATCH /api/invoices/:id`
`POST /api/invoices/:id/payments`

Financial totals and balances are authoritative on the backend.

## 11. Inventory
`GET /api/products`
`GET /api/products/:id`
`POST /api/products`
`PATCH /api/products/:id`
`GET /api/inventory/stock`
`POST /api/inventory/movements`
`GET /api/suppliers`
`POST /api/purchase-orders`

Stock-changing operations require appropriate permissions and auditability.

## 12. Reports
Reports should use dedicated read endpoints and must enforce clinic scope and reporting permissions.

## 13. Pagination
List endpoints use this contract (implemented in B-04): query parameters `page` (default 1, 1 to 1,000,000) and `pageSize`
(default 25, 1 to 100); out-of-range values are `422 VALIDATION_ERROR`, not clamped. `total` is the number of
matching items across all pages.

{
  "data": [],
  "pagination": {
    "page": 1,
    "pageSize": 25,
    "total": 0,
    "totalPages": 0
  }
}

An endpoint may adopt cursor pagination later if offset paging proves too slow; that will be documented per endpoint.

Enum values (statuses, roles) are sent and accepted only as their exact names, e.g. `"Active"`, `"Admin"`. Numbers,
other casings (`"admin"`) and comma lists are rejected: `400 BAD_REQUEST` in a JSON body, `422 VALIDATION_ERROR` in a
query string.

## 14. API stability
Do not change response fields or semantics casually. If a breaking change is required, update this document and coordinate frontend changes.

## 15. Frontend rule
The frontend must consume only documented API behavior. If a needed endpoint is missing, update the contract before inventing a frontend workaround.

## 16. Operational endpoints
`GET /api/health` — anonymous. Returns `200` with `{ "status": "Healthy" }` when the service is up, or `503` with
`{ "status": "Unhealthy" }` (or `"Degraded"` with `200`) when a dependency check fails. It reports overall status only,
never configuration or dependency details. Checks that the database accepts connections (5-second timeout).
Whether to split liveness and readiness probes is deferred to deployment work (X-05).
