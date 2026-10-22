# Database Specification

## 1. General principles
Use a relational schema with UUID-style identifiers unless the selected stack has a documented alternative.

Every persistent entity should have, as appropriate:
- `id`
- `created_at`
- `updated_at`
- optional `deleted_at` only where soft deletion is justified
- ownership/clinic scope where multi-tenancy requires it

Do not physically delete records when doing so would destroy required clinical, financial, or audit history.

## 2. Tenant / clinic model
### Clinic
Represents a veterinary practice.

Suggested fields:
- id
- name
- legal/business name
- contact information
- address
- timezone (implemented in B-08 as `time_zone`: IANA id, default `UTC`)
- currency
- tax configuration
- settings
- status (`Active`, `Suspended`; users of a suspended clinic cannot sign in)
- created_at
- updated_at

All clinic-owned records must be scoped to the appropriate clinic.

## 3. Identity and access
### User
- id
- clinic_id
- name
- email (stored trimmed and lower-cased; unique across the platform case-insensitively, enforced by a
  case-insensitive collation; see ADR-003)
- phone
- password_hash
- status (`Active`, `Disabled`; enforced by a CHECK constraint)
- last_login_at
- failed_login_count, locked_until (temporary lockout)
- created_at
- updated_at

### UserSession
- id
- user_id
- last_seen_at
- expires_at (absolute end)
- revoked_at nullable
- created_at
- updated_at

Server-side sign-in sessions (ADR-003). Rows are kept after revocation or expiry for auditability; a retention and
cleanup policy belongs to X-05.

### Roles and permissions
Roles (`Admin`, `Veterinarian`, `Nurse`, `Receptionist`, `Inventory`) and the permissions they grant are fixed in
code (ADR-004), so there are no Role, Permission or RolePermission tables.

### UserRole
- user_id
- role (role name; CHECK constraint lists the valid names)
- created_at

Primary key (user_id, role). Foreign key to users with RESTRICT; rows are removed explicitly when roles change.

## 4. Clients and patients
### Client
Implemented in B-05 (table `clients`).
- id
- clinic_id (FK to clinics, RESTRICT)
- first_name, last_name (required; CHECK: not blank)
- email (optional, stored lower-cased (CHECK); **not** unique: households share addresses; default collation, because
  the case-insensitive collation used for users would only matter for uniqueness and breaks ILIKE before PostgreSQL 18)
- phone
- address: address_line1, address_line2, city, region (state/province/county), postal_code, country (all optional)
- preferred_contact_method (`Email`, `Phone`, `Sms`, or null for no preference; CHECK constraint)
- notes
- status (`Active`, `Inactive`; CHECK constraint)
- created_at, updated_at

Keys and indexes: unique (clinic_id, id), so child tables can use a composite foreign key (below); one index per
list sort: (clinic_id, last_name, first_name, id), (clinic_id, created_at, id), (clinic_id, updated_at, id). Clients
are never deleted.

**Rule for tables that reference a clinic-owned row (from B-06):** reference it with a composite foreign key
`(clinic_id, <parent>_id) → <parent>(clinic_id, id) ON DELETE RESTRICT`, so the database itself refuses a link to
another clinic's row (ADR-005).

### Patient
Implemented in B-06 (table `patients`).
- id
- clinic_id (FK to clinics, RESTRICT)
- client_id: the primary owner; composite FK `(clinic_id, client_id) → clients(clinic_id, id)` RESTRICT, so the owner is
  always of the same clinic
- name (required; CHECK: not blank)
- species (`Dog`, `Cat`, `Bird`, `Rabbit`, `Rodent`, `Reptile`, `Horse`, `Other`; CHECK)
- breed (free text)
- sex (`Male`, `Female`, `Unknown`; CHECK)
- reproductive_status (`Intact`, `Neutered`, `Unknown`; CHECK)
- date_of_birth (`date`), date_of_birth_estimated (boolean; CHECK: only true with a date)
- markings (colour and markings, free text)
- microchip_number (normalised: upper-case letters and digits only, CHECK; unique per clinic where not null)
- weight_kg (`numeric(7,3)`, CHECK 0 < weight ≤ 2000): current weight; dated history comes with consultation vitals
- status (`Active`, `Inactive`, `Deceased`; CHECK), date_of_death (CHECK: only with `Deceased`, not before birth)
- notes
- created_at, updated_at

