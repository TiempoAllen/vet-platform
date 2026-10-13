# API Contract

Machine-readable form of the implemented endpoints: `openapi.yaml`. Change both together; this document wins if
they disagree.

## 1. Conventions
Base path: `/api`

Use JSON for ordinary requests/responses.

Use ISO-8601 timestamps with timezone information.

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
`400 BAD_REQUEST` when the body is missing or not valid JSON (parser details are not echoed).

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
      "clinic": { "id": "uuid", "name": "Happy Paws" },
      "roles": ["Admin", "Veterinarian"],
      "permissions": ["users.manage", "users.read"]
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
**Permissions** granted so far: `users.read`, `users.manage`, both to `Admin` only. Later milestones add permissions
and grant them to roles; `GET /api/auth/me` lists the caller's.

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

## 5. Clients
`GET /api/clients`
`GET /api/clients/:id`
`POST /api/clients`
`PATCH /api/clients/:id`

Support pagination, search, filtering, and sorting as documented by the implementation.

## 6. Patients
`GET /api/patients`
`GET /api/patients/:id`
`POST /api/patients`
`PATCH /api/patients/:id`

Patient endpoints must enforce clinic scope and client access permissions.

## 7. Appointments
`GET /api/appointments`
`GET /api/appointments/:id`
`POST /api/appointments`
`PATCH /api/appointments/:id`
`POST /api/appointments/:id/check-in`
`POST /api/appointments/:id/complete`
`POST /api/appointments/:id/cancel`

The backend must validate veterinarian availability and appointment conflicts.

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
List endpoints use this contract (implemented in B-04): query parameters `page` (default 1, minimum 1) and `pageSize`
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
