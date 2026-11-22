# Trà Hiên V2 — rules for coding agents (Codex and Antigravity)

Rebuild Trà Hiên V2 (TeaPremium) strictly from the committed specs. Old Trà Hiên code is context, never a requirement. Work as a careful engineer: inspect, plan, implement, prove, get independent review. Write working reports and issue comments in Vietnamese; keep identifiers and code idioms in their normal technical language.

## Sources and precedence
1. `docs/spec/` is the only authority: `00-README.md` (terms, IDs, `DEC-*`), `01`–`09` Markdown and `schema.sql`. SEO: `09-seo.md` wins (DEC-21). DB shape: `schema.sql`, reconciled with `03-database.md` and the business rules.
2. Reuse `FR-*`, `BR-*`, `NFR-*`, `AC-*`, `DEC-*` and roadmap IDs `P0-01`…`P6-*`. Never invent `REQ-###`, `TASK-XXX` or a second roadmap.
3. `docs/analysis/`, `docs/architecture/`, `docs/plans/`, `docs/decisions/` and `docs/reviews/` hold audits, evidence, proposals and separately approved amendments; they never silently override `docs/spec/`.
4. When specs disagree, never choose silently: record exact file, heading and statements in a gap entry and ask. A need that cannot be tied to a spec is an `ASSUMPTION` needing a decision, not a feature.
5. Never edit `docs/spec/` without explicit owner direction; propose corrections as gap entries with file/line and options. `08-chatgpt-playbook.md` is legacy collaboration guidance.

## Gates (procedures live in the skills and `docs/WORKFLOW.md`)
- **G0** Verify repo state, base (`origin/main`), spec version and task scope. Do not assume claims in generated specs were executed or legally verified.
- **G1** `spec-audit`: FULL at baseline or broad spec change (keep inventory/gaps/traceability with the audited SHA); otherwise TARGETED against the audited SHA for the task; rerun FULL if the baseline is missing or the impact is broad. **G2** `architecture-plan`: full at baseline or material design change; otherwise a scoped check against approved records. Before the first implementation task, and whenever G1/G2 evidence is missing, establish the baselines first. Never skip silently, never build an alternate roadmap, never let unrelated later-phase questions block current work; flag evidence-backed architecture changes for owner approval.
- **G3** `implementation-slice`: ONE owner-approved `P*-*` task per dedicated worktree and branch, with tests and migration/rollback where applicable. Stop at `READY_FOR_LOCAL_G4`. No commit, push or PR before G4 PASS and explicit owner permission.
- **G4** `review-gate`: a fresh, independent session (preferably another model vendor) reads the SAME worktree — committed, staged, unstaged and untracked — against `origin/main`, FR/BR/AC, specs and tests, and distinguishes VERIFIED from NOT RUN. Read-only (no edits, commits, push, approval or merge); verdict PASS / CONDITIONAL / BLOCK, bound to the candidate's content manifest. Implementer self-review never counts. If the worktree is not readable, report NOT RUN and request an owner-authorized reviewable snapshot.
- **Tiers (A01):** A = migration/DDL/grants, money, inventory, auth/permissions/CSRF/PII, public checkout, index/canonical/sitemap; mixed scope is A. B = Admin UI on reviewed APIs, CRUD without money/stock/PII, documentation, behavior-preserving refactors. Both need independent G4, full change-set review, owner publish permission and CI at the published HEAD; B only shortens reporting. Never omit evidence to meet a page target.
- **Publish** `publish-pr`: only after G4 PASS and the owner's explicit instruction; one PR per task; verify GitHub Actions at the published SHA; fix CI failures with focused re-review. The owner merges unless they delegate that specific merge. If `origin/main` moved: `base-integration`, then focused G4.

## Engineering rules
- Before edits, read the task's specs, decisions, code and tests. Each task record (`docs/plans/task-template.md`) lists the `P*-*` ID, linked FR/BR/NFR/AC/DEC, scope, source paths/lines, dependencies, failure cases, tests, migration and rollback.
- Minimal changes; no speculative framework, microservice, queue, CMS, vendor or data model.
- Validate untrusted input, least privilege, protect admin operations, sanitize rich content. Raise Vietnamese privacy/e-commerce/legal questions for professional verification; never invent compliance conclusions.
- SEO work: test crawlability, canonical URL, metadata, redirects, sitemap, structured data, indexability and SSR.
- DB changes: forward migration plus rollback or recovery plan; never destructive changes against real data; never run against production.
- Do not break external APIs, auth flows or analytics measurement.
- Protect secrets and PII everywhere: code, logs, audit, fixtures, test data, artifacts, prompts and chat. Never commit `.env`, tokens, credentials, real customer datasets or DB backups. Never fabricate logs, test results, coverage, performance or review approval; claim behavior only with tests.
- Edit repository files directly with the tool; do not paste full file bodies into chat unless asked.
- On completion report changed files, exact commands and results, NOT RUN items, known gaps, risk and the next safe step.

## Review severity
P0 data loss, secret exposure, privilege bypass, clearly unsafe operation · P1 broken required behavior, schema integrity, security defect, missing critical test · P2 maintainability, edge case, test quality, observability, performance, accessibility · P3 cosmetic or optional. Findings cite path:line, reproduction, consequence and a practical fix; never bury blockers under praise.

## Tools, orchestration and tokens
- Skills in `.agents/skills/` are shared by Codex (`$name`) and Antigravity (skill name or path): `spec-audit`, `architecture-plan`, `implementation-slice`, `review-gate`, `base-integration`, `publish-pr`.
- One main thread per task; invoking a skill does not require a subagent. Use subagents only for a real task-relevant gap or an independent workstream with exclusive file ownership; concurrency limits are ceilings, not targets.
- Codex and Antigravity may run in parallel only in different worktrees (`scripts/new-task-worktree.ps1`): one writer tool per worktree; G4 reads it read-only.
- Verification and API generation run through `scripts/verify-linux.ps1` (Windows blocks the OpenAPI generator); candidate content is fingerprinted with `scripts/candidate-manifest.ps1` for both tiers.
- Reuse verified G1/G2 records after checking the spec diff; reread the whole spec only for a full audit. Handoffs stay short (task ID, SHAs, paths, tests, blockers); never paste whole spec or source files unless asked.
- First run and every new session: read `docs/WORKFLOW.md` and `docs/CODEX_USAGE.md` (process and prompts for Codex and Antigravity) before starting a gate.
