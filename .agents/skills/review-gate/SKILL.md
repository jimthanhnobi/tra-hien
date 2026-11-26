---
name: review-gate
description: G4 for Trà Hiên V2 — independent, skeptical, read-only Blind-First review of a local task worktree or authorized snapshot against base and specs (Tier A or B), bound to the exact candidate manifest. Also used for focused re-review after remediation or base integration.
---

# Independent review gate (G4 — Blind-First)

Shared by Codex (`$review-gate`) and Antigravity (name the skill or its path). Follow `AGENTS.md`, `docs/WORKFLOW.md`, and `docs/TOOLING.md`.

## Independence and authority
- **Independent context (model-independent):** Initial G4 runs in a fresh conversation completely separate from the Implementer. Review independence comes from independent context, read-only authority, verifiable evidence, and exact candidate identity — never model name or vendor (the Owner chooses models/thinking levels).
- **Read-only:** Never edit candidate files, commit, push, approve, or merge. Running read-only git checks, tests, or `./scripts/verify-linux.ps1` is allowed (Docker needs approval in sandboxed agents).
- **Readable worktree required:** If you cannot read the exact candidate worktree, report `NOT_RUN` and ask the Owner for an authorized reviewable snapshot. Never review stale remote code as if it were the local candidate.
- **Evidence invariants:** Test `PASS` is not G4 `PASS`; `CANDIDATE MATCH` is not proof that tests ran; never fabricate findings when no real defect exists.

## Blind-First review order (complete steps 1–4 before comparing G3 summary)

### 1. Verify candidate identity and inventory the full change set
- Run `./scripts/candidate-manifest.ps1 -Verify <manifest>` (both tiers). Missing manifest or any mismatch means you are not reviewing the handed-off candidate: report it and STOP until the implementer regenerates the handoff. Record the exact `manifest sha256` reviewed.
- Inspect `git status --short -uall`, `git diff <base> --stat` and full diff, `git diff --cached`, and `git ls-files --others --exclude-standard`; **read every untracked file in full**.
- Flag any unauthorized change outside scope or under `docs/spec/` (must have explicit Owner approval), and confirm no tracked Immutable Implementation Record embeds self-referencing manifest SHA-256, unpublished commit SHA, future CI results, or G4 verdict.

### 2. Independently verify requirements and decision provenance
- Read the task row in `docs/spec/07-roadmap.md` (or Owner-authorized chore scope), linked `FR/BR/NFR/AC/DEC`, accepted ADRs, and tracked Immutable Implementation Record (if applicable).
- Distinguish `OWNER_APPROVED` from `IMPLEMENTED_BUT_NOT_CONFIRMED`, `PROPOSED`, `CONFLICT`, and `UNRESOLVED`. Flag scope creep, missing AC coverage, or silent promotion of unconfirmed assumptions.

### 3. Trà Hiên invariant and failure-path checklist (apply what the change touches)
- **Money:** VND as `long`, wide intermediates, rounding exact per BR; no floating point.
- **Inventory:** locks product → variant → stock in ascending UUID order (`docs/plans/p1-inventory-architecture.md`); append-only ledger; `Σ movements == on_hand`; `reserved` rules.
- **Permissions / PII / Secrets:** server-side enforcement; JSON non-disclosure per permission verified on real JSON, not UI; CSRF on unsafe admin/public state-changing calls; zero PII/secrets in code, logs, audit, fixtures, or artifacts.
- **Transactions:** one command = one transaction; audit and outbox atomic in same transaction; idempotency keys; `rowVersion`/`xmin` conflicts → `409`.
- **Database:** new forward migration with Down/recovery, grants `tp_app`/`tp_readonly`, parity with `schema.sql` and `03-database.md`, old migrations untouched.
- **API:** contract regenerated via `-Mode api -WriteBack`, not hand-edited; Problem Details codes per `05-api.md`; operation/schema changes explained.
- **SEO (`DEC-21`):** canonical, indexability, sitemap, redirects, and structured data per `09-seo.md` when touched.
- **Tests & Governance:** would tests fail without the change? Negative, boundary, permission, and PG17 concurrency cases; 0 skipped; no weakened assertions; for governance/tooling/docs changes, verify semantic cross-document consistency, script signature parity, Markdown/TOML validity, and workflow walkthroughs.
- **Tier B:** same full inventory and independent Blind-First flow, checklist scoped to touched areas, report may be briefer without omitting required evidence.

### 4. Independently run required verification
- Execute or verify exact required commands per scope (see `docs/TOOLING.md`): check `RESULT=PASS`, expected `coverage` (`FULL_BACKEND`, `FULL_ALL`, `FRONTEND`, `DOCS`), exact `manifest_sha256`, and `exit_code=0`.
- Reject `RESULT=FOCUSED_PASS` (`-Filter`) as a substitute for full candidate verification, and reject `-Mode docs` outside `README.md` / `docs/**/*.md`.
- Respect host PG lock rules (exits `5`/`6`/`7`, `-LockWaitSeconds`, ban on `TRA_HIEN_TEST_LOCK_PATH`, never kill another agent's container, no infinite retry). Distinguish local `FULL_ALL` from GitHub Actions CI and `Docker regression builds` (`NOT_RUN`).

### 5. Compare with G3 handoff conclusions
- Only after steps 1–4, compare your findings and verification results against the G3 handoff report; flag any discrepancy, hidden failed run, or unsupported claim.

## Focused re-review (after remediation or base integration)
- You may reuse the independent G4 conversation when context and independence remain intact; open a fresh reviewer conversation when context degrades or a full independent reassessment is needed.
- Determine review scope from **actual delta, root cause, blast radius, shared execution paths, and security/data/transaction invariants** — never rely solely on a `% changed files` threshold or remediation-round count.
- Require a new candidate manifest and matching verification evidence for any candidate change; focused source review does **not** automatically permit replacing full verification with filtered tests. Mark every prior finding `CLOSED` or `OPEN` with exact evidence.

## Unified Handoff Report (Vietnamese, findings first)

```text
TASK / SCOPE / TIER / STATUS: <P*-* hoặc chore> / G4 <INITIAL | FOCUSED_REREVIEW> / A | B / VERDICT: <PASS | CONDITIONAL | BLOCK>
WORKTREE / BRANCH / BASE / HEAD: <path> / <branch> / <base SHA> / <HEAD SHA>
CHANGED: <n tracked, m untracked, k staged>
MANIFEST: <path> sha256=<digest> (<n> entries, base=<sha>) MATCH
FINDINGS / CLOSURE:
- [P0|P1|P2|P3] <path:line> — evidence | impact | repro | minimal fix
- (hoặc: không có finding cần xử lý trong phạm vi review; hoặc trạng thái CLOSED/OPEN của finding cũ)
COVERAGE & VERIFICATION: <VERIFIED commands, coverage token, RESULT, exit_code, log paths>
FAILED_RUNS_AND_RETRIES: none | <meaningful failures/retries>
NOT_RUN / NOT_APPLICABLE: <items + reason (e.g. GitHub Actions CI, Docker regression builds)>
BLOCKERS / RESIDUAL_RISK / NEXT: <traceability gaps / residual risk / next safe step>
VERDICT: PASS | CONDITIONAL | BLOCK
```

- `BLOCK` on any open `P0`/`P1`; `CONDITIONAL` when material checks remain unverified; `PASS` only with verified evidence. With no findings, write "không có finding cần xử lý trong phạm vi review", never "không còn bug".
- Verdict `PASS` binds strictly to the exact reported `manifest sha256`. Publish still requires explicit Owner authorization and CI verification at the published HEAD (`publish-pr`).
