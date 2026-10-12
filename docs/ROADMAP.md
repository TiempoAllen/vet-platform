# Implementation Roadmap

## Working method
Each milestone should be small enough to implement, test, review, and integrate independently.

Backend milestones are prefixed `B`.
Frontend milestones are prefixed `F`.
Cross-cutting milestones are prefixed `X`.

Do not start a dependent frontend milestone until the required API contract is stable enough to consume.

## Phase 0 — Foundation
### X-01
Finalize architecture, stack, environments, coding conventions, and repository structure.

### B-01
Backend project setup, configuration, health endpoint, error handling, logging, testing foundation.

### B-02
Database setup and initial migration infrastructure.

### F-01
Frontend project setup, routing, layout, design-system foundation, API client, testing foundation.

## Phase 1 — Identity
### B-03
Authentication.

### B-04
Users, roles, permissions, authorization middleware. Also: the `AuditLog` table and audit events for authentication
(from B-03) and permission changes, a way to create the first clinic administrator, and tenant scoping from the
validated session's clinic.

### F-02
Login/logout/session handling and protected application shell.

### F-03
Role-aware navigation and user profile.

## Phase 2 — Clients and Patients
### B-05
Client CRUD, search, pagination.

### B-06
Patient CRUD, relationships, search, pagination.

### B-07
Patient history read model/API foundations.

### F-04
Client management UI.

### F-05
Patient management UI.

### F-06
Patient profile and history UI.

## Phase 3 — Scheduling
### B-08
Veterinarian availability and schedule exceptions.

### B-09
Appointment CRUD and conflict validation.

### B-10
Appointment status/check-in workflow.

### F-07
Calendar and schedule UI.

### F-08
Appointment creation/editing/rescheduling/cancellation.

### F-09
Check-in and appointment workflow UI.

## Phase 4 — Clinical
### B-11
Consultations.

### B-12
Diagnoses and treatments.

### B-13
Prescriptions.

### B-14
Vaccinations.

### B-15
Attachments and clinical notes.

### F-10
Consultation UI.

### F-11
Diagnosis/treatment UI.

### F-12
Prescription UI.

### F-13
Vaccination and attachment UI.

## Phase 5 — Billing
### B-16
Invoices and invoice items.

### B-17
Payments and balances.

### B-18
Receipts and financial audit events.

### F-14
Invoice management UI.

### F-15
Payment/receipt UI.

## Phase 6 — Inventory
### B-19
Products and stock.

### B-20
Stock movements and auditability.

### B-21
Suppliers and purchase orders.

### B-22
Low-stock and expiration queries.

### F-16
Inventory dashboard/products.

### F-17
Stock movement and receiving UI.

### F-18
Suppliers/purchasing UI.

## Phase 7 — Reporting and Notifications
### B-23
Operational reports.

### B-24
Financial/inventory reports.

### B-25
Notification infrastructure and preferences.

### F-19
Reports UI.

### F-20
Notifications UI.

## Phase 8 — Administration
### B-26
Clinic settings.

### B-27
Audit log querying.

### F-21
Settings UI.

### F-22
Audit log UI for authorized administrators.

## Phase 9 — Production hardening
### X-02
End-to-end testing of critical workflows.

### X-03
Security review and tenant-isolation verification.

### X-04
Performance review.

### X-05
Observability, backups, deployment, and production checklist. Includes from earlier milestones: Data Protection key
ring persistence, forwarded headers, separate database roles for migrations and for the API (ADR-004), session and
audit retention policies.

## Definition of milestone completion
A milestone is complete when:
- implementation matches its scope
- relevant automated tests exist/pass
- lint/type/build checks pass where applicable
- API/documentation changes are recorded
- no unrelated functionality was rewritten
- known issues are documented

## Future candidates
Potential later features:
- multi-location clinics
- client mobile app
- online booking
- reminders via email/SMS
- payment gateway integrations
- accounting integrations
- advanced analytics
- AI-assisted documentation
- telemedicine
- laboratory integrations
- pharmacy integrations
