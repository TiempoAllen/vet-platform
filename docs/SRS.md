# Software Requirements Specification (SRS)

Veterinary Management Platform. Version 1.3, 2026-10-22, updated for B-07.

## 1. Introduction

### 1.1 Purpose
This document states what the platform must do, as numbered, testable requirements. It is the reference for
acceptance and for the traceability matrix in `TEST_PLAN.md`. It does not restate design: how a requirement is met
lives in `SDD.md` and the ADRs in `ARCHITECTURE.md`.

### 1.2 Relationship to other documents
| Document | Role |
|---|---|
| `PRODUCT.md` | Vision, users, workflows (source for this SRS) |
| `API.md` | Narrative frontend/backend contract; wins over this SRS on exact HTTP behaviour |
| `openapi.yaml` | Machine-readable API specification of implemented endpoints |
| `DATABASE.md` | Data model and integrity rules |
| `SECURITY.md` | Security and privacy baseline |
| `SDD.md` | Software design |
| `TEST_PLAN.md` | Verification strategy and traceability |
| `RUNBOOK.md` | Operating procedures |
| `ROADMAP.md` | Milestones; each requirement below names the milestone that delivers it |

When a requirement here conflicts with a more specific document, flag the conflict (AGENTS.md) and prefer the more
specific, current one.

### 1.3 Scope
A multi-tenant web application for veterinary clinics: staff accounts and access control, clients and patients,
scheduling, clinical records, billing, inventory, notifications, reporting, settings and audit. Out of scope for the
first release (`PRODUCT.md` section 6, `ROADMAP.md` "Future candidates"): multi-location enterprise features,
client-facing apps and online booking, payment gateways, accounting, laboratory or pharmacy integrations, AI
features, telemedicine.