Keys and indexes: unique (clinic_id, id) for later composite FKs; unique (clinic_id, microchip_number) where not null;
(clinic_id, name, id), (clinic_id, created_at, id), (clinic_id, updated_at, id) for the list sorts; (clinic_id,
client_id, name, id) for a client's patients.

A patient belongs to one primary client in the initial release. Multiple responsible owners, if required, become a
`patient_owners` table (patient, client, role) beside `client_id`, which stays the primary owner (ADR-006).

## 5. Clinical records
### Appointment
Implemented in B-09 (table `appointments`, ADR-009).
- id, clinic_id (FK clinics, RESTRICT); unique (clinic_id, id) for later composite FKs (consultations, invoices)
- patient_id, client_id, veterinarian_id, created_by_user_id: composite FKs `(clinic_id, x) → patients / clients /
  users (clinic_id, id)`, RESTRICT. client_id is the patient's owner at booking, set by the server.
- start_at, end_at (`timestamptz`; CHECK: 5-minute grid, 5 minutes to 12 hours)
- type (`appointment_type` in the original list; `Consultation`, `Vaccination`, `FollowUp`, `Surgery`, `Dental`,
  `Grooming`, `Emergency`, `Other`; CHECK)
- reason (max 500), notes (max 4000)
- status (`Scheduled`, `CheckedIn`, `Completed`, `Cancelled`, `NoShow`; CHECK; transitions in the service, B-10)
- checked_in_at, completed_at, no_show_at, cancelled_at (`timestamptz`), cancelled_by (`Client`/`Clinic`),
  cancellation_reason (max 500), status_changed_by_user_id (composite FK to users): CHECK `ck_appointments_lifecycle`
  gives every status exactly its own timestamps (e.g. `Completed` needs check-in and completion, `Scheduled` none);
  CHECK `ck_appointments_cancellation` keeps cancelled_by (`ck_appointments_cancelled_by`) and the reason to
  `Cancelled` rows; CHECK `ck_appointments_lifecycle_order` keeps completion and cancellation at or after check-in
  (B-10)
- overridden_conflicts (`text[]` of `OutsideWorkingHours`, `VeterinarianDoubleBooked`, `PatientDoubleBooked`;
  CHECK), override_reason (max 500): CHECK that the reason is set exactly when the list is non-empty
- created_at, updated_at

Exclusion constraint `ex_appointments_veterinarian_no_overlap` (extension `btree_gist`): no two `Scheduled` or
`CheckedIn` appointments of one veterinarian with overlapping `[start_at, end_at)` unless one of them carries an
override. Indexes (clinic_id, start_at, id), (clinic_id, veterinarian_id, start_at), (clinic_id, patient_id,
start_at), (clinic_id, client_id, start_at).

### Consultation
- id
- clinic_id
- appointment_id
- patient_id
- veterinarian_id
- started_at
- completed_at
- subjective_notes
- objective_notes
- assessment_notes
- plan_notes
- status

### Diagnosis
- id
- consultation_id
- code if a coding system is adopted
- name
- notes
- type/status

### Treatment
- id
- consultation_id
- name
- instructions
- dosage if applicable
- frequency if applicable
- duration if applicable
- notes

### Prescription
- id
- consultation_id
- patient_id
- veterinarian_id
- medication_name
- dosage
- route
- frequency
- duration
- quantity
- instructions
- status

### Vaccination
- id
- patient_id
- consultation_id nullable
- vaccine_name
- administered_at
- next_due_at
- lot_number nullable
- manufacturer nullable
- veterinarian_id

### ClinicalNote
- id
- patient_id
- consultation_id nullable
- author_id
- content
- created_at

## 6. Documents
### Attachment
- id
- clinic_id
- patient_id/client_id/consultation_id as appropriate
- storage_key
- original_filename
- mime_type
- size_bytes
- uploaded_by
- created_at

Access must be authorized through the owning record and clinic scope.

## 7. Billing
### Invoice
- id
- clinic_id
- client_id
- patient_id nullable
- invoice_number
- status
- issued_at
- due_at
- subtotal
- discount_total
- tax_total
- total
- balance_due

### InvoiceItem
- id
- invoice_id
- description
- quantity
- unit_price
- discount
- tax
- total
- product_id/service_id nullable

### Payment
- id
- clinic_id
- invoice_id
- amount
- method
- status
- reference
- paid_at
- received_by

## 8. Inventory
### Product
- id
- clinic_id
- sku
- name
- category
- type
- unit
- reorder_level
- active

