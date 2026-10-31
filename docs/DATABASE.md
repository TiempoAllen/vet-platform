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
Implemented in B-11 (tables `consultations`, `consultation_addenda`; ADR-011).
- id, clinic_id (FK clinics, RESTRICT); unique (clinic_id, id) for addenda and later diagnoses, treatments and
  prescriptions
- patient_id, veterinarian_id, completed_by_user_id, created_by_user_id: composite FKs to patients / users
  (clinic_id, id), RESTRICT; appointment_id (nullable) composite FK to appointments (clinic_id, id)
- status (`InProgress`, `Completed`, `Discarded`; CHECK), started_at, completed_at, discarded_at, discard_reason:
  CHECK `ck_consultations_lifecycle` gives each status exactly its own fields (and completed_at ≥ started_at)
- subjective, objective, assessment, plan (`subjective_notes` etc. in the original list; max 10,000 each)
- vitals: weight_kg `numeric(7,3)`, temperature_c `numeric(4,1)`, heart_rate, respiratory_rate,
  body_condition_score, pain_score (each with a range CHECK)
- created_at, updated_at

Partial unique indexes: one `InProgress` consultation per (clinic_id, patient_id); one non-discarded consultation per
(clinic_id, appointment_id). Index (clinic_id, patient_id, started_at).

`consultation_addenda`: id, clinic_id, consultation_id (composite FK to consultations), author_user_id (composite FK
to users), text (max 4000, CHECK not blank), created_at. **Append-only:** triggers reject UPDATE, DELETE and
TRUNCATE, like `audit_logs`, and an INSERT unless the consultation is `Completed`. A further trigger rejects any UPDATE
or DELETE of a consultation that is no longer `InProgress`. Rolling back the migration refuses while any consultation
or addendum exists.

### Diagnosis
Implemented in B-12 (table `diagnoses`, ADR-012).
- id, clinic_id (FK clinics); consultation_id and patient_id: composite FK `(clinic_id, patient_id, consultation_id)
  → consultations (clinic_id, patient_id, id)` RESTRICT, so a diagnosis always belongs to its consultation's patient
  (consultations gained that unique key in B-12)
- name (max 200, CHECK not blank), code and code_system (max 50; CHECK: no code system without a code)
- status (`Provisional`, `Confirmed`, `RuledOut`; CHECK), is_primary (partial unique index: one primary per
  consultation), notes (max 2000), created_by_user_id (composite FK to users), created_at, updated_at
- unique (clinic_id, consultation_id, id) for treatment links

### Treatment
Implemented in B-12 (table `treatments`). `instructions`/dosage in the original list became dose, route, frequency,
duration and notes.
- id, clinic_id; consultation_id, patient_id: composite FK to consultations as above
- diagnosis_id (nullable): composite FK `(clinic_id, consultation_id, diagnosis_id) → diagnoses (clinic_id,
  consultation_id, id)`, so a link stays within the consultation
- name (max 200, CHECK not blank), type (`Procedure`, `Medication`, `Fluids`, `Other`; CHECK), dose, route,
  frequency, duration (max 100 each), notes (max 2000), created_by_user_id, created_at, updated_at

Triggers on both tables follow the consultation (reading its status `FOR SHARE`): anything while it is `InProgress`;
on a `Completed` consultation only a diagnosis's status may change (every other column, compared as a whole row, must
stay); nothing on a `Discarded` one. `TRUNCATE` is refused on both. Rolling back refuses while rows exist.

