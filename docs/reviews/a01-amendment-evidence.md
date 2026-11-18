# A01 amendment — hồ sơ G4 và evidence sau merge

**Gate A01 hiện hành (18/11/2026):** OWNER APPROVED / MERGED qua [PR #20](https://github.com/jimthanhnobi/tra-hien/pull/20). Independent local G4 **tầng A PASS trước publish/merge**, theo **bằng chứng reviewer do owner cung cấp** ngày 18/11/2026; phiên cập nhật hồ sơ này không tự thực hiện hay chạy lại G4/DDL. CI tại PR HEAD và merge SHA đã xác minh riêng bằng GitHub. Owner phê duyệt D1–D14/finalize §8 và hai bổ sung: backup kiểm soát/điều tra tách quyền; owner thứ hai qua Admin API bảo vệ. Không thay quyết định hay cấp quyền triển khai closure/P1-01.

## 0. Bằng chứng hiện hành và nguồn

| Bằng chứng | Kết quả / revision | Nguồn và giới hạn |
|---|---|---|
| Independent local G4 tầng A trước publish/merge | PASS; không finding actionable P0/P1/P2/P3; baseline `288ca8b55cab2017c9ee42bd35036e2d2e5bd87c`; reviewer kiểm toàn bộ 21 file, gồm tracked, unstaged và untracked; `READY_FOR_OWNER_PUBLISH_APPROVAL: YES` | Báo cáo reviewer **do owner cung cấp** trong yêu cầu cập nhật ngày 18/11/2026; PR #20 mô tả cùng kết quả. Không phải review do phiên này chạy, không viết lại thành review sau merge |
| Kiểm chứng của reviewer | PostgreSQL 17.12: 79 bảng/11 schema, 43 constraint cases PASS; 12 SQL MIME/purpose/byte boundary cases PASS; 8 session cấp 160 sequence values không trùng; Markdown/docs và Git whitespace PASS | Cùng bằng chứng reviewer do owner cung cấp; EF/runtime amendment, provisioning/grants thực, backup/restore và P0 acceptance chưa chạy. §2 giữ log/lệnh implementer tại thời điểm trước G4 |
| PR #20 | `merged=true`, 21 file; base `288ca8b55cab2017c9ee42bd35036e2d2e5bd87c`; PR HEAD `47f2c7b491ab124c91e827fda6e010171abeaba7`; merge commit `024ea998c43baf0afde30b069ed6b4876cb96f4a`; merged at `2026-11-18T06:28:13Z` | VERIFIED qua GitHub connector `fetch_pr` và GET `/repos/jimthanhnobi/tra-hien/pulls/20`; metadata không tự chứng minh reviewer đã chạy test |
| CI tại published PR HEAD | [Run 44250706587](https://github.com/jimthanhnobi/tra-hien/actions/runs/44250706587): `pull_request`, head SHA `47f2c7b491ab124c91e827fda6e010171abeaba7`, completed/success; ba jobs PASS | VERIFIED qua GET `/repos/jimthanhnobi/tra-hien/actions/runs/44250706587`, `fetch_commit_workflow_runs` và `fetch_workflow_run_jobs`; không suy từ G4 |
| CI riêng tại merge SHA | [Run 44251560930](https://github.com/jimthanhnobi/tra-hien/actions/runs/44251560930): `push`, head SHA `024ea998c43baf0afde30b069ed6b4876cb96f4a`, completed/success; ba jobs PASS | VERIFIED riêng qua GET `/repos/jimthanhnobi/tra-hien/actions/runs?head_sha=024ea998c43baf0afde30b069ed6b4876cb96f4a&per_page=100` và `fetch_workflow_run_jobs`; không suy từ PR HEAD |

Ba jobs ở **mỗi run**: Backend and API contract, Frontend verification, Docker regression builds — đều completed/success. Đây là CI regression của published revision; không chuyển runtime/grants thực/MFA recovery/P0 acceptance của amendment thành PASS.

Phiên cập nhật hồ sơ xác minh `git fetch origin --prune` exit 0, HEAD = origin/main = merge SHA `024ea998c43baf0afde30b069ed6b4876cb96f4a`, nhánh `docs/a01-g4-record`, worktree ban đầu sạch. `git diff --name-status 47f2c7b491ab124c91e827fda6e010171abeaba7 origin/main -- docs/spec` exit 0, không khác biệt. Tái sử dụng full G1/G2 và targeted A01 đã ghi bên dưới; không audit toàn spec lại hoặc sửa authoritative specs.

## 1. Lịch sử revision và scope trước G4

- Branch `planning/a01-p0-closure`; HEAD/origin/main/input `288ca8b55cab2017c9ee42bd35036e2d2e5bd87c`. Sau amendment **UNCOMMITTED**, không bịa SHA commit mới. Full G1 baseline `cadce575597738bf40ad7c45689482de5a5fbc3d`, G2 baseline conformance/readiness được tái sử dụng; lần này **TARGETED G1/G2**, có diff/affected-contract checks, không full audit mới.
- Specs: 00/02/03/04/05/06/07/09 Markdown + schema.sql; 01/08 không sửa. Workflow: AGENTS.md + docs/WORKFLOW.md. Supporting: ADR A01, P1-01 task record, runbook design, scoped inventory/gaps/traceability/conformance/readiness và hồ sơ này.
- D1 DDL split/deferred FK; D2/D3 import/SKU; D4 SEO count/manual/thin/metadata unchanged; D5 inactive NULL/activation/publish; D6/D7 R2/evidence; D8 MFA/operator/atomic audit/revoke/onboarding/owner API; D9 closure gate; D10 A/B không bỏ gate; D11 provisioning + backup/điều tra; D12 entity/path audit; D13 opaque rowVersion; D14 internal InventoryAlert/retry bounded/no stale replay.
- Hợp đồng importer P1-07 (stable variant key/import lại/ô trống/group conflict/atomicity) **OPEN** tại đúng task, không thêm auto-upsert. G03/G27 và gap pha sau không bị tự đóng. CONTRACT_ALIGNED trong gap register chỉ contract local đã sửa, không implementation verified.

## 2. Lịch sử kiểm chứng implementer trước G4

Các lệnh/kết quả dưới giữ nguyên theo hồ sơ trước G4 tại baseline 288ca8b; không phải kết quả chạy mới của phiên cập nhật sau merge. Approval amendment ban đầu chỉ cho sửa specs/workflow/tài liệu, chưa cấp commit/push/PR/merge tại thời điểm đó. Revision/publish/CI hiện hành xem §0.

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

- **NOT RUN:** EF build/tests/migrations cho amendment, runtime API/UI/MFA recovery/R2/SEO/permissions, provisioning/grants thực trên DB mới/existing, backup/restore, diễn tập runbook và P0 acceptance. CI regression hiện có ở §0 không chứng minh các hành vi amendment này; phiên cập nhật không chạy source/migration/DB thật.
- Runbook mới là **thiết kế NOT RUN**: DB mới/existing, tạo B bằng session A đã ready có staff.manage + CSRF, B session riêng đổi password/TOTP/login lại, API me/staff kiểm active/owner/TOTP và negative unauthorized/CSRF. Không giả định Staff UI đầy đủ; không dùng tp_readonly để query audit.
- `tp_readonly` là backup có kiểm soát, full backup có thể đọc credentials/DP keys cần bảo vệ credential/execution. Điều tra dùng identity/quyền read riêng tối thiểu, mặc định loại IAM credentials/tokens/stamps/DP xml; SELECT không tự phân biệt pg_dump/SQL tay. Các kiểm chứng này thuộc closure/P3-10.
- **Giới hạn approval:** approval ADR ban đầu không tự cấp publish permission; amendment sau đó đã publish/merge qua PR #20 theo metadata §0. Closure #1/#2 và P1-01 vẫn cần owner giao riêng. Phiên cập nhật hồ sơ này không commit/push/PR/merge/deploy; app-code coverage không áp dụng cho thay đổi tài liệu.

## 4. Lịch sử checklist giao reviewer tầng A trước publish

Checklist bên dưới là handoff trước G4, được giữ để truy vết; G4 tầng A đã PASS theo nguồn §0, không phải yêu cầu chạy lại A01 sau merge.

1. Đọc cùng worktree; `git diff origin/main`, staged/unstaged và **nội dung untracked** (`git ls-files --others --exclude-standard`), kể cả ADR/task/runbook/evidence. Báo cáo P0 gốc untracked là tài liệu owner thêm, giữ nguyên.
2. So D1–D14/§8 + hai bổ sung owner với spec/workflow diff; kiểm không nới metadata/upsert/inactive-price và không thêm roadmap ID. Đối chiếu G1 gaps/traceability/G2/readiness.
3. Có thể chạy lại harness PG17 disposable và docs checker; phân biệt constraints DDL với runtime chưa làm. Manifest SHA256 worktree ở artifacts/a01-amendment/manifest.json để đối chiếu revision, không thay commit SHA/G4 approval.
4. PASS/BLOCK theo evidence, không edits/commit/push/merge. Sau G4 còn cần owner giao slice closure riêng, owner publish permission riêng và CI đúng published head. P1-01 vẫn HOLD C1–C3/closure #1/#2/nghiệm thu.


Ghi chú Markdown: chỉ chuẩn hóa LF và escape dấu phân cách literal trong code span của bảng (ví dụ query enum/SEO title); không đổi ngữ nghĩa API/metadata. Checker đã sửa nhận diện backslash bên trong code span, không sửa contract để né kiểm.

## 5. Bàn giao cập nhật hồ sơ sau merge — tầng B

- Scope: chỉ ADR A01, hồ sơ này, task-readiness và p1-01-implementation; base/HEAD `024ea998c43baf0afde30b069ed6b4876cb96f4a`, nhánh `docs/a01-g4-record`. Bản cập nhật chưa có commit mới; không thay specs/source/migration/workflow hay D1–D14.
- A01 từ trạng thái chờ G4/publish → MERGED, G4 tầng A PASS theo evidence owner cung cấp và CI đúng hai SHA đã xác minh riêng; giữ nguyên phần lịch sử trước G4. C1 P1-01 hoàn tất cho amendment; C2 closure #1/#2/runbook/P0 acceptance và C3 role/helper/evidence triển khai chưa đủ, P1-01 vẫn HOLD / NOT AUTHORIZED G3. Import contract P1-07 vẫn OPEN.
- Kiểm tra bản cập nhật: `python -` (script inline qua PowerShell here-string) exit 0, PASS UTF-8/Markdown/bảng/code fence/đường dẫn liên kết local của cả bốn file; allowlist đúng bốn tài liệu; bảng D1–D14 và ADR §6 lịch sử giữ nguyên. `git -c core.autocrlf=false diff --check origin/main` và `git -c core.autocrlf=false diff --cached --check`: exit 0, không diagnostic. `git diff origin/main` đã đọc; `git status --short` chỉ bốn file sửa unstaged, `git diff --cached --stat` và `git ls-files --others --exclude-standard` rỗng. Đây là kiểm tra tài liệu, không phải G4 độc lập hay chạy lại DDL.
- **READY_FOR_LOCAL_G4 — tầng B cho bản cập nhật hồ sơ**; G4 tầng B chưa chạy trong phiên chỉnh sửa này. Reviewer session/context mới đọc cùng worktree, kiểm full committed/staged/unstaged/untracked diff so origin/main và nguồn evidence. Không yêu cầu chạy lại G4 tầng A của A01 hoặc database.