### 1.4 Definitions
| Term | Meaning |
|---|---|
| Clinic | A veterinary practice; the tenant. All business data belongs to exactly one clinic. |
| User / staff | A person with a login, belonging to one clinic. |
| Client | A pet owner (the clinic's customer). |
| Patient | An animal treated by the clinic. |
| Role | One of the fixed staff roles: Admin, Veterinarian, Nurse, Receptionist, Inventory. |
| Permission | A named capability (e.g. `users.manage`) granted to roles. |
| Session | A server-side sign-in record referenced by an HttpOnly cookie. |
| Audit entry | An append-only record of a security or business event. |

### 1.5 Requirement conventions
- IDs: `FR-<area>-<n>` functional, `NFR-<area>-<n>` non-functional, `C-<n>` constraints.
- "must" is mandatory; "should" is expected unless a documented reason exists.
- Status: **Done** (implemented and tested), **Planned** (milestone named), **Open** (no milestone yet; needs a
  decision).

## 2. Overall description

### 2.1 Product perspective
A modular-monolith ASP.NET Core API over PostgreSQL, consumed by a same-site web frontend (framework to be chosen in
F-01). There is no public sign-up: clinics are provisioned by an operator.

### 2.2 User classes
| Class | Role | Main needs |
|---|---|---|
| Clinic administrator / owner | Admin | Staff, permissions, settings, finance, reports |
| Veterinarian | Veterinarian | Appointments, consultations, diagnoses, prescriptions, vaccinations |
| Nurse / technician | Nurse | Patient preparation, treatment records, inventory tasks |
| Receptionist | Receptionist | Clients, pets, appointments, check-in, billing |
| Inventory / pharmacy | Inventory | Products, stock, suppliers, purchasing |
| Platform operator | (none; CLI and infrastructure) | Provision clinics, deploy, back up, respond to incidents |

A user may hold several roles; their permissions are the union.

### 2.3 Operating environment
- Server: .NET 10 runtime, PostgreSQL 16+ (18 in development and tests), Linux or Windows host or container.
- Client: current evergreen desktop and mobile browsers. HTTPS only.

### 2.4 Constraints
| ID | Constraint |
|---|---|
| C-1 | Backend: .NET 10, ASP.NET Core controllers, single `VetPlatform.Api` project (ADR-001). |
| C-2 | Database: PostgreSQL via EF Core; snake_case, UUIDv7 keys, `timestamptz` UTC, money `numeric(19,4)` (ADR-002). |
| C-3 | Frontend and API are served from the same site; the API has no CORS (ADR-003). |
| C-4 | HTTPS everywhere, including local development. |
| C-5 | Schema changes only through reviewed EF Core migrations; the app never migrates on startup. |
| C-6 | No secrets in source control. |
| C-7 | Emails are unique platform-wide: one account belongs to one clinic. |

### 2.5 Assumptions and dependencies
- Each clinic operates in one timezone and one currency (to be configured in B-26).
- Email and SMS delivery providers are not chosen; notifications (B-25) depend on that choice.
- File storage provider for attachments is not chosen (decision due in B-15).
- Applicable privacy and veterinary record-keeping regulations depend on the deployment jurisdiction and must be
  reviewed before production (README "Important").

## 3. Functional requirements

### 3.1 Authentication (FR-AUTH) — B-03
| ID | Requirement | Status |
|---|---|---|
| FR-AUTH-1 | A user signs in with email and password. Email matching ignores case and surrounding spaces. | Done |
| FR-AUTH-2 | Every sign-in failure (unknown email, wrong password, locked, disabled user, suspended clinic) returns the same response, and the response time must not reveal which. | Done |
| FR-AUTH-3 | 5 consecutive wrong passwords lock the account for 15 minutes; a successful sign-in resets the count. Concurrent guesses must not bypass the lock. | Done |
| FR-AUTH-4 | Sign-in attempts are limited to 10 per minute per client address (IPv6 grouped by /64); excess requests get `429` with `Retry-After`. | Done |
| FR-AUTH-5 | A session ends after 2 hours idle, 12 hours after sign-in, on logout, or immediately when the user is disabled or the clinic suspended. | Done |
| FR-AUTH-6 | Logout revokes the session server-side; a replayed cookie is rejected. | Done |
| FR-AUTH-7 | Signing in again from the same browser replaces the previous session. | Done |
| FR-AUTH-8 | A signed-in user can see their identity, clinic, roles and permissions (`GET /api/auth/me`). | Done |
| FR-AUTH-9 | A signed-in user can change their password by giving the current one; other sessions are signed out, the current one is kept. Wrong current passwords count toward lockout; the locking attempt signs out every session. | Done |
| FR-AUTH-10 | Administrator-initiated password reset and "must change password at next sign-in". | Open (needs B-25 for email) |
| FR-AUTH-11 | Self-service "forgot password" by email. | Open (needs B-25) |
| FR-AUTH-12 | Multi-factor authentication. | Open (future) |

### 3.2 Users, roles and permissions (FR-USR) — B-04
| ID | Requirement | Status |
|---|---|---|
| FR-USR-1 | An operator can create a clinic and its first administrator from the command line; the password is never passed as an argument. | Done |
| FR-USR-2 | Users with `users.read` can list the clinic's users with search (name/email), filter (status, role), sort and pagination, and view one user. | Done |
| FR-USR-3 | Users with `users.manage` can create users (name, email, optional phone, initial password, at least one role). | Done |
| FR-USR-4 | Users with `users.manage` can update name, email, phone and status, and replace a user's roles. | Done |
| FR-USR-5 | Disabling a user ends all their sessions and blocks sign-in; re-enabling does not revive old sessions. | Done |
| FR-USR-6 | A clinic always keeps at least one active administrator; an administrator cannot disable themselves. Concurrent changes must not violate this. | Done |
| FR-USR-7 | Role and status changes apply to the affected user's next request. | Done |
| FR-USR-8 | Permissions are granted to roles in code; each milestone adds the permissions it needs. | Done (mechanism) |
| FR-USR-9 | Clinics can define custom roles. | Open (not planned; ADR-004) |

### 3.3 Clients (FR-CLI) — B-05, F-04
| ID | Requirement | Status |
|---|---|---|
| FR-CLI-1 | Authorised staff can create, view, update and deactivate clients (name, email, phone, address, preferred contact method, notes, status). | Done |
| FR-CLI-2 | Client lists support search, filtering, sorting and pagination (`API.md` sections 5 and 13). | Done |
| FR-CLI-3 | Clients are never physically deleted while they have patients, appointments, clinical or financial history. | Done (no delete endpoint; references from patients etc. are added with RESTRICT from B-06) |

### 3.4 Patients (FR-PAT) — B-06, B-07, F-05, F-06
| ID | Requirement | Status |
|---|---|---|
| FR-PAT-1 | Authorised staff can create, view and update patients (name, species, breed, sex, reproductive status, date of birth, markings, microchip, weight, status, notes). | Done |
| FR-PAT-2 | Each patient belongs to one primary client; the model must allow multiple responsible owners later. | Done (primary owner; extra owners via a later `patient_owners` table, ADR-006) |
| FR-PAT-3 | Patient lists support search (including by client and microchip), filtering, sorting and pagination. | Done |
| FR-PAT-4 | A patient history view aggregates consultations, diagnoses, treatments, prescriptions, vaccinations, attachments and billing. | Partly done: timeline API with the `PatientRegistered` entry (B-07); each clinical and billing milestone adds its entry types |

### 3.5 Scheduling (FR-SCH) — B-08..B-10, F-07..F-09
| ID | Requirement | Status |
|---|---|---|
| FR-SCH-1 | Veterinarian weekly availability and schedule exceptions (leave, closures) can be recorded. | Planned (B-08) |
| FR-SCH-2 | Appointments (patient, client, veterinarian, start, end, type, reason, notes) can be created, edited and rescheduled. | Planned (B-09) |
| FR-SCH-3 | The server rejects appointments outside availability, inside exceptions, or overlapping another appointment of the same veterinarian, including under concurrent booking. | Planned (B-09) |
| FR-SCH-4 | Appointment status follows a defined lifecycle: scheduled → checked-in → completed, or cancelled / no-show. Invalid transitions are rejected. | Planned (B-10) |
| FR-SCH-5 | Calendar day and week views, filterable by veterinarian. | Planned (F-07) |

### 3.6 Clinical records (FR-CLN) — B-11..B-15, F-10..F-13
| ID | Requirement | Status |
|---|---|---|
| FR-CLN-1 | A veterinarian can open a consultation for a patient (optionally from an appointment) and record SOAP notes. | Planned (B-11) |
| FR-CLN-2 | Completed consultations are not destructively edited; corrections preserve history (append-oriented). | Planned (B-11) |
| FR-CLN-3 | Diagnoses and treatments can be recorded against a consultation. | Planned (B-12) |
| FR-CLN-4 | Prescriptions (medication, dosage, route, frequency, duration, quantity, instructions) can be issued by authorised clinicians. | Planned (B-13) |
| FR-CLN-5 | Vaccinations (vaccine, administered date, next due date, lot, manufacturer) can be recorded with or without a consultation. | Planned (B-14) |
| FR-CLN-6 | Files (images, documents) can be attached to patients, clients or consultations, with type and size validation and private storage. | Planned (B-15) |
| FR-CLN-7 | Clinical notes can be added to a patient. | Planned (B-15) |
| FR-CLN-8 | Every clinical read and write path is permission-checked; changes are audited. | Planned |

### 3.7 Billing (FR-BIL) — B-16..B-18, F-14, F-15
| ID | Requirement | Status |
|---|---|---|
| FR-BIL-1 | Invoices with line items (services/products) can be created for a client and optionally a patient; invoice numbers are unique per clinic. | Planned (B-16) |
| FR-BIL-2 | Subtotal, discounts, tax, total and balance due are calculated by the server, using clinic tax configuration; client-supplied totals are ignored. | Planned (B-16) |
| FR-BIL-3 | Payments (amount, method, reference) can be recorded against an invoice; the balance updates atomically; overpayment rules are defined in B-17. | Planned (B-17) |
| FR-BIL-4 | Receipts can be produced; every invoice and payment change is audited. | Planned (B-18) |
| FR-BIL-5 | Issued invoices and payments are never physically deleted; corrections use adjustments or voids. | Planned |

### 3.8 Inventory (FR-INV) — B-19..B-22, F-16..F-18
| ID | Requirement | Status |
|---|---|---|
| FR-INV-1 | Products (SKU, name, category, type, unit, reorder level, active) can be managed. | Planned (B-19) |
| FR-INV-2 | Stock is held per lot with optional expiration date and location, and is changed only through recorded stock movements (receive, dispense, sell, adjust, return, expire). | Planned (B-19, B-20) |
| FR-INV-3 | Stock may not go negative unless a documented rule allows it. | Planned (B-20) |
| FR-INV-4 | Suppliers and purchase orders can be managed; receiving a purchase order creates stock movements. | Planned (B-21) |
| FR-INV-5 | Low-stock (below reorder level) and expiring-soon queries are available. | Planned (B-22) |

### 3.9 Notifications, reports, settings, audit — B-23..B-27
| ID | Requirement | Status |
|---|---|---|
| FR-RPT-1 | Operational reports (appointments, patients seen) and financial/inventory reports, scoped to the clinic and permission-checked. | Planned (B-23, B-24) |
| FR-NOT-1 | In-app notifications with per-user preferences; channel infrastructure for future email/SMS. | Planned (B-25) |
| FR-SET-1 | Administrators can manage clinic settings: legal name, contact, address, timezone, currency, tax configuration. | Planned (B-26) |
| FR-AUD-1 | The system records audit entries for authentication events, permission changes, clinical record changes, invoice/payment changes, stock adjustments, sensitive exports and configuration changes. | Done for auth, users, clients and patients; rest Planned |
| FR-AUD-2 | Authorised administrators can search the clinic's audit log by date, actor, action and entity. | Planned (B-27, F-22) |

### 3.10 Dashboard and UI (FR-UI) — F-01..F-22
| ID | Requirement | Status |
|---|---|---|
| FR-UI-1 | Primary navigation per `UI.md` section 2; items hidden when the user lacks the permission. | Planned (F-03) |
| FR-UI-2 | Dashboard shows today's and upcoming appointments, patients needing attention, outstanding balances, low-stock and expiring items, quick actions. | Planned |
| FR-UI-3 | Every async screen handles loading, empty, success, validation error, permission denied, not found and server/network error. | Planned |
| FR-UI-4 | Public marketing pages follow `SEO.md`; authenticated pages are never indexed. | Planned |

## 4. Non-functional requirements

### 4.1 Security and privacy (NFR-SEC)
| ID | Requirement | Status |
|---|---|---|
| NFR-SEC-1 | Every endpoint requires a signed-in user unless explicitly documented as anonymous. | Done |
| NFR-SEC-2 | A user of one clinic can never read or change another clinic's data; other clinics' records are reported as `404`. Enforced on both read and write paths. | Done (mechanism) |
| NFR-SEC-3 | Every unsafe request carries a session-bound CSRF token. | Done |
| NFR-SEC-4 | Session cookie is HttpOnly, Secure, SameSite=Strict, host-prefixed. | Done |
| NFR-SEC-5 | Passwords hashed with PBKDF2 (ASP.NET Core Identity hasher); policy 12–1024 characters, not the email. | Done |
| NFR-SEC-6 | Logs never contain passwords, tokens, session secrets, query strings, headers or bodies. | Done |
| NFR-SEC-7 | Audit log is append-only at database level; entries commit atomically with the change they describe. | Done |
| NFR-SEC-8 | Errors never expose stack traces or internals; unexpected errors are a generic `500`. | Done |
| NFR-SEC-9 | Uploaded files are validated (type, size), stored privately and never executed. | Planned (B-15) |
| NFR-SEC-10 | Production runs migrations and the API under separate database roles; the API role has only INSERT/SELECT on `audit_logs`. | Planned (X-05) |
| NFR-SEC-11 | HSTS outside development; secure headers reviewed before release. | Partly done (HSTS) |

### 4.2 Reliability and data integrity (NFR-REL)
| ID | Requirement | Status |
|---|---|---|
| NFR-REL-1 | Multi-record operations (booking, payments, stock movements) run in a single transaction. | Planned per milestone |
| NFR-REL-2 | Foreign keys, CHECK constraints and unique indexes enforce integrity in the database, not only in code. | Done for identity, clients and patients |
| NFR-REL-3 | Clinical, financial and audit history is not physically deleted. | Ongoing rule |
| NFR-REL-4 | Backups with a verified restore procedure; target RPO ≤ 24 h, RTO ≤ 4 h until X-05 sets final values. | Planned (X-05) |
| NFR-REL-5 | Health endpoint reports database reachability within 5 seconds. | Done |

### 4.3 Performance (NFR-PERF)
Targets for a single clinic of up to 50 concurrent staff; verified in X-04.

| ID | Requirement | Status |
|---|---|---|
| NFR-PERF-1 | 95th percentile API latency ≤ 300 ms for reads and ≤ 500 ms for writes, excluding reports and uploads. | Planned (X-04) |
| NFR-PERF-2 | List endpoints are paginated (max page size 100) and backed by indexes starting with `clinic_id`. | Done (users, clients, patients) |
| NFR-PERF-3 | Session validation costs at most one indexed read per request and one write per session per minute. | Done |

### 4.4 Usability and accessibility (NFR-UX)
| ID | Requirement | Status |
|---|---|---|
| NFR-UX-1 | WCAG 2.2 AA where practical: keyboard access, visible focus, labels, contrast, accessible errors. | Planned |
| NFR-UX-2 | Responsive on desktop, tablet and mobile; appointments and patient lookup must work on phones. | Planned |
| NFR-UX-3 | Human, clear language ("Client", "Patient"); validation shown next to the field; input kept after recoverable errors. | Planned |

### 4.5 Maintainability and operability (NFR-OPS)
| ID | Requirement | Status |
|---|---|---|
| NFR-OPS-1 | Every response carries an `X-Request-Id`, also present in error bodies and log scopes. | Done |
| NFR-OPS-2 | Structured JSON logs outside development. | Done |
| NFR-OPS-3 | Build passes with warnings as errors; automated tests cover authorization, tenant isolation and validation for every milestone. | Done (ongoing) |
| NFR-OPS-4 | A test fails if the EF model and the latest migration disagree. | Done |
| NFR-OPS-5 | Configuration via environment variables / secret store, validated at startup. | Done |
| NFR-OPS-6 | Data Protection key ring persisted and encrypted so restarts do not sign users out; forwarded headers configured behind a proxy. | Planned (X-05) |

## 5. External interface requirements
- **User interface:** `UI.md`.
- **API:** JSON over HTTPS under `/api`; contract in `API.md`, machine-readable form in `openapi.yaml`.
- **Command line:** `create-admin` (see `RUNBOOK.md`).
- **Database:** PostgreSQL; schema in `DATABASE.md`.
- **External services:** none yet. Email/SMS (B-25), file storage (B-15) and payment providers (future) will be
  added behind interfaces.

## 6. Open questions
| # | Question | Needed by |
|---|---|---|
| Q-1 | Frontend framework and component library. | F-01 |
| Q-2 | File storage provider and maximum upload size. | B-15 |
| Q-3 | Appointment slot granularity, buffer times, and whether double-booking can be overridden. | B-09 |
| Q-4 | Tax model (inclusive/exclusive, per-line rates) and rounding rules. | B-16 |
| Q-5 | Whether stock may go negative and how controlled drugs are logged. | B-20 |
| Q-6 | Email/SMS provider. | B-25 |
| Q-7 | Hosting platform, backup tooling, final RPO/RTO. | X-05 |
| Q-8 | Jurisdictional record-retention and privacy obligations. | Before production |
