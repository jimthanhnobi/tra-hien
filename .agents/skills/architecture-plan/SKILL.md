---
name: architecture-plan
description: G2 for Trà Hiên V2 — check a task or phase against the already-approved architecture (DEC-*) and the P0–P6 roadmap, identify implementation risks and propose minimal corrections as ADR-PROPOSALs instead of inventing a second architecture or backlog.
---

# Architecture conformance and task sequencing (G2)

Shared by Codex (`$architecture-plan`) and Antigravity (name the skill or its path).

1. Read `AGENTS.md`, the applicable DEC decisions and task in `07-roadmap.md`, `docs/architecture/`, `docs/analysis/spec-gaps.md`, and only the relevant parts of `02-architecture.md`, `03-database.md`, `schema.sql`, `06-infrastructure.md` and related specs. Inspect the complete context only for an initial baseline or a material architecture change.
2. The architecture is already specified: DEC-01 modular monolith, .NET 10, PostgreSQL 17, separate Next.js store/admin, same-origin reverse proxy. Do not replace these or offer unrequested options. If evidence shows a concrete flaw, write an `ADR-PROPOSAL` in `docs/decisions/` with impact and alternatives for owner approval.
3. The only backlog is `07-roadmap.md` (`P0-01` onwards). Never create TASK-XXX items or a second tasks file. `docs/plans/task-readiness.md` may map existing IDs to dependencies, linked IDs and open decisions.
4. Check risks relevant to the task: schema/EF parity, lock order and concurrency, auth/CSRF, privacy, SEO, operations. Never claim SQL ran, provider permissions, legal validity or performance without execution.
5. Scoped task: check its dependencies and the later tasks it affects; unrelated later-phase decisions must not block it. Phase planning: identify minimum vertical slices and meaningful gates.
6. Baseline or material change: update `docs/architecture/conformance-review.md` and `docs/plans/task-readiness.md` as PROPOSED. Unchanged bounded task: reuse those records and return only a scoped assessment. Never edit `docs/spec/` or write application code here.
7. Return:

```text
TASK: <P*-*>   ARCH BASELINE: <doc + sha> reused | updated
DEPENDENCIES: <P*-* merged? evidence>
RISKS: <risk — mitigation / test>
PROPOSALS: none | ADR-PROPOSAL-<name> (owner approval needed)
RECOMMENDATION: GO | HOLD — next action: <one scoped step>
```
