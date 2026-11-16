---
name: spec-audit
description: Audit the committed TeaPremium V2 specs (all docs/spec/*.md plus schema.sql), preserve existing FR/BR/NFR/AC/DEC/P task identifiers, cross-check risks and omissions without implementing code.
---

# Spec audit for the actual V2 document set

1. Read `AGENTS.md`, `docs/spec/00-README.md`, **all** other `docs/spec/**/*.md` including `08-chatgpt-playbook.md` and `09-seo.md`, and `docs/spec/schema.sql`. Do not skip 00-README.md; it contains DEC-01..DEC-24 and source precedence.
2. Record the audited git commit SHA and enumerate all source files. Treat the claims "tested SQL" and "decisions approved" in AI-generated documentation as claims to review, not proof of actual execution/user signoff.
3. Reuse original IDs: `FR-*`, `NFR-*`, `BR-*`, `AC-*`, `DEC-*`, `P*-*`. **Never invent REQ-### duplicates**. Record source path/line/heading and actual phase. `schema.sql` is the structural reference for DB and must be checked against 03/04/05/09.
4. Check consistency across identity/auth/CSRF, stock/unique piece/concurrency, payments, orders, CRM/consent, APIs, database constraints, SEO pages/redirects/feed, implementation roadmap, cost/operations. For SEO conflicts use DEC-21 precedence, but still log the discrepancy.
5. Distinguish CONFIRMED_CONFLICT, MISSING_CONTRACT, DESIGN_RISK, EXTERNAL_VERIFICATION, and SUGGESTION. Give exact citations, impact, task(s) affected and minimal possible resolution; NEVER present a guess as a confirmed defect.
6. Separate scope-critical blockers (before P0-01), task-specific blockers (before relevant P task), and pre-go-live decisions (Q1–Q8). Unrelated phase-open questions must not stop P0 foundation work.
7. Propose measurable Given/When/Then AC only when required by a task, with happy/bad/boundary/security/concurrency tests; preserve existing official AC IDs.
8. Write `docs/analysis/spec-inventory.md`, `docs/analysis/spec-gaps.md`, `docs/analysis/traceability.md` (FR/BR/NFR -> P task -> AC/test). Make no changes to `docs/spec/` or application code.
9. End with evidence-backed critical findings, exact affected tasks, audit coverage, and an explicit gate recommendation (P0-01 ready / blocked) without self-approving.
