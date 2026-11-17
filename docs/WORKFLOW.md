# Trà Hiên V2 – workflow Codex theo spec đã chốt

**Nguồn sự thật:** `docs/spec/00-README.md`, `01`–`09` Markdown và `schema.sql`; `docs/spec/07-roadmap.md` giữ duy nhất các ID `P0-01`…`P6-*`. Không tạo `TASK-XXX`/`REQ-###`. DEC-21 xác định ưu tiên SEO.

## 0. Phân công và tối ưu token
- Owner duyệt scope và tự merge `main`. Codex không tự merge.
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

## 4. G3: Một roadmap task
Trên `main` sạch đã đồng bộ, tạo feature branch riêng. Ví dụ task hiện tại (đổi ID cho task khác):
```text
Use $implementation-slice for P0-10 in docs/spec/07-roadmap.md.
Follow AGENTS.md and verified task-specific G1/G2 evidence.
Audit existing implementation and tests; add only missing scope.
Work in main Codex thread for this bounded task; no subagents unless justified.
Run applicable checks and report exact results/NOT RUN.
Commit/push feature branch, prepare PR; do not edit docs/spec or merge main.
```
Không mở rộng task sang runtime/refactor/migration không liên quan. Báo blocker có bằng chứng nếu vượt scope.

## 5. G4: Independent Review và Publish Gate
Review trong Codex session mới, không cho implementer tự review:
```text
Use $review-gate for P0-10.
Review actual feature-branch diff against latest origin/main, task spec and tests.
Report P0/P1 with file:line, impact, reproduction and minimal fix.
Separate VERIFIED / NOT RUN / NOT APPLICABLE; verdict PASS / CONDITIONAL / BLOCK.
Read-only: no edits, push, approval or merge.
```
BLOCK nếu còn P0/P1; sửa tối thiểu trên cùng branch và review lại. Owner tự merge khi G4 PASS, scope/test evidence đầy đủ. Chưa có CI thì chỉ báo local checks, không tự nhận CI PASS.

## 6. Sau khi spec/architecture đổi
Kiểm diff từ SHA baseline, đánh giá impact và chạy lại phần G1/G2 bị ảnh hưởng; full audit nếu đổi diện rộng. Không tự sửa `docs/spec/**`. Spec Kit, Superpowers, CodeRabbit chỉ tùy chọn, không thuộc workflow bắt buộc.
