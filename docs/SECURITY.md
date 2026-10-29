# Security and Privacy Requirements

## 1. Core principles
- Least privilege
- Defense in depth
- Secure defaults
- Explicit authorization
- Data minimization
- Auditability
- No secrets in source control

## 2. Authentication
Use a secure, established authentication approach appropriate to the chosen stack.

Requirements:
- strong password handling if passwords are used
- secure session/token management
- session expiration/rotation as appropriate
- protection against credential abuse
- secure logout
- account status checks

## 3. Authorization
Authorization must be enforced on the backend.

Every protected operation must verify:
1. identity
2. clinic/tenant scope
3. role/permission
4. resource ownership/access relationship where relevant

Never trust IDs supplied by the client simply because they belong to a valid user.

## 4. Tenant isolation
A user from Clinic A must never be able to read or mutate Clinic B data.

Apply tenant scoping consistently to:
- clients
- patients
- appointments
- clinical records
- invoices
- inventory
- attachments
- reports
- audit logs

## 5. Input validation
Validate all external input:
- request bodies
- query parameters
- path parameters
- file uploads
- imported data

Use allowlists and explicit schemas where practical.

## 6. File uploads
Validate:
- authenticated uploader
- authorization
- file type
- file size
- safe storage key
- content handling

Do not execute uploaded files. Prefer private storage with authorized access.

Since B-15 (ADR-015): the permission is checked before the body is read; only JPEG, PNG, WebP and PDF are accepted,
recognised from the leading bytes (name and declared type ignored); at most 20 MB; content is stored under a random
32-hex key outside any web root, never under the uploader's name; downloads go only through the API, always as
`attachment` with `nosniff`, a sandboxing CSP and `no-store`; the uploader's file name is reduced to a safe download
name. There is no malware scanning yet (X-05); files are never opened server-side beyond the first 12 bytes.

## 7. Sensitive information
Avoid unnecessary collection and logging of personal, clinical, and financial information.

Never log:
- passwords
- authentication tokens
- session secrets
- full payment credentials
- private keys

## 8. Web security
Protect against common application threats including:
- SQL injection
- XSS
- CSRF where applicable
- insecure direct object references
- broken access control
- SSRF where applicable
- unsafe file upload
- rate-limit abuse

Use framework/library protections rather than custom cryptography or security primitives.

## 9. Financial operations
Payment creation, refunds, invoice adjustments, and balance calculations require strong authorization and audit events.

Since B-16 (ADR-016): invoices need `billing.read` / `billing.manage`; amounts are computed only by the server (no
client-supplied totals exist in the contract); issued invoices are frozen and only voided, and the database refuses any
other change, including issuing with totals that differ from the lines; every change is audited without descriptions,
notes or reasons. Invoice numbers are gapless per clinic.

Since B-17 (ADR-017): payments need `payments.record` (Admin, Receptionist) and refunds `payments.refund` (Admin only,
as money leaves the clinic). Payments and refunds are never edited or deleted; overpayment is refused; a payment
reference that looks like a card number (13 to 19 digits passing the Luhn check) is refused, so card data is never
stored; references and refund reasons never reach the audit log.

Since B-18 (ADR-018): receipts are frozen copies, never changed or deleted (database-enforced), read with
`billing.read`; opening one is audited. The per-invoice activity trail shows audit metadata only, which never holds
free text, and names only the caller's clinic's staff.

## 10. Clinical records
Clinical record access must be permission-controlled and auditable. Avoid destructive edits where history should be preserved.

The patient history (B-07) names the staff member behind each entry to everyone with `patients.read`, as a paper
record would; only names of the caller's own clinic are ever resolved (ADR-007). Since B-11 clinical reads are audited: opening a
consultation (`consultation.viewed`), listing a patient's consultations (`consultation.listed`) and reading a history
page that contains clinical entries (`patient.history_viewed`), each with record ids but never note text. Clinical
history entries (`ConsultationCompleted`) are gated by `clinical.read` inside the history query, so callers without
it neither see nor count them. Completed and discarded consultations are frozen and corrections are append-only addenda; both
are enforced by database triggers, not only by the service (ADR-011). Diagnoses (`diagnosis.listed`) and
prescriptions (`prescription.listed`) read through a patient's lists are audited the same way. Only veterinarians
hold `prescriptions.issue`; prescriptions of completed consultations can only be cancelled, never edited, which a
database trigger enforces too (ADR-013). Audit metadata never holds medication names, instructions or reasons.
Vaccination records are never edited or deleted, only voided with a reason, enforced by a trigger (ADR-014). The
clinic-wide due list (`vaccinations.read_due`) is the one clinical-derived read open to reception: it shows patient,
owner contact, vaccine name and dates only, and every read of it is audited.

Since B-10 the history also shows finished appointments (date, type, veterinarian, actor, who cancelled; never the
cancellation reason). This relies on every role with `patients.read` also holding `appointments.read`, which is true
today (Admin, Veterinarian, Nurse, Receptionist). A role change that breaks that overlap must gate the appointment
source on `appointments.read` inside the history query (ADR-010).

## 11. Audit logging
Record important security and business actions such as:
- authentication events
- permission changes
- clinical record changes
- invoice/payment changes
- stock adjustments
- sensitive exports
- configuration changes

Do not put secrets into audit metadata.

## 12. Secrets and configuration
Use environment/secret management. Never commit production secrets.

## 13. Dependencies
Keep dependencies updated. Investigate security advisories before adding or retaining high-risk packages.

## 14. Production checklist
Before release:
- authorization tests
- tenant isolation tests
- input validation tests
- secure cookie/token configuration
- HTTPS
- rate limiting
- secure headers
- backup/recovery verification
- logging/monitoring
- error message review
- dependency audit
- secret scan
