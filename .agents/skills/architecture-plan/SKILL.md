---
name: architecture-plan
description: Review and reconcile the already-approved architecture and P0-P6 task roadmap of TeaPremium V2; identify implementation risks and propose minimal corrections instead of inventing a second architecture/backlog.
---

# Architecture conformance review & task sequencing

1. Read `AGENTS.md`, applicable DEC decisions and task in `07-roadmap.md`, existing `docs/architecture/` and `docs/analysis/spec-gaps.md`, plus relevant sections of `02-architecture.md`, `03-database.md`, `schema.sql`, `06-infrastructure.md` and related specs. For initial full conformance review or material architectural changes, inspect the complete architecture/spec context; do not re-read unrelated sections for a routine task.
2. Architecture is **already specified**: DEC-01 modular monolith, .NET 10, PostgreSQL 17, separate Next.js store/admin, same-origin reverse proxy. Do NOT silently replace these or create unrequested options. If evidence reveals a concrete flaw, write an `ADR-PROPOSAL` with impact and alternatives for approval.
3. The authoritative backlog is `07-roadmap.md` task IDs `P0-01` onwards. Do NOT create TASK-XXX duplicate work or a second unrelated `docs/plans/tasks.md`. Optionally create `docs/plans/task-readiness.md` mapping existing task IDs to dependencies, linked BR/FR/AC and outstanding decisions.
4. Check risks relevant to the assigned task (e.g., schema/EF parity, concurrency, auth/CSRF, privacy, SEO, operations). For a full architecture baseline, audit cross-cutting risks across phases. Never claim SQL runs, provider permissions, legal validity or performance benchmarks without execution.
5. For initial phase planning, identify minimum vertical slices and meaningful gates. For a scoped task, inspect its dependencies and affected later tasks; avoid unrelated later-phase decisions blocking current work.
6. On initial baseline or material architecture change, create/update `docs/architecture/conformance-review.md` and `docs/plans/task-readiness.md` with PROPOSED changes. For an unchanged bounded task, reuse those records and return a concise task-scoped assessment; do not rewrite entire documents without new findings. Never edit `docs/spec/` or generate application code in this skill.
7. Finish with GO/HOLD recommendation for the requested `P*-*` task, concrete remaining approvals, and one scoped next action.
