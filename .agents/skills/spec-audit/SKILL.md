---
name: spec-audit
description: G1 for Trà Hiên V2 — audit the committed TeaPremium V2 specs (all docs/spec/*.md plus schema.sql) in FULL or TARGETED mode, preserve existing FR/BR/NFR/AC/DEC/P task identifiers, and report conflicts, missing contracts and risks without implementing code.
---

# Spec audit (G1)

Shared by Codex (`$spec-audit`) and Antigravity (name the skill or its path).

1. **Mode.** FULL for the initial baseline, missing audit evidence or broad spec changes: read `AGENTS.md`, `docs/spec/00-README.md`, every other `docs/spec/*.md` (including `08-chatgpt-playbook.md` and `09-seo.md`) and `docs/spec/schema.sql`. TARGETED for a bounded task, only after diffing the current specs against the last audited SHA: read affected specs, DEC/precedence context and dependencies. Never call a targeted audit complete.
2. Record the audited commit SHA and the files covered. Treat claims such as "tested SQL" or "decision approved" inside AI-written documents as claims to verify, not proof.
3. Reuse original IDs (`FR-*`, `NFR-*`, `BR-*`, `AC-*`, `DEC-*`, `P*-*`); never invent `REQ-###`. Cite path, line and heading. `schema.sql` is the structural reference and must be checked against 03/04/05/09.
4. FULL: check consistency across identity/auth/CSRF, stock/concurrency, payments/orders, CRM/consent, APIs, DB constraints, SEO/redirects/feed, roadmap and operations. TARGETED: affected contracts and cross-file dependencies. SEO conflicts follow DEC-21 but are still logged.
5. Classify findings as CONFIRMED_CONFLICT, MISSING_CONTRACT, DESIGN_RISK, EXTERNAL_VERIFICATION or SUGGESTION, each with citation, impact, affected tasks and the smallest resolution. Never present a guess as a confirmed defect.
6. Separate blockers for the requested task from unrelated later-phase or pre-go-live questions (Q1–Q8); do not block earlier tasks on them.
7. Propose Given/When/Then AC only when a task needs it, covering happy/bad/boundary/security/concurrency; keep official AC IDs.
8. FULL updates `docs/analysis/spec-inventory.md`, `spec-gaps.md` and `traceability.md`. TARGETED updates only changed gap/traceability entries. Never edit `docs/spec/` or application code.
9. Return, without self-approving:

```text
AUDIT: FULL | TARGETED   SPEC SHA: <sha> (diff from <baseline sha>)   COVERAGE: <files/sections>
FINDINGS: [<class>] <file:line> — impact | affected P*-* | minimal resolution
TASK BLOCKERS (<P*-*>): none | <list>
DEFERRED (not blocking): <list>
RECOMMENDATION: GO | HOLD for <P*-*>
```
