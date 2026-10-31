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
- `413` upload too large
- `415` unsupported media type or file type
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
`CSRF_TOKEN_INVALID`, `NOT_FOUND`, `METHOD_NOT_ALLOWED`, `CONFLICT`, `PAYLOAD_TOO_LARGE` (413, uploads, since B-15),
`UNSUPPORTED_MEDIA_TYPE`, `VALIDATION_ERROR`, `RATE_LIMITED`, `INTERNAL_ERROR`.

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
| `appointments.read`, `appointments.manage` | ✓ | ✓ | ✓ | ✓ | – |
| `clinical.read` | ✓ | ✓ | ✓ | – | – |
| `clinical.write` | – | ✓ | ✓ | – | – |
| `consultations.complete` | – | ✓ | – | – | – |
| `prescriptions.issue` | – | ✓ | – | – | – |
| `vaccinations.read_due` | ✓ | ✓ | ✓ | ✓ | – |
| `billing.read` | ✓ | ✓ | – | ✓ | – |
| `billing.manage` | ✓ | – | – | ✓ | – |
| `payments.record` | ✓ | – | – | ✓ | – |
| `payments.refund` | ✓ | – | – | – | – |
| `inventory.read` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `inventory.manage` | ✓ | – | – | – | ✓ |

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
Implemented in B-08; the currency in B-16 (the other clinic settings follow in B-26).

`GET /api/clinic` (any signed-in user): `{ "id": "uuid", "name": "Happy Paws", "timeZone": "Europe/London",
"currency": "EUR" | null }`.

`PATCH /api/clinic` (`settings.manage`): `{ "timeZone": "Europe/London", "currency": "EUR" }` (either or both).
- `currency` is an upper-case ISO 4217 code (`422` otherwise, field `currency`). It is `null` until an administrator
  sets it, and invoices cannot be created before. It can change only while the clinic has **no invoice**, no
  costed stock receipt and no product default price (`409`), because those amounts are in it (B-19). `GET /api/auth/me` does not carry the currency; read it here.
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
| `AppointmentCompleted` | An appointment was completed (`occurredAt` = `completedAt`). | `appointment`: `{ id, startAt, endAt, type, veterinarian: { id, name } }` |
| `AppointmentCancelled` | An appointment was cancelled (`occurredAt` = cancellation time). | `appointment` as above, `cancelledBy`: `Client` or `Clinic`. The reason is not shown. |
| `AppointmentNoShow` | An appointment was marked no-show. Disappears if the no-show is undone. | `appointment` as above |
| `ConsultationCompleted` | A consultation was completed (`occurredAt` = `completedAt`). **Only for callers with `clinical.read`**; for others it is neither shown nor counted. | `consultation`: `{ id, startedAt, completedAt, appointmentId, veterinarian: { id, name }, assessment, diagnoses: [ { id, name, status, isPrimary } ], prescriptions: [ { id, medicationName, status } ] }` (diagnoses primary first, prescriptions in the order recorded; statuses as they are now) |
| `VaccinationGiven` | A vaccine dose was recorded (`occurredAt` = the start of `administeredOn` in the clinic's time zone when the dose was recorded; a later time-zone change does not move it). Voided doses stay, with their status. **Only for callers with `clinical.read`**. | `vaccination`: `{ id, vaccineName, administeredOn, nextDueOn, administeredElsewhere, veterinarian: { id, name } \| null, status }` (status as it is now) |
| `ClinicalNoteAdded` | A clinical note was added (`occurredAt` = its time). **Only for callers with `clinical.read`**. | `note`: `{ id, text, consultationId }` |
| `InvoiceIssued` | An invoice naming the patient was issued (`occurredAt` = `issuedAt`). Voided invoices stay, with their status; drafts never appear. **Only for callers with `billing.read`**; for others it is neither shown nor counted. | `invoice`: `{ id, number, total, currency, status, paymentStatus, balanceDue }` (status `Issued` or `Void`; payment state as it is now, `paymentStatus` `null` for void invoices) |
| `AttachmentAdded` | A file was attached to the patient or one of its consultations (`occurredAt` = upload time). Withdrawn files disappear; client files never appear. **Only for callers with `clinical.read`**. | `attachment`: `{ id, fileName, contentType, sizeBytes, description, consultationId }` |

A history page that contains clinical entries is an audited clinical read (`patient.history_viewed`, with the
consultation, vaccination, note and attachment ids shown).

For appointment entries `id` is the appointment's id and `actor` is the staff member who made the change. For
`ConsultationCompleted`, `id` is the consultation's id and `actor` is the veterinarian who completed it. For
`VaccinationGiven`, `id` is the vaccination's id and `actor` is the staff member who recorded it. For
`ClinicalNoteAdded` and `AttachmentAdded`, `actor` is the author or uploader. For `InvoiceIssued`, `id` is the invoice's
id and `actor` is the staff member who issued it. All history
instants (`occurredAt` and those in `details`) are UTC, unlike the consultation and appointment objects, which use the
clinic's local offset. Scheduled and checked-in appointments are not history (use `GET /api/appointments?patientId=`).

## 7. Appointments
Implemented in B-09 (booking, editing, rescheduling; ADR-009) and B-10 (status lifecycle; ADR-010). There is no
`DELETE` (`405`).
Other clinics' appointments are `404`. Permissions: `appointments.read` and `appointments.manage` (Admin,
Veterinarian, Nurse, Receptionist).

**Appointment object** (`startAt` and `endAt` carry the clinic's local offset, like section 7.1's working hours;
`createdAt` / `updatedAt` are UTC)

    {
      "id": "uuid",
      "patient": { "id": "uuid", "name": "Rex", "species": "Dog" },
      "client": { "id": "uuid", "firstName": "Jane", "lastName": "Doe" },
      "veterinarian": { "id": "uuid", "name": "Jo Vet" },
      "startAt": "2026-11-02T09:00:00+00:00", "endAt": "2026-11-02T09:30:00+00:00",
      "type": "Consultation" | "Vaccination" | "FollowUp" | "Surgery" | "Dental" | "Grooming" | "Emergency" | "Other",
      "reason": "Limping" | null, "notes": "..." | null,
      "status": "Scheduled" | "CheckedIn" | "Completed" | "Cancelled" | "NoShow",
      "checkedInAt": "..." | null, "completedAt": "..." | null, "noShowAt": "..." | null,
      "cancellation": { "at": "...", "by": "Client" | "Clinic", "reason": "..." | null } | null,
      "override": { "reason": "Emergency", "conflicts": ["OutsideWorkingHours", "VeterinarianDoubleBooked", "PatientDoubleBooked"] } | null,
      "createdBy": { "id": "uuid", "name": "Sam Reception" } | null,
      "createdAt": "...", "updatedAt": "..."
    }

- `client` is the patient's owner **when the appointment was booked** (or moved to that patient); it is set by the
  server and never sent. A later transfer of the patient does not change existing appointments.
- `override` is present when the appointment was placed despite conflicts (see below); otherwise `null`.
- New appointments are `Scheduled`; the lifecycle below changes the status. Only `Scheduled` appointments can be
  edited with `PATCH` (`409` otherwise).

`GET /api/appointments` (`appointments.read`): paginated (section 13), by `startAt` then id.
- `from` / `to`: instants with offset (encode `+` as `%2B`); appointments ending after `from` / starting before `to`.
  Invalid values or `to` not after `from` are `422`.
- `veterinarianId`, `patientId`, `clientId`: exact matches (an unknown id gives an empty page; not a GUID → `422`).
- `status`: an exact status name (`422` otherwise).
- All statuses are included unless `status` is given.

