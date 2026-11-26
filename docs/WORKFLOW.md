# Trà Hiên V2 — Quy trình vận hành Workflow V2

Luật bắt buộc và bất biến nằm trong [AGENTS.md](../AGENTS.md). Chữ ký lệnh, mã lỗi, giới hạn môi trường và khóa PG nằm trong [TOOLING.md](TOOLING.md). Hướng dẫn sử dụng thực tế và prompt mẫu ngắn ở [CODEX_USAGE.md](CODEX_USAGE.md). Các bước thực thi chi tiết của từng cổng nằm trong `.agents/skills/*/SKILL.md`. Tài liệu này mô tả trình tự, vòng đời, điều kiện chuyển cổng và quy tắc chuyển tiếp.

## 0. Vai trò, thẩm quyền và vòng đời Technical Lead

### 0.1 Owner
- Duyệt phạm vi task/chore, chốt các quyết định nghiệp vụ/kiến trúc (`OWNER_APPROVED`), cấp quyền bắt đầu G3, cấp quyền publish và tự merge vào `main` (trừ khi Owner ủy quyền rõ ràng cho đúng một PR cụ thể).
- **Chính sách độc lập với mô hình:** Owner tự chọn mô hình và mức suy luận trên giao diện hoặc bằng cấu hình do Owner kiểm soát. Quy trình không áp đặt tên mô hình, nhà cung cấp, mức suy luận hay yêu cầu review chéo hãng; agent không tự chuyển mô hình.

### 0.2 Technical Lead (điều phối — không mặc định viết production code)
- **Trách nhiệm:** đọc trực tiếp Git và trạng thái thực tế; phân tích dependency, file nóng dùng chung (`Program.cs`, `AppDbContext`, migrations/snapshot, `openapi.json`, `schema.ts`) và shared contracts; phát hiện Owner decision blockers; soạn prompt thực thi đúng phạm vi; đánh giá báo cáo G3/G4 bằng bằng chứng xác minh được; điều phối publish và bàn giao.
- **Tri thức bền vững & ranh giới quyết định:**
  - Git và `docs/spec/` là nguồn sự thật có thẩm quyền. ADR trong `docs/decisions/` lưu nguồn gốc quyết định (provenance), không bao giờ âm thầm ghi đè `docs/spec/`.
  - Luôn tách bạch trạng thái quyết định: `OWNER_APPROVED`, `IMPLEMENTED_BUT_NOT_CONFIRMED`, `PROPOSED`, `CONFLICT`, `UNRESOLVED`, `SUPERSEDED`. Việc merge PR không mặc nhiên là phê duyệt các giả định nghiệp vụ chưa có xác nhận tường minh của Owner.
  - Các snapshot như [task-readiness.md](plans/task-readiness.md) bắt buộc có thời điểm kiểm chứng và không bao giờ thay thế trạng thái Git trực tiếp (`git fetch`, `git log`, `git worktree list`, `candidate-manifest.ps1 -Verify`).
- **Quản lý ngữ cảnh & bàn giao (Stateless Lead):**
  - Không đặt ngưỡng token hay độ dài hội thoại cứng. Khi ngữ cảnh bắt đầu suy giảm hoặc khó kiểm chứng chính xác, Lead bảo toàn các quyết định/rủi ro quan trọng vào tài liệu bền vững (checkpoint/ADR/snapshot khi được phép), thu gọn ngữ cảnh hoặc chuyển sang hội thoại mới.
  - Phiên Lead mới tiếp quản hoàn toàn từ bằng chứng bền vững trong Git và trạng thái sống trên máy, không dựa vào trí nhớ hội thoại cũ.

## 1. Lập kế hoạch theo mức rủi ro và luồng cổng G0–G4

Giữ nguyên khung cổng **G0–G4** và phân tầng **A/B**, nhưng không bắt buộc mở hội thoại Planning riêng cho mọi task:

| Đường đi | Khi nào áp dụng | Thực hiện | Worktree |
|---|---|---|---|
| **A. Nhỏ, rõ scope** | Task/chore phạm vi hẹp, không có rủi ro nghiệp vụ/hợp đồng mới, baseline G1/G2 còn hiệu lực | Chạy scoped G1/G2 ngay trong G3 (`implementation-slice` §2) | Tạo ở G0 trước khi sửa candidate |
| **B. Phức tạp / rủi ro cao** | Có rủi ro đáng kể về nghiệp vụ, transaction, migration/DDL, auth/PII, shared contracts hoặc dependency phức tạp | Tách bước `spec-audit` / `architecture-plan` riêng để chốt hợp đồng/blocker trước G3 | Chưa tạo worktree nếu chưa sẵn sàng G3 |
| **C. Read-only Design Spike** | Chưa đủ readiness hoặc cần xác minh phương án kỹ thuật/tương tác mã nguồn trước khi Owner cấp quyền G3 | Chạy `architecture-plan` ở chế độ Design Spike chỉ đọc | **Không** tạo worktree |

