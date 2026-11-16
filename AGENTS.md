# Trà Hiên V2 - Instructions for Codex and other coding agents

## Mission
Rebuild Trà Hiên V2 based on user-provided Markdown specifications. The old implementations are NOT a source of requirements. Work as a careful engineer: inspect, plan, implement, prove, and review.

## Sources and precedence
1. The committed **TeaPremium v2** specification set under `docs/spec/` is the source of truth: `00-README.md`, `01`–`09` Markdown, and `schema.sql`. Read `00-README.md` for terminology, spec IDs, and `DEC-*` decisions.
2. For SEO, `09-seo.md` takes precedence over conflicting SEO guidance elsewhere (DEC-21). For schema shape, `schema.sql` is the DDL target; reconcile its meaning with `03-database.md` and domain rules.
3. Existing spec IDs `FR-*`, `BR-*`, `NFR-*`, `AC-*`, `DEC-*` and task IDs `P0-01`…`P6-*` must be reused. Do NOT invent a parallel `REQ-###` scheme or new roadmap.
4. `docs/analysis/`, `docs/architecture/`, `docs/plans/`, and `docs/decisions/` contain audit notes, review proposals or separately approved amendments; they do not silently override `docs/spec/`.
5. Existing code and old Trà Hiên implementations are context only, never source requirements.

If two specifications disagree, DO NOT choose one silently. Report the exact file, heading, and conflicting statements in a gap register. Do not treat this README or older Trà Hiên code as authority for new features.

## Hard gates
- G0: V2 spec is present. Verify version, scope, and input commit; do not assume all external claims in generated specs have been executed or legally verified.
- G1: Run `$spec-audit`, read **all** specs including `00-README.md` and `schema.sql`. Produce file/ID inventory, inconsistencies, risk register, task/acceptance traceability. Never change source specs automatically.
- G2: Run `$architecture-plan` as a **conformance and feasibility review** of the ALREADY-SPECIFIED modular monolith, DEC-01..DEC-24 and `07-roadmap.md`. Do not re-design or create an alternate task numbering scheme. Flag only evidence-backed changes for user approval. Q1–Q8 in `00-README.md` are not automatically blockers for P0–P2; restrict blocking to genuinely affected tasks.
- G3: Execute ONE user-approved `P*-*` task at a time with `$implementation-slice` (start with P0-01), on a branch/PR; include tests and migrations where applicable.
- G4: Review independently using `$review-gate`, compare actual diff to FR/BR/AC and tests. Do not auto-merge or assert unrun checks passed.

## Coding rules
- Before edits: read specs applicable to the task, existing decisions, and relevant code/tests.
- In every task record: existing `P*-*` ID; linked FR/BR/NFR/AC/DEC IDs; scope; source paths/lines; dependencies; failure cases; tests; migration and rollback.
- If a requirement cannot be tied to a spec, label it `ASSUMPTION` and request a decision; do not add it as a product feature.
- Prefer minimal changes; no speculative framework, microservice, queue, CMS, vendor, or data model.
- Protect secrets and PII. Never commit .env, tokens, real customer datasets, credentials, DB backups.
- Validate untrusted input; least privilege; protect admin operations; sanitize rich content when relevant; raise applicable Vietnamese privacy/e-commerce/legal questions for professional verification, do not invent compliance conclusions.
- When specs require SEO, check crawlability, canonical URL, metadata, redirects, sitemap, structured data, indexability, and SSR behavior in tests.
- DB changes require forward migration and rollback or recovery plan; never execute destructive changes against real data.
- Avoid breaking external APIs, auth flows, and analytics measurement.
- Use tests before claiming behavior works. Never fabricate logs, performance, review approval, or coverage.
- No changes to `docs/spec/` without explicit user direction. Propose corrections as issue/gap entries with exact file/line and options. Treat `08-chatgpt-playbook.md` as legacy AI collaboration guidance: in Codex edit files directly; do not paste full file bodies into chat unless requested.
- On task completion report changed files, tests with exact command/result, known gaps, risk, next safe step.

## Skill routing
- Read / audit a batch of requirements: `.agents/skills/spec-audit/SKILL.md`
- Evaluate architecture and write phased tasks: `.agents/skills/architecture-plan/SKILL.md`
- Write a scoped feature with tests: `.agents/skills/implementation-slice/SKILL.md`
- Audit implementation and PR evidence: `.agents/skills/review-gate/SKILL.md`

## Review severity
- P0: data loss, secret exposure, privilege bypass, clearly unsafe operation.
- P1: broken required business behavior, schema integrity, security defects, missing critical tests.
- P2: maintainability, edge case, test quality, observability, performance, accessibility.
- P3: cosmetic or optional improvement.
Findings must cite paths/lines, reproduction, consequence, and practical fix. Do not bury blockers under praise.

## Operating language
Use Vietnamese in working reports and issue comments. Keep identifiers and code idioms in their normal technical language.

## First-run workflow
Read `docs/WORKFLOW.md` and `docs/CODEX_USAGE.md`. Before starting P0-01, conduct spec audit and task-scoped feasibility review; keep unresolved P3/production details from stalling unrelated P0 work.