`GET /api/appointments/:id` (`appointments.read`): `200` or `404`.

`POST /api/appointments` (`appointments.manage`):

    { "patientId": "uuid", "veterinarianId": "uuid", "startAt": "2026-11-02T09:00:00Z", "endAt": "2026-11-02T09:30:00Z",
      "type": "Consultation", "reason": "Limping", "notes": null,
      "allowConflicts": false, "overrideReason": null }

- Required: `patientId`, `veterinarianId`, `startAt`, `endAt`, `type`. `reason` max 500, `notes` max 4000 (trimmed;
  blank stored as `null`).
- `patientId` must be an **active** patient of the clinic, `veterinarianId` an **active** veterinarian of the clinic.
  Otherwise `422` on that field, with one message for every case (unknown, inactive, other clinic, not a vet).
- Times: instants with an explicit offset (`400` without one), on the 5-minute grid, 5 minutes to 12 hours long (may
  cross midnight), between 2000 and 2100, and **not starting before today** (local midnight in the clinic's time zone,
  so a walk-in earlier today can still be recorded). Otherwise `422` (`startAt` / `endAt`).
- `allowConflicts: true` needs an `overrideReason` (max 500); an `overrideReason` without it is `422`. Omit
  `overrideReason` or send `null` when not overriding (`""` counts as sent).
- `201` with the appointment and a `Location` header.

**Conflicts** are checked on create and whenever the patient, veterinarian or times change (listed in this order):

| Conflict | Meaning | `field` in the `409` |
|---|---|---|
| `OutsideWorkingHours` | Not fully inside one of the veterinarian's working periods (section 7.1: weekly hours minus exceptions and closures) | `startAt` |
| `VeterinarianDoubleBooked` | Overlaps another `Scheduled` or `CheckedIn` appointment of the veterinarian | `veterinarianId` |
| `PatientDoubleBooked` | Overlaps another `Scheduled` or `CheckedIn` appointment of the patient | `patientId` |

- Times are half-open: an appointment ending at 09:30 and one starting at 09:30 do not overlap.
- Without an override, any conflict is `409 CONFLICT` listing **every** conflict found in `details` (each message
  starts with the conflict name). Field errors (`422`) are reported before conflicts.
- With `allowConflicts: true` and a reason, the appointment is saved and `override` records the reason and exactly
  which conflicts were accepted. If there were none, nothing is recorded. Overrides are audited.
- Two bookings racing for the same slot cannot both succeed: one gets `201`, the other `409`.

`PATCH /api/appointments/:id` (`appointments.manage`): edit and reschedule. Omitted or `null` fields are unchanged;
`""` clears `reason` or `notes`.

    { "startAt": "2026-11-02T10:00:00Z", "endAt": "2026-11-02T10:30:00Z", "allowConflicts": true, "overrideReason": "Owner insists" }

