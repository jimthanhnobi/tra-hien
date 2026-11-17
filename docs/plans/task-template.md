# Task record template — authoritative roadmap IDs only

> Supporting task record, not a new backlog. Use existing `P*-*` IDs in `docs/spec/07-roadmap.md` and original FR/BR/NFR/AC/DEC IDs. Never create `TASK-XXX` or `REQ-###`.

## P*-*: <Existing title from 07-roadmap>
- **Gate/status:** PROPOSED | G1/G2 GO | G3 IN PROGRESS | G4 PASS/CONDITIONAL/BLOCK.
- **Baseline:** `origin/main` SHA; audited spec SHA/diff.
- **Source/traceability:** relevant `docs/spec/...` headings and `FR-*`, `BR-*`, `NFR-*`, `AC-*`, `DEC-*`.
- **Dependencies:** existing P task IDs and verification evidence.
- **Current coverage:** source/tests to reuse and precise gaps.
- **Scope / out of scope:** affected files and excluded work.
- **Acceptance criteria:** Given/When/Then mapped to official specs; local IDs clearly non-authoritative.
- **Cases:** happy, bad, boundary, abuse, permission, concurrency as applicable.
- **Tests/evidence:** commands, PASS/FAIL/NOT RUN, environment, proof.
- **Migration/recovery:** impacts, rollback and operational safety.
- **Observability/security/privacy:** task-relevant risks.
- **Publish gate:** changed files, commit/PR, G4 verdict/findings, owner merge.
