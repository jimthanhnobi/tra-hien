# Trà Hiên V2 – workflow theo spec đã chốt (Codex + Antigravity)

**Nguồn sự thật:** `docs/spec/00-README.md`, `01`–`09` Markdown và `schema.sql`. `docs/spec/07-roadmap.md` là nơi duy nhất giữ các ID `P0-01`…`P6-*`. Không tạo `TASK-XXX`/`REQ-###`. Khi có xung đột về SEO thì theo DEC-21. Luật bắt buộc nằm trong `AGENTS.md`, quy trình từng bước nằm trong skill ở `.agents/skills/`. File này mô tả cách các bước nối vào nhau. Prompt mẫu ở [CODEX_USAGE.md](CODEX_USAGE.md).

## 0. Vai trò và nguyên tắc tiết kiệm
- Owner duyệt scope, cho phép publish và tự merge `main`. Agent không tự merge, trừ khi owner ủy quyền rõ cho đúng PR đó.
- Mỗi task chạy một main thread với đúng skill tương ứng. Gọi skill không có nghĩa là phải spawn subagent. Chỉ gọi subagent khi có gap thật sự hoặc có workstream độc lập với file ownership riêng.
- G4 phải là session mới và độc lập, nên dùng model khác hãng với lúc code. Implementer không tự chấm G4.
- Tái sử dụng G1/G2 đã có sau khi kiểm diff spec. Chỉ đọc lại toàn bộ spec khi chạy full audit.
- Handoff dùng block cố định của skill: task ID, SHA, file, lệnh test kèm kết quả, NOT RUN, blocker. Không paste nguyên spec hay source.

## 1. Luồng một task

| Bước | Skill | Đầu ra |
|---|---|---|
| G0 tạo worktree từ `origin/main` mới | `scripts/new-task-worktree.ps1` | `D:\tra-hien-<task>`, branch riêng, base SHA |
| G1/G2 scoped (xác minh baseline G1/G2, kiểm diff spec, dependency, hợp đồng) | trong `implementation-slice` §2; thiếu baseline hoặc spec đổi lớn thì chạy `spec-audit` FULL / `architecture-plan` baseline **trước** G3 | `READINESS: GO` hoặc `GAP` kèm phương án |
| G3 code, test, hồ sơ ngắn, manifest nội dung | `implementation-slice` | `READY_FOR_LOCAL_G4` + `candidate.manifest` |
| G4 review độc lập, chỉ đọc | `review-gate` | PASS / CONDITIONAL / BLOCK gắn với SHA-256 của manifest |
| Sửa theo finding | `implementation-slice` (remediation) → `review-gate` (focused) | lặp tới khi không còn P0/P1 |
| `main` đổi trước khi publish | `base-integration` → focused G4 (cả hai tầng) | delta tích hợp đã kiểm, chưa commit |
| Publish khi owner cho phép | `publish-pr` | index khớp manifest, rồi commit, push, PR, CI tại published SHA |
| Merge | owner | — |

- Có GAP thì dừng lại hỏi owner. Không tự thêm bảng, endpoint, setting hay vendor.
- G3 không đồng nghĩa với publish. Không nói CI đã chạy khi code chưa push. Chỉ test trên môi trường được phép, không đụng production.
- G4 bắt buộc rà soát `git diff origin/main`, `git diff --cached`, `git status --short` và `git ls-files --others --exclude-standard`, đồng thời **đọc trực tiếp nội dung file untracked**. Nếu reviewer không đọc được worktree thì báo NOT RUN. Chỉ khi owner cho phép mới publish reviewable snapshot để review remote.

## 2. Phân tầng review A01, không bỏ gate

| Tầng | Phạm vi | Review |
|---|---|---|
| A | Migration/DDL/grants, tiền, kho, auth/quyền/CSRF/PII, checkout công khai, index/canonical/sitemap | G4 độc lập đầy đủ, hồ sơ ≤ 2 trang |
| B | UI Admin dùng API đã review, CRUD không tiền/kho/PII, tài liệu, refactor không đổi hành vi | G4 độc lập, checklist theo phạm vi, hồ sơ ≤ 1 trang |

Task chạm cả hai tầng thì tính là A. Cả A và B đều cần: session mới đọc cùng worktree, rà toàn bộ change set kể cả untracked, G4 PASS, owner cho publish, CI tại đúng published SHA và owner merge. Giới hạn trang chỉ là mục tiêu, không được cắt bằng chứng về acceptance, security hay test.