- Changing `patientId` (the client follows the new patient's owner), `veterinarianId`, `startAt` or `endAt` applies
  the same rules and conflict checks as create **to the resulting placement**: the patient and veterinarian (sent or
  current) must be active, and the start must not be before today, even when only one of them changes. A placement
  without conflicts clears an earlier override; editing only `type`, `reason` or `notes` keeps it.
- `allowConflicts` / `overrideReason` only take effect when the placement changes; otherwise they are ignored, but
  must still be paired correctly. That pairing is checked first, before `404` / `409`.
- Concurrent edits of the same appointment are applied one after the other; the second is checked against what the
  first left, and may get `409`.
- `200` with the appointment; `404` unknown; `409` not `Scheduled` or conflicts; `422` / `400` as for create.

Every create and change is audited (`appointment.created`, `appointment.updated`): patient and veterinarian ids,
changed field names and the overridden conflicts. Reasons and notes are not copied into the audit log.

**Status lifecycle** (B-10, ADR-010). Each change is its own `POST` with no body (except cancel), requires
`appointments.manage`, and returns `200` with the appointment:

| Action | From → to | Rule (clinic time zone) |
|---|---|---|
| `POST /api/appointments/:id/check-in` | `Scheduled` → `CheckedIn` | only on the appointment's local day |
| `POST /api/appointments/:id/undo-check-in` | `CheckedIn` → `Scheduled` | only on the day of the check-in |
| `POST /api/appointments/:id/complete` | `CheckedIn` → `Completed` | final |
| `POST /api/appointments/:id/cancel` | `Scheduled` or `CheckedIn` → `Cancelled` | final; body below |
| `POST /api/appointments/:id/no-show` | `Scheduled` → `NoShow` | only once the appointment has started |
| `POST /api/appointments/:id/undo-no-show` | `NoShow` → `Scheduled` | only on the day it was marked, and only if the veterinarian and patient have not been booked into the slot again (`409` listing the clash) |

- Cancel body: `{ "cancelledBy": "Client" | "Clinic", "reason": "..." | null }`. `cancelledBy` is required (`422`
  when missing, `400` when not an exact name); `reason` max 500 (trimmed; blank stored as `null`).
- `undo-no-show` refuses any veterinarian or patient clash, even of a kind the booking originally overrode: the
  override covered other appointments, not whoever was booked into the freed slot since. Working hours are not
  re-checked. To keep both, book again with an explicit override.
- A change that is not allowed from the current status, or not at this time, is `409 CONFLICT` with a message saying
  why. Other clinics' appointments are `404`.
- Concurrent changes of one appointment are applied one after the other; the second is judged against the status the
  first left (e.g. complete and cancel at once: one `200`, one `409`).
- Fields: `checkedInAt` (kept after completion, and after a cancellation that followed check-in), `completedAt`,
  `noShowAt`, and `cancellation: { "at", "by": "Client" | "Clinic", "reason" }` (only when `Cancelled`). These
  instants carry the clinic's local offset like `startAt`. Undo clears the matching field.
- `Cancelled` and `NoShow` appointments no longer occupy the calendar, so their slot can be booked again. Overrides
  recorded on other bookings are left as they were.
- Every change is audited (`appointment.checked_in`, `.check_in_undone`, `.completed`, `.cancelled`, `.no_show`,
  `.no_show_undone`) with the old and new status and, for cancellations, `cancelledBy`; never the reason.

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
- Appointments are not subtracted: this is when the veterinarian works, not when they are free. Booking (section 7)
  checks both.

Every create, update and delete of hours and exceptions is audited (`working_hours.*`, `schedule_exception.*`).
Metadata holds the veterinarian and the changed field names; the type only for closures; never the reason.

`Location` headers (here and on every create) are URI references: they may be absolute or relative to the API.

If two people change or delete the same record at the same moment, the loser gets `409 CONFLICT` ("changed or
removed by someone else"); reload and retry.

## 8. Consultations
Implemented in B-11 (design: `ARCHITECTURE.md` ADR-011). Clinical records: every read and write is permission-checked
and **audited** (SECURITY.md section 10). Other clinics' consultations and patients are `404`. There is no `DELETE`.

Permissions: `clinical.read` (Admin, Veterinarian, Nurse) reads; `clinical.write` (Veterinarian, Nurse) opens,
edits, discards and adds addenda; `consultations.complete` (Veterinarian only) signs a consultation off;
`prescriptions.issue` (Veterinarian only) writes prescriptions (section 9).
Receptionist and Inventory have no clinical access.

**Consultation object** (`startedAt`, `completedAt`, `discardedAt` and addendum times carry the clinic's local offset;
`createdAt` / `updatedAt` are UTC)

    {
      "id": "uuid",
      "patient": { "id": "uuid", "name": "Rex", "species": "Dog" },
      "appointmentId": "uuid" | null,
      "veterinarian": { "id": "uuid", "name": "Jo Vet" },
      "status": "InProgress" | "Completed" | "Discarded",
      "startedAt": "...", "completedAt": "..." | null, "completedBy": { "id", "name" } | null,
      "discardedAt": "..." | null, "discardReason": "..." | null,
      "subjective": "..." | null, "objective": "..." | null, "assessment": "..." | null, "plan": "..." | null,
      "vitals": { "weightKg": 31.25 | null, "temperatureC": 38.6 | null, "heartRate": 96 | null,
                  "respiratoryRate": 24 | null, "bodyConditionScore": 5 | null, "painScore": 3 | null },
      "diagnoses": [ ... section 9 ... ], "treatments": [ ... section 9 ... ], "prescriptions": [ ... section 9 ... ],
      "addenda": [ { "id": "uuid", "text": "...", "author": { "id", "name" } | null, "createdAt": "..." } ],
      "createdBy": { "id", "name" } | null, "createdAt": "...", "updatedAt": "..."
    }

`GET /api/patients/:id/consultations` (`clinical.read`): the patient's consultations, newest `startedAt` first,
paginated (section 13), each row the full consultation object (notes and addenda included). `status` filters by an
exact status; without it, `Discarded` ones are left out. Unknown or
another clinic's patient is `404`. Audited (`consultation.listed`, with the ids shown).

`GET /api/consultations/:id` (`clinical.read`): `200` or `404`. Audited (`consultation.viewed`).

`POST /api/consultations` (`clinical.write`): opens an `InProgress` consultation.

    { "patientId": "uuid", "veterinarianId": "uuid", "appointmentId": "uuid" | null,
      "subjective": "...", "objective": "...", "assessment": "...", "plan": "...",
      "weightKg": 31.25, "temperatureC": 38.6, "heartRate": 96, "respiratoryRate": 24,
      "bodyConditionScore": 5, "painScore": 3 }

- `patientId`: an **active** patient of the clinic. `veterinarianId`: an active veterinarian of the clinic (the
  veterinarian of record; a nurse can open on their behalf). Otherwise `422` on that field (one message per field).
- `appointmentId` (optional): a `CheckedIn` appointment of the same patient (`422` otherwise). At most one
  non-discarded consultation per appointment (`409`). Without it, the consultation is a walk-in.
- **One open consultation per patient:** a second `InProgress` one is `409` until the first is completed or
  discarded (also under concurrent requests).
- Notes: max 10,000 characters each, trimmed, blank stored as `null`.
- Vitals (all optional): `weightKg` > 0 and ≤ 2000 with at most 3 decimals; `temperatureC` 25–45 with at most 1
  decimal; `heartRate` 1–400; `respiratoryRate` 1–200; `bodyConditionScore` 1–9; `painScore` 0–10. Out of range is
  `422` on that field. In responses vitals sit in `vitals`; in requests they are top-level fields.
- `201` with the consultation and a `Location` header.

`PATCH /api/consultations/:id` (`clinical.write`): edits an `InProgress` consultation (`409` otherwise). Omitted or
`null` notes are unchanged and `""` clears a note; each vital is unchanged when omitted and cleared by an explicit
`null`. `veterinarianId` changes the veterinarian of record (active veterinarian, `422` otherwise).

`POST /api/consultations/:id/complete` (`consultations.complete`): `InProgress` → `Completed`, recording `completedAt`
and `completedBy`. From then on the notes and vitals are frozen. A recorded `weightKg` becomes the patient's current
weight in the same step (audited as a patient change).

`POST /api/consultations/:id/discard` (`clinical.write`): `{ "reason": "..." }` (required, max 500). `InProgress` →
`Discarded`; kept for the audit trail, left out of lists by default and never shown in the history. Completed
consultations cannot be discarded (`409`).

`POST /api/consultations/:id/addenda` (`clinical.write`): `{ "text": "..." }` (required, max 4000, trimmed). Appends a
signed, dated addendum to a **completed** consultation (`409` for in-progress ones: edit the notes instead). At most
50 addenda per consultation (`409` beyond). Addenda can never be edited or deleted, and completed or discarded
consultations can never be changed; the database itself refuses both. Returns `200` with the consultation.

Concurrent changes to one consultation are applied one after the other. Every change is audited
(`consultation.created`, `.updated`, `.completed`, `.discarded`, `.addendum_added`) with ids and changed field names,
never note or addendum text.

## 9. Diagnoses, treatments, prescriptions, vaccinations
Diagnoses and treatments are implemented (B-12, ADR-012), prescriptions (B-13, ADR-013) and vaccinations (B-14,
ADR-014) too.

Diagnoses and treatments belong to one consultation and appear in the consultation object (section 8) as
`diagnoses` (primary first, then in the order recorded) and `treatments` (in the order recorded). Every change below
returns `200` with the whole consultation, runs under the consultation's lock and is audited (`diagnosis.added`,
`.updated`, `.removed`, `treatment.added`, `.updated`, `.removed`; ids and field names, never names or notes). An
unknown consultation, diagnosis or treatment (or another clinic's, or one of another consultation) is `404`, whatever
the consultation's state. Item `createdAt` / `updatedAt` are UTC.

**Diagnosis**: `{ "id", "name", "code" | null, "codeSystem" | null, "status": "Provisional" | "Confirmed" | "RuledOut",
"isPrimary", "notes" | null, "createdAt", "updatedAt" }`. `name` is required (max 200). `code` and `codeSystem` (max
50 each, e.g. `"VeNom"`, `"SNOMED CT"`) are stored as given and not validated against a terminology; a `codeSystem`
needs a `code`. `notes` max 2000. Differentials are `Provisional` diagnoses. At most one diagnosis per consultation is
primary: making one primary clears the flag on the others.

**Treatment**: `{ "id", "name", "type": "Procedure" | "Medication" | "Fluids" | "Other", "diagnosisId" | null,
"dose", "route", "frequency", "duration" (free text, max 100 each, or null), "notes" | null, "createdAt", "updatedAt" }`.
Care given during the consultation; take-home medication is a prescription (B-13). `diagnosisId` links one diagnosis
of the **same** consultation (`422` otherwise).

| Endpoint | Permission | While `InProgress` | After `Completed` |
|---|---|---|---|
| `POST /api/consultations/:id/diagnoses` | `clinical.write` | adds (`422` on invalid fields) | `409` |
| `PATCH /api/consultations/:id/diagnoses/:diagnosisId` | `clinical.write` | any field; omitted or `null` unchanged, `""` clears optional text | **only `status`**, and only by veterinarians (`consultations.complete`; otherwise `403`); any other field is `409` whoever sends it |
| `DELETE /api/consultations/:id/diagnoses/:diagnosisId` | `clinical.write` | removes it; its treatments are unlinked, not removed | `409` |
| `POST /api/consultations/:id/treatments` | `clinical.write` | adds | `409` |
| `PATCH /api/consultations/:id/treatments/:treatmentId` | `clinical.write` | any field; `""` clears optional text; `"diagnosisId": null` unlinks | `409` |
| `DELETE /api/consultations/:id/treatments/:treatmentId` | `clinical.write` | removes it | `409` |

A discarded consultation's items cannot change at all (`409`). At most 50 diagnoses and 100 treatments per
consultation (`409`). The database enforces the same freeze, so these rules hold even outside the API.

**Prescription**: take-home medication. Only veterinarians write prescriptions (`prescriptions.issue`); every
clinical reader sees them.

    {
      "id": "uuid", "medicationName": "Amoxicillin 250 mg", "dose": "1 tablet", "route": "Oral" | null,
      "frequency": "Twice daily", "duration": "7 days" | null, "quantity": 14, "quantityUnit": "tablets",
      "refills": 0, "instructions": "Give with food" | null, "diagnosisId": "uuid" | null,
      "status": "Active" | "Cancelled", "prescribedBy": { "id", "name" } | null,
      "cancelledAt": "..." | null, "cancelledBy": { "id", "name" } | null, "cancelReason": "..." | null,
      "createdAt": "...", "updatedAt": "..."
    }

`medicationName` (max 200), `dose` and `frequency` (max 100 each), `quantity` and `quantityUnit` (max 50) are required;
`route`, `duration` (max 100 each) and `instructions` (max 2000) are optional. `medicationName` is free text; linking it
to a catalogue product comes with dispensing (B-20, ADR-019). `quantity` is a number above 0 and at most 100000 with at most 3
decimals (e.g. 14 tablets, 10.5 ml); it is returned in its shortest form. `refills` (0 to 12, default 0) is recorded
only; repeat supplies are not tracked yet. `diagnosisId` links one diagnosis of the **same** consultation (`422`
otherwise); removing that diagnosis unlinks the prescription. `prescribedBy` is the veterinarian who added it.
`Active` means "not cancelled", not "course still running". `cancelledAt` carries the clinic's offset; `createdAt` /
`updatedAt` are UTC. Prescriptions appear in the consultation's `prescriptions`, in the order recorded.

| Endpoint | Permission | While `InProgress` | After `Completed` |
|---|---|---|---|
| `POST /api/consultations/:id/prescriptions` | `prescriptions.issue` | adds (`422` on invalid fields) | `409` |
| `PATCH /api/consultations/:id/prescriptions/:prescriptionId` | `prescriptions.issue` | any field; omitted or `null` unchanged, `""` clears optional text (required text cannot be blank: `422`), `"diagnosisId": null` unlinks | `409` |
| `DELETE /api/consultations/:id/prescriptions/:prescriptionId` | `prescriptions.issue` | removes it | `409` |
| `POST /api/consultations/:id/prescriptions/:prescriptionId/cancel` | `prescriptions.issue` | `409` (edit or remove it instead) | `{ "reason": "..." }` (required, max 500, trimmed): `Active` → `Cancelled`, recording who and when; an already cancelled one is `409` |

Each returns `200` with the whole consultation. A body that fails field validation (missing, blank or too long) is
`422` before the consultation's state is looked at (`409`), as everywhere (section 2). A discarded consultation's
prescriptions cannot change at all (`409`). At most 50 prescriptions per consultation (`409`). Unknown prescriptions (or another clinic's, or one of
another consultation) are `404`. The database enforces the same freeze: after completion only the cancellation step
is possible. Audited (`prescription.added`, `.updated`, `.removed`, `.cancelled`) with ids, field names and statuses,
never medication names, instructions or the cancellation reason. Controlled-drug logging is not part of this
milestone (SRS Q-5, B-20).

`GET /api/patients/:id/prescriptions` (`clinical.read`): the patient's **medication list**: prescriptions of completed
consultations, most recently completed consultation first (`consultationCompletedAt` descending), in the order
recorded within one, paginated. `status` filters by an exact status (`Active`, `Cancelled`). An unknown or another
clinic's patient is `404`. Each item: `{ "prescription": { ... }, "consultationId", "consultationCompletedAt" (clinic
offset), "veterinarian": { id, name } }` (the consultation's veterinarian of record). In-progress and discarded
consultations are left out. A clinical read: audited (`prescription.listed`).

`GET /api/patients/:id/diagnoses` (`clinical.read`): the patient's **problem list**: diagnoses of completed
consultations, most recently completed consultation first (`consultationCompletedAt` descending), primary first within
one, paginated. An unknown or another clinic's patient is `404`. `status` filters by an exact status.
Each item: `{ "diagnosis": { ... }, "consultationId", "consultationCompletedAt" (clinic offset), "veterinarian": { id, name } }`.
In-progress and discarded consultations are left out. A clinical read: audited (`diagnosis.listed`).

### Vaccinations
Implemented in B-14 (ADR-014). A vaccination is a record of the **patient**, given here or elsewhere, with an optional
link to a consultation. Records are never edited or deleted: one entered in error is **voided** with a reason (it is
kept, shown as voided and left out of due dates) and the right one is recorded again.

    {
      "id": "uuid", "patientId": "uuid", "consultationId": "uuid" | null,
      "vaccineName": "Rabies", "manufacturer": "VetPharma" | null, "lotNumber": "R-77" | null,
      "lotExpiresOn": "2027-10-01" | null, "administeredOn": "2026-10-25", "nextDueOn": "2029-10-25" | null,
      "administeredElsewhere": "Previous clinic" | null, "veterinarian": { "id", "name" } | null,
      "notes": "..." | null, "status": "Active" | "Voided",
      "voidedAt": "..." | null, "voidedBy": { "id", "name" } | null, "voidReason": "..." | null,
      "recordedBy": { "id", "name" } | null, "createdAt": "...", "updatedAt": "..."
    }

Dates are calendar days (`YYYY-MM-DD`) in the clinic's time zone; `voidedAt` carries the clinic's offset;
`createdAt` / `updatedAt` are UTC.

`POST /api/patients/:id/vaccinations` (`clinical.write`: Veterinarian, Nurse) records a dose: `201` with the
vaccination and `Location: /api/vaccinations/:id`.
- `vaccineName` (required, max 200; free text; a catalogue link comes with dispensing, B-20), `manufacturer` (max 100),
  `lotNumber` (max 50), `lotExpiresOn` (2000-01-01 to 2100-12-31), `notes` (max 2000). Optional text may be omitted,
  `null` or blank.
- `administeredOn` (required): from 2000-01-01 up to **today** in the clinic's time zone; past dates record earlier
  history.
- `nextDueOn` (optional): after `administeredOn`, at most 2100-12-31. It may already be past.
- **Given here:** `veterinarianId` is required and must be an active veterinarian of the clinic. **Given elsewhere:**
  `administeredElsewhere` (max 200, e.g. the previous clinic) is set and `veterinarianId` must be left out.
- `consultationId` (optional): an in-progress or completed consultation of the **same** patient; unknown, discarded,
  another patient's or another clinic's is one identical `422`. The link does not make the record follow the
  consultation's state.
- Order of checks: missing or too long fields `422` first (before the patient is looked at); then an unknown or
  another clinic's patient `404` and a patient that is not `Active` `409`; then the date, veterinarian and
  consultation checks, reported together in one `422`. Limits apply to the value as sent, before trimming. A date that
  is not exactly `YYYY-MM-DD` or a malformed id is `400`. Audited (`vaccination.recorded`) with ids, never the vaccine
  name or notes.

`GET /api/patients/:id/vaccinations` (`clinical.read`): the patient's doses, newest `administeredOn` first (then
newest recorded), paginated; voided ones included unless `status` (`Active` or `Voided`) filters them. Unknown or
another clinic's patient `404`. Audited (`vaccination.listed`).

`GET /api/vaccinations/:id` (`clinical.read`): `200` or `404`. Audited (`vaccination.viewed`).

`POST /api/vaccinations/:id/void` (`clinical.write`): `{ "reason": "..." }` (required, max 500, trimmed). `Active` →
`Voided`, recording who and when; an already voided one is `409`; unknown `404`. Returns `200` with the vaccination.
Audited (`vaccination.voided`) without the reason. There is no `PATCH` and no `DELETE`; the database refuses both.

`GET /api/vaccinations/due?to=YYYY-MM-DD[&from=YYYY-MM-DD]` (`vaccinations.read_due`: Admin, Veterinarian, Nurse,
Receptionist): the clinic's **due list** for reminder calls. For each active patient and vaccine (names compared
ignoring case), the latest non-voided dose counts; it is listed if its `nextDueOn` is on or before `to` and, when
`from` is given, on or after it. Without `from` every overdue dose is included. A newer dose without `nextDueOn` ends
the series; voiding the newest dose brings the previous one back. Ordered by `nextDueOn`, then patient name, then
`vaccinationId`; paginated.
`to` is required; `to` before `from`, or a date outside 2000-01-01 to 2100-12-31, is `422`. Each item:

    {
      "vaccinationId": "uuid", "vaccineName": "Rabies", "lastAdministeredOn": "2023-10-25", "nextDueOn": "2026-10-25",
      "patient": { "id", "name", "species" },
      "client": { "id", "firstName", "lastName", "phone" | null, "email" | null, "preferredContactMethod" | null } | null
    }

It carries no lot, notes or other clinical detail, because reception reads it. Audited (`vaccination.due_listed`, with
the range and the vaccination ids shown).

### Attachments
Implemented in B-15 (ADR-015). A file (image or PDF) belongs to exactly **one** owner: a patient, a consultation or a
client. Files are kept in private storage under random keys and are only ever served by the download endpoint below.
Access follows the owner:

| Owner | Upload / withdraw | Read, list, download |
|---|---|---|
| Patient, consultation | `clinical.write` (Veterinarian, Nurse) | `clinical.read` (Admin, Veterinarian, Nurse) |
| Client (consent forms, ID) | `clients.manage` (Admin, Receptionist) | `clients.read` (Admin, Veterinarian, Nurse, Receptionist) |

    {
      "id": "uuid", "ownerType": "Patient" | "Consultation" | "Client",
      "patientId": "uuid" | null, "consultationId": "uuid" | null, "clientId": "uuid" | null,
      "fileName": "Lateral thorax.png", "contentType": "image/png", "sizeBytes": 523412, "sha256": "64 hex",
      "description": "..." | null, "status": "Active" | "Withdrawn",
      "withdrawnAt": "..." | null, "withdrawnBy": { "id", "name" } | null, "withdrawReason": "..." | null,
      "uploadedBy": { "id", "name" } | null, "createdAt": "...", "updatedAt": "..."
    }

A consultation file also carries the consultation's `patientId`. `withdrawnAt` carries the clinic's offset;
`createdAt` / `updatedAt` are UTC.

**Upload:** `POST /api/patients/:id/attachments`, `POST /api/consultations/:id/attachments` (`clinical.write`) or
`POST /api/clients/:id/attachments` (`clients.manage`), as `multipart/form-data` with exactly one `file` part and an
optional `description` text part (max 500). `201` with the attachment and `Location: /api/attachments/:id`.
- **Types:** JPEG, PNG, WebP and PDF, recognised from the file's **leading bytes**; the file name and the part's
  `Content-Type` are ignored. Anything else (HTML, SVG, executables, text) is `415 UNSUPPORTED_MEDIA_TYPE`, as is a
  body that is not `multipart/form-data`.
- **Size:** at most **20 MB** (20,971,520 bytes); larger is `413 PAYLOAD_TOO_LARGE`. An empty file, no `file` part
  or more than one file is `422`.
- **Owners:** patients and clients of any status (records of inactive or deceased animals still grow);
  in-progress or completed consultations (late lab reports), but a discarded one is `409`. Unknown or another
  clinic's owner `404`.
- Order of checks: permission (`403`); owner (`404` / `409`, checked before the body is read); the request itself (`415` if it is not `multipart/form-data`, `413` if the body exceeds 20 MB plus framing, `400` if the multipart body is malformed); form fields (`422`); file type (`415`); file size (`413`).
- `fileName` is the uploader's base name without any path, control or invisible formatting characters (e.g.
  bidirectional overrides) or quotes, with the extension replaced by the detected type's (`.jpg`, `.png`, `.webp`,
  `.pdf`), so a file can never be saved as something executable; at most 255 characters ("file" plus the extension
  when nothing is left). It is used only as the download name.
- Audited (`attachment.uploaded`) with ids, owner type, content type and size; never the file name or description.

**Lists:** `GET /api/patients/:id/attachments` (`clinical.read`; the patient's files **and** those of its
consultations), `GET /api/consultations/:id/attachments` (`clinical.read`), `GET /api/clients/:id/attachments`
(`clients.read`). Newest first, paginated; `Active` files only unless `status=Withdrawn`. Unknown or another
clinic's owner `404`. Audited (`attachment.listed`).

`GET /api/attachments/:id`: the metadata. `GET /api/attachments/:id/content`: the content. Both need the owner's read
permission (`403` without it; another clinic's or unknown `404`) and are audited (`attachment.viewed`,
`attachment.downloaded`). The content is always a download: `Content-Disposition: attachment` with the file name,
`X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; sandbox` and
`Cache-Control: private, no-store`. A withdrawn file's content is `409`.

`POST /api/attachments/:id/withdraw` (the owner's write permission): `{ "reason": "..." }` (required, max 500,
trimmed). `Active` → `Withdrawn`, recording who and when; an already withdrawn file is `409`. The file disappears from
lists and downloads; its row and stored content stay (purging content is a retention task, X-05). Audited
(`attachment.withdrawn`) without the reason. As everywhere, a missing or too long `reason` is `422` before the file
is looked up, so it comes before `404` and the owner-based `403`. There is no `PATCH` and no `DELETE`; the database
refuses both.

### Clinical notes
Implemented in B-15 (ADR-015). A dated, signed text note on a patient outside the SOAP notes (phone advice, a lab
result received, an owner update). **Append-only**: never edited or deleted; a correction is a new note.

    { "id": "uuid", "patientId": "uuid", "consultationId": "uuid" | null, "text": "...",
      "author": { "id", "name" } | null, "createdAt": "..." }

`createdAt` carries the clinic's offset.

`POST /api/patients/:id/clinical-notes` (`clinical.write`): `{ "text": "...", "consultationId": "uuid" }`. `text`
required (max 4000, trimmed). `consultationId` optional: an in-progress or completed consultation of the **same**
patient (unknown, discarded, another patient's or another clinic's is one identical `422`). Any patient status.
`201` with the note and `Location: /api/clinical-notes/:id`. Unknown or another clinic's patient `404`. Audited
(`clinical_note.added`) without the text.

`GET /api/patients/:id/clinical-notes` (`clinical.read`): newest first, paginated; audited (`clinical_note.listed`).
`GET /api/clinical-notes/:id` (`clinical.read`): `200` or `404`; audited (`clinical_note.viewed`). There is no
`PATCH` and no `DELETE`; the database refuses both.

## 10. Billing
Invoices and tax rates are implemented in B-16 (ADR-016), payments, refunds and balances in B-17 (ADR-017), receipts,
frozen bill-to and invoice activity in B-18 (ADR-018).
Totals are **always computed by the server**; the contract has no way to send them (extra fields are ignored).

Permissions: `billing.read` (Admin, Veterinarian, Receptionist) reads; `billing.manage` (Admin, Receptionist)
creates, edits, issues, voids and deletes drafts. Tax rates are managed with `settings.manage` (Admin) and read with
`billing.read` or `settings.manage`. Nurses and Inventory have no billing access. Other clinics' invoices and rates are
`404`; another clinic's client, patient or rate in a body is `422`.

### 10.1 Money, tax and rounding (SRS Q-4)
- Every invoice is in the clinic currency (section 4.1), copied when the invoice is created. Amounts are JSON
  decimals in that currency, returned in their shortest form (`45`, `10.81`); do not do arithmetic on them in binary
  floating point.
- Prices **exclude tax**. Each line has its own tax rate.
- Each line is computed and rounded on its own, **half away from zero**, to the currency's minor units (2 for EUR or
  USD, 0 for JPY, 3 for KWD):
  `net = round(quantity × unitPrice)`; `discount = round(net × percent / 100)` or the discount amount;
  `tax = round((net − discount) × taxRate / 100)`; `total = net − discount + tax`.
- The invoice's `subtotal`, `discountTotal`, `taxTotal` and `total` are the sums of the lines' `net`, `discount`, `tax`
  and `total`, so a printed invoice always adds up.

### 10.2 Tax rates
`{ "id": "uuid", "name": "Standard", "ratePercent": 20, "status": "Active" | "Archived", "createdAt", "updatedAt" }`

- `GET /api/tax-rates` (`billing.read` or `settings.manage`): ordered by name, paginated; `status` filters.
- `POST /api/tax-rates` (`settings.manage`): `{ "name": "Standard", "ratePercent": 20 }`. Name required (max 100);
  `ratePercent` 0 to 100 with at most 4 decimals. One **active** rate per name, ignoring case (`409`). `201` with the
  rate (no `Location`: there is no single-rate `GET`).
- `PATCH /api/tax-rates/:id` (`settings.manage`): `{ "name": "...", "status": "Archived" | "Active" }`. The percentage
  never changes: archive the rate and create a new one. Archived rates cannot be picked for new lines; lines that
  already use a rate keep a **copy** of its name and percentage, so renaming or archiving changes no invoice.
  Renaming or restoring a rate onto a name used by another active rate (ignoring case) is `409`. A blank name is
  `422` before the rate is looked up.
- Rates are never deleted. Changes are audited (`tax_rate.created`, `tax_rate.updated`).

### 10.3 Invoices

    {
      "id": "uuid", "number": 42 | null, "status": "Draft" | "Issued" | "Void",
      "client": { "id", "firstName", "lastName" } | null, "patient": { "id", "name", "species" } | null,
      "billTo": { "name", "addressLine1", "addressLine2", "city", "region", "postalCode", "country", "patientName" } | null,
      "currency": "EUR", "notes": "..." | null,
      "items": [ {
        "id": "uuid", "position": 1, "description": "Consultation", "quantity": 1, "unitPrice": 45,
        "discountType": "None" | "Percent" | "Amount", "discountValue": 0,
        "taxRate": { "id": "uuid", "name": "Standard", "percent": 20 }, "consultationId": "uuid" | null,
        "productId": "uuid" | null, "net": 45, "discount": 0, "tax": 9, "total": 54
      } ],
      "subtotal": 45, "discountTotal": 0, "taxTotal": 9, "total": 54,
      "amountPaid": 20, "balanceDue": 34, "paymentStatus": "Unpaid" | "PartiallyPaid" | "Paid" | null,
      "issuedAt": "..." | null, "dueOn": "2026-11-30" | null, "issuedBy": { "id", "name" } | null,
      "voidedAt": "..." | null, "voidedBy": { "id", "name" } | null, "voidReason": "..." | null,
      "createdBy": { "id", "name" } | null, "createdAt": "...", "updatedAt": "..."
    }

`issuedAt` and `voidedAt` carry the clinic's offset; `createdAt` / `updatedAt` are UTC; `dueOn` is a clinic-local
date. Lines are ordered by `position`. `client` or `patient` is `null` only if it can no longer be shown.
`client` and `patient` show today's names; **`billTo`** (B-18) is who the invoice is addressed to, **frozen when it is
issued** (the client's "First Last" name and address, the patient's name; address parts may be `null`), and is
`null` on drafts. Print an issued invoice from `billTo`, so it never changes after a rename or a move.

**Lifecycle:** `Draft` → `Issued` → `Void`. A draft is freely editable, has no number and may be deleted. Issuing
assigns the clinic's next **gapless** number (1, 2, 3, ... per clinic, never reused) and freezes the invoice; an issued
invoice is never edited or deleted, only voided (its number stays taken). The database enforces the same rules.

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /api/invoices` | `billing.read` | Newest first, paginated; filters `status`, `clientId`, `patientId`, `paymentStatus` (`Unpaid`, `PartiallyPaid`, `Paid`; issued invoices only) and `overdue=true` (issued, balance left, `dueOn` before the clinic-local today; `false` or omitted applies no filter). |
| `GET /api/invoices/:id` | `billing.read` | `200` or `404`. |
| `POST /api/invoices` | `billing.manage` | `{ "clientId", "patientId"?, "notes"? }`: a draft. The clinic currency must be set (`409`). The client must be active; the patient (optional) must currently belong to the client, any patient status (`422`). `201` with `Location: /api/invoices/:id`. |
| `PATCH /api/invoices/:id` | `billing.manage` | Drafts only. `clientId`, `patientId` (`null` removes it), `notes` (`""` clears; max 2000). Changing the patient while lines bill its consultations is `409`. |
| `DELETE /api/invoices/:id` | `billing.manage` | Drafts only (`204`); an issued invoice is `409`. |
| `POST /api/invoices/:id/items` | `billing.manage` | Adds a line (fields below); at most 200 lines (`409`). |
| `PATCH /api/invoices/:id/items/:itemId` | `billing.manage` | Partial update; `consultationId: null` or `productId: null` unlinks; a new `taxRateId` copies the new rate. |
| `DELETE /api/invoices/:id/items/:itemId` | `billing.manage` | Removes a line. |
| `POST /api/invoices/:id/issue` | `billing.manage` | Optional body `{ "dueOn": "YYYY-MM-DD" }` (default: the clinic-local issue day; earlier, or after 2100-12-31, is `422`). Needs at least one line (`409`). |
| `POST /api/invoices/:id/void` | `billing.manage` | `{ "reason": "..." }` (required, max 500, trimmed). Issued invoices only; a draft (delete it), a void invoice, or one that still holds payments (refund them first) is `409`. |

