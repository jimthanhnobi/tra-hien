---
name: architecture-plan
description: G2 for Trà Hiên V2 — validate baseline conformance, plan complex/high-risk tasks, or run a read-only Design Spike against approved DEC-* architecture and the P0–P6 roadmap without creating a second architecture, backlog, or premature worktree.
---

# Architecture conformance, complex planning, and Design Spike (G2)

Shared by Codex (`$architecture-plan`) and Antigravity (name the skill or its path). Follow `AGENTS.md` and `docs/WORKFLOW.md`. Never edit `docs/spec/` or write application code here. Do **not** create a worktree solely to store an unready plan or Design Spike.

1. **Choose the G2 mode (risk-based planning).**
   - **`BASELINE`:** initial architecture baseline or material design change; inspect full relevant architecture/spec context.
   - **`COMPLEX_PLAN`:** separate planning for a task with material risk in business rules, transactions, migrations/DDL, auth/PII, shared contracts, or multi-task dependencies.
   - **`DESIGN_SPIKE` (read-only):** verify readiness, technical feasibility, shared-contract impact, or options before the Owner authorizes G3 — zero repo edits and no worktree creation.
   - Routine small, clear-scope tasks on a valid baseline do not need a separate G2 session; they run scoped G1/G2 inside `implementation-slice` §2.
2. **Inputs & provenance.** Read `AGENTS.md`, applicable `DEC-*`, the task row in `docs/spec/07-roadmap.md`, `docs/architecture/`, `docs/analysis/spec-gaps.md`, relevant ADRs in `docs/decisions/`, live Git state (`origin/main` + merged dependencies), and only the relevant sections of `02-architecture.md`, `03-database.md`, `schema.sql`, `06-infrastructure.md`, and related specs. Distinguish `OWNER_APPROVED`, `IMPLEMENTED_BUT_NOT_CONFIRMED`, `PROPOSED`, `CONFLICT`, `UNRESOLVED`, and `SUPERSEDED`; dated snapshots (e.g. `docs/plans/task-readiness.md`) never override live Git state.
3. **Architecture & backlog invariants.** The architecture is already specified (`DEC-01` modular monolith, .NET 10, PostgreSQL 17, separate Next.js store/admin, same-origin reverse proxy). Do not invent alternatives or a second backlog (`TASK-XXX`/`REQ-###`). If evidence proves a concrete flaw, draft an `ADR-PROPOSAL` in `docs/decisions/` with impact and options for Owner approval; ADRs record decision provenance and never silently override `docs/spec/`.
4. **Risk & contract checks.** Inspect task-relevant risks: schema/EF parity, lock order & concurrency, auth/CSRF/PII, shared contracts & hot files (`Program.cs`, `AppDbContext`, migrations/snapshot, `openapi.json`, `schema.ts`), SEO (`DEC-21`), and operations. Never claim SQL executed, provider permissions, legal compliance, or performance without execution (`VERIFIED` vs `NOT_RUN`).
5. **Dependencies & sequencing.** Verify prerequisites against live Git history; separate current-task blockers from unrelated later-phase decisions. Identify Owner decision blockers clearly so G3 is not launched prematurely.
6. **Record updates.** Only in `BASELINE` or authorized `COMPLEX_PLAN` doc updates: update `docs/architecture/conformance-review.md` or dated snapshot entries as `PROPOSED`. In `DESIGN_SPIKE` or unchanged bounded tasks: stay read-only and reuse existing approved records.
7. **Unified Handoff Report (Vietnamese).** Omit unapplicable fields (such as `MANIFEST`) in read-only runs:

```text
TASK / SCOPE / TIER / STATUS: <P*-* | phase> / G2 <BASELINE | COMPLEX_PLAN | DESIGN_SPIKE> / <A | B> / <GO | HOLD>
WORKTREE / BRANCH / BASE / HEAD: <inspected path> / <branch> / <origin/main SHA> / <HEAD SHA>
ARCH_BASELINE: <doc + sha> reused | updated
DEPENDENCIES & SHARED_CONTRACTS: <P*-* merged? VERIFIED evidence; hot files / shared contracts>
CHANGED: none (read-only) | <updated planning/ADR files>
COVERAGE & VERIFICATION: <commands/inspections run + VERIFIED/REPORTED/UNKNOWN status>
FINDINGS / CLOSURE: <risk — mitigation / test | decision conflicts>
PROPOSALS / OWNER_BLOCKERS: none | <ADR-PROPOSAL or Owner decisions needed>
NOT_RUN / NOT_APPLICABLE: <items + reason>
BLOCKERS / RESIDUAL_RISK / NEXT: RECOMMENDATION: <GO | HOLD> — <one concrete next step>
```
