---
name: implementation-slice
description: G3 for Trà Hiên V2 (TeaPremium) — implement one Owner-approved P0–P6 roadmap task or approved governance/tooling/maintenance chore as a small tested vertical slice in its own worktree, then stop at READY_FOR_LOCAL_G4. Also used for focused remediation of G4 findings on the same worktree.
---

# Implementation slice (G3)

Shared by Codex (`$implementation-slice`) and Antigravity (name the skill or its path). Follow `AGENTS.md`, `docs/WORKFLOW.md`, and `docs/TOOLING.md`.

## 1. G0 preflight and worktree
- G0 and an isolated worktree are mandatory **before editing any candidate file**; do not create a worktree solely for an unready plan.
- Run `git fetch origin --prune`, verify `origin/main` against the approved BASE (stop and report `BASE_DRIFT` if they differ), and create the task worktree with `./scripts/new-task-worktree.ps1 -Task <slug> -Branch <prefix>/<short-name> -Base <approved-base>`.
- **One writer per worktree.** Never `reset --hard`, `clean -fd`, `git stash`, or alter local `main` / other worktrees.
- Read `AGENTS.md`, the task row in `docs/spec/07-roadmap.md` (or the Owner-authorized chore scope), linked `FR/BR/NFR/AC/DEC`, accepted ADRs, and only the relevant spec sections, code, and tests.

## 2. Risk-based scoped readiness (G1/G2)
- Small, clear-scope tasks on a valid baseline run scoped G1/G2 here. Complex/high-risk tasks or uncertain readiness should already have completed separate G1/G2 or a read-only Design Spike; if you discover missing/unverifiable G1/G2 baselines or a broad spec diff since the **Full G1 audited spec SHA** (`docs/analysis/spec-inventory.md`), STOP — never substitute the latest commit touching `docs/spec/` for an audited SHA, and never skip Full G1 when baseline grounds are insufficient.
- Verify prerequisites against live Git history (dated snapshots like `docs/plans/task-readiness.md` never override Git). Distinguish `OWNER_APPROVED` from `IMPLEMENTED_BUT_NOT_CONFIRMED`, `PROPOSED`, `CONFLICT`, and `UNRESOLVED`.
- Block only on task-relevant `MISSING_CONTRACT`, `CONFIRMED_CONFLICT`, data-integrity, or security gaps: STOP and return `file:line`, options, and a recommendation. Never invent tables, endpoints, settings, vendors, `TASK-XXX`, or `REQ-###`.

## 3. Implement
- Before editing, state current vs intended behavior, authorized touched files, and happy/bad/boundary/permission/concurrency cases (as applicable).
- Use RED → GREEN where practical; add characterization tests before altering existing logic.
- Minimal change: invariants, input validation, error mapping, transaction boundaries, permissions, observability, secret/PII protection. No speculative framework, table, or service.
- Schema change: new forward migration (never edit old ones) with Down/recovery, grants for `tp_app`/`tp_readonly`, data impact, and deploy order. Never touch production.
- API change: regenerate with `./scripts/verify-linux.ps1 -Mode api -WriteBack`; never hand-edit or hand-merge `openapi.json` / `schema.ts`.

## 4. Immutable Record and Candidate Freeze
- **Roadmap `P*-*` tasks:** write or update the **Immutable Implementation Record** `docs/plans/<task>-implementation.md` from `docs/plans/task-template.md` **before** freezing the manifest. No hard page or line limits — keep it complete, clear, and usable without pasting raw logs or full specs.
- **Owner-approved non-roadmap chores** (governance, tooling, maintenance): do not invent `TASK-XXX`/`REQ-###` or add a tracked `docs/plans/` file outside the authorized allowlist; the Unified Handoff Report and PR body carry the record and status.
- **No self-reference:** never embed the candidate's own manifest SHA-256, unpublished commit SHA, future G4 verdict, CI results, or merge status inside any tracked candidate file.
- **Freeze manifest (both tiers):** after the final edit, run:
  ```powershell
  ./scripts/candidate-manifest.ps1 -Write -Base <approved-base-sha> -Out artifacts/<slug>/candidate.manifest
  ```
  Verify the manifest (`-Verify`, `-RequireBase`, `-PublishMode initial` for unpublished branches). Never edit any candidate file after freezing without rewriting the manifest and re-running required verification.