Every successful change to a draft or its lines (and issue / void) returns the whole invoice with `200` and
recomputes all amounts; changes to a non-draft are `409`. Concurrent changes to one invoice are applied one after the
other.

**Line fields:** `description` (required, max 500), `quantity` (above 0, at most 1,000,000, at most 3 decimals),
`unitPrice` (0 or more, at most 1,000,000,000, at most 4 decimals, before tax), `taxRateId` (required; an active rate
of the clinic), `discountType` (`None` by default, `Percent` or `Amount`) with `discountValue` (omit or `0` for
`None`; a percentage above 0 and at most 100 with at most 4 decimals; an amount above 0 in the currency's minor units
and **not more than the line's net**), `consultationId` (optional; a non-discarded consultation of the invoice's patient, so the invoice needs
a patient), `productId` (optional, B-19; an **active** product of the clinic, services included; the line keeps its
own description and price, and stock does not change). Order of checks: missing or too long fields are `422` before the invoice is looked up; then unknown
invoice or line `404`; then the invoice's state (`409` for a non-draft) and the 200-line cap (`409`); then the value
rules (ranges, decimals, tax rate, consultation, product, `dueOn`), reported together in one `422`. A line total or invoice total
above 1,000,000,000,000 (in the invoice currency) is `422`. Issuing is also `409` when the patient no longer belongs to
the invoice's client (change one of them first) or when the stored amounts differ from a recomputation (edit a line to
recompute).

