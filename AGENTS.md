# Trà Hiên V2 — rules for coding agents (Codex and Antigravity)

Rebuild Trà Hiên V2 (TeaPremium) strictly from the committed specs. Old Trà Hiên code is context, never a requirement. Work as a careful engineer: inspect, plan, implement, prove, get independent review. Write working reports and issue comments in Vietnamese; keep identifiers and code idioms in their normal technical language.

## Governance map (single primary source per rule)
- `AGENTS.md` (this file): authority, general principles, and mandatory invariants.
- `docs/WORKFLOW.md`: execution sequence, gates, roles/Technical Lead lifecycle, record separation, unified handoff contract, review/remediation policy, and transition rules.
- `docs/TOOLING.md`: exact script signatures, verification coverage tokens, exit codes, host lock behavior/limitations, and operational follow-ups.
- `docs/CODEX_USAGE.md`: practical usage for Codex/Antigravity and short copy-paste prompts.
- `.agents/skills/*/SKILL.md`: task-specific execution procedures. Templates live in `docs/plans/` and `.github/pull_request_template.md`; Codex role configs in `.codex/agents/*.toml`.

## Sources, precedence, and knowledge durability
1. `docs/spec/` is the only authority for requirements, business rules, DDL, and API contracts: `00-README.md` (terms, IDs, `DEC-*`), `01`–`09` Markdown, and `schema.sql`. SEO: `09-seo.md` wins (`DEC-21`). DB shape: `schema.sql`, reconciled with `03-database.md` and the business rules.
2. Reuse `FR-*`, `BR-*`, `NFR-*`, `AC-*`, `DEC-*`, and roadmap IDs `P0-01`…`P6-*`. Never invent `REQ-###`, `TASK-XXX`, or a second roadmap. Owner-approved non-roadmap chores (governance, tooling, maintenance) stay explicitly scoped without inventing roadmap IDs.
3. Git and `docs/spec/` are the durable sources of truth. `docs/analysis/`, `docs/architecture/`, `docs/plans/`, `docs/decisions/`, and `docs/reviews/` hold audits, evidence, ADRs (decision provenance), proposals, and separately approved amendments; they never silently override `docs/spec/`.
4. Always distinguish decision states: `OWNER_APPROVED`, `IMPLEMENTED_BUT_NOT_CONFIRMED`, `PROPOSED`, `CONFLICT`, `UNRESOLVED`, and `SUPERSEDED`. Merging a PR is never implicit approval of unconfirmed business assumptions.
5. When specs disagree, never choose silently: record exact file, heading, and statements in a gap entry and ask. Any need untraceable to a spec is an `ASSUMPTION` requiring a decision, not a feature.
6. Never edit `docs/spec/` without explicit Owner direction; propose corrections as gap entries with file/line and options. `08-chatgpt-playbook.md` is legacy collaboration guidance.
7. Dated readiness snapshots (such as `docs/plans/task-readiness.md`) never replace live Git state.

## Model-independent policy
- The Owner chooses the model and thinking level directly in the UI or via Owner-controlled configuration.
- Governance does not require or recommend any specific model name, vendor, thinking level, cross-vendor review, or agent self-switching of models.
- Review independence rests on independent conversation context, read-only authority, verifiable evidence, and exact candidate identity — not model vendor.
- Keep valid technical guidance specific to Codex, Antigravity, Windows, Docker, or execution environments. Do not rewrite historical ADRs, task records, or past evidence merely to scrub model names used at the time.

