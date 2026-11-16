---
name: architecture-plan
description: Build an evidence-backed architecture proposal and phased implementation plan for Trà Hiên V2 from audited specs and explicitly approved decisions. Use after spec-audit, before application scaffolding or major architecture changes.
---

# Architecture and planning

1. Read `AGENTS.md`, entire `docs/spec/`, `docs/analysis/spec-inventory.md`, `docs/analysis/spec-gaps.md`, and accepted ADRs. If missing audit or BLOCKER decisions materially impact architecture, stop with a decision list rather than pretend approval.
2. Derive quality attributes, system boundaries, user journeys, module responsibilities and data ownership from cited requirements. Prior codebases are at most optional references, not source of truth.
3. Evaluate at least two realistic implementation choices when a material design decision exists; compare complexity, security, SEO, operability, cost, growth, and team skills. Recommend the simplest design meeting requirements. Label all trade-offs and assumptions.
4. Include data entities/invariants, API contracts, auth & permission strategy, content/SEO, integration failure handling, auditability, deployment, monitoring, backup, migrations and test pyramid **where requirements justify them**.
5. Write `docs/architecture/proposal.md` with status PROPOSED; ADR proposals in `docs/decisions/` with status Proposed; `docs/plans/implementation-plan.md` and `docs/plans/tasks.md`.
6. Each task must include ID, REQ references, dependencies, acceptance tests, failure cases, and verification evidence expected. Sequence foundation, thin vertical slices, hardening, release. Flag optional or speculative features as out-of-scope.
7. Provide a milestone map with explicit gates, scope cuts, and risk-driven spikes. Never create code in this phase.
8. Ask for architecture/plan approval at the gate and do not self-approve.