**Audit:** `invoice.created`, `.updated`, `.deleted`, `.item_added`, `.item_updated`, `.item_removed`, `.issued`,
`.voided`, with ids, field names, numbers, totals and currency; never descriptions, notes or the void reason.
Invoice reads are not audited (they are not clinical records).

**Payment state** (B-17): `amountPaid` is the payments minus refunds, `balanceDue` is `total − amountPaid`, and
`paymentStatus` is `Unpaid` (nothing paid), `PartiallyPaid` or `Paid` (balance 0; also an invoice with a zero total).
Drafts and void invoices have `amountPaid` 0 and `paymentStatus` `null`. Receipts: section 10.5.

### 10.4 Payments and refunds
Implemented in B-17 (ADR-017). Payments are recorded against **issued** invoices. Nothing here is ever edited,
voided or deleted: money given back, or a payment recorded by mistake, is a **refund** against that payment. The
database enforces the same rules.

    {
      "id": "uuid", "amount": 20, "method": "Cash" | "Card" | "BankTransfer" | "Cheque" | "Other",
      "reference": "Slip 0042" | null, "paidAt": "...", "receivedBy": { "id", "name" } | null,
      "refundedAmount": 0,
      "refunds": [ { "id": "uuid", "amount": 5, "method": "Cash", "reason": "...", "refundedAt": "...",
                     "refundedBy": { "id", "name" } | null, "receipt": { "id": "uuid", "number": 2 } | null } ],
      "receipt": { "id": "uuid", "number": 1 } | null,
      "invoice": { "id": "uuid", "total": 54, "amountPaid": 20, "balanceDue": 34, "paymentStatus": "PartiallyPaid" },
      "createdAt": "..."
    }

