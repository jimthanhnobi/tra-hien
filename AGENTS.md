# Trà Hiên V2 - Instructions for Codex and other coding agents

## Mission
Rebuild Trà Hiên V2 based on user-provided Markdown specifications. The old implementations are NOT a source of requirements. Work as a careful engineer: inspect, plan, implement, prove, and review.

## Sources and precedence
1. `docs/spec/**/*.md` are the canonical *proposed* business specifications after they are committed.
2. Decisions explicitly approved by the user in `docs/decisions/` resolve ambiguities without silently rewriting the specification.
3. `docs/analysis/` and `docs/plans/` are AI-generated working artifacts, not new requirements.
4. Existing code describes implementation behavior, not necessarily correct business behavior.

If two specifications disagree, DO NOT choose one silently. Report the exact file, heading, and conflicting statements in a gap register. Do not treat this README or older Trà Hiên code as authority for new features.

## Hard gates
- G0: No spec? Only repository/tooling setup. Do not scaffold application modules, schema, API, UI, or integrations.
- G1: Once specs arrive, run `spec-audit`. Produce inventory, contradictions, open questions, requirement traceability, and a risk assessment.
- G2: User reviews major product ambiguities and architecture choice. Draft architecture and plan with `architecture-plan`.
- G3: Implement ONE small, reviewable vertical slice per branch/PR using `implementation-slice`. Include tests and migration safety as applicable.
- G4: Review independently using `review-gate`. Do not approve/merge changes on your own or assert unrun checks passed.

## Coding rules
- Before edits: read specs applicable to the task, existing decisions, and relevant code/tests.
- In every task record: goal; scope; spec file + heading; acceptance criteria; dependencies; test plan; rollback/operability.
- If a requirement cannot be tied to a spec, label it `ASSUMPTION` and request a decision; do not add it as a product feature.
- Prefer minimal changes; no speculative framework, microservice, queue, CMS, vendor, or data model.
- Protect secrets and PII. Never commit .env, tokens, real customer datasets, credentials, DB backups.
- Validate untrusted input; least privilege; protect admin operations; sanitize rich content when relevant; assess GDPR/local privacy obligations without guessing applicable law.
- When specs require SEO, check crawlability, canonical URL, metadata, redirects, sitemap, structured data, indexability, and SSR behavior in tests.
- DB changes require forward migration and rollback or recovery plan; never execute destructive changes against real data.
- Avoid breaking external APIs, auth flows, and analytics measurement.
- Use tests before claiming behavior works. Never fabricate logs, performance, review approval, or coverage.
- No changes to `docs/spec/` without explicit user direction. Propose corrections separately.
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
