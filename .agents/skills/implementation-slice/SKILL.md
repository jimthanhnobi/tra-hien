---
name: implementation-slice
description: Implement one approved Trà Hiên V2 feature/task as a small tested vertical slice with spec traceability, negative tests, migration safety, and PR evidence. Use only after spec audit and architecture/plan approval.
---

# Implementation slice

1. Read `AGENTS.md`, relevant `docs/spec/` sources, accepted ADRs and exactly one task or tightly related task group from `docs/plans/tasks.md`.
2. Block or explicitly report missing acceptance criteria, conflicting source specs, permissions, data integrity or unresolved architectural decisions. Never fabricate a requirement.
3. Before editing explain current behavior, intended behavior, touched files, happy/bad/edge/unauthorized scenarios, tests and risk. Avoid unrelated refactors.
4. Use characterization tests where changing existing logic; write or update automated tests for required behavior, including failures, validation and authorization where applicable. Prefer red/green flow if practical. Run tests, lint, typecheck, build and security checks appropriate to changed stack and available environment.
5. Implement minimally: application/domain invariants, validation, error handling, transaction boundaries, observability and secrets handling appropriate to the task. Don't add a framework/service or database table just for future possibilities.
6. For schema changes, assess existing data, indexes, transactions, down/restore plan and deployment ordering. Never execute on production.
7. Update documentation and traceability. Provide actual commands + results and list not-run checks honestly.
8. Prepare PR with spec/task references, diff summary, screenshots only when UI actually changed and useful, test proof, risk and rollback. Do not merge or claim independently reviewed.