`paidAt` and `refundedAt` carry the clinic's offset; `createdAt` is UTC. `invoice` is the invoice's payment state
after the change (in `GET`, its current state), so the screen need not reload it. Refunds are oldest first.

- `GET /api/invoices/:id/payments` (`billing.read`): the invoice's payments, oldest `paidAt` first, each with its
  refunds (a plain array; at most 100 payments per invoice). Unknown or another clinic's invoice `404`.
- `POST /api/invoices/:id/payments` (`payments.record`: Admin, Receptionist):
  `{ "amount": 20, "method": "Cash", "reference": "...", "paidAt": "..." }`.
  - `amount` (required): above 0, in the currency's minor units, and **at most the balance due**; an overpayment is
    `422` (for cash, the frontend works out the change and records only the amount applied).
  - `method` (required): an exact method name (anything else `400`).
  - `reference` (optional, max 100 as sent, trimmed; blank is stored as `null`): a terminal slip, authorisation or
    cheque number. **A card number anywhere in it is refused** (`422`): 13 to 19 digits, in one group or in
    consecutive groups joined by single spaces, dashes, dots or slashes, passing the Luhn check.
  - `paidAt` (optional, with an explicit offset): defaults to now; may be earlier, but not in the future and not
    before the invoice was issued (`422`).
  - A draft or void invoice is `409`; more than 100 payments on one invoice `409`.
  - `201` with the payment (no `Location`: there is no single-payment `GET`).
