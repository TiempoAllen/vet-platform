# API Contract

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

Error codes (stable; clients may switch on them): `BAD_REQUEST`, `UNAUTHENTICATED`, `FORBIDDEN`, `NOT_FOUND`,
`METHOD_NOT_ALLOWED`, `CONFLICT`, `UNSUPPORTED_MEDIA_TYPE`, `VALIDATION_ERROR`, `RATE_LIMITED`, `INTERNAL_ERROR`.

Status mapping for request-body problems: `422 VALIDATION_ERROR` when the body parses but field values are invalid;
`400 BAD_REQUEST` when the body is missing or not valid JSON (parser details are not echoed).

Request correlation: every response, success or error, carries an `X-Request-Id` header equal to the `requestId`
in error bodies. A client may supply its own `X-Request-Id` (max 64 characters of `A-Z a-z 0-9 - _ .`);
otherwise the server generates one. Unsafe values are replaced, not rejected.

Do not expose stack traces or sensitive internal details in production. Unexpected errors return a generic
`500 INTERNAL_ERROR`; details are logged server-side only.

## 3. Authentication
`POST /api/auth/login`
`POST /api/auth/logout`
`POST /api/auth/refresh` if token refresh architecture is used
`GET /api/auth/me`

Authentication mechanism is determined by `ARCHITECTURE.md` and must use secure cookie/token practices appropriate to the chosen stack.

## 4. Users and access
`GET /api/users`
`GET /api/users/:id`
`POST /api/users`
`PATCH /api/users/:id`
`POST /api/users/:id/roles`

Permission checks are mandatory.

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
Default list responses should support a consistent pagination contract, for example:

{
  "data": [],
  "pagination": {
    "page": 1,
    "pageSize": 25,
    "total": 0,
    "totalPages": 0
  }
}

The exact pagination strategy can change if cursor pagination is more appropriate.

## 14. API stability
Do not change response fields or semantics casually. If a breaking change is required, update this document and coordinate frontend changes.

## 15. Frontend rule
The frontend must consume only documented API behavior. If a needed endpoint is missing, update the contract before inventing a frontend workaround.

## 16. Operational endpoints
`GET /api/health` — anonymous. Returns `200` with `{ "status": "Healthy" }` when the service is up, or `503` with
`{ "status": "Unhealthy" }` (or `"Degraded"` with `200`) when a dependency check fails. It reports overall status only,
never configuration or dependency details. Checks that the database accepts connections (5-second timeout).
Whether to split liveness and readiness probes is deferred to deployment work (X-05).
