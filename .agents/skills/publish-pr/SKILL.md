---
name: publish-pr
description: Publish one Trà Hiên V2 task or approved chore only after independent G4 PASS and explicit Owner authorization — enforce all four PR-2 publish checkpoints (pre-stage, post-stage/pre-commit, post-commit/pre-push, post-push), prepare the PR with Dynamic Task Status, and verify all four GitHub Actions jobs at the published HEAD. Never merges.
---

# Publish a reviewed task (4 Checkpoints)

Shared by Codex (`$publish-pr`) and Antigravity. Follow `AGENTS.md`, `docs/WORKFLOW.md`, and `docs/TOOLING.md`. Git network/write commands require sandbox bypass/approval in sandboxed agents. **Never force push. Never merge unless the Owner explicitly delegates that specific PR.**

## Checkpoint A — Pre-stage (STOP if any fails)
1. **Owner authorization & G4 PASS:** Confirm explicit Owner authorization to publish in the current conversation, and confirm independent G4 `PASS` bound to the exact candidate manifest SHA-256 (for both Tier A and Tier B).
2. **Fetch & base freshness:** Run `git fetch origin --prune`. Verify `origin/main` matches the manifest base (if `origin/main` moved, STOP and run `base-integration` + required G4 first).
3. **Scope, topology, and remote branch expectation:**
   - **Initial publish (new branch):**
     ```powershell
     ./scripts/candidate-manifest.ps1 -Verify artifacts/<slug>/candidate.manifest -RequireBase origin/main -PublishMode initial
     ```
     Confirm `CANDIDATE MATCH` with exact reviewed SHA-256, and confirm the remote branch does not unexpectedly exist (`git ls-remote --heads origin <branch>`).
   - **Republish (existing published branch — CI remediation or post-`base-integration` merge):**
     ```powershell
     ./scripts/candidate-manifest.ps1 -Verify artifacts/<slug>/candidate.manifest -RequireBase origin/main -PublishMode republish -PublishedHead origin/<branch>
     ```
     Because `-PublishedHead` validates ancestry (`merge-base --is-ancestor`), **also explicitly verify actual remote HEAD** (`git ls-remote --heads origin <branch>` == `git rev-parse origin/<branch>`) and confirm every commit in `origin/main..HEAD` is part of the authorized branch history.

## Checkpoint B — Post-stage, pre-commit
1. **Stage exact manifest paths only:** Stage only the paths listed in the manifest (including `DELETED` entries via `git rm` / `git add --`). Never run a blind `git add -A` across unrelated files.
2. **Verify working tree + staged index + base + topology together:**
   - Initial:
     ```powershell
     ./scripts/candidate-manifest.ps1 -Verify artifacts/<slug>/candidate.manifest -Both -RequireBase origin/main -PublishMode initial
     ```
   - Republish:
     ```powershell
     ./scripts/candidate-manifest.ps1 -Verify artifacts/<slug>/candidate.manifest -Both -RequireBase origin/main -PublishMode republish -PublishedHead origin/<branch>
     ```
   On any mismatch, run `git restore --staged -- <paths>` and STOP.
3. **Index inventory & secrets check:** Check `git diff --cached --stat` and index modes/blobs (`git ls-files -s`) to confirm zero `.env`, secrets, `artifacts/`, real customer data, or backups.
4. **Independent complete candidate tree equivalence (non-circular):**
   - Compute staged tree: `$stagedTree = (git write-tree).Trim()`.
   - Reconstruct the reviewed candidate tree **independently** from `<manifest base>` + manifest entries into a temporary `GIT_INDEX_FILE` (`git read-tree <base>` + `git update-index --add --cacheinfo <mode>,<blob>,<path>` / `--force-remove` → `git write-tree`), or cross-check against the `tree <snapshotTree>` recorded by `verify-linux.ps1 -Manifest` for the exact reviewed `manifest_sha256`.
   - Confirm `$stagedTree == $reviewedTree`. Never merely compare `git write-tree` of the staged index against itself.