### Prescription
Implemented in B-13 (table `prescriptions`, ADR-013). `veterinarian_id` in the original list became
`prescribed_by_user_id`; `dosage` became `dose`.
- id, clinic_id (FK clinics); consultation_id, patient_id: composite FK `(clinic_id, patient_id, consultation_id) →
  consultations (clinic_id, patient_id, id)` RESTRICT, so a prescription always belongs to its consultation's patient
  (its index also serves the patient's medication list)
- diagnosis_id (nullable): composite FK `(clinic_id, consultation_id, diagnosis_id) → diagnoses (clinic_id,
  consultation_id, id)` RESTRICT; the service unlinks it before removing the diagnosis (a raw delete of a linked
  diagnosis fails)
- product_id (nullable, B-20): composite FK (clinic_id, product_id) → products; the service requires an active
  `Medication` product; frozen after sign-off with the rest of the row
- medication_name (max 200), dose, frequency (max 100 each), quantity_unit (max 50): required, CHECK not blank; route,
  duration (max 100 each), instructions (max 2000) optional
- quantity `numeric(10,3)` (CHECK > 0 and ≤ 100000), refills (CHECK 0 to 12)
- status (`Active`, `Cancelled`; CHECK); cancelled_at, cancelled_by_user_id (composite FK to users), cancel_reason (max
  500): CHECK `ck_prescriptions_lifecycle` sets them exactly when `Cancelled` (reason not blank)
- prescribed_by_user_id (composite FK to users, required), created_at, updated_at

A trigger follows the consultation (reading its status `FOR SHARE`): anything while it is `InProgress` except a
cancelled row; on a `Completed` consultation only the single step `Active` → `Cancelled`, changing nothing but the
status, the cancellation columns and `updated_at` (compared as whole rows); nothing on a `Discarded` one. `TRUNCATE` is
refused. Rolling back refuses while rows exist. The trigger also refuses any UPDATE that changes `clinic_id`,
`patient_id` or `consultation_id`, because it judges a change by the new parent only; B-13 added the same check to the
diagnoses and treatments triggers.

Indexes: one per foreign key, all leading with clinic_id: (clinic_id, patient_id, consultation_id) (also the medication
list), (clinic_id, consultation_id, diagnosis_id), (clinic_id, prescribed_by_user_id), (clinic_id,
cancelled_by_user_id).

### Vaccination
Implemented in B-14 (table `vaccinations`, ADR-014). `administered_at` / `next_due_at` in the original list became the
dates `administered_on` / `next_due_on`.
- id, clinic_id (FK clinics); patient_id: composite FK `(clinic_id, patient_id) → patients` RESTRICT
- consultation_id (nullable): composite FK `(clinic_id, patient_id, consultation_id) → consultations (clinic_id,
  patient_id, id)` RESTRICT, so a linked consultation is always the same patient's (the patient keeps its own key
  because a composite key with a null part is not checked)
- product_id (nullable, B-20): composite FK (clinic_id, product_id) → products; the service requires an active
  `Vaccine` product
- vaccine_name (max 200, CHECK not blank), manufacturer (max 100), lot_number (max 50), lot_expires_on (date), notes
  (max 2000)
- administered_on (date), next_due_on (date): CHECK `ck_vaccinations_dates` (2000-01-01 to 2100-12-31; next due after
  administered); administered_day_starts_at: the start of administered_on in the clinic's time zone when recorded
  (the patient history orders by it; not part of the API)
- administered_elsewhere (max 200), veterinarian_id (composite FK to users): CHECK `ck_vaccinations_given_by`, exactly
  one of them (a veterinarian for doses given here, a place for doses given elsewhere)
- status (`Active`, `Voided`; CHECK); voided_at, voided_by_user_id (composite FK to users), void_reason (max 500):
  CHECK `ck_vaccinations_lifecycle` sets them exactly when `Voided`
- recorded_by_user_id (composite FK to users), created_at, updated_at

Trigger `vaccinations_guard`: a new row must be `Active` and may not link a discarded consultation (status read `FOR
SHARE`, then tested; a later discard keeps the link);
the only UPDATE is `Active` → `Voided`, changing nothing but the status, the void columns and `updated_at` (compared as
whole rows); DELETE and TRUNCATE are refused. Rolling back refuses while rows exist. Indexes: (clinic_id, patient_id,
administered_on) (patient list and history; also the patient key), (clinic_id, patient_id, consultation_id), and one
per user key.

