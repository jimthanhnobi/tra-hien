# Working agreement: spec -> plan -> code -> review

## 0. Import the specification
- Clone repository and copy your real `.md` files recursively into `docs/spec/`.
- Preserve filenames, links, and directory structure. Never replace the real spec with AI summaries.
- Keep sensitive business/client information out of this PUBLIC repository or change visibility before committing.
- Create a branch `docs/import-v2-spec`; commit and open a PR for the specs.
- `docs/spec/README.md` is only a placeholder/instructions and is not a business requirement.

## 1. Spec audit (no code)
Ask Codex: "Use the spec-audit skill. Read every docs/spec/**/*.md file except the placeholder README. Inventory requirements, identify contradictions, omissions, implicit assumptions, risks, and traceability. Do not code or modify my specs."

Expected artifacts:
- `docs/analysis/spec-inventory.md`: spec source map (file, heading, requirement ID, summary).
- `docs/analysis/spec-gaps.md`: OPEN and BLOCKER questions with recommended options.
- `docs/analysis/traceability.md`: requirement -> acceptance tests -> proposed implementation.
Review this audit and record decisions. Repeat after spec updates.

## 2. Architecture and implementation plan (no code)
Ask Codex: "Use architecture-plan. Rely ONLY on the audited V2 specs, approved decisions, and documented constraints. Compare viable designs with trade-offs, prepare ADR proposals, module boundaries, data/API contracts, deployment/security/SEO considerations, milestones and vertical-slice tasks. Do not code."

Expected artifacts:
- `docs/architecture/proposal.md`
- `docs/decisions/ADR-XXXX-....md` for material choices
- `docs/plans/implementation-plan.md`
- `docs/plans/tasks.md` with IDs, dependency and acceptance criteria.

## 3. Code in slices
Ask Codex: "Use implementation-slice for TASK-XXX from docs/plans/tasks.md. Before coding show spec references, scope, negative cases and tests. Implement only the task. Run available tests and give exact outcomes. Open a PR; do not merge."

Recommended PR size: one domain capability or a narrowly-scoped cross-layer feature; avoid giant all-at-once changes. Keep change history reviewable.

## 4. Review and converge
Ask a fresh Codex review context or another reviewer: "Use review-gate on this PR. Check actual diff against specs and tasks; prioritize correctness, authorization, validation, data safety, edge cases, and missing tests. Classify P0-P3 findings, include file:line, and distinguish tested vs unverified."

Re-run real tests after fixes; review CI status, migrations and security assumptions. Human approval required to merge. If P0/P1 remains, do not merge.

## 5. Tools worth evaluating
- `github/spec-kit`: optional built-in spec/plan/task process; if installed, align its generated specs with `docs/spec/` instead of replacing the user's originals.
- `obra/superpowers`: optional structured brainstorming, TDD, execution and reviews. Avoid duplicating/conflicting instructions; project `AGENTS.md` wins for business boundaries.
- `openai/plugins`: current official examples for Codex plugins/skills.
These are third-party or optional installations, not prerequisites for this repository. Review permissions and code before using them.

## Exit criteria by phase
- Audit: all spec files listed; high-impact ambiguities flagged; no invented facts.
- Plan: requirements traced to deliverables and acceptance tests; unknowns called out; architectural choices reviewed.
- Slice: behavior + failing/passing tests documented, error/bad-case coverage, migrations assessed.
- Review: no unresolved P0/P1, CI and tests reviewed, evidence and known limitations attached.

## Local checks
`python scripts/verify_repository.py`

The repository guard validates tooling/doc structure only. It does NOT replace .NET, frontend, E2E, security, or acceptance tests after application code is introduced.
