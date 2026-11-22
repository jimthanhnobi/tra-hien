---
name: publish-pr
description: Publish one Trà Hiên V2 task only after independent G4 PASS and explicit owner permission — confirm the reviewed candidate, commit exactly those files, push the feature branch, prepare the PR (gh may be missing) and verify all GitHub Actions jobs at the published HEAD. Never merges.
---

# Publish a reviewed task

Shared by Codex (`$publish-pr`) and Antigravity. Git network/write commands need sandbox bypass/approval in sandboxed agents.

## Preconditions — STOP if any fails

- G4 verdict PASS for this exact candidate manifest (SHA-256 quoted in the G4 report), for Tier A and Tier B alike; record reviewer tool/model.
- The owner explicitly authorized publishing this task in the current conversation.
- `git fetch origin --prune`: `origin/main` equals the manifest base. Otherwise run `base-integration` and a focused G4 first.

## Steps

1. **Working tree == reviewed.** `./scripts/candidate-manifest.ps1 -Verify artifacts/<task>/candidate.manifest` must print `CANDIDATE MATCH`, and the manifest SHA-256 must equal the one G4 reviewed. Any mismatch → STOP.
2. **Stage exactly the manifest paths.** `git add -- <paths listed in the manifest>` (deletions included), never a blind `git add -A`. Then `./scripts/candidate-manifest.ps1 -Verify artifacts/<task>/candidate.manifest -Index` must print `CANDIDATE MATCH` for the staged index. On mismatch run `git restore --staged -- <paths>` and STOP. Also check `git diff --cached --stat` for `.env`, secrets, `artifacts/`, real customer data or backups.
3. **Commit** immediately after the index check, with Conventional Commits, for example `feat(inventory): implement P1-10 low-stock alerts and nightly reconciliation`. One commit per task unless the owner asks otherwise. A merge prepared by `base-integration` (`MERGE_HEAD` present) is committed here, only under this authorization.
4. **Push** `git push -u origin <branch>`.
5. **PR.** Write the body to `artifacts/<task>/pr-body.md` (Vietnamese): task and spec IDs, scope/out of scope, migration/rollback, real test results, NOT RUN, G4 verdict, reviewer and manifest SHA-256, risks. With `gh`: `gh pr create --base main --head <branch> --title "<commit subject>" --body-file <file>`. Without `gh`: return `https://github.com/jimthanhnobi/tra-hien/pull/new/<branch>` plus the body file. In PowerShell never inline `${{ }}` or backticks in command strings; always use the body file.
6. **CI at the published SHA.** All four jobs must succeed: `Classify changes and validate docs`, `Backend and API contract`, `Frontend verification`, `Docker regression builds`. If you cannot read Actions, say NOT VERIFIED and give the Actions URL. On failure: fix on the same branch, new manifest, focused G4, publish again only with owner approval.

## Return

```text
PUBLISHED: <branch> @ <commit SHA>        PR: <url | create link + body file>
REVIEWED == PUBLISHED: YES | NO (manifest sha256 <digest>; working tree and staged index MATCH)
CI @ <SHA>: <job: result, ...> | NOT VERIFIED (<url>)
NEXT: owner reviews and merges manually
```

Never merge unless the owner explicitly delegates that specific PR.