## 3. Chạy song song Codex và Antigravity
- **Một tool ghi trên một worktree.** Hai tool có thể chạy cùng lúc nhưng mỗi tool ở một worktree riêng (`scripts/new-task-worktree.ps1`). Reviewer G4 đọc worktree của task với quyền chỉ đọc.
- **Pipeline gợi ý:** tool X làm G3 task N+1 (chỉ khi dependency của nó đã merge, hoặc phần việc không phụ thuộc task N), trong khi tool Y review G4 task N. Đổi vai giữa hai tool để review luôn chéo hãng.
- **Task phụ thuộc nhau** (ví dụ P1-13 cần P1-10): làm nối tiếp. Nếu bắt buộc chuẩn bị trước thì chỉ làm phần độc lập, và chạy `base-integration` sau khi task trước merge.
- **File nóng dùng chung** (`Program.cs`, `AppDbContext`, migration snapshot, `openapi.json`, `schema.ts`): PR merge sau phải chạy `base-integration` và sinh lại contract, không merge tay.
- **Tài nguyên:** hai lần chạy `verify-linux.ps1` song song dùng chung cache volume `tra-hien-tools-cache` một cách an toàn, mỗi lần tạo PostgreSQL Testcontainers riêng và chạy bằng image ID cố định. Máy yếu thì đừng chạy hai full suite cùng lúc.

## 4. Kiểm chứng và bằng chứng
- `./scripts/verify-linux.ps1` chạy các bước giống job CI backend (classifier, restore khóa, build `-warnaserror`, format, toàn bộ backend test trên PG17, kiểm 0 skip, migration SQL, `check:api`). `-Mode api -WriteBack` sinh lại contract và chỉ báo PASS khi ghi đủ cả hai file và hash khớp. `-Mode frontend`/`full` chạy thêm frontend, `-Filter` dùng cho test tập trung. Log lưu ở `artifacts/verify-linux/` (git-ignored).
- **Cô lập:** container chỉ nhận một snapshot đã lọc trên host, gồm file tracked và file untracked không bị ignore. Vì vậy `infra/.env`, `artifacts/`, `bin/obj`, `node_modules` không bao giờ vào container. Tên file giống secret (`.env*` trừ `*.example`, `*.pem`, `*.key`, `*.pfx`, `*.p12`, `*.secret`, `secrets/`) bị từ chối ngay trên host, kể cả khi đã bị force-add vào index. Docker socket chỉ mount cho mode `backend`/`full` (Testcontainers cần). Image được gắn tag theo SHA-256 của `infra/Dockerfile.tools` và chạy bằng image ID, nên hai worktree chạy song song không tranh nhau tag.
- Bản candidate cuối luôn chạy full suite một lần với `-Manifest`. Khi đó snapshot được dựng từ git object có ID trùng manifest (base tree + các entry đã review), nên container chạy đúng candidate đã đóng dấu, và log ghi SHA-256 của manifest. Không thay bước này bằng việc tái sử dụng bằng chứng theo hash.
- Hồ sơ task lập theo [task-template.md](plans/task-template.md). **Manifest nội dung bắt buộc cho cả hai tầng** (`scripts/candidate-manifest.ps1`, ghi Git mode và blob ID của từng path): G3 ghi, G4 xác minh trước khi review, `publish-pr` xác minh lại working tree và index ngay trước khi commit. Sửa bất kỳ byte nào hoặc đổi mode file sau G4 sẽ làm lệch manifest và chặn publish.
- **Tích hợp base không dùng stash:** các worktree dùng chung `refs/stash`, nên `base-integration` dùng `scripts/task-integrate.ps1`. Script ghim candidate vào ref riêng của task (`refs/task-integration/<task>`), rebase branch chưa publish, rồi trả candidate về trạng thái chưa commit. Ref chỉ bị xóa khi vẫn đúng SHA đã ghi. Không dùng `git stash`.
- Agent chạy trong sandbox cần bypass/approval cho Docker và cho các lệnh git ghi `.git` hay truy cập mạng (fetch, worktree, commit, push).

## 5. Publish, CI và merge
- **G4 PASS:** dừng lại chờ owner **cho phép publish rõ ràng**. Sau đó chạy `publish-pr`: đối chiếu bản đã review với bản publish, rồi commit, push và mở **một PR**.
- **CI:** chạy trên `pull_request` vào `main` và `push` lên `main`. Cả bốn job `Classify changes and validate docs`, `Backend and API contract`, `Frontend verification`, `Docker regression builds` phải PASS tại đúng HEAD. CI lỗi thì sửa trên cùng branch, chạy focused G4, rồi publish lại khi owner đồng ý. Push lên `main` dùng `cancel-in-progress: false` để không hủy CI của merge.
- **Merge:** owner duyệt và merge thủ công. Không tự động deploy production.
- **Sau G4:** nếu nội dung không đổi thì chỉ cần kiểm CI và head SHA, không review lại toàn bộ.

## 6. Khi spec hoặc kiến trúc đổi
Kiểm diff so với SHA baseline, đánh giá ảnh hưởng và chạy lại phần G1/G2 bị ảnh hưởng. Đổi diện rộng thì chạy full audit. Không tự sửa `docs/spec/**`. Spec Kit, Superpowers, CodeRabbit chỉ là công cụ tùy chọn, không thuộc workflow bắt buộc ([TOOLING.md](TOOLING.md)).
