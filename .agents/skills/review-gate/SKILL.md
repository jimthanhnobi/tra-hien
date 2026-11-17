---
name: review-gate
description: Perform a skeptical spec-to-code and change-risk review of Trà Hiên V2 implementations or pull requests. Use before merge, after feature implementation, or after significant changes. Focus on actionable correctness, security, data safety and tested evidence.
---

# Independent review gate

1. Read `AGENTS.md`, full task requirements, accepted ADRs, exact code diff and surrounding implementation. For local-first G4, use a **fresh context with access to the same worktree**; review committed, staged, unstaged and **untracked** files (list and inspect their full content). If access is unavailable, report NOT RUN rather than silently reviewing stale remote code. Do not rely solely on the author summary or previous review.
2. Check requirement coverage and unintended scope, business state transitions, validation/negative cases, authorization, privacy, concurrency, idempotency, transaction/data integrity, migrations/rollback, external failures, SEO, API contracts and performance as applicable.
3. Inspect tests for behavior coverage and whether they would detect the failure. Check actual CI statuses and logs when accessible. Do NOT claim commands ran if they did not.
4. Report findings first. Each finding: `P0|P1|P2|P3`, file:line, specific evidence, impact, reproduction or realistic path, and proposed correction. Avoid speculative noise.
5. Explicitly distinguish VERIFIED, NOT RUN, and NOT APPLICABLE. List exact commands/output and CI links when available.
6. Add acceptance traceability gaps and residual risks. Recommend BLOCK on unresolved P0/P1, CONDITIONAL on material unverified checks, PASS only with evidence. Review is read-only: no edits, commits, push or merge. After local G4 PASS, the owner must explicitly authorize publication; verify CI separately at published HEAD. The human owner retains merge authority unless explicitly delegated for a specific PR.
7. If no findings, state that no actionable issues were found in reviewed scope, not that the application is bug-free.
