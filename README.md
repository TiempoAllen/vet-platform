# Veterinary Management Platform — AI Development Documentation

This folder contains the shared source of truth for using multiple coding agents to build the veterinary management platform.

## Recommended agent split

### Backend agent
Primary owner:
- `backend/`
- `DATABASE.md`
- `API.md`
- `SECURITY.md`

### Frontend agent
Primary owner:
- `frontend/`
- `UI.md`
- `API.md`

### Both agents
Must read:
- `AGENTS.md`
- relevant sections of `PRODUCT.md`
- relevant sections of `ARCHITECTURE.md`
- current `ROADMAP.md` milestone

## Token-efficient workflow

Do not paste the entire product specification into every prompt.

Instead:
1. Tell the agent which milestone it owns.
2. Ask it to read the relevant Markdown files.
3. Have it inspect existing code.
4. Implement only that milestone.
5. Run checks.
6. Update documentation if the contract changed.

## Suggested prompts

### Backend
Read `AGENTS.md`, `PRODUCT.md`, `ARCHITECTURE.md`, `DATABASE.md`, `API.md`, `SECURITY.md`, and `ROADMAP.md`.

Implement only milestone `[B-XX]`.

Inspect existing code first. State a short plan, implement it, run relevant tests/type checks/lint, fix failures caused by the change, and summarize files changed and remaining issues.

### Frontend
Read `AGENTS.md`, `PRODUCT.md`, `ARCHITECTURE.md`, `API.md`, `UI.md`, and `ROADMAP.md`.

Implement only milestone `[F-XX]`.

Inspect existing code first. Reuse existing components, follow the API contract, implement loading/empty/error/accessibility states, run relevant checks, fix failures caused by the change, and summarize files changed and remaining issues.

## Important
These documents are an initial specification. Before production implementation, review technology choices, regulatory/privacy requirements applicable to the deployment jurisdiction, payment requirements, backup strategy, and exact business rules.
