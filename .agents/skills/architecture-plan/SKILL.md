---
name: architecture-plan
description: Review and reconcile the already-approved architecture and P0-P6 task roadmap of TeaPremium V2; identify implementation risks and propose minimal corrections instead of inventing a second architecture/backlog.
---

# Architecture conformance review & task sequencing

1. Read `AGENTS.md`; `docs/spec/00-README.md` DEC log; `02-architecture.md`; `03-database.md`; `schema.sql`; `06-infrastructure.md`; `07-roadmap.md`; `09-seo.md`; the rest of the relevant source specs and `docs/analysis/spec-gaps.md`.
2. Architecture is **already specified**: DEC-01 modular monolith, .NET 10, PostgreSQL 17, separate Next.js store/admin, same-origin reverse proxy. Do NOT silently replace these or create unrequested options. If evidence reveals a concrete flaw, write an `ADR-PROPOSAL` with impact and alternatives for approval.
3. The authoritative backlog is `07-roadmap.md` task IDs `P0-01` onwards. Do NOT create TASK-XXX duplicate work or a second unrelated `docs/plans/tasks.md`. Optionally create `docs/plans/task-readiness.md` mapping existing task IDs to dependencies, linked BR/FR/AC and outstanding decisions.
4. Check testability of the schema/EF migration parity; edge cases of order/payment/inventory; auth/CSRF; production costs and runtime memory; SEO requirements and external platform policy; privacy/security. Never claim SQL runs, provider permissions, legal validity or performance benchmarks without execution.
5. For each phase, identify minimum vertical slices and meaningful gates; avoid P3 decisions blocking P0/P1/P2 unless a concrete dependency requires it.
6. Produce `docs/architecture/conformance-review.md` and `docs/plans/task-readiness.md` with PROPOSED changes only. Do not edit `docs/spec/` or generate application code in this skill.
7. Finish with GO/HOLD recommendation for `P0-01`, outstanding approvals, and a single scoped next task.