### InventoryItem / StockLot
- id
- product_id
- lot_number nullable
- expiration_date nullable
- quantity
- unit_cost
- location nullable

### StockMovement
- id
- clinic_id
- product_id
- stock_lot_id nullable
- movement_type
- quantity
- reference_type
- reference_id
- performed_by
- created_at
- notes

Stock should be derived from auditable movements/lots rather than arbitrary direct edits wherever practical.

### Supplier
- id
- clinic_id
- name
- contact information
- status

### PurchaseOrder
- id
- clinic_id
- supplier_id
- status
- ordered_at
- received_at
- total

### PurchaseOrderItem
- id
- purchase_order_id
- product_id
- quantity
- unit_cost

## 9. Scheduling
Implemented in B-08 (ADR-008). Every time and instant is on a 5-minute grid.

### VeterinarianAvailability (table `working_hours`)
- id, clinic_id (FK clinics, RESTRICT)
- veterinarian_id: composite FK `(clinic_id, veterinarian_id) → users(clinic_id, id)` RESTRICT (users gained a
  unique `(clinic_id, id)` key in B-08)
- day_of_week (`Monday` … `Sunday`; CHECK)
- start_minute, end_minute: minutes after local midnight (CHECK: 0 ≤ start < end ≤ 1440, both multiples of 5);
  the API shows them as `HH:mm`, with 1440 as `24:00`
- effective_from (`date`), effective_to (`date`, nullable, inclusive; CHECK ≥ effective_from)
- created_at, updated_at

No overlapping blocks for one veterinarian, day and overlapping dates (at most 200 blocks per veterinarian): checked
in the service under a `FOR NO KEY UPDATE` row lock on the veterinarian's user row (an exclusion constraint would need `btree_gist`; revisit with B-09's appointment constraint).
Index (clinic_id, veterinarian_id, day_of_week, start_minute).

### ScheduleException (table `schedule_exceptions`)
- id, clinic_id (FK clinics, RESTRICT)
- veterinarian_id: nullable composite FK as above; null exactly for clinic closures (CHECK with type)
- type (`Leave`, `Sickness`, `Training`, `Other`, `Closure`; CHECK)
- starts_at, ends_at (`timestamptz`; CHECK ends_at > starts_at, both on the 5-minute grid)
- reason (max 500; may be staff health information, never copied into audit metadata)
- created_at, updated_at

Indexes (clinic_id, veterinarian_id, starts_at), (clinic_id, veterinarian_id, ends_at) and (clinic_id, starts_at,
id). Dates and instants are limited to 2000–2100 by the API.

Appointment availability must account for existing appointments and schedule exceptions.

## 10. Audit
### AuditLog
- id
- clinic_id nullable (null only when no clinic is known, e.g. a sign-in attempt for an unknown email)
- actor_user_id nullable
- action (stable names such as `auth.login_failed`, `user.roles_changed`)
- entity_type nullable
- entity_id nullable
- occurred_at (application clock)
- recorded_at (always the database clock, set by a trigger)
- metadata (jsonb)
- request_id nullable

`clinic_id` and `actor_user_id` are foreign keys with RESTRICT. Do not store secrets in audit metadata. The table is
append-only: triggers reject UPDATE, DELETE and TRUNCATE, and the migration's rollback refuses to drop a non-empty
table.
Indexed by (clinic_id, occurred_at) and (entity_type, entity_id).

## 11. Integrity requirements
- Foreign keys must enforce valid relationships.
- Clinic scoping must prevent cross-clinic access. Clinic-owned entities implement `IClinicOwned` and are filtered
  and write-guarded automatically (ADR-004).
- Appointment overlap rules must be enforced server-side.
- Financial totals must be calculated/validated server-side.
- Stock movements must preserve an auditable history.
- Clinical history should be append-oriented where practical.
- Sensitive records require authorization checks on every read/write path.

## 12. Schema evolution
Use migrations. Never modify production schema manually without a tracked migration.

Engine, naming, key, timestamp and money conventions are recorded in `ARCHITECTURE.md` ADR-002. In short: PostgreSQL;
the snake_case names in this document are the real table/column names; ids are `uuid`; timestamps are `timestamptz`
in UTC; money is `numeric(19,4)`.

Every model change ships with an EF Core migration in the same change (`dotnet ef migrations add <Name>`); a test fails
if the model and the latest migration disagree. Review generated migrations before committing: no unintended drops,
and no destructive change to clinical, financial or audit data without a documented data-preserving plan.