- `POST /api/invoices/:id/payments/:paymentId/refunds` (`payments.refund`: Admin only):
  `{ "amount": 5, "method": "Cash", "reason": "..." }`. `amount` above 0 in minor units and at most what remains of the
  payment (`422`); `method` as for payments (`400` for anything else); `reason` required (max 500, trimmed) and, like a
  reference, never a card number (`422`); a fully refunded payment is `409`; at most 20 refunds per payment (`409`).
  Dated now. A void invoice's payments are all refunded, so a refund there is `409`. `201` with the whole payment.
  Another clinic's invoice, or a payment of another invoice, is `404`.
- Concurrent payments and refunds on one invoice are applied one after the other under the invoice's row lock, so
  the balance is always exact and never negative (tested: of six simultaneous payments that together exceed the
  balance, only those that fit succeed).
- Order of checks: permission (`403`), malformed body or enum (`400`), missing or too long fields (`422`), unknown
  invoice or payment (`404`), the invoice's or payment's state and the caps (`409`), then the value rules (`422`,
  reported together).
- Audited (`payment.recorded`, `payment.refunded`) with ids, amount, method and currency; never the reference or
  the reason.
- Since B-18 every payment and every refund gets a **receipt** in the same transaction (section 10.5); `receipt` is
  its id and number (`null` only for money recorded before B-18).

### 10.5 Receipts
Implemented in B-18 (ADR-018). A receipt is a structured document the frontend renders and prints; everything on it
is **frozen** when it is created, so a reprint is always identical. Payment and refund receipts share one **gapless**
number sequence per clinic. Receipts are never changed or deleted (the database refuses both).

    {
      "id": "uuid", "number": 1, "kind": "Payment" | "Refund", "clinicName": "Happy Paws",
      "invoice": { "id": "uuid", "number": 42, "total": 54, "currency": "EUR" },
      "billTo": { "name", "addressLine1", ... , "patientName" },   (never null: receipts exist only for issued invoices)
      "paymentId": "uuid", "refundId": "uuid" | null,
      "amount": 20, "method": "Card", "reference": "Slip 0042" | null, "occurredAt": "...",
      "amountPaidAfter": 20, "balanceDueAfter": 34,
      "issuedBy": { "id": "uuid", "name": "Pat Desk" }, "createdAt": "..."
    }

- `amount` is always positive; `kind` says whether money came in (`Payment`) or went back (`Refund`). `method` is the
  payment's method, or the refund's own method on refund receipts. `reference` is the payment's (refund receipts have
  none; refund reasons are not printed). `occurredAt` is the payment's `paidAt` or
  the refund's `refundedAt` (clinic offset); `createdAt` is UTC.
- `clinicName`, the invoice number and total, the amounts after the change and the cashier's name (`issuedBy.name`) are
  copies made at the time; `billTo` is the invoice's frozen block (section 10.3).
- `GET /api/receipts/:id` (`billing.read`): `200` or `404`. Opening a receipt (a view or a reprint) is audited
  (`receipt.viewed`).
- `GET /api/receipts` (`billing.read`): newest number first, paginated; filters `invoiceId` and `number` (an exact
  positive number; `422` otherwise). Each list call is audited once (`receipt.listed`, with the filters and the receipt
  ids shown), since it returns the same details as opening each receipt.
- Creating a receipt is audited (`receipt.issued`) with its number, kind, ids and amount.

### 10.6 Invoice activity
`GET /api/invoices/:id/activity` (`billing.read`): how an invoice reached its state. The audit entries of the invoice,
its payments (refunds are recorded on their payment) and its receipts, **oldest first**, paginated. Unknown or another
clinic's invoice `404`.

    { "id": "uuid", "occurredAt": "...", "action": "payment.recorded", "entityType": "Payment", "entityId": "uuid",
      "actor": { "id", "name" } | null, "details": { ... } | null }