### ClinicalNote
Implemented in B-15 (table `clinical_notes`, ADR-015). `author_id` / `content` in the original list became
`author_user_id` / `text`.
- id, clinic_id (FK clinics); patient_id: composite FK `(clinic_id, patient_id) → patients` RESTRICT
- consultation_id (nullable): composite FK `(clinic_id, patient_id, consultation_id) → consultations (clinic_id,
  patient_id, id)` RESTRICT
- author_user_id (composite FK to users), text (max 4000, CHECK not blank), created_at

**Append-only:** trigger `clinical_notes_guard` refuses UPDATE and DELETE, and an INSERT linking a discarded
consultation (status read `FOR SHARE`, then tested); TRUNCATE is refused. Indexes (clinic_id, patient_id, created_at),
(clinic_id, patient_id, consultation_id) for the consultation key, and one for the author key.

## 6. Documents
### Attachment
Implemented in B-15 (table `attachments`, ADR-015). `original_filename` / `mime_type` / `uploaded_by` in the original
list became `file_name` / `content_type` / `uploaded_by_user_id`.
- id, clinic_id (FK clinics)
- owner, exactly one (CHECK `ck_attachments_owner`): client_id alone (composite FK to clients), or patient_id (composite
  FK to patients) with an optional consultation_id (composite FK `(clinic_id, patient_id, consultation_id) →
  consultations`, so a consultation file always names the consultation's patient); all RESTRICT
- storage_key (`char(32)`, CHECK lowercase hex, unique): the content's key in the file store
- file_name (max 255, CHECK not blank), content_type (CHECK `image/jpeg`, `image/png`, `image/webp`,
  `application/pdf`), size_bytes (CHECK 1 to 20,971,520), sha256 (`char(64)`, CHECK lowercase hex), description
  (max 500)
- status (`Active`, `Withdrawn`; CHECK); withdrawn_at, withdrawn_by_user_id (composite FK to users), withdraw_reason
  (max 500): CHECK `ck_attachments_lifecycle` sets them exactly when `Withdrawn`
- uploaded_by_user_id (composite FK to users), created_at, updated_at

Access is authorised through the owning record and clinic scope. Trigger `attachments_guard`: a new row must be
`Active` and may not link a discarded consultation; the only UPDATE is `Active` → `Withdrawn`, changing nothing but the
status, the withdraw columns and `updated_at` (whole-row comparison); DELETE and TRUNCATE are refused. Indexes:
(clinic_id, patient_id, created_at), (clinic_id, client_id, created_at), (clinic_id, patient_id, consultation_id),
unique storage_key, one per user key. Rolling back refuses while notes or attachments exist; the stored files are
never touched by migrations.

## 7. Billing
Implemented in B-16 (ADR-016): tax rates, invoices and their items. The clinic gained `currency` (`char(3)`, nullable,
CHECK upper-case ISO 4217 pattern; validated against the ISO 4217 list by the service), copied by each invoice.

### TaxRate
Table `tax_rates`: id, clinic_id (FK clinics), name (max 100, CHECK not blank), rate_percent `numeric(7,4)` (CHECK 0 to
100; never changed), status (`Active`, `Archived`; CHECK), created_at, updated_at. Unique (clinic_id, id) for item
references; partial unique index `ux_tax_rates_active_name` on (clinic_id, lower(name)) where active. Trigger
`tax_rates_guard`: no DELETE, no change of rate_percent (renaming and archiving are allowed); TRUNCATE refused.

### InvoiceNumberSequence
Table `invoice_number_sequences`: clinic_id (PK, FK clinics), next_number (CHECK > 0). Created on first issue and
updated in the issuing transaction, so numbers are gapless and unique per clinic.

### Invoice
Table `invoices`. `due_at` / `balance_due` in the original list became `due_on` (a date) / B-17.
- id, clinic_id (FK clinics); client_id: composite FK to clients (clinic_id, id); patient_id (nullable): composite FK to
  patients; all RESTRICT
- number (nullable; CHECK > 0; partial unique index `ux_invoices_clinic_number` on (clinic_id, number) where set)
- status (`Draft`, `Issued`, `Void`; CHECK); CHECK `ck_invoices_lifecycle`: a draft has no number, issue or void fields;
  an issued invoice has number, issued_at, due_on and issued_by_user_id; a void one also voided_at (≥ issued_at),
  voided_by_user_id and a non-blank void_reason (max 500)
- currency (`char(3)`, CHECK pattern), notes (max 2000)
- subtotal, discount_total, tax_total, total (`numeric(19,4)`): CHECK non-negative, discount_total ≤ subtotal,
  total = subtotal − discount_total + tax_total and total ≤ 1,000,000,000,000 (keeps sums far from the column's 15
  integer digits)
- issued_at, due_on, issued_by_user_id, voided_at, voided_by_user_id, void_reason, created_by_user_id (composite FKs to
  users), created_at, updated_at

Indexes: (clinic_id, client_id, created_at), (clinic_id, patient_id, created_at), (clinic_id, created_at), the number
index, one per user key. Trigger `invoices_guard`: new rows are drafts; drafts change freely and may be deleted;
Draft → Issued only with at least one line whose sums equal the stored totals; Issued → Void changing nothing but the
status, the void fields and `updated_at` (whole-row comparison); nothing else, no DELETE of a non-draft, no TRUNCATE.

### InvoiceItem
Table `invoice_items` (no DbSet; reached through the invoice). `discount` / `tax` in the original list became the
discount type and value plus computed amounts; `product_id` arrived with B-19 (services are products of type
`Service`, so there is no `service_id`).
- id, clinic_id; invoice_id: composite FK (clinic_id, invoice_id) → invoices (clinic_id, id)
- position (CHECK > 0; unique per invoice), description (max 500, CHECK not blank)
- quantity `numeric(12,3)` (CHECK > 0 and ≤ 1,000,000), unit_price `numeric(19,4)` (CHECK 0 to 1,000,000,000)
- discount_type (`None`, `Percent`, `Amount`; CHECK) with discount_value `numeric(19,4)` (CHECK: 0 for None, above 0 and
  ≤ 100 for Percent, above 0 for Amount)
- tax_rate_id: composite FK to tax_rates (clinic_id, id); tax_rate_name and tax_rate_percent: copies (CHECK 0 to 100)
- consultation_id (nullable): composite FK to consultations (clinic_id, id); the service requires the invoice's patient
- product_id (nullable, B-19): composite FK (clinic_id, product_id) → products (clinic_id, id), index
  (clinic_id, product_id); the service requires an active product when linking. Alternate key (clinic_id, id) for
  stock sales (B-20)
- net, discount, tax, total `numeric(19,4)`: CHECK non-negative, discount ≤ net, total = net − discount + tax and
  total ≤ 1,000,000,000,000
- created_at, updated_at

Trigger `invoice_items_follow_invoice`: any change only while the invoice is a draft (status read `FOR SHARE`, then
tested); a line never moves to another invoice; a billed consultation must be a non-discarded consultation of the
invoice's patient (read `FOR SHARE`, then tested); TRUNCATE refused. Rolling back the migration refuses while issued or
void invoices exist. Indexes: (clinic_id, invoice_id, position) unique, one per foreign key.

### Payment
Implemented in B-17 (table `payments`, ADR-017). `status` in the original list was dropped: payments are never voided;
refunds are their own rows. `received_by` became `received_by_user_id`.
- id, clinic_id; invoice_id: composite FK (clinic_id, invoice_id) → invoices (clinic_id, id) RESTRICT
- amount `numeric(19,4)` (CHECK > 0 and ≤ 1,000,000,000,000), method (`Cash`, `Card`, `BankTransfer`, `Cheque`, `Other`;
  CHECK), reference (max 100, CHECK not blank; never a card number, checked by the service)
- paid_at, received_by_user_id (composite FK to users), created_at, updated_at
- unique (clinic_id, invoice_id, id) for refunds; indexes (clinic_id, invoice_id, paid_at), (clinic_id,
  received_by_user_id)

Trigger `payments_guard`: INSERT only while the invoice is issued (status read `FOR SHARE`, then tested); no UPDATE
or DELETE; TRUNCATE refused.

### Refund
Table `refunds` (no DbSet; reached through the payment).
- id, clinic_id; invoice_id, payment_id: composite FK (clinic_id, invoice_id, payment_id) → payments (clinic_id,
  invoice_id, id), so a refund always names its payment's invoice
- amount (CHECK > 0 and ≤ 1,000,000,000,000), method (CHECK as above), reason (max 500, CHECK not blank; never a card
  number, checked by the service), refunded_at, refunded_by_user_id (composite FK to users), created_at, updated_at
- indexes (clinic_id, invoice_id, payment_id), (clinic_id, refunded_by_user_id)

Trigger `refunds_guard`: INSERT only while the payment's refunds stay within its amount (the payment row is locked
`FOR NO KEY UPDATE`, so concurrent refunds of one payment queue and see each other); no UPDATE or DELETE; TRUNCATE
refused.

