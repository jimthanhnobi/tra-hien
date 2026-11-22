---
name: implementation-slice
description: G3 for Trà Hiên V2 (TeaPremium) — implement exactly one owner-approved P0–P6 roadmap task as a small tested vertical slice in its own worktree, then stop at READY_FOR_LOCAL_G4. Also used for focused remediation of G4 findings on the same worktree.
---

# Implementation slice (G3)

Shared by Codex (`$implementation-slice`) and Antigravity (name the skill or its path).

## 1. Start
- Use a dedicated worktree per task: `./scripts/new-task-worktree.ps1 -Task <p1-10> -Branch feature/<p1-10-short-name>`. One writer tool per worktree; never let Codex and Antigravity write the same worktree concurrently.
- Record the base (`origin/main` SHA). Never `reset --hard` or `clean -fd`; preserve local edits.
- Read `AGENTS.md`, the task row in `docs/spec/07-roadmap.md`, its FR/BR/NFR/AC/DEC, accepted ADRs, then only the relevant spec sections and existing code/tests. `docs/plans/` is evidence, not a backlog.

## 2. Scoped readiness (G1/G2, short)
- Locate the verified baselines first: the audited spec SHA (full G1 in `docs/analysis/spec-inventory.md`, later targeted records in `docs/plans/task-readiness.md` and recent task records) and the approved G2 records (`docs/architecture/conformance-review.md`, `docs/plans/task-readiness.md`). If either baseline is missing or cannot be verified, or the spec diff since the audited SHA has broad impact, STOP: the owner must run `spec-audit` FULL and/or `architecture-plan` at baseline first. No GO without verified G1/G2 baselines.
- Otherwise diff `docs/spec/` against the audited SHA and note task-relevant changes only.
- Confirm dependencies are merged. Block only on task-relevant MISSING_CONTRACT, CONFIRMED_CONFLICT, data-integrity or security gaps: STOP and return file:line, 2–3 options and a recommendation. Never invent a table, endpoint, setting, vendor or requirement; label anything untraceable `ASSUMPTION` and ask.
- Write the outcome (audited SHA, G2 record, GO or GAP) in the task record's Readiness line. Do not create extra readiness documents.

## 3. Implement
- Before editing, state current vs intended behavior, touched files, and happy/bad/boundary/unauthorized/concurrency cases.
- RED → GREEN where practical; characterization tests before changing existing logic.
- Minimal change: invariants, validation, error mapping, transaction boundaries, permissions, observability, secret/PII handling. No speculative framework, table or service.
- Schema change: new forward migration (never edit old ones) with Down/recovery, grants for `tp_app`/`tp_readonly`, data impact and deploy order. Never touch production.
- API change: regenerate with `./scripts/verify-linux.ps1 -Mode api -WriteBack`. Never hand-edit or hand-merge `openapi.json`/`schema.ts`.

## 4. Verify (real commands only)
- While working: focused runs, e.g. `./scripts/verify-linux.ps1 -Filter "FullyQualifiedName~Inventory"`.
- Final candidate: after the last edit write the manifest (§5), then always one full `./scripts/verify-linux.ps1 -Manifest artifacts/<task>/candidate.manifest` (= CI backend job, about 4 minutes; the log records the manifest SHA-256); add `-Mode frontend` when `apps/` or `packages/` changed; run `git diff --check`. Do not replace the final full run with hash-based evidence reuse.
- Docker and git writes need sandbox bypass/approval in sandboxed agents. Report anything not run as NOT RUN.

## 5. Evidence (right-sized)
- One record `docs/plans/<task>-implementation.md` from `docs/plans/task-template.md`: Tier A ≤ 2 pages, Tier B ≤ 1 page. Link logs in `artifacts/verify-linux/` (git-ignored) instead of pasting output; no repeated spec text.
- Content fingerprint for **both tiers**: after the last edit run `./scripts/candidate-manifest.ps1 -Write -Base <base SHA> -Out artifacts/<task>/candidate.manifest` (git mode + blob ID per path; refuses secret-like files), run the final verification with `-Manifest`, and quote the manifest SHA-256. Rewrite it after every remediation or base integration; never edit files after writing it.

## 6. Handoff — then STOP
No commit, push, PR or merge; publishing is `publish-pr` after G4 PASS and explicit owner permission. Return exactly:

```text
TASK: <P*-*> — <roadmap title>        TIER: A | B
WORKTREE / BRANCH / BASE: <path> / <branch> / <origin/main SHA>
READINESS: GO | GAP (<file:line>, options, recommendation)
TRACE: <FR/BR/NFR/AC/DEC> -> <files / tests>
CHANGED: <tracked M/A/D>   UNTRACKED: <paths>   STAGED: none
MANIFEST: artifacts/<task>/candidate.manifest sha256=<digest> (<n> entries)
MIGRATION / ROLLBACK: none | <migration id> + Down/recovery
TESTS: <command> -> PASS n/n, 0 skipped | FAIL (<log path>)
NOT RUN: <item — reason>
RISKS / FOLLOW-UPS: <short>
READY_FOR_LOCAL_G4: YES | NO
```

Remediation mode (after G4 BLOCK/CONDITIONAL): snapshot the candidate, fix only the listed findings with RED → GREEN proof, rerun focused tests and the full suite, rewrite the manifest, return the same block plus `FINDINGS: <id> FIXED | NOT FIXED (reason)`.
