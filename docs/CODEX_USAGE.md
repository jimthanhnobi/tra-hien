# Dùng Codex trực tiếp trong Trà Hiên V2

## Bước 1: Mở đúng repository

~~~powershell
git clone https://github.com/jimthanhnobi/tra-hien.git
cd tra-hien
codex
~~~

Nếu đã clone trước khi cấu hình mới được push, chỉ cần `git pull origin main` trên nhánh main không có local edits xung đột, rồi mở lại Codex.

Có thể mở thư mục `tra-hien` bằng Codex desktop hoặc Codex extension trong VS Code thay cho Codex CLI.

## Bước 2: Kiểm tra skill và agents

- Codex đọc `AGENTS.md` ở gốc repository tự động.
- Các skill nằm ở `.agents/skills/<name>/SKILL.md`; trong ô chat Codex, dùng `/skills` hoặc nhập `$` để chọn skill.
- Các custom subagent nằm ở `.codex/agents/*.toml` và được bật bằng `.codex/config.toml`.
- Nếu chưa thấy skill/agent, kiểm tra project đã được trust và khởi động lại phiên Codex. Kiểm tra folder gốc, các file thực sự có mặt và tình trạng tin cậy/trust của workspace.

## Các skill hiện có

- `$spec-audit`: kiểm kê yêu cầu, xung đột, khoảng trống; chưa code.
- `$architecture-plan`: so sánh phương án, thiết kế, ADR, milestone/task; chưa code.
- `$implementation-slice`: implement từng task đã duyệt và test.
- `$review-gate`: review thay đổi theo spec, rủi ro, test, bằng chứng.

## Các agent hiện có

- `spec_analyst`: đọc và kiểm tra spec (read-only theo quy trình).
- `planner`: đối chiếu kiến trúc và xây kế hoạch (read-only theo quy trình).
- `implementer`: thực hiện một task đã duyệt.
- `reviewer`: review độc lập, không tự sửa.

Các file agent kế thừa permission/sandbox của phiên Codex cha. Chỉ ghi 'không sửa' trong instruction không biến agent thành sandbox read-only về mặt bảo mật.

## Prompt sử dụng sau khi đã paste spec

**Audit (vẫn là vòng không code):**

~~~text
Use $spec-audit. Delegate the requirements inventory to spec_analyst.
Read docs/spec/00-README.md, all 01–09 Markdown and schema.sql; preserve existing FR/BR/NFR/AC/DEC IDs and the 07-roadmap P0–P6 task list. Do not change source specs.
Produce docs/analysis/spec-inventory.md, spec-gaps.md, traceability.md.
Report BLOCKER questions first. Do not design architecture or write application code yet.
~~~

**Lập plan:**

~~~text
Use $architecture-plan. Delegate an independent conformance review to planner.
Respect the decisions in 00-README.md and the P0-P6 roadmap in 07-roadmap.md.
Write docs/architecture/conformance-review.md and docs/plans/task-readiness.md;
do not redesign or invent TASK-XXX. Assess readiness for P0-01. No code yet.
~~~

**Code theo task:**

~~~text
Use $implementation-slice. Delegate P0-01 in docs/spec/07-roadmap.md to implementer only after approval.
Restrict changes to that task; add tests and execute checks.
Return exact commands/results and a PR-ready summary. Do not merge.
~~~

**Review độc lập:**

~~~text
Use $review-gate. Delegate diff review to reviewer; keep implementer out of the reviewer role.
Check this branch against main and against approved spec/task acceptance criteria.
Find P0/P1 issues first. List tests actually verified versus not run. Do not merge.
~~~

Trong Codex CLI, `/subagents` cho phép xem các thread agent. Trong ứng dụng/IDE, theo dõi subagent từ giao diện thread hoặc panel khi hỗ trợ. Subagents có thể tiêu tốn thêm token.

## Khi cập nhật bộ spec

Các file spec thật hiện nằm trong `docs/spec/`, gồm `00-README.md`, `01`–`09` Markdown và `schema.sql`. `08-chatgpt-playbook.md` là mẫu giao việc, không phải chỉ dẫn buộc Codex xuất toàn bộ nội dung file vào chat. Có thể commit/push thay đổi spec mà không cần chạy script xác minh repo hay GitHub Actions guard. Sau khi push, chạy lại `$spec-audit` để cập nhật phát hiện và traceability; chỉ triển khai code sau khi các quyết định quan trọng được duyệt.

Xem: https://learn.chatgpt.com/docs/build-skills và https://learn.chatgpt.com/docs/agent-configuration/subagents
