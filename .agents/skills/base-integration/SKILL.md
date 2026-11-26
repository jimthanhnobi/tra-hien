---
name: base-integration
description: Bring a Trà Hiên V2 task worktree up to date with a newer origin/main without losing reviewed work — snapshot, integrate without git stash, regenerate API contracts if needed, rewrite manifest, re-verify under PR-2 contracts, and report the delta for review.
---

# Base integration

Shared by Codex (`$base-integration`) and Antigravity. Follow `AGENTS.md`, `docs/WORKFLOW.md`, and `docs/TOOLING.md`. Never `reset --hard`, `clean -fd`, `git stash`, rebase a published branch, or create a final commit here.

1. **Record current candidate state.** Note old BASE, HEAD, current candidate manifest, and its SHA-256. Run `git fetch origin --prune` (sandbox bypass/approval in sandboxed agents) and record the new `origin/main` SHA.
2. **Snapshot first.** Copy every changed and untracked candidate file to `artifacts/<slug>/pre-integration/` together with the previous manifest. Keep the snapshot until re-verification passes.
3. **Integrate without committing.**
   - **Unpublished branch with uncommitted candidate (normal before publish):** run `./scripts/task-integrate.ps1 -Task <slug> -Action start -Onto origin/main`. It never touches shared `refs/stash`: it pins the candidate as a local WIP commit under `refs/task-integration/<slug>`, rebases the unpublished branch onto `origin/main`, and undoes the WIP commit with `reset --mixed` (mode-changed entries stay staged to preserve modes on Windows). On conflicts (`exit 2`): resolve files, `git add` them, then run `./scripts/task-integrate.ps1 -Task <slug> -Action finish`. Drop the recovery ref only after step 6 passes (`-Action drop`, atomic compare-and-delete). Recovery before drop: `git restore --source=refs/task-integration/<slug> --worktree -- .`.
   - **Published branch with commits:** run `git merge --no-ff --no-commit origin/main`. Resolve conflicts and stop with `MERGE_HEAD` present; the merge commit is created only by `publish-pr` under Owner authorization. No rebase or force push.
4. **Inspect incoming delta:** `git diff <old-base> origin/main --stat`. Check hot files (`backend/src/TeaPremium.Api/Program.cs` and DI registrations, `AppDbContext`, migrations & model snapshot, `packages/api-client/openapi.json`, `src/generated/schema.ts`, Owner-approved `docs/spec/` amendments). Preserve both sides' registrations, endpoints, and amendments.
5. **Generated contracts, EF snapshot, and Immutable Record.**
   - Never hand-merge generated contracts: take either side, then run `./scripts/verify-linux.ps1 -Mode api -WriteBack`.
   - If both sides added migrations, keep IDs ordered, regenerate the model snapshot with EF tooling, and confirm `has-pending-model-changes` reports no pending changes.
   - If the candidate includes a tracked Immutable Implementation Record (`docs/plans/<task>-implementation.md`) whose approved BASE or integration notes must reflect the new base, update it **before** writing the new manifest (never embed self-referencing manifest SHA, CI, or verdict).
6. **Rewrite manifest and verify under PR-2 contracts.**
   - Write the new manifest against the new base: `./scripts/candidate-manifest.ps1 -Write -Base origin/main -Out artifacts/<slug>/candidate.manifest`.
   - Run full manifest-bound verification matching the real scope (`docs/TOOLING.md`): `./scripts/verify-linux.ps1 -Manifest ...` (`-Mode backend` or `-Mode full`, or `-Mode frontend` for frontend-only, or `-Mode docs` ONLY when every path is strictly in `README.md` / `docs/**/*.md`). Never combine `-Manifest` with `-Filter`; verify `RESULT=PASS`, `coverage`, `manifest_sha256`, `exit_code=0`, and `git diff --check`. Respect host PG lock rules (exits `5`/`6`/`7`, `-LockWaitSeconds`, ban on `TRA_HIEN_TEST_LOCK_PATH`).
   - Drop `refs/task-integration/<slug>` (`-Action drop`) only after verification passes.
7. **Unified Handoff Report — then STOP.**
   - If the task already had a G4 verdict, focused G4 on the integration delta is required for both tiers (scope guided by actual delta, blast radius, and shared invariants).
   - Transition rule: if the task had **no G4 verdict yet** and governance allows integrating before review, proceed to one complete independent G4 on the integrated candidate instead of an unnecessary stale-base review (never skip mandatory gates).

```text
TASK / SCOPE / TIER / STATUS: <P*-* hoặc chore> / BASE_INTEGRATION / A | B / READY_FOR_LOCAL_G4
WORKTREE / BRANCH / BASE / HEAD: <path> / <branch> / <old SHA> -> <new origin/main SHA> / <HEAD SHA>
INCOMING: <n files; notable areas / merged PRs>
CONFLICTS: none | <file — resolution>
GENERATED_CONTRACT: regenerated (sha256 openapi/schema) | unchanged | NOT_APPLICABLE
CHANGED: <candidate delta vs previous reviewed snapshot>
STATE: refs/task-integration/<slug> <sha> dropped after verification | MERGE_HEAD pending (commit only via publish-pr)
SNAPSHOT: artifacts/<slug>/pre-integration/ (<n files>)
MANIFEST: artifacts/<slug>/candidate.manifest sha256=<digest> (<n> entries, base=<new SHA>)
COVERAGE & VERIFICATION: <coverage token> | <exact command> -> RESULT=<PASS|FAIL> exit_code=<n> (<log path>)
FAILED_RUNS_AND_RETRIES: none | <details>
NOT_RUN / NOT_APPLICABLE: <items + reason>
BLOCKERS / RESIDUAL_RISK / NEXT: <focused G4 on integration delta | one complete G4 if unreviewed>, then publish-pr
```
