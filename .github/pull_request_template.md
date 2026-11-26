## Dynamic Task Status

- **Task / Approved Chore & Tier:** <!-- `P*-*` từ `docs/spec/07-roadmap.md`, hoặc mô tả non-roadmap chore được Owner phê duyệt (không tạo `TASK-XXX`/`REQ-###`) · Tier A | B -->
- **Approved BASE → Published HEAD:** <!-- `<base-sha>` → `<published-head-sha>` (branch `<branch>`) -->
- **Candidate Manifest:** <!-- `artifacts/<slug>/candidate.manifest` · `sha256=<digest>` · `<n> entries` · `tree=<tree-sha>` -->
- **Independent G4 Verdict & Closure:** <!-- `PASS` (đối chiếu exact manifest) · Findings closed: `none` hoặc `<IDs CLOSED>` -->
- **Local Verification Coverage:** <!-- `coverage=<FULL_BACKEND|FULL_ALL|FRONTEND|DOCS>` · `RESULT=PASS` · `exit_code=0` · log path; ghi rõ failed runs/retries có ý nghĩa nếu có -->
- **GitHub Actions CI @ Published HEAD (4 jobs):**
  - `Classify changes and validate docs`: <!-- `NOT_RUN` / `PASS (mode=...)` -->
  - `Backend and API contract`: <!-- `NOT_RUN` / `PASS (suite executed)` / `PASS (not applicable for scope)` -->
  - `Frontend verification`: <!-- `NOT_RUN` / `PASS (suite executed)` / `PASS (not applicable for scope)` -->
  - `Docker regression builds`: <!-- `NOT_RUN` / `PASS (images built)` / `PASS (fast-path skipped for docs)` -->
- **Current Blockers / Merge State:** <!-- `none — chờ Owner duyệt và merge thủ công` hoặc danh sách blocker -->

## Goal / scope
<!-- Hành vi nghiệp vụ hoặc thay đổi governance/tooling nào được áp dụng? Phần nào cố ý nằm ngoài phạm vi? -->

## Spec and decision traceability
- Spec paths + headings:
- FR/BR/NFR/AC/DEC IDs (or Owner authorization ref for non-roadmap chore):
- Immutable Record / Accepted ADRs (phân biệt `OWNER_APPROVED` vs `IMPLEMENTED_BUT_NOT_CONFIRMED` / `PROPOSED`):

## Change summary
-

## Acceptance verification
| Scenario (happy/bad/edge/security/consistency) | Command / evidence | Coverage / Status (`VERIFIED`/`REPORTED`/`NOT_RUN`/`NOT_APPLICABLE`) |
| --- | --- | --- |
| | | `NOT_RUN` |

## Risk and rollout
- Breaking changes:
- Security/privacy/auth impacts:
- Database migration + rollback:
- SEO/content impacts (if relevant):
- Monitoring / recovery:
- Open risks, `NOT_RUN` items, and follow-ups:

## Review and publish checklist
- [ ] Authoritative specs/decisions verified; no unconfirmed assumptions silently promoted to `OWNER_APPROVED`
- [ ] Changes strictly match approved task/chore scope (no secrets, PII, `.env`, or `artifacts/`)
- [ ] Required tests/validations added and executed without substituting filtered runs (`FOCUSED_PASS`) for full verification
- [ ] Independent Blind-First G4 `PASS` bound to exact candidate manifest SHA-256 (reviewed committed + staged + unstaged + untracked)
- [ ] No unresolved P0/P1 findings; tracked Immutable Record (if any) was not edited after G4 without re-freeze and re-review
- [ ] Publish checkpoints A–D completed (`-RequireBase`, `-Both`, `-PublishMode`, independent candidate tree equivalence, no force push)
- [ ] All four GitHub Actions jobs verified at exact published HEAD (distinguishing job success, suite executed, and fast-path/not applicable)
- [ ] Owner explicitly authorized publication; ready for **manual Owner merge** (no automatic merge)
