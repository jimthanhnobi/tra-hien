# Trà Hiên V2 – workflow Codex theo spec đã chốt

**Nguồn sự thật:** `docs/spec/00-README.md`, `01`–`09` Markdown và `schema.sql`; `docs/spec/07-roadmap.md` giữ duy nhất các ID `P0-01`…`P6-*`. Không tạo `TASK-XXX`/`REQ-###`. DEC-21 xác định ưu tiên SEO.

## 0. Phân công và tối ưu token
- Owner duyệt scope, cho phép publish và tự merge `main`. Codex không tự merge trừ khi owner ủy quyền rõ cho đúng PR.
- Mặc định **một main Codex thread + đúng skill** cho một task nhỏ/vừa. Skill không bắt buộc gọi subagent tương ứng.
- Chỉ gọi `spec_analyst`/`planner` khi có gap cần giải quyết; chỉ spawn `implementer` nếu chia được workstream độc lập, rõ file ownership. Không cho hai agent sửa cùng file.
- G4 là reviewer độc lập trong session/context mới (skill `$review-gate` hoặc reviewer agent). Implementer không tự chấm G4.
- Không đọc lại toàn bộ spec/viết lại hồ sơ audit trên mọi PR. Tái sử dụng G1/G2 đã có **sau khi xác minh commit/diff spec và phạm vi ảnh hưởng**; vẫn soát requirement/dependency từng task.
- Đầu ra ngắn: task ID, base/head SHA, scope, diff, test commands/results, NOT RUN, blocker, next step. Không paste nguyên spec/source.

## 1. G0: Repo, scope và base
Trước khi làm: `git status --short`, `git fetch origin --prune`, `git log -1 --oneline`, HEAD của `origin/main`, task trong `07-roadmap.md` và dependency. Không `reset --hard`/`clean -fd` xóa thay đổi local.

## 2. G1: Spec audit
- **FULL:** chạy `$spec-audit` trên toàn bộ docs/spec (kể cả `00-README.md`, `09-seo.md`, `schema.sql`) khi chưa có baseline hoặc thay đổi diện rộng; lưu SHA, inventory, gaps, traceability.
- **TARGETED:** xác minh SHA/diff spec so với baseline, đọc phần liên quan, map FR/BR/NFR/AC/DEC và gap mới. Gọi `$spec-audit` targeted khi cần. Không sửa `docs/spec/**` nếu owner chưa yêu cầu.
- Không biến các vấn đề P3+ hoặc Q1–Q8 không liên quan thành blocker.

## 3. G2: Architecture/readiness
- Full `$architecture-plan` khi khởi tạo hoặc kiến trúc thay đổi đáng kể; tái sử dụng `docs/architecture/` và `docs/plans/task-readiness.md` nếu vẫn còn phù hợp.
- Mỗi task cần scoped G2 check: dependency, quyết định đã chốt, migration/security/testability, GO/HOLD và blocker trực tiếp.
- Không tạo kiến trúc thứ hai hoặc viết lại hàng loạt tài liệu nếu không có finding mới.

## 4. G3: Implement local, không tự publish
Trên `main` sạch đã đồng bộ, tạo feature branch local cho **một** roadmap task. Không chạy `git reset --hard`/`clean -fd` để giấu thay đổi. Mẫu prompt:
```text
Use $implementation-slice for the approved P*-* task in docs/spec/07-roadmap.md.
Follow AGENTS.md and verified task-specific G1/G2 evidence.
Audit code/tests first; implement only the scoped task.
Work in one Codex main thread; use subagents only when justified.
Run applicable focused/full checks; report exact PASS/FAIL/NOT RUN.
Stay on the local feature branch. DO NOT commit, push, open/update PR or merge.
Report git status --short, diff against origin/main, staged and untracked inventory,
changed files, tests and risks. Return READY_FOR_LOCAL_G4, then STOP.
```
G3 không đồng nghĩa với lệnh publish. Không cam kết CI đã chạy nếu code chưa được push. Chỉ test trên environment được phép, không động vào production.

## 5. G4 local → sửa → Publish → CI → owner merge
**G4 độc lập, context/session mới**, đọc cùng local worktree với G3. Bắt buộc rà soát `git diff origin/main`, `git diff --cached`, `git status --short` và `git ls-files --others --exclude-standard`; **đọc trực tiếp nội dung file untracked** vì `git diff` không tự hiển thị chúng. Đối chiếu spec, tests và thực tế hệ thống, không chỉ báo cáo implementer.
```text
Use $review-gate for the approved P*-* task, in a fresh independent session.
Review the SAME local feature worktree against latest origin/main.
Include committed/staged/unstaged changes AND untracked file contents.
Report P0/P1 findings with file:line, impact, reproduction and minimal fix.
Distinguish VERIFIED / NOT RUN / NOT APPLICABLE.
Return PASS / CONDITIONAL / BLOCK. Read-only: no edits/commit/push/merge.
```
Nếu reviewer không thấy được local worktree, **không** tự nhận G4 PASS; báo thiếu quyền truy cập. Chỉ khi owner cho phép mới publish reviewable snapshot/PR để reviewer làm G4 remote.

- **BLOCK:** G3 sửa đúng finding trên cùng local branch, chạy focused tests; G4 focused re-review thay vì audit lại toàn repo. Lặp tới khi không còn P0/P1.
- **G4 PASS:** Dừng và chờ owner **cho phép publish rõ ràng**. Sau đó mới commit/push, mở hoặc cập nhật **một PR**, đối chiếu published SHA với bản đã review.
- **CI:** chạy ở PR target `main`; ba required job backend/API, frontend, Docker phải PASS tại đúng HEAD. Nếu CI lỗi, sửa trên cùng branch, review thay đổi cần thiết, rồi publish lại theo owner approval.
- **Merge:** owner duyệt và merge thủ công; chỉ ủy quyền merge trong một yêu cầu rõ ràng. Push vào `main` sau merge cũng chạy CI, không push/deploy production tự động.
- **CI trigger:** `pull_request` vào `main` và `push` lên `main` (không đồng thời push `feature/**`). Tránh một HEAD xuất hiện 3 PASS + 3 CANCELLED do concurrency hủy run trùng.
- **Handoff tiết kiệm token:** G3 giao file/status/tests ngắn; G4 tập trung diff và failure cases; sau G4 chỉ đọc CI/head SHA thay vì full review lại nếu nội dung không đổi.

## 6. Sau khi spec/architecture đổi
Kiểm diff từ SHA baseline, đánh giá impact và chạy lại phần G1/G2 bị ảnh hưởng; full audit nếu đổi diện rộng. Không tự sửa `docs/spec/**`. Spec Kit, Superpowers, CodeRabbit chỉ tùy chọn, không thuộc workflow bắt buộc.
