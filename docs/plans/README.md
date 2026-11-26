# Plans & Implementation Records

Thư mục này chứa **Immutable Implementation Records** (`<task>-implementation.md`) cho các mã roadmap hiện hữu `P0-01`…`P6-*`, lập theo [task-template.md](task-template.md). Backlog duy nhất của dự án là `docs/spec/07-roadmap.md`: không tạo `tasks.md`, `implementation-plan.md`, `TASK-XXX`, `REQ-###` hay danh sách công việc song song.

## Phân tách Immutable Record và Dynamic Task Status

- **Immutable Implementation Record (`<task>-implementation.md`):** tài liệu theo dõi cố định cho lát cắt G3 (phạm vi, approved BASE, yêu cầu, tham chiếu quyết định, thiết kế, tài sản thay đổi, bằng chứng có trước khi freeze, giới hạn đã biết). Hoàn tất **trước** khi tạo `candidate.manifest`; không nhúng manifest SHA-256 tự tham chiếu, published commit SHA, kết quả CI hay trạng thái merge chưa tồn tại; không sửa file đã track sau G4 mà giữ nguyên verdict cũ.
- **Dynamic Task Status:** verdict G3/G4, digest manifest đã review, trạng thái đóng findings, published HEAD, URL PR, kết quả 4 job CI, trạng thái merge và blocker hiện hành được ghi tại PR body, báo cáo bàn giao theo Unified Handoff Contract, `artifacts/` hoặc snapshot có mốc thời gian tại [task-readiness.md](task-readiness.md) (không thay thế trạng thái Git trực tiếp). Không bắt buộc commit docs mới cho mỗi cập nhật trạng thái động.
- **Non-roadmap chores được Owner phê duyệt** (tooling, governance, maintenance): không tạo mã `TASK-XXX`/`REQ-###` và không tự thêm file tracked mới trong `docs/plans/` ngoài phạm vi allowlist đã duyệt; hồ sơ và trạng thái động nằm ở báo cáo bàn giao, PR body và/hoặc ADR theo thẩm quyền (xem [WORKFLOW.md](../WORKFLOW.md) §2.2).
- Ghi chú thiết kế dùng chung (ví dụ [p1-inventory-architecture.md](p1-inventory-architecture.md)) bao phủ nhiều task hiện hữu; các hồ sơ task cũ đã merge giữ nguyên làm bằng chứng lịch sử.