Entries include reads of its receipts (`receipt.viewed`, one per view or reprint) as well as changes.
`occurredAt` carries the clinic's offset. `details` is the entry's metadata as recorded (ids, field names, numbers,
amounts, methods, statuses), which never holds descriptions, notes, references or reasons. `actor` is `null` when the
entry had no signed-in user or the account cannot be shown (only this clinic's staff are named). The clinic-wide audit
search is B-27.

## 11. Inventory
Products and stock are implemented in B-19 (ADR-019). Stock is held **per lot** and changes **only through recorded
stock movements**; B-19 records one movement type, `Receive`. Outgoing movements (dispense, sell, adjust, return,
expire), the negative-stock rule and controlled-drug logging come with B-20 (SRS Q-5); suppliers and purchase orders
with B-21; low-stock and expiring-soon queries with B-22.

Permissions: `inventory.read` (Admin, Veterinarian, Nurse, Receptionist, Inventory) reads products, stock and
movements; `inventory.manage` (Admin, Inventory) creates and edits products and receives stock. Other clinics' products
are `404`; another clinic's product in a body is `422` with the same message as an unknown one.

### 11.1 Products

    {
      "id": "uuid", "sku": "AMX-250", "name": "Amoxicillin 250 mg", "category": "Antibiotics" | null,
      "type": "Medication" | "Vaccine" | "Supply" | "Retail" | "Service",
      "unit": "Each" | "Tablet" | "Capsule" | "Millilitre" | "Litre" | "Gram" | "Kilogram" | "Dose" | "Vial" | "Ampoule" | "Bottle" | "Box" | "Pack",
      "reorderLevel": 100 | null, "defaultUnitPrice": 0.45 | null,
      "defaultTaxRate": { "id": "uuid", "name": "Standard", "percent": 20, "status": "Active" | "Archived" } | null,
      "status": "Active" | "Inactive", "quantityOnHand": 130.5 | null, "createdAt": "...", "updatedAt": "..."
    }

- `quantityOnHand` is the sum of the product's lots, in its `unit`; `null` for a `Service`, which is billed but never
  stocked (no lots, no movements, no reorder level).
- `defaultUnitPrice` (clinic currency, before tax) and `defaultTaxRate` are suggestions for invoice lines: the frontend
  prefills a line from them; the server copies nothing. The default rate may have been archived since it was set
  (`status` tells), and an archived rate cannot be used on a new line.

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /api/products` | `inventory.read` | Ordered by name, paginated. `q` (part of the name or SKU, ignoring case), `type`, `status`, `category` (exact, ignoring case). |
| `GET /api/products/categories` | `inventory.read` | The categories in use, as a sorted JSON array of strings (one spelling per category ignoring case), for a picker. |
| `GET /api/products/:id` | `inventory.read` | `200` or `404`. |
| `POST /api/products` | `inventory.manage` | Fields below. `201` with `Location: /api/products/:id`. |
| `PATCH /api/products/:id` | `inventory.manage` | Partial update: omitted fields are unchanged; `category: ""` clears it; `reorderLevel`, `defaultUnitPrice` or `defaultTaxRateId` set to `null` clears them; `status` `Inactive` deactivates, `Active` restores. |

**Fields:** `sku` (required, max 50, trimmed; letters, digits and `- . _ /`, starting with a letter or digit; **unique
per clinic ignoring case**, inactive products included: `409`), `name` (required, max 200), `category` (optional, max
100), `type` and `unit` (required on create), `reorderLevel` (0 to 1,000,000, at most 3 decimals; not on services),
`defaultUnitPrice` (0 to 1,000,000,000, at most 4 decimals), `defaultTaxRateId` (an active rate of the clinic).
Missing, too long or malformed fields (SKU pattern, blank name) are `422` before the product is looked up; then
`404`; then the value rules (amounts, tax rate) in one `422`. In a `PATCH`, `null` for `sku`, `name`, `category`,
`type`, `unit` or `status` leaves the field unchanged.
**The type and unit cannot change once the product has a stock lot** (`409`); before that they can. Products are never
deleted: deactivate them. An inactive product cannot be received into or linked to a new invoice line; its stock and
existing links stay. Changes are audited (`product.created`, `product.updated`, with field names, type and status;
never names, SKUs or categories). Reads are not audited.

### 11.2 Stock lots

    {
      "id": "uuid", "product": { "id": "uuid", "sku": "AMX-250", "name": "Amoxicillin 250 mg", "unit": "Tablet" },
      "lotNumber": "L-1" | null, "expiresOn": "2027-10-01" | null, "location": "Pharmacy" | null,
      "quantity": 120.5, "createdAt": "...", "updatedAt": "..."
    }

`GET /api/inventory/stock` (`inventory.read`): lots of the clinic, paginated, ordered by product name, then the lot
expiring first (lots without expiry last). Filters: `productId`; `includeEmpty=true` also lists lots with nothing
left (by default only lots holding stock). A lot is identified by its product, **lot number, expiry date and
location** (each optional; lot number and location compared ignoring case); it is never deleted and never edited:
only movements change its quantity, and the database checks at commit that it equals the sum of its movements.

### 11.3 Stock movements

    {
      "id": "uuid", "type": "Receive",
      "product": { "id", "sku", "name", "unit" },
      "lot": { "id": "uuid", "lotNumber": "L-1" | null, "expiresOn": "2027-10-01" | null, "location": "Pharmacy" | null, "quantity": 120.5 },
      "quantity": 20.5, "unitCost": 0.1234 | null, "currency": "EUR" | null,
      "reference": "DN-5531" | null, "notes": "..." | null,
      "occurredAt": "...", "performedBy": { "id", "name" } | null, "createdAt": "..."
    }

`quantity` is the signed change of the lot (positive for stock coming in); `lot.quantity` is the lot's quantity
**now**. `occurredAt` carries the clinic's offset. Movements are never edited or deleted.

- `GET /api/inventory/movements` (`inventory.read`): newest first (`occurredAt`), paginated; filters `productId`,
  `stockLotId`, `type`.
- `POST /api/inventory/movements` (`inventory.manage`): records a movement; B-19 accepts only `"type": "Receive"`
  (any other value is `400`). `201` with the movement (no `Location`: there is no single-movement `GET`).

      { "type": "Receive", "productId": "uuid", "quantity": 100, "lotNumber": "L-1", "expiresOn": "2027-10-01",
        "location": "Pharmacy", "unitCost": 0.1234, "reference": "DN-5531", "notes": "...", "occurredAt": "..." }

  `productId` (required): an **active**, non-`Service` product of the clinic (`422`). `quantity` (required): above 0,
  at most 1,000,000, at most 3 decimals, in the product's unit. `lotNumber` (max 50), `location` (max 100):
  optional, trimmed. `expiresOn`: optional, but **required for `Medication` and `Vaccine`** products; not before the
  clinic-local today (expired stock is not received) and not after 2100-12-31. `unitCost` (optional; 0 to
  1,000,000,000, at most 4 decimals) is in the clinic currency, which must be set (`422`); the movement keeps the
  currency. `reference` (max 100, e.g. the delivery note) and `notes` (max 500) are optional. `occurredAt` (optional,
  default now) may be earlier but not in the future or before 2000.
  The stock goes into the product's lot with the **same lot number, expiry and location**, which is topped up; when
  there is none, a lot is created. A lot holds at most 1,000,000,000 (`409`). Receipts of one product are applied one
  after the other. Audited as `stock.received` with ids, quantity, unit cost and currency; never lot numbers,
  locations, references or notes.

Billing a product on an invoice line (section 10.3, `productId`) does not change stock in B-19.

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
