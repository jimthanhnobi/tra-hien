# Task record template — authoritative roadmap IDs only

> Supporting record, not a new backlog. Use existing `P*-*` IDs from `docs/spec/07-roadmap.md` and original FR/BR/NFR/AC/DEC IDs. Never create `TASK-XXX` or `REQ-###`. Target length: Tier A ≤ 2 pages, Tier B ≤ 1 page. Link logs under `artifacts/verify-linux/` instead of pasting output; never cut acceptance, security or test evidence to fit.

## P*-*: <existing title from 07-roadmap>

- **Gate/status:** G3 IN PROGRESS | READY_FOR_LOCAL_G4 | G4 PASS/CONDITIONAL/BLOCK | PUBLISHED.
- **Tier:** A | B, with reason.
- **Worktree/base:** path, branch, `origin/main` SHA.
- **Readiness (scoped G1/G2):** audited spec SHA and approved G2 record used (or the baseline gate that was run first), spec diff since that SHA, dependencies merged, contracts confirmed, or GAP with file:line and the owner decision.
- **Traceability:** `docs/spec/...` paths/lines and headings, and `FR-*`, `BR-*`, `NFR-*`, `AC-*`, `DEC-*` → files/tests.
- **Scope / out of scope:** what changed and what belongs to later tasks.
- **Cases:** happy, bad, boundary, permission, concurrency, abuse (as applicable) → test names.
- **Migration/recovery:** none, or migration ID, Down/recovery, grants, data impact.
- **Verification:** exact commands → PASS/FAIL counts, 0 skipped, log paths; NOT RUN with reason.
- **Risks/follow-ups:** residual risk, hand-offs to later `P*-*` tasks.
- **Review/publish:** candidate manifest SHA-256 (both tiers); G4 reviewer tool/model + verdict + findings closed; PR/commit and CI at the published SHA (filled by `publish-pr`).
