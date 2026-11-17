---
name: implementation-slice
description: Implement one approved P0–P6 TeaPremium V2 roadmap task as a small tested vertical slice with spec traceability, negative tests, migration safety, and PR evidence. Use only after spec audit and architecture/plan approval.
---

# Implementation slice

1. Read `AGENTS.md`, relevant `docs/spec/` sources, accepted ADRs and exactly one authoritative `P*-*` task from `docs/spec/07-roadmap.md` (plus its FR/BR/AC references). `docs/plans/` is supporting notes, not a replacement task backlog.
2. Check only unresolved decisions that actually block this task; do not hold P0-01 for pre-go-live Q1–Q8. Block on material missing acceptance criteria, conflicting source specs, data integrity or security; never fabricate a requirement.
3. Before editing explain current behavior, intended behavior, touched files, happy/bad/edge/unauthorized scenarios, tests and risk. Avoid unrelated refactors.
4. Use characterization tests where changing existing logic; write or update automated tests for required behavior, including failures, validation and authorization where applicable. Prefer red/green flow if practical. Run tests, lint, typecheck, build and security checks appropriate to changed stack and available environment.
5. Implement minimally: application/domain invariants, validation, error handling, transaction boundaries, observability and secrets handling appropriate to the task. Don't add a framework/service or database table just for future possibilities.
6. For schema changes, assess existing data, indexes, transactions, down/restore plan and deployment ordering. Never execute on production.
7. Update documentation and traceability. Provide actual commands + results and list not-run checks honestly.
8. Stop at **local G3 handoff**: provide original `P*-*` task + FR/BR/NFR/AC/DEC references, all changed/staged/untracked paths, tests/results, risk and rollback; return `READY_FOR_LOCAL_G4`. **Do not commit, push or open/update a PR** by default. Do not claim independently reviewed.
9. Only after independent G4 PASS **and explicit owner publish permission**: commit/push feature branch and open/update one PR with diff summary, actual test proof and rollback. Verify GitHub Actions at the exact published HEAD; leave merge to owner unless expressly delegated.