**Quy tắc baseline G1/G2:**
- Không được bỏ qua Full G1 (`spec-audit` FULL) khi baseline chưa tồn tại, không xác minh được hoặc `docs/spec/` thay đổi diện rộng.
- Không dùng commit gần nhất chạm `docs/spec/` làm bằng chứng thay thế cho **audited spec SHA** thực tế của kỳ Full G1.
- Không tạo worktree sớm chỉ để lưu một file plan chưa sẵn sàng. **G0 và worktree riêng vẫn bắt buộc trước khi sửa bất kỳ file candidate nào.**
- Không tạo thêm mã roadmap hay backlog song song ngoài `docs/spec/07-roadmap.md`.

### Bảng chuyển bước một task

| Bước | Skill / Công cụ | Đầu ra & Điều kiện dừng |
|---|---|---|
| **G0** Preflight & worktree | `git fetch origin --prune` + `scripts/new-task-worktree.ps1` | Worktree riêng `D:\tra-hien-<task>`, nhánh riêng, xác minh BASE == `origin/main` |
| **G1/G2** Kiểm tra readiness | Scoped trong `implementation-slice` §2, hoặc `spec-audit` / `architecture-plan` riêng (kể cả Design Spike chỉ đọc) | `RECOMMENDATION: GO` hoặc `HOLD` kèm `file:line`, phương án & blocker |
| **G3** Triển khai & đóng băng | `implementation-slice` | Code/docs + tests + Immutable Record (nếu áp dụng) + `candidate.manifest` + full verification → **STOP tại `READY_FOR_LOCAL_G4`** |
| **G4** Review độc lập (Blind-First) | `review-gate` (hội thoại độc lập, chỉ đọc) | `VERDICT: PASS \| CONDITIONAL \| BLOCK` gắn đúng manifest SHA-256 |
| **Remediation & Re-review** | `implementation-slice` (remediation) → `review-gate` (focused hoặc full) | Manifest mới + verification phù hợp, lặp đến khi đóng hết P0/P1 |
| **`origin/main` thay đổi** | `base-integration` → `review-gate` (focused hoặc full theo chuyển tiếp) | Candidate tích hợp chưa commit + manifest mới + kiểm chứng mới |
| **Publish** (sau G4 PASS + lệnh Owner) | `publish-pr` (4 mốc kiểm tra A–D) | Commit đúng manifest, push, 1 PR mang Dynamic Task Status, kiểm đủ 4 job CI tại đúng published SHA |
| **Merge** | Owner | Merge thủ công; không tự deploy production |

## 2. Phân tầng A/B, Immutable Implementation Record và Dynamic Task Status

### 2.1 Phân tầng review A01
- **Tầng A:** Migration/DDL/grants, tiền, kho, auth/quyền/CSRF/PII, checkout công khai, index/canonical/sitemap, hoặc thay đổi luật kiểm soát bảo mật/cổng duyệt; phạm vi hỗn hợp tính là **Tầng A**.
- **Tầng B:** UI Admin trên API đã review, CRUD không tiền/kho/PII, tài liệu thông thường, refactor không đổi hành vi.
- **Bất biến chung:** Cả hai tầng đều cần independent G4 trên cùng worktree (đọc toàn bộ change set gồm cả untracked), candidate manifest, xác minh đầy đủ theo phạm vi thực tế, Owner cho publish, CI tại đúng published SHA và Owner merge. Không đặt giới hạn số trang hay số dòng cứng; đánh giá độ dài theo tính cần thiết và khả năng sử dụng, tuyệt đối không cắt bằng chứng bắt buộc.