## 5. Verify (PR-2 contracts — real commands only)
- **Focused runs during work:** `./scripts/verify-linux.ps1 -Filter "<expr>"` emits `RESULT=FOCUSED_PASS` (`coverage=FOCUSED_BACKEND|FOCUSED_ALL`; zero-skip check `NOT_RUN`). Never substitute focused runs for full candidate verification, and never combine `-Manifest` with `-Filter` (exit `2`).
- **Final manifest-bound verification (exact commands in `docs/TOOLING.md`):**
  - Backend-only or full-scope candidate: `./scripts/verify-linux.ps1 -Manifest artifacts/<slug>/candidate.manifest` (`coverage=FULL_BACKEND`) or `./scripts/verify-linux.ps1 -Mode full -Manifest artifacts/<slug>/candidate.manifest` (`coverage=FULL_ALL` when frontend/full scope applies).
  - Frontend-only candidate: `./scripts/verify-linux.ps1 -Mode frontend -Manifest artifacts/<slug>/candidate.manifest` (`coverage=FRONTEND`).
  - **Docs-only candidate ONLY** (every changed path strictly matches `scripts/ci-fastpath.py` allowlist `README.md` or `docs/**/*.md`): `./scripts/verify-linux.ps1 -Mode docs -Manifest artifacts/<slug>/candidate.manifest` (`coverage=DOCS`). **Never** use `-Mode docs` for `AGENTS.md`, `.agents/`, `.codex/`, `scripts/`, `.github/`, or `docs/spec/schema.sql`.
  - Check all four summary markers simultaneously: `RESULT=PASS`, expected `coverage`, `manifest_sha256` matching the frozen manifest, and `exit_code=0` (plus 0 skipped backend tests where applicable), plus `git diff --check`.
- **Host PG lock & reliability:** `backend`/`full` use the host PG lock (exits `5` contention, `6` residual workload, `7` lock filesystem/permission error; optional `-LockWaitSeconds`). Never set `TRA_HIEN_TEST_LOCK_PATH` in actual candidate verification, never kill/rm another agent's workload, and never retry infinitely. Record meaningful failed runs and controlled retries honestly; do not claim a definitive root cause without diagnostics.
- Local `FULL_ALL` does **not** prove GitHub Actions CI or `Docker regression builds` ran — report them as `NOT_RUN`. Re-verify manifest and `git status --short -uall` after verification to confirm the candidate stayed untouched.

## 6. Unified Handoff Report — then STOP
No commit, push, PR, or merge (that belongs to `publish-pr` after independent G4 PASS and explicit Owner authorization). Return **one** Vietnamese report following the Unified Handoff Contract:

```text
TASK / SCOPE / TIER / STATUS: <P*-* hoặc chore> / <phạm vi> / A | B / READY_FOR_LOCAL_G4 | BLOCKED
WORKTREE / BRANCH / BASE / HEAD: <path> / <branch> / <approved base SHA> / <HEAD SHA>
READINESS: GO | GAP (<audited spec SHA, G2/ADR refs hoặc file:line + options>)
TRACE: <FR/BR/NFR/AC/DEC hoặc Owner scope> -> <files / tests>
CHANGED: <tracked M/A/D> | UNTRACKED: <paths> | STAGED: none
MANIFEST: artifacts/<slug>/candidate.manifest sha256=<digest> (<n> entries, base=<sha>)
MIGRATION / ROLLBACK: none | <migration id> + Down/recovery
COVERAGE & VERIFICATION: <coverage token> | <exact command> -> RESULT=<PASS|FAIL> exit_code=<n> (<log path>)
FAILED_RUNS_AND_RETRIES: none | <failed runs, exit codes, logs, retry reason>
FINDINGS / CLOSURE: none | <[remediation] finding ID -> FIXED | NOT FIXED (evidence)>
NOT_RUN / NOT_APPLICABLE: <items + reason (e.g. GitHub Actions CI, Docker regression builds)>
BLOCKERS / RESIDUAL_RISK / NEXT: <short> | READY_FOR_LOCAL_G4: YES | NO
```

## 7. Remediation mode (after G4 `BLOCK` / `CONDITIONAL`)
- Reuse the G3 conversation when root cause is clear, context is reliable, and fix scope is bounded; start a fresh G3 conversation when severe findings recur, context degrades, or shared design/invariants need reassessment.
- Fix only the listed findings (and genuine side effects within scope) with RED → GREEN proof, update the tracked Immutable Record if needed **before** re-freezing, rewrite `candidate.manifest`, re-run full manifest-bound verification (never replace with filtered tests), and return the Unified Handoff Report above with `FINDINGS / CLOSURE` populated, then STOP.
