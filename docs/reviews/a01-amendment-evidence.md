# A01 amendment — local handoff cho G4 độc lập

**Gate:** READY_FOR_LOCAL_G4; **G4 NOT RUN**, không phải self-review PASS. Owner phê duyệt A01 D1–D14/finalize §8 ngày 18/11/2026 và hai bổ sung: backup kiểm soát/điều tra tách quyền; owner thứ hai qua Admin API bảo vệ. Chỉ cho amendment specs/workflow và tài liệu liên quan, không source code/EF migrations/commit/push/PR/merge.

## 1. Revision và scope

- Branch `planning/a01-p0-closure`; HEAD/origin/main/input `288ca8b55cab2017c9ee42bd35036e2d2e5bd87c`. Sau amendment **UNCOMMITTED**, không bịa SHA commit mới. Full G1 baseline `cadce575597738bf40ad7c45689482de5a5fbc3d`, G2 baseline conformance/readiness được tái sử dụng; lần này **TARGETED G1/G2**, có diff/affected-contract checks, không full audit mới.
- Specs: 00/02/03/04/05/06/07/09 Markdown + schema.sql; 01/08 không sửa. Workflow: AGENTS.md + docs/WORKFLOW.md. Supporting: ADR A01, P1-01 task record, runbook design, scoped inventory/gaps/traceability/conformance/readiness và hồ sơ này.
- D1 DDL split/deferred FK; D2/D3 import/SKU; D4 SEO count/manual/thin/metadata unchanged; D5 inactive NULL/activation/publish; D6/D7 R2/evidence; D8 MFA/operator/atomic audit/revoke/onboarding/owner API; D9 closure gate; D10 A/B không bỏ gate; D11 provisioning + backup/điều tra; D12 entity/path audit; D13 opaque rowVersion; D14 internal InventoryAlert/retry bounded/no stale replay.
- Hợp đồng importer P1-07 (stable variant key/import lại/ô trống/group conflict/atomicity) **OPEN** tại đúng task, không thêm auto-upsert. G03/G27 và gap pha sau không bị tự đóng. CONTRACT_ALIGNED trong gap register chỉ contract local đã sửa, không implementation verified.

## 2. Kiểm chứng đã chạy

| Lệnh / bằng chứng | Kết quả | Giới hạn |
|---|---|---|
| `git fetch origin --prune`; `git rev-parse HEAD`; `git rev-parse origin/main` | Exit 0; cùng base 288ca8b… | Không commit/publish |
| `git diff --stat cadce575597738bf40ad7c45689482de5a5fbc3d HEAD -- docs/spec` | Chỉ 02/03/07/schema đổi từ full G1 baseline trước A01 | Amendment worktree kiểm riêng; không gán là full audit |
| `python artifacts/a01-amendment/verify-ddl.py` | Exit 0: PostgreSQL **17.12**, toàn schema đích tạo được **79 bảng/11 schema**; **43 ca** happy/negative/boundary constraints PASS + sequence 9999→10000 PASS | Tạo 3 role fixture local; không phải production role script/grants hay EF migration |
| `python artifacts/a01-amendment/verify-docs.py` | Exit 0: Markdown/UTF-8/ID/scope PASS; 180 BR/50 AC/30 DEC/81 task, ID cũ giữ nguyên; log artifacts/a01-amendment/docs-check.log | Kiểm văn bản/ID/scope/allowlist; không runtime behavior |
| `git -c core.autocrlf=false diff --check`; `git diff --cached --check` | Exit 0, không có whitespace diagnostic | Untracked còn được checker đọc trực tiếp; git diff một mình không gồm chúng |

DDL SHA-256 đã test: `f90d6f7f4e922435466832e1cae8d79c49e1c749d500adf2ba6883af020dc140`. Metadata/log/SQL/harness ở `artifacts/a01-amendment/summary.json`, `ddl.log`, `checks.sql`, `verify-ddl.py` (ignored, chỉ test target DDL). Container riêng `trahien-a01-ddl-3b38db721b`, network none, tmpfs, không host volume/port; fixture dữ liệu giả, role không credential thật; đã `docker rm -f` **đúng container do test tạo**, cleanup exit 0.

43 ca bao gồm giới hạn ảnh 15 MiB/video evidence 50 MiB trên **assets và upload_sessions**, 0/âm/+1, MIME/purpose/video catalog/content/branding/SVG sai, sku_prefix/material code/XX/UNIQUE, sku_base regex/UNIQUE, price NULL/1/max/0/âm/max+1, SKU UNIQUE, count âm và cờ manual độc lập. Chạy DDL bằng postgres **không chứng minh** grants app/backup hoặc quy trình điều tra; SQL count update không chứng minh app tự bỏ thin noindex đúng.

Lần thử đầu qua Unix socket gặp init-server readiness race trước khi chạy DDL; cleanup exit 0, log `ddl-first-attempt.log`. Harness sửa cách chờ **TCP final server** rồi chạy lại PASS; không sửa spec để né lỗi môi trường.

## 3. NOT RUN / không áp dụng

- **NOT RUN:** G4 độc lập, CI tại published SHA, EF build/tests/migrations, runtime API/UI/MFA/R2/SEO/permissions/provisioning existing DB/backup restore hoặc diễn tập runbook. Không triển khai source/migration để tạo bằng chứng giả cho amendment.
- Runbook mới là **thiết kế NOT RUN**: DB mới/existing, tạo B bằng session A đã ready có staff.manage + CSRF, B session riêng đổi password/TOTP/login lại, API me/staff kiểm active/owner/TOTP và negative unauthorized/CSRF. Không giả định Staff UI đầy đủ; không dùng tp_readonly để query audit.
- `tp_readonly` là backup có kiểm soát, full backup có thể đọc credentials/DP keys cần bảo vệ credential/execution. Điều tra dùng identity/quyền read riêng tối thiểu, mặc định loại IAM credentials/tokens/stamps/DP xml; SELECT không tự phân biệt pg_dump/SQL tay. Các kiểm chứng này thuộc closure/P3-10.
- **NOT APPLICABLE:** commit/push/PR/merge/deploy và app-code coverage trong amendment này. Không có publish permission từ approval ADR.

## 4. Checklist cho reviewer độc lập

1. Đọc cùng worktree; `git diff origin/main`, staged/unstaged và **nội dung untracked** (`git ls-files --others --exclude-standard`), kể cả ADR/task/runbook/evidence. Báo cáo P0 gốc untracked là tài liệu owner thêm, giữ nguyên.
2. So D1–D14/§8 + hai bổ sung owner với spec/workflow diff; kiểm không nới metadata/upsert/inactive-price và không thêm roadmap ID. Đối chiếu G1 gaps/traceability/G2/readiness.
3. Có thể chạy lại harness PG17 disposable và docs checker; phân biệt constraints DDL với runtime chưa làm. Manifest SHA256 worktree ở artifacts/a01-amendment/manifest.json để đối chiếu revision, không thay commit SHA/G4 approval.
4. PASS/BLOCK theo evidence, không edits/commit/push/merge. Sau G4 còn cần owner giao slice closure riêng, owner publish permission riêng và CI đúng published head. P1-01 vẫn HOLD C1–C3/closure #1/#2/nghiệm thu.


Ghi chú Markdown: chỉ chuẩn hóa LF và escape dấu phân cách literal trong code span của bảng (ví dụ query enum/SEO title); không đổi ngữ nghĩa API/metadata. Checker đã sửa nhận diện backslash bên trong code span, không sửa contract để né kiểm.