### 2.2 Tách Immutable Implementation Record khỏi Dynamic Task Status
- **Immutable Implementation Record** (`docs/plans/<task>-implementation.md` theo [task-template.md](plans/task-template.md) cho task roadmap `P*-*`):
  - Lưu nội dung bền vững của lát cắt: phạm vi, approved BASE, yêu cầu/traceability, tham chiếu quyết định, thiết kế/shared contracts, tài sản thay đổi, bằng chứng có trước khi freeze và các giới hạn đã biết.
  - Nếu là tracked file thuộc candidate, **phải hoàn tất trước khi tạo manifest freeze**.
  - **Cấm tự tham chiếu:** không ghi manifest SHA-256 của chính candidate, published commit SHA, kết quả CI hay trạng thái merge chưa tồn tại vào tracked record.
  - **Cấm sửa sau G4 mà giữ verdict cũ:** sửa bất kỳ byte nào trên tracked record làm đổi manifest và bắt buộc re-freeze + re-review.
- **Dynamic Task Status:**
  - Lưu trạng thái sống: verdict G3/G4, digest manifest đã review, danh sách finding và trạng thái đóng, published HEAD, URL PR, kết quả 4 job CI, trạng thái merge và blocker hiện hành.
  - Lưu tại PR body (`.github/pull_request_template.md`), báo cáo bàn giao, `artifacts/` hoặc snapshot `task-readiness.md` có mốc thời gian theo thẩm quyền. Không bắt buộc tạo commit docs mới cho mỗi lần cập nhật trạng thái động.
- **Non-roadmap chores được Owner duyệt** (tooling, governance, maintenance): không tạo mã `TASK-XXX`/`REQ-###` và không bắt buộc thêm tracked file trong `docs/plans/` ngoài allowlist; báo cáo bàn giao theo Unified Handoff Contract, PR body (và ADR nếu Owner yêu cầu) đóng vai trò hồ sơ chính thức.

## 3. Unified Handoff Contract và chuẩn bằng chứng

Mỗi bước thực thi chỉ trả **đúng một báo cáo bàn giao bằng tiếng Việt** theo cấu trúc thống nhất dưới đây (không lặp lại cùng nội dung dưới nhiều mẫu). Tầng B có thể viết gọn hơn nhưng không bỏ bằng chứng bắt buộc. Báo cáo G1/G2 chỉ đọc không cần điền `MANIFEST` hoặc trường không áp dụng. Không dán toàn bộ source code, spec hoặc raw log vào báo cáo.

```text
TASK / SCOPE / TIER / STATUS: <P*-* hoặc chore được duyệt> / <phạm vi ngắn> / A | B / <trạng thái cổng>
WORKTREE / BRANCH / BASE / HEAD: <path> / <branch> / <base SHA> / <HEAD SHA>
CHANGED: <tracked M/A/D; untracked; staged — hoặc none nếu chỉ đọc>
MANIFEST: <path> sha256=<digest> (<n> entries, base=<sha>) [MATCH|MISMATCH]   # bỏ qua nếu không áp dụng
COVERAGE & VERIFICATION: <coverage token> | <lệnh chính xác> -> RESULT=<...> exit_code=<n> (<log/evidence path>)
FAILED_RUNS_AND_RETRIES: none | <lần chạy lỗi có ý nghĩa, mã lỗi, log và lý do retry>
FINDINGS / CLOSURE: none | <[P0|P1|P2|P3] path:line ... hoặc trạng thái CLOSED/OPEN>
NOT_RUN / NOT_APPLICABLE: <hạng mục + lý do>
BLOCKERS / RESIDUAL_RISK / NEXT: <blocker, rủi ro còn lại và đúng một bước an toàn kế tiếp>
```

**Nhãn bằng chứng chuẩn (bắt buộc nhất quán):**
- `VERIFIED`: tự chạy lệnh hoặc tự đọc nguồn trực tiếp trong phiên hiện tại.
- `REPORTED`: do agent khác hoặc Owner báo cáo, chưa tự kiểm chứng lại trong phiên hiện tại.
- `UNKNOWN`: chưa có bằng chứng xác định.
- `NOT_RUN`: kiểm tra có áp dụng nhưng chưa chạy (ví dụ GitHub Actions CI hoặc Docker regression builds khi còn ở local).
- `NOT_APPLICABLE`: không áp dụng cho phạm vi thay đổi.

Không che giấu các lần chạy thất bại có ý nghĩa và các lần thử lại. Không suy đoán nguyên nhân gốc chắc chắn khi log/diagnostics chưa đủ chứng cứ.

## 4. Independent G4 (Blind-First), Remediation và Re-review

