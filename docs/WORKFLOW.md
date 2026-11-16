# Working agreement: spec -> plan -> code -> review

## 0. Import the specification
- Clone repository and copy your real `.md` files recursively into `docs/spec/`.
- Preserve filenames, links, and directory structure. Never replace the real spec with AI summaries.
- Keep sensitive business/client information out of this PUBLIC repository or change visibility before committing.
- Commit and push spec changes directly to `main` if you want, or create a docs branch/PR when peer review is useful. There is no mandatory repository validation step for spec updates.
- `docs/spec/00-README.md` is part of the provided V2 document set; analyze its actual contents rather than assuming a fixed placeholder filename.

## 1. Spec audit (no code)
Ask Codex: "Use `$spec-audit`. Read all applicable docs/spec/**/*.md files, including 00-README.md, and inspect supporting schema.sql. Inventory requirements, identify contradictions, omissions, implicit assumptions, risks, and traceability. Do not code or modify my specs."

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

Re-run actual tests after fixes, review migrations and security assumptions; review CI results only for workflows that are actually configured. Human approval is required to merge application changes. If P0/P1 remains, do not merge.

## 5. Tools worth evaluating
- `github/spec-kit`: optional built-in spec/plan/task process; if installed, align its generated specs with `docs/spec/` instead of replacing the user's originals.
- `obra/superpowers`: optional structured brainstorming, TDD, execution and reviews. Avoid duplicating/conflicting instructions; project `AGENTS.md` wins for business boundaries.
- `openai/plugins`: current official examples for Codex plugins/skills.
These are third-party or optional installations, not prerequisites for this repository. Review permissions and code before using them.

## Exit criteria by phase
- Audit: all spec files listed; high-impact ambiguities flagged; no invented facts.
- Plan: requirements traced to deliverables and acceptance tests; unknowns called out; architectural choices reviewed.
- Slice: behavior + failing/passing tests documented, error/bad-case coverage, migrations assessed.
- Review: no unresolved P0/P1; tests reviewed and, when relevant workflows exist, CI results reviewed; evidence and known limitations attached.

## Updating specifications

1. Change or replace the required files inside `docs/spec/` without moving their referenced relative paths unexpectedly.
2. Commit and push. There is no `verify_repository.py` command and no repository-guard GitHub Actions job.
3. Run `$spec-audit` when requirements change, update the gap register and traceability, and revisit any affected decisions/plans.
4. Application CI and tests, when added later, should target application correctness and security rather than block spec uploads based on hard-coded filenames.

The spec-audit and design gates are process reviews performed through Codex, **not** automated GitHub push protection.