Deferred constraint triggers `payments_match_invoice` and `refunds_match_invoice` (`ledger_matches_invoice`): at
commit, the invoice of every new payment or refund must show exactly its payments minus refunds as `amount_paid`, so a
row inserted without updating the invoice cannot be committed.

### Invoice amount paid (B-17)
`invoices.amount_paid` `numeric(19,4)` (default 0): CHECK `ck_invoices_amount_paid` (0 ≤ amount_paid ≤ total, and 0
unless the invoice is issued, so an invoice holding money cannot be voided). `invoices_guard` now also allows an issued
invoice to change **only** its amount_paid (and updated_at), and only to the sum of its payments minus refunds;
issuing requires amount_paid 0, and voiding requires the payments minus refunds to be 0 (checked on the rows, not only
on amount_paid). The service saves the payment or refund row before the invoice in the same transaction. Rolling back the migration refuses
while payments exist and restores the B-16 guard.

### Receipt
Implemented in B-18 (table `receipts`, ADR-018); numbers from `receipt_number_sequences` (clinic_id PK, next_number),
updated in the creating transaction, so gapless per clinic.
- id, clinic_id; number (CHECK > 0; unique (clinic_id, number) `ux_receipts_clinic_number`); kind (`Payment`,
  `Refund`; CHECK; refund_id set exactly for `Refund`)