### 4.1 Independent G4 và quy trình Blind-First
- G4 bắt buộc chạy trên **hội thoại độc lập** với Implementer cho cả Tầng A và Tầng B, ở chế độ **chỉ đọc** (không sửa file, commit, push, approve hay merge).
- **Trình tự Blind-First:**
  1. Xác minh danh tính candidate (`candidate-manifest.ps1 -Verify`) và rà soát toàn bộ change set gồm committed, staged, unstaged và **đọc trực tiếp nội dung mọi file untracked**.
  2. Tự đọc yêu cầu, `docs/spec/`, quyết định đã duyệt và mã/tài liệu candidate.
  3. Kiểm tra các bất biến, đường lỗi, biên bảo mật/dữ liệu/transaction.
  4. Tự chạy kiểm chứng thực tế theo phạm vi bắt buộc.
  5. **Sau đó mới** đối chiếu kết luận bàn giao của G3.
- Verdict (`PASS` / `CONDITIONAL` / `BLOCK`) chỉ có hiệu lực với đúng `manifest sha256` đã kiểm.
- Test `PASS` không đồng nghĩa `G4 PASS`. `CANDIDATE MATCH` không chứng minh test đã chạy. Không ép tạo finding khi không có lỗi thực tế (ghi rõ không có lỗi cần xử lý trong phạm vi review, không tuyên bố "không còn bug").

### 4.2 Remediation và Focused Re-review
- **Chọn phiên G3 cho remediation:**
  - *Tái sử dụng phiên G3* khi nguyên nhân gốc rõ ràng, ngữ cảnh còn đáng tin cậy và phạm vi sửa giới hạn.
  - *Mở phiên G3 mới* khi finding nghiêm trọng lặp lại, ngữ cảnh suy giảm hoặc cần đánh giá lại thiết kế/bất biến dùng chung.
- **Chọn phiên G4 cho re-review:**
  - *Được phép tái sử dụng phiên G4 độc lập* cho focused re-review khi ngữ cảnh và tính độc lập vẫn được bảo toàn.
  - *Mở phiên G4 mới* khi ngữ cảnh không còn đáng tin cậy hoặc cần một lượt đánh giá độc lập toàn diện mới.
- **Xác định phạm vi re-review:** căn cứ vào **delta thực tế**, **nguyên nhân gốc**, **bán kính ảnh hưởng (blast radius)**, **đường thực thi dùng chung** và **bất biến bảo mật/dữ liệu/transaction**. Không dùng phần trăm file thay đổi hay số vòng remediation làm hard gate duy nhất.
- Mọi thay đổi candidate đều phải tạo manifest mới và có bằng chứng kiểm chứng tương ứng. Focused source review **không mặc nhiên** cho phép thay full verification bằng filtered tests.

## 5. Thực thi song song, kiểm chứng PR-2 và Host PG Lock

- **Một writer duy nhất trên mỗi worktree.** Cho phép chạy song song trên các worktree riêng (`scripts/new-task-worktree.ps1`) khi dependency, shared contracts và file nóng dùng chung không xung đột. Không hardcode số lượng agent tối đa nếu không có giới hạn tài nguyên thực tế.
- **Hợp đồng kiểm chứng (chi tiết lệnh & mã lỗi tại [TOOLING.md](TOOLING.md)):**
  - Luôn kiểm đồng thời 4 trường đầu ra của `verify-linux.ps1`: `RESULT`, `coverage`, `manifest_sha256`, `exit_code`.
  - Mức phủ: `FULL_BACKEND`, `FULL_ALL`, `FOCUSED_BACKEND`, `FOCUSED_ALL`, `API`, `FRONTEND`, `DOCS`, và `NONE` khi `FAIL`.
  - Chạy có `-Filter` cho kết quả `RESULT=FOCUSED_PASS` (không chạy kiểm tra zero-skip), **không được thay thế** full candidate verification và **cấm** kết hợp `-Manifest` với `-Filter`.
  - Kiểm chứng `-Mode docs -Manifest <manifest>` chỉ áp dụng khi candidate nằm hoàn toàn trong allowlist của `scripts/ci-fastpath.py` (`README.md`, `docs/**/*.md`). **Không** áp dụng docs fast-path cho `AGENTS.md`, `.agents/`, `.codex/`, `scripts/`, `.github/` hay `docs/spec/schema.sql`.
  - Phân biệt phân loại CI (`docs`/`frontend`/`backend`/`full`) với kiểm chứng local: local `FULL_ALL` không chứng minh GitHub Actions hay `Docker regression builds` đã chạy.
