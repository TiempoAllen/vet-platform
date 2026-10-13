# Veterinary Management Platform — Agent Instructions

## Mission
Build a production-quality veterinary practice management platform. The repository is the source of truth; do not rely on chat history for requirements.

## Repository
- `frontend/` — web application and user experience
- `backend/` — API, business logic, authentication, authorization
- `docs/` — requirements and technical source of truth

## Documentation hierarchy
- `docs/PRODUCT.md` — product scope and user workflows
- `docs/ARCHITECTURE.md` — technical architecture
- `docs/DATABASE.md` — data model and integrity rules
- `docs/API.md` — frontend/backend contract
- `docs/UI.md` — UX, navigation, design system
- `docs/SECURITY.md` — security and privacy requirements
- `docs/ROADMAP.md` — implementation milestones
- `docs/SRS.md` — numbered requirements with status (traced in `TEST_PLAN.md`)
- `docs/SDD.md` — software design; references the ADRs in `ARCHITECTURE.md`
- `docs/openapi.yaml` — machine-readable spec of implemented endpoints (`API.md` wins on conflict)
- `docs/TEST_PLAN.md` — test levels, per-milestone checklist, traceability matrix
- `docs/RUNBOOK.md` — operating, deployment and incident procedures

When documents conflict, flag the conflict and prefer the more specific/current requirement rather than silently inventing behavior.

## Engineering rules
1. Inspect existing code before changing it.
2. Implement only the requested scope.
3. Reuse existing patterns and components.
4. Keep business rules on the backend.
5. Treat authorization as a backend responsibility; frontend visibility is not security.
6. Validate external input at system boundaries.
7. Never expose secrets or privileged credentials to the frontend.
8. Do not introduce dependencies without a clear reason.
9. Prefer simple, maintainable abstractions over speculative frameworks.
10. Preserve existing behavior unless the task explicitly changes it.
11. Add or update tests for meaningful behavior.
12. Update documentation when contracts, architecture, schema, or user behavior changes: `API.md` and `openapi.yaml`
    together, the `SRS.md` status and `TEST_PLAN.md` traceability for delivered requirements, and `RUNBOOK.md` for
    operational changes.

## Frontend rules
- Follow `docs/UI.md`.
- Use accessible semantic HTML.
- Provide loading, empty, success, validation-error, and server-error states.
- Use the API contract in `docs/API.md`; do not invent endpoints.
- Avoid hardcoded production data where an API exists.
- Keep presentation concerns separate from business logic.

## Backend rules
- Follow `docs/DATABASE.md`, `docs/API.md`, and `docs/SECURITY.md`.
- Enforce authorization for every protected operation.
- Validate input and return consistent errors.
- Use transactions where required for data integrity.
- Avoid leaking sensitive information through errors or logs.
- Add tests for authorization and important business rules.

## Workflow
For a substantial task:
1. Read the relevant documentation.
2. Inspect the relevant repository code.
3. Identify the smallest set of files that should change.
4. State a short implementation plan.
5. Implement only that milestone.
6. Run relevant tests, lint, type checks, and build checks.
7. Fix regressions caused by the change.
8. Report files changed, checks run, and remaining issues.

Do not implement future roadmap milestones without explicit instruction.

## Definition of done
A task is complete only when the implementation matches the documented requirements, relevant checks pass, and no known regression introduced by the task remains.

## Project subagents
Defined in `.claude/agents/`:
- `backend-milestone-implementer` — implements one B-XX milestone following the workflow above.
- `security-tenancy-reviewer` — read-only review of authorization, tenant isolation, and sensitive-data handling.
- `api-contract-guardian` — read-only check that backend behaviour matches `docs/API.md`.
- `data-model-reviewer` — read-only review of entities and migrations against `docs/DATABASE.md` and ADR-002.

Run the security and API reviewers on every backend milestone before calling it done, and the data-model reviewer
on every milestone that changes entities or migrations.