- invoice_id, payment_id: composite FK (clinic_id, invoice_id, payment_id) → payments; refund_id (nullable): composite
  FK (clinic_id, payment_id, refund_id) → refunds (clinic_id, payment_id, id); unique payment_id where kind is
  `Payment`, unique refund_id where set (one receipt per payment and per refund)
- frozen copies: clinic_name, invoice_number, currency (CHECK pattern), invoice_total, amount (CHECK > 0 and ≤
  1,000,000,000,000), method (CHECK), reference, occurred_at, amount_paid_after (0 to invoice_total),
  balance_due_after (CHECK = invoice_total − amount_paid_after), issued_by_user_id (composite FK to users) and
  issued_by_name (CHECK not blank)
- created_at, updated_at; index (clinic_id, invoice_id, number)

Trigger `receipts_guard`: a new receipt must state exactly its payment's or refund's amount, method, reference and
time (source read `FOR SHARE`) and its invoice's number, currency and total, and its number must already have been
handed out by the clinic's sequence (below `next_number`); no UPDATE or DELETE; TRUNCATE refused. Refunds gained the
unique key `ak_refunds_clinic_id_payment_id_id` for the refund reference.

### Invoice bill-to block (B-18)
`invoices.bill_to_name` (max 201), `bill_to_address_line1/2`, `bill_to_city`, `bill_to_region`, `bill_to_postal_code`,
`bill_to_country`, `bill_to_patient_name`: copied from the client and patient when the invoice is issued.
`ck_invoices_lifecycle` now requires bill_to_name on issued and void invoices (and a patient name exactly when the
invoice has a patient) and none of the eight bill-to columns on drafts; the existing `invoices_guard` whole-row
comparison freezes them like every other column. The migration backfilled invoices issued before B-18 from the
clients' and patients' details **at migration time**, not at issue (the best available; a client who moved in between
shows the newer address on those old invoices). Rolling back refuses while receipts or non-draft invoices exist.