- **Host PostgreSQL Lock:** chế độ `backend`/`full` dùng khóa PG cấp máy chủ (exit `5` contention, `6` residual workload, `7` lỗi filesystem/quyền; hỗ trợ `-LockWaitSeconds`); các chế độ `api`/`frontend`/`docs` không dùng khóa này. Worktree cũ chưa tích hợp PR-2 không được khóa tự động; khóa không bảo vệ mọi workload Docker/OS user/Docker host hay child-container sót lại không gắn nhãn. Cấm dùng `TRA_HIEN_TEST_LOCK_PATH` khi kiểm chứng candidate thật, cấm `kill`/`rm` container của agent khác, không retry vô hạn.
- **Tích hợp base không dùng stash:** `base-integration` dùng `scripts/task-integrate.ps1` (ref riêng `refs/task-integration/<task>`) cho nhánh chưa publish và `git merge --no-ff --no-commit origin/main` cho nhánh đã publish; không dùng `git stash`.

## 6. Publish qua 4 mốc kiểm tra, CI và Merge

Chi tiết lệnh từng bước nằm tại `.agents/skills/publish-pr/SKILL.md` và [TOOLING.md](TOOLING.md):

1. **Mốc A — Pre-stage:** xác nhận lệnh cho phép publish của Owner trong phiên hiện tại, Independent G4 `PASS` cho đúng manifest SHA-256, `git fetch origin --prune` + base freshness, phạm vi/topology và trạng thái nhánh remote (`-PublishMode initial` hoặc `-PublishMode republish -PublishedHead origin/<branch>` kèm kiểm trực tiếp remote HEAD và lịch sử commit được ủy quyền, không chỉ dựa vào ancestry).
2. **Mốc B — Post-stage, pre-commit:** chỉ stage đúng các path trong manifest (kể cả `DELETED`, không `git add -A` mù); chạy `-Verify <manifest> -Both -RequireBase origin/main -PublishMode ...`; kiểm inventory mode/blob của index; đối chiếu **tương đương cây candidate hoàn chỉnh** giữa staged index (`git write-tree`) với cây candidate đã review dựng độc lập từ `<base>` + manifest (hoặc `tree <snapshotTree>` đã xác minh trong log `verify-linux.ps1 -Manifest`), không tự so sánh `git write-tree` của staged index với chính nó.
3. **Mốc C — Post-commit, pre-push:** commit theo Conventional Commits (hoặc commit merge hợp lệ có `MERGE_HEAD` từ `base-integration`), kiểm committed tree, parent/commit topology, delta được ủy quyền, working tree sạch, base freshness và remote branch expectation. **Không** chạy `-PublishMode initial` sau khi commit (vì `HEAD != base`). **Cấm force push.**
4. **Mốc D — Post-push:** xác nhận `remote HEAD == published HEAD`; PR body chứa Dynamic Task Status; xác minh đủ **4 job GitHub Actions** (`Classify changes and validate docs`, `Backend and API contract`, `Frontend verification`, `Docker regression builds`) tại đúng published SHA, phân biệt rõ job `success`, suite đã thực sự chạy và fast-path/`not applicable`. Owner tự merge.
- Nếu CI lỗi phải sửa candidate: sửa trên cùng nhánh, tạo manifest mới, chạy kiểm chứng và re-review tương ứng, rồi mới republish khi Owner đồng ý. Nếu `origin/main` dịch chuyển: chạy `base-integration` theo quy định hiện hành.

## 7. Quy tắc chuyển tiếp (Transition Rules) và thay đổi đặc tả

- Các task đang triển khai dở dang trước khi Workflow V2 merge **không** phải triển khai lại toàn bộ chỉ vì quy trình được cập nhật.
- Review đã có của một candidate chỉ còn giá trị đối với **đúng snapshot/manifest đã được review**.
- Trước khi publish, mọi task đều phải đáp ứng đầy đủ yêu cầu hiện hành về `base-integration`, kiểm chứng manifest-bound, re-review và 4 mốc publish.
- Nếu một task **chưa có G4 verdict** và governance cho phép tích hợp base mới trước khi review, ưu tiên tích hợp base trước rồi chạy **một lần G4 đầy đủ** trên candidate đã tích hợp thay vì tạo thêm vòng review trên base cũ không cần thiết — tuyệt đối không bỏ qua cổng bắt buộc nào.
- Khi `docs/spec/` hoặc kiến trúc thay đổi: kiểm diff so với audited spec SHA, chạy lại phần G1/G2 bị ảnh hưởng (hoặc Full audit nếu thay đổi diện rộng). Không tự sửa `docs/spec/`.
