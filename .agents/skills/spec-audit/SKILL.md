---
name: spec-audit
description: Audit the committed TeaPremium V2 specs (all docs/spec/*.md plus schema.sql), preserve existing FR/BR/NFR/AC/DEC/P task identifiers, cross-check risks and omissions without implementing code.
---

# Spec audit for the actual V2 document set

1. Use FULL mode for initial G1 baseline, missing audit evidence or broad spec changes: read `AGENTS.md`, `docs/spec/00-README.md`, **all** other `docs/spec/**/*.md` including `08-chatgpt-playbook.md` and `09-seo.md`, and `docs/spec/schema.sql`. Use TARGETED mode for later bounded tasks only after verifying prior audited spec SHA against current spec diffs: read affected specs, precedence/DEC context and dependencies. Never label a targeted audit as complete.
2. Record the audited git commit SHA and enumerate all source files. Treat the claims "tested SQL" and "decisions approved" in AI-generated documentation as claims to review, not proof of actual execution/user signoff.
3. Reuse original IDs: `FR-*`, `NFR-*`, `BR-*`, `AC-*`, `DEC-*`, `P*-*`. **Never invent REQ-### duplicates**. Record source path/line/heading and actual phase. `schema.sql` is the structural reference for DB and must be checked against 03/04/05/09.
4. In FULL mode, check consistency across identity/auth/CSRF, stock/concurrency, payments/orders, CRM/consent, APIs, database constraints, SEO/redirects/feed, roadmap and operations. In TARGETED mode, check affected contracts and cross-file dependencies. For SEO conflicts use DEC-21 precedence, but still log discrepancies.
5. Distinguish CONFIRMED_CONFLICT, MISSING_CONTRACT, DESIGN_RISK, EXTERNAL_VERIFICATION, and SUGGESTION. Give exact citations, impact, task(s) affected and minimal possible resolution; NEVER present a guess as a confirmed defect.
6. Separate blockers affecting the currently requested P task from unrelated future-phase/pre-go-live decisions (Q1–Q8). Do not block earlier tasks on unrelated later-phase uncertainties.
7. Propose measurable Given/When/Then AC only when required by a task, with happy/bad/boundary/security/concurrency tests; preserve existing official AC IDs.
8. In FULL mode, create/update `docs/analysis/spec-inventory.md`, `docs/analysis/spec-gaps.md`, `docs/analysis/traceability.md` (FR/BR/NFR -> P task -> AC/test). In TARGETED mode, update only changed gap/traceability entries when needed; do not rewrite full inventory needlessly. Make no changes to `docs/spec/` or application code.
9. End with evidence-backed findings, affected task IDs, audited SHA and coverage (FULL/TARGETED), and GO/HOLD for the requested task without self-approving.
