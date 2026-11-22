---
name: base-integration
description: Bring a Trà Hiên V2 task worktree up to date with a newer origin/main (another PR merged) without losing reviewed work — snapshot, integrate, regenerate the API contract, re-verify and report the delta for focused G4. Use when main moved before publish or a published PR is behind main.
---

# Base integration

Shared by Codex (`$base-integration`) and Antigravity. Never `reset --hard`, `clean -fd`, rebase a published branch or create a commit here.

1. **Know the candidate.** Record old base, HEAD, the current candidate manifest and its SHA-256. `git fetch origin --prune` (sandbox bypass in sandboxed agents) and record the new `origin/main`.
2. **Snapshot first.** Copy every changed and untracked file to `artifacts/<task>/pre-integration/` together with the current manifest. Do not delete the snapshot until verification passes.
3. **Integrate without committing.**
   - Unpublished branch with an uncommitted candidate (normal before publish): `./scripts/task-integrate.ps1 -Task <task> -Action start -Onto origin/main`. It never touches the shared `refs/stash`: it pins the whole candidate as a local WIP commit under the task-private ref `refs/task-integration/<task>`, rebases the unpublished branch and undoes the WIP commit with `reset --mixed`, leaving the integrated candidate uncommitted. Entries with changed Git modes stay staged to preserve those modes on Windows. On conflicts (exit 2): resolve the files, `git add` them, then run `./scripts/task-integrate.ps1 -Task <task> -Action finish` (continues the rebase with `GIT_EDITOR=true` and undoes the WIP commit). Never use `git stash`. After step 6 passes run `-Action drop` (atomic compare-and-delete of that ref only, using the recorded WIP SHA). Recovery before drop: `git restore --source=refs/task-integration/<task> --worktree -- .`.
   - Published branch with commits: `git merge --no-ff --no-commit origin/main`. Resolve conflicts and stop with `MERGE_HEAD` present; the merge commit is created only by `publish-pr` under owner authorization. No rebase or force-push.
4. **Inspect the incoming delta:** `git diff <old-base> origin/main --stat`. Hot spots: `backend/src/TeaPremium.Api/Program.cs` and DI registrations, `AppDbContext`, migrations and the model snapshot, `packages/api-client/openapi.json`, `src/generated/schema.ts`, owner-approved `docs/spec/` amendments. Keep both sides' registrations, endpoints and amendments.
5. **Generated and EF files.** Never hand-merge generated contracts: take either side, then `./scripts/verify-linux.ps1 -Mode api -WriteBack`. If both sides added migrations, keep IDs ordered, regenerate the model snapshot with EF tooling, and confirm `has-pending-model-changes` reports no changes.
6. **Verify.** Write a new manifest against the new base: `./scripts/candidate-manifest.ps1 -Write -Base origin/main -Out artifacts/<task>/candidate.manifest`, then run the full `./scripts/verify-linux.ps1 -Manifest artifacts/<task>/candidate.manifest`; add `-Mode frontend` when frontend inputs changed; `git diff --check`.
7. **Report, then STOP.** Focused G4 on the integration delta is required for Tier A and Tier B: a new base can change APIs or behavior the reviewed work depends on.

```text
TASK: <P*-*>   BASE: <old SHA> -> <new origin/main SHA> (<merged PRs>)
INCOMING: <n files; notable areas>
CONFLICTS: none | <file — resolution>
GENERATED CONTRACT: regenerated (sha256 openapi/schema) | unchanged
CANDIDATE DELTA VS REVIEWED: <files whose content changed because of integration>
STATE: refs/task-integration/<task> <sha> dropped after verification | MERGE_HEAD pending (commit only via publish-pr)
SNAPSHOT: artifacts/<task>/pre-integration/ (<n files>)
MANIFEST: artifacts/<task>/candidate.manifest sha256=<digest>
TESTS: <command> -> <result> (<log path>)
NEXT: focused G4 on the integration delta (both tiers), then publish-pr
```