## 8. Inventory
Products, stock lots and stock movements are implemented in B-19 (ADR-019); outgoing movements, controlled drugs
and reversals in B-20 (ADR-020). Stock is derived from movements: a lot's stored quantity must equal the sum of its
movements at commit, and never goes below 0.

### Product
Table `products`.
- id, clinic_id
- sku (max 50, `case_insensitive` collation; CHECK `sku COLLATE "C" ~ '^[A-Za-z0-9][A-Za-z0-9._/-]*$'`): unique
  index `ux_products_clinic_id_sku` on (clinic_id, sku), inactive products included
- name (max 200, CHECK not blank), category (max 100, nullable, `case_insensitive`, CHECK not blank)
- type (`Medication`, `Vaccine`, `Supply`, `Retail`, `Service`; CHECK), unit (`Each`, `Tablet`, `Capsule`,
  `Millilitre`, `Litre`, `Gram`, `Kilogram`, `Dose`, `Vial`, `Ampoule`, `Bottle`, `Box`, `Pack`; CHECK)
- reorder_level `numeric(12,3)` (nullable; CHECK null for services, else 0 to 1,000,000)
- default_unit_price `numeric(19,4)` (nullable; CHECK 0 to 1,000,000,000)
- default_tax_rate_id (nullable): composite FK (clinic_id, default_tax_rate_id) → tax_rates (clinic_id, id)
- status (`Active`, `Inactive`; CHECK), created_at, updated_at
- controlled (boolean, default false, B-20; CHECK only for `Medication`)

Alternate key (clinic_id, id) for lots, movements and invoice lines. Indexes (clinic_id, name) and
(clinic_id, default_tax_rate_id). Trigger `products_guard`: no DELETE; id and clinic never change; type, unit and
controlled change only while the product has no lot (the UPDATE holds the row lock, so a lot being inserted has committed and is
seen). `products_no_truncate` refuses TRUNCATE.

### StockLot
Table `stock_lots` (the original "InventoryItem / StockLot"; unit cost moved to the movement).
- id, clinic_id; product_id: composite FK (clinic_id, product_id) → products (clinic_id, id)
- lot_number (max 50), expires_on (date, 2000-01-01 to 2100-12-31), location (max 100): each nullable; text CHECKs
  not blank; lot number and location use the `case_insensitive` collation
- quantity `numeric(15,3)` (CHECK 0 to 1,000,000,000)
- created_at, updated_at

Unique index `ux_stock_lots_identity` on (clinic_id, product_id, lot_number, expires_on, location) `NULLS NOT
DISTINCT`: one lot per identity. Alternate key (clinic_id, product_id, id) for movements. Trigger `stock_lots_guard`:
an INSERT locks the product `FOR SHARE`, then refuses a lot of a service and a medication or vaccine lot without
expiry; an UPDATE may change nothing but quantity and `updated_at`; no DELETE; TRUNCATE refused. Deferred constraint
trigger `stock_lots_match_movements`: at commit, the quantity equals the sum of the lot's movements.

