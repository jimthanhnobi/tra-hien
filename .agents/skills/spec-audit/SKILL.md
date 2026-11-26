---
name: spec-audit
description: G1 for Trà Hiên V2 — audit committed TeaPremium V2 specs (docs/spec/*.md plus schema.sql) in FULL or TARGETED mode, preserve existing FR/BR/NFR/AC/DEC/P*-* IDs, and report conflicts, missing contracts, and risks without implementing code.
---

# Spec audit (G1)

Shared by Codex (`$spec-audit`) and Antigravity (name the skill or its path). Follow `AGENTS.md` and `docs/WORKFLOW.md` (single-source governance). Do not create a worktree solely for a read-only audit; never edit `docs/spec/` or application code.

1. **Mode & audited SHA integrity.**
   - **FULL** is mandatory for the initial baseline, missing/unverifiable audit evidence, or broad spec changes: read `AGENTS.md`, `docs/spec/00-README.md`, every other `docs/spec/*.md` (including `08-chatgpt-playbook.md` and `09-seo.md`), and `docs/spec/schema.sql`.
   - **TARGETED** applies only to a bounded task after diffing current `docs/spec/` against the verified **Full G1 audited spec SHA**: read affected specs, `DEC-*`/precedence context, and dependencies. Never substitute the latest commit touching `docs/spec/` for an actual audited spec SHA, and never call a targeted audit complete.
2. **Evidence discipline.** Record the exact audited commit SHA, baseline diff SHA, and files covered. Treat claims inside AI-written documents (e.g. "tested SQL", "decision approved") as claims to verify (`REPORTED`/`UNKNOWN`), not proof (`VERIFIED`).
3. **Authoritative IDs & structure.** Reuse original IDs (`FR-*`, `NFR-*`, `BR-*`, `AC-*`, `DEC-*`, `P*-*`); never invent `REQ-###` or `TASK-XXX`. Cite exact `path:line` and heading. Reconcile `schema.sql` against `03`/`04`/`05`/`09`; SEO conflicts follow `DEC-21` (`09-seo.md`) and are still logged.
4. **Scope of checks.** FULL checks consistency across identity/auth/CSRF, stock/concurrency, payments/orders, CRM/consent, APIs, DB constraints, SEO/redirects/feed, roadmap, and operations. TARGETED checks affected contracts and cross-file dependencies.
5. **Finding classes & decision provenance.** Classify findings as `CONFIRMED_CONFLICT`, `MISSING_CONTRACT`, `DESIGN_RISK`, `EXTERNAL_VERIFICATION`, or `SUGGESTION` with citation, impact, affected `P*-*`, and minimal resolution. Distinguish `OWNER_APPROVED`, `IMPLEMENTED_BUT_NOT_CONFIRMED`, `PROPOSED`, `CONFLICT`, `UNRESOLVED`, and `SUPERSEDED`; never present a guess as a confirmed defect.
6. **Blocker isolation.** Separate current-task blockers from unrelated later-phase or pre-go-live questions (`Q1`–`Q8`, `G24`); do not block earlier tasks on them. Propose Given/When/Then AC only when a task needs it while preserving official AC IDs.
7. **Outputs.** When explicitly authorized to write analysis docs: FULL updates `docs/analysis/spec-inventory.md`, `spec-gaps.md`, and `traceability.md`; TARGETED updates only affected gap/traceability entries. In read-only mode, write no files.
8. **Unified Handoff Report (Vietnamese, without self-approving).** Omit unapplicable fields (such as `MANIFEST`) in read-only runs:

```text
TASK / SCOPE / TIER / STATUS: <P*-* | baseline> / G1 spec-audit (<FULL | TARGETED>) / <A | B | N/A> / <GO | HOLD>
WORKTREE / BRANCH / BASE / HEAD: <path> / <branch> / <base SHA> / <HEAD SHA>
AUDIT_BASE: audited_spec_sha=<sha> (diff from <baseline audited sha>; latest_spec_commit=<sha>) | COVERAGE: <files/sections>
CHANGED: none (read-only) | <updated analysis docs>
COVERAGE & VERIFICATION: <commands/inspections run + VERIFIED/REPORTED/UNKNOWN status>
FINDINGS / CLOSURE: [<class>] <file:line> — impact | affected P*-* | minimal resolution
TASK_BLOCKERS (<P*-*>): none | <list>
DEFERRED (not blocking): <list>
NOT_RUN / NOT_APPLICABLE: <items + reason>
BLOCKERS / RESIDUAL_RISK / NEXT: RECOMMENDATION: <GO | HOLD> — <one concrete next step>
```