## Mandatory gate invariants (procedures live in `docs/WORKFLOW.md` and skills)
- **G0 (Preflight & worktree):** Verify live Git state, approved base (`origin/main`), spec baseline, and task scope before editing any candidate. G0 and an isolated worktree are mandatory before candidate edits; do not create a worktree solely to store an unready plan. Do not assume claims in AI-written docs were executed or legally verified.
- **G1 (`spec-audit`) & G2 (`architecture-plan`) — risk-based planning:** Keep G0–G4 and Tier A/B. Small, clear-scope tasks on a valid baseline may run scoped G1/G2 inside G3; complex or high-risk tasks use separate planning; uncertain readiness uses a read-only Design Spike before G3 authorization. Never skip Full G1 when the baseline lacks sufficient grounds, and never substitute the latest commit touching `docs/spec/` for an actual audited spec SHA.
- **G3 (`implementation-slice`):** ONE Owner-approved `P*-*` task (or Owner-approved chore) per dedicated worktree and branch, with tests and migration/rollback where applicable. Stop at `READY_FOR_LOCAL_G4`. No commit, push, PR, or merge before independent G4 PASS and explicit Owner publish authorization.
- **G4 (`review-gate`):** Independent, read-only, Blind-First review of the SAME worktree (committed, staged, unstaged, and untracked files) against base, specs, decisions, and tests. Verdict (`PASS` / `CONDITIONAL` / `BLOCK`) binds to the exact candidate manifest SHA-256. Implementer self-review never counts. Test PASS is not G4 PASS; manifest MATCH is not proof that tests ran. If the worktree is unreadable, report `NOT_RUN` and request an Owner-authorized reviewable snapshot.
- **Tiers (A01):** Tier A = migration/DDL/grants, money, inventory, auth/permissions/CSRF/PII, public checkout, index/canonical/sitemap, or security/governance gates; mixed scope is A. Tier B = Admin UI on reviewed APIs, CRUD without money/stock/PII, documentation, behavior-preserving refactors. Both require independent G4, full change-set review, candidate manifest, Owner publish permission, and CI verification at the published HEAD. No hard page or line limits; judge length by necessity and usability, and never omit required evidence.
- **Immutable record vs Dynamic status:** Finalize any tracked Immutable Implementation Record before freezing the candidate manifest; never put self-referencing manifest SHA-256, unpublished commit SHA, future CI results, or merge status inside a tracked candidate file, and never edit a tracked record after G4 while reusing the old verdict. Keep Dynamic Task Status (verdicts, manifest digest, finding closures, published HEAD, PR URL, CI/merge state, live blockers) in PR bodies, handoffs, artifacts, or dated readiness snapshots.
- **Publish (`publish-pr`) & Base integration (`base-integration`):** Publish only after independent G4 PASS and explicit Owner authorization in the current session, passing all four checkpoints (pre-stage, post-stage/pre-commit, post-commit/pre-push, post-push), one PR per task, never force-pushing, and verifying all four GitHub Actions jobs at the published SHA. Owner merges manually unless explicitly delegating a specific merge. If `origin/main` moved, integrate base and run required review before publishing.

## Engineering, verification, and evidence invariants
- Read the task's specs, approved decisions, code, and tests before editing. Make minimal changes; no speculative framework, microservice, queue, CMS, vendor, or data model.
- Validate untrusted input, enforce least privilege, protect admin operations, and sanitize rich content. Raise Vietnamese privacy/e-commerce/legal questions for professional verification; never invent compliance conclusions.
- SEO work: test crawlability, canonical URL, metadata, redirects, sitemap, structured data, indexability, and SSR.
- DB changes: forward migration plus Down/recovery plan; never edit old migrations; never run destructive changes against real data or touch production.
- Do not break external APIs, auth flows, or analytics measurement.
- Protect secrets and PII everywhere (code, logs, audit, fixtures, test data, artifacts, prompts, chat). Never commit `.env`, tokens, credentials, real customer datasets, or DB backups.
- **Verification & host lock:** Run local verification and API generation through `scripts/verify-linux.ps1` and fingerprint candidates with `scripts/candidate-manifest.ps1` (see `docs/TOOLING.md`). Always check `RESULT`, `coverage`, `manifest_sha256`, and `exit_code` together. Filtered runs (`-Filter` -> `RESULT=FOCUSED_PASS`) never replace full candidate verification and cannot be combined with `-Manifest`. `-Mode docs` applies ONLY to `README.md` and `docs/**/*.md`. Local `FULL_ALL` does not prove GitHub Actions or Docker regression builds ran. Never set `TRA_HIEN_TEST_LOCK_PATH` in actual candidate verification, never kill or remove another agent's workload, and never retry infinitely.
- **Evidence discipline:** Use standard labels: `VERIFIED`, `REPORTED`, `UNKNOWN`, `NOT_RUN`, `NOT_APPLICABLE`. Never fabricate logs, test counts, coverage, or approval; never hide meaningful failed runs or retries; never state a definitive root cause when diagnostics are insufficient.
- Edit repository files directly with tools; return one concise Vietnamese handoff report per step without pasting full files, specs, or raw logs.

## Review severity
`P0` data loss, secret exposure, privilege bypass, clearly unsafe operation · `P1` broken required behavior, schema integrity, security defect, missing critical test · `P2` maintainability, edge case, test quality, observability, performance, accessibility · `P3` cosmetic or optional. Findings cite `path:line`, reproduction, consequence, and a minimal fix; never bury blockers under praise, and never invent findings when no real defect exists.

## Orchestration and session start
- Shared skills in `.agents/skills/`: `spec-audit`, `architecture-plan`, `implementation-slice`, `review-gate`, `base-integration`, `publish-pr`.
- One main thread per task; invoking a skill does not require a subagent. Use subagents only for a real task-relevant gap or independent workstream with exclusive file ownership.
- **One writer per worktree.** Parallel work across isolated worktrees (`scripts/new-task-worktree.ps1`) is allowed when dependencies, shared contracts, and hot files do not conflict; G4 reads the candidate worktree read-only.
- New sessions: read `docs/WORKFLOW.md` (and `docs/TOOLING.md` / `docs/CODEX_USAGE.md` as needed) before starting a gate.
