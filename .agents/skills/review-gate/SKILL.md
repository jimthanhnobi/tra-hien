---
name: review-gate
description: G4 for Trà Hiên V2 — independent, skeptical, read-only review of a local task worktree or PR against origin/main and the specs (Tier A or B), in a fresh session preferably on a different model vendor than the implementer. Also used for focused re-review after remediation.
---

# Independent review gate (G4)

Shared by Codex (`$review-gate`) and Antigravity (name the skill or its path).

## Independence
- Fresh session/context, never the implementer's. Prefer cross-vendor: Codex-implemented work is reviewed in Antigravity (Claude/Gemini) and vice versa.
- Read-only: no edits, commits, push, approval or merge. Running tests or `./scripts/verify-linux.ps1` is allowed (Docker needs approval in sandboxed agents).
- If you cannot read the same worktree, report NOT RUN and ask the owner for an authorized reviewable snapshot. Never review stale remote code as if it were the local candidate.

## 1. Inventory the full change set
`git status --short`; `git diff origin/main --stat` and the full diff; `git diff --cached`; `git ls-files --others --exclude-standard` and read every untracked file in full. Verify the candidate manifest for both tiers with `./scripts/candidate-manifest.ps1 -Verify <manifest>`; a missing manifest or any mismatch means you are not reviewing the handed-off candidate: report it and stop until the implementer regenerates the handoff. Report the manifest SHA-256 you reviewed. Changes under `docs/spec/` must be owner-approved, otherwise report them.

## 2. Compare with requirements
Task row in `docs/spec/07-roadmap.md`, linked FR/BR/NFR/AC/DEC, ADRs and `docs/plans/<task>-implementation.md`. Report scope creep, missing AC coverage and untraceable behavior. Do not rely on the implementer's summary.

## 3. Trà Hiên checklist (apply what the change touches)
- Money: VND as `long`, wide intermediates, rounding exactly as the BR states; no floating point.
- Inventory: locks product → variant → stock in ascending UUID order as in `docs/plans/p1-inventory-architecture.md`; append-only ledger; Σ movements = on_hand; `reserved` rules.
- Permissions/PII: server-side checks; JSON non-disclosure per permission (for example `avgCostVnd`, low/onHand) verified on real JSON, not UI; CSRF on unsafe admin calls; no PII/secrets in logs or audit.
- Transactions: one command = one transaction; audit and outbox in the same transaction; idempotency keys; rowVersion/xmin conflicts → 409.
- Database: new forward migration with Down/recovery, grants `tp_app`/`tp_readonly`, parity with `schema.sql` and `03-database.md`, old migrations untouched.
- API: contract regenerated, not hand-edited; Problem Details codes per `05-api.md`; operation/schema count changes explained.
- SEO (DEC-21): canonical, index, sitemap and structured data per `09-seo.md` when touched.
- Tests: would they fail without the change? Negative, boundary, permission and PG17 concurrency cases; 0 skipped; no weakened assertions.
- Tier B (Admin UI on reviewed APIs, CRUD without money/stock/PII, docs, behavior-preserving refactor): same inventory step, checklist only for touched areas, short report.

## 4. Report in Vietnamese, findings first

```text
TASK / TIER / REVIEWED: <P*-*> / A | B / <branch> @ <HEAD> + <n> uncommitted, <m> untracked, manifest sha256 <digest> (MATCH)
FINDINGS:
- [P0|P1|P2|P3] <file:line> — evidence | impact | repro | minimal fix
VERIFIED: <commands actually run + results>
NOT RUN: <items>        NOT APPLICABLE: <items>
TRACEABILITY GAPS / RESIDUAL RISK: <short>
VERDICT: PASS | CONDITIONAL | BLOCK
```

- BLOCK on any open P0/P1; CONDITIONAL when material checks are unverified; PASS only with evidence. With no findings write "no actionable issues in reviewed scope", never "bug-free".
- Focused re-review: verify the new manifest, mark each prior finding CLOSED or OPEN with evidence, review only the remediation or integration delta, then give a verdict.
- After PASS the owner decides on publishing; the PASS applies only to the reported manifest SHA-256, and CI must pass at the published HEAD (see `publish-pr`).