## Checkpoint C — Post-commit, pre-push
1. **Commit:** Commit immediately after Checkpoint B passes using Conventional Commits (one commit per task unless the Owner requests otherwise). If `base-integration` prepared a valid `MERGE_HEAD` on a published branch, commit that merge here under this authorization.
2. **Verify committed state before pushing:**
   - Committed tree equals independently verified reviewed tree: `git rev-parse HEAD^{tree}` == `$reviewedTree`.
   - Parent/commit topology is valid (`git rev-list --parents -n 1 HEAD`, `git log --oneline <base>..HEAD`, valid 2-parent merge only when prepared by `base-integration`).
   - Authorized delta matches manifest scope (`git diff --stat <base>..HEAD`).
   - Working tree is completely clean (`git status --short -uall` empty).
   - Base freshness (`git rev-parse origin/main` unchanged vs `<base>`) and remote branch expectation still hold.
   - **Important:** Do **not** run `-PublishMode initial` after commit (because `HEAD` is now ahead of `base`). **Never force push.**

## Checkpoint D — Post-push, PR, and 4-job CI verification
1. **Push & confirm remote HEAD:** Push `git push -u origin <branch>` (or `git push origin <branch>`, never `--force`). Verify `git ls-remote --heads origin <branch>` equals `git rev-parse HEAD` (`remote HEAD == published HEAD`).
2. **Prepare/update PR with Dynamic Task Status:** Write the Vietnamese PR body to `artifacts/<slug>/pr-body.md` following `.github/pull_request_template.md` (include Dynamic Task Status: Task/Tier, BASE → published HEAD, exact manifest SHA-256 & tree, independent G4 verdict & finding closures, local verification coverage, `NOT_RUN`, risks).
   - With `gh`: `gh pr create --base main --head <branch> --title "<commit subject>" --body-file artifacts/<slug>/pr-body.md` (or `gh pr edit` on republish).
   - Without `gh`: return `https://github.com/jimthanhnobi/tra-hien/pull/new/<branch>` and the body file path.
   - In PowerShell, never inline `${{ }}` or backticks in CLI strings; always use `--body-file`.
3. **Verify all 4 GitHub Actions jobs at exact published SHA:**
   - `Classify changes and validate docs`
   - `Backend and API contract`
   - `Frontend verification`
   - `Docker regression builds`
   Distinguish clearly between **job `success`**, **suite executed**, and **fast-path / `not applicable`** for the actual scope. If Actions cannot be read, mark `UNKNOWN` (`NOT_VERIFIED`) and include the Actions URL.
4. **Failure or drift handling:**
   - CI remediation that modifies the candidate requires a new manifest, required verification, review evidence, and Owner authorization before republishing.
   - If `origin/main` moves, run `base-integration` per current governance.
   - Owner reviews and merges manually.

## Unified Handoff Report (Vietnamese)

```text
TASK / SCOPE / TIER / STATUS: <P*-* hoặc chore> / PUBLISH (<initial | republish>) / A | B / PUBLISHED | BLOCKED
WORKTREE / BRANCH / BASE / HEAD: <path> / <branch> / <base SHA> / <published HEAD SHA>
PUBLISHED: <branch> @ <published HEAD SHA> | REMOTE_HEAD_MATCH: YES | NO
MANIFEST: <path> sha256=<digest> (<n> entries) | CANDIDATE_TREE: <tree SHA> | REVIEWED == PUBLISHED: YES | NO
PR: <url | create link + body file>
COVERAGE & VERIFICATION: Checkpoints A–D (<commands + exit codes>)
CI @ <published HEAD SHA>:
- Classify changes and validate docs: <PASS (mode=...) | FAIL | UNKNOWN>
- Backend and API contract: <PASS (executed) | PASS (not applicable) | FAIL | UNKNOWN>
- Frontend verification: <PASS (executed) | PASS (not applicable) | FAIL | UNKNOWN>
- Docker regression builds: <PASS (built) | PASS (fast-path skipped) | FAIL | UNKNOWN>
FAILED_RUNS_AND_RETRIES: none | <details>
NOT_RUN / NOT_APPLICABLE: <items + reason>
BLOCKERS / RESIDUAL_RISK / NEXT: Owner reviews and merges manually (no auto-merge)
```