### StockMovement
Table `stock_movements`. Append-only: `stock_movements_guard` refuses UPDATE and DELETE; on INSERT it locks the
product `FOR SHARE` (refusing a `Receive` into a product that is not active), then the lot `FOR NO KEY UPDATE` (so a
lot's movements queue), and checks that `balance_after` is the sum of the lot's earlier movements plus this one, the
controlled-drug reason (Dispense, Sell) and witness (Sell, Adjust, ReturnToSupplier, Expire, Reversal) rules, that
a reversal exactly undoes a non-reversal of the same lot, that a dispensed consultation is not discarded (locked
`FOR SHARE`, then tested), and that a sale's invoice line is on an issued invoice for the same product (the invoice
is locked `FOR SHARE` first).
`stock_movements_no_truncate` refuses TRUNCATE.
- id, clinic_id; product_id and stock_lot_id: composite FK (clinic_id, product_id, stock_lot_id) → stock_lots
  (clinic_id, product_id, id), so the lot is always the product's; also (clinic_id, product_id) → products
- type (`Receive`, `Dispense`, `Sell`, `Adjust`, `ReturnToSupplier`, `ReturnFromClient`, `Expire`, `Reversal`; CHECK;
  max 20)
- quantity `numeric(12,3)`: signed change of the lot (CHECK not 0, at most 1,000,000 either way, above 0 for
  `Receive` and `ReturnFromClient`, below 0 for `Dispense`, `Sell`, `ReturnToSupplier` and `Expire`)
- balance_after `numeric(15,3)` (B-20; CHECK 0 to 1,000,000,000): the lot after this movement; B-19 rows backfilled
- unit_cost `numeric(19,4)` and currency `char(3)`: both null or both set, receipts only (CHECK; cost 0 to
  1,000,000,000; ISO pattern)
- reason (max 500; CHECK not blank; required for `Adjust`, both returns, `Expire` and `Reversal`)
- patient_id (CHECK set exactly for `Dispense`): composite FK (clinic_id, patient_id) → patients; consultation_id
  (CHECK `Dispense` only, since the key is not checked without a patient): composite FK (clinic_id, patient_id,
  consultation_id) → consultations (clinic_id, patient_id, id)
- invoice_item_id (CHECK `Sell` only): composite FK (clinic_id, invoice_item_id) → invoice_items (clinic_id, id)
- reverses_movement_id (CHECK set exactly for `Reversal`): composite FK (clinic_id, stock_lot_id, reverses_movement_id)
  → stock_movements (clinic_id, stock_lot_id, id), so it is a movement of the same lot; partial unique index
  `ux_stock_movements_reverses_movement_id` (one reversal per movement)
- witness_user_id: composite FK to users (CHECK not the performer)
- reference (max 100), notes (max 500): nullable, CHECK not blank
- occurred_at, performed_by_user_id (composite FK to users), created_at, updated_at

Indexes: (clinic_id, product_id, occurred_at), `ix_stock_movements_lot_occurred_at` on (clinic_id, product_id,
stock_lot_id, occurred_at), (clinic_id, occurred_at, id) for the unfiltered list, `ix_stock_movements_register` on
(clinic_id, product_id, created_at, id) for the register, and one per foreign key (users, witness, invoice line,
(clinic_id, patient_id, consultation_id), (clinic_id, stock_lot_id, reverses_movement_id)). Alternate key
(clinic_id, stock_lot_id, id) for reversals. Deferred constraint trigger `stock_movements_match_lot`: at commit, the
lot's quantity equals the sum of its movements, so a movement without the lot's change (or the reverse) cannot commit.
The original `reference_type` / `reference_id` columns became explicit links (patient, consultation, invoice line,
reversed movement; purchase orders with B-21).

Rolling the B-19 migration back locks the inventory tables and invoice lines, then refuses while stock movements
exist or issued invoice lines link products (products, empty lots and draft-line links are dropped, as drafts are in
B-16). Rolling the B-20 migration back refuses while any movement is not a plain receipt (another type, a reason or
a witness), a product is controlled, or a vaccination or prescription links a product; it restores the B-19 guards.

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
