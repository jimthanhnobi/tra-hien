# Immutable Implementation Record Template — Authoritative Roadmap IDs Only

> **Quy tắc hồ sơ (xem [WORKFLOW.md](../WORKFLOW.md) §2.2):** Đây là **Immutable Implementation Record** cho một task roadmap `P*-*` từ `docs/spec/07-roadmap.md`, không phải backlog mới. Giữ nguyên `FR-*`, `BR-*`, `NFR-*`, `AC-*`, `DEC-*`; không tạo `TASK-XXX` hoặc `REQ-###`. Không đặt giới hạn số trang/dòng cứng — viết đủ rõ, dễ kiểm chứng, dẫn link log dưới `artifacts/verify-linux/` thay vì dán raw output.
>
> **Chống tự tham chiếu:** Hoàn tất tệp này **trước** khi tạo `candidate.manifest`. **Không** ghi manifest SHA-256 của chính candidate này, published commit SHA, verdict G4, kết quả CI hay trạng thái merge chưa tồn tại vào đây. Không sửa tệp sau G4 mà tiếp tục dùng verdict cũ. Mọi **Dynamic Task Status** (manifest SHA-256, log verify sau freeze, verdict G4, đóng findings, published HEAD, URL PR, CI 4 job, trạng thái merge) được lưu ở báo cáo bàn giao, PR body, `artifacts/` hoặc snapshot `task-readiness.md`.

## P*-*: <existing title from 07-roadmap.md>

- **Tier:** `A` | `B` (lý do phân tầng theo A01).
- **Approved BASE & Context:** `origin/main` SHA `<base-sha>`, branch `<branch>`, worktree `<path>`.
- **Scope / Out of scope:** hành vi/tài sản thay đổi trong lát cắt này; phần thuộc task `P*-*` sau hoặc ngoài phạm vi.
- **Readiness & Decision References:**
  - Audited Full G1 spec SHA + diff `docs/spec/` liên quan (không dùng commit chạm spec gần nhất thay thế audited SHA).
  - Tham chiếu G2 (`docs/architecture/conformance-review.md`, `docs/plans/task-readiness.md`, ADR liên quan) và xác nhận dependencies đã merge.
  - Phân biệt rõ trạng thái từng quyết định được tham chiếu: `OWNER_APPROVED`, `IMPLEMENTED_BUT_NOT_CONFIRMED`, `PROPOSED`, `CONFLICT`, `UNRESOLVED`.
- **Requirements & Spec Traceability:** `docs/spec/...` (path, heading, line) và `FR-*`, `BR-*`, `NFR-*`, `AC-*`, `DEC-*` → files / tests.
- **Design Decisions & Shared Contracts:** bất biến nghiệp vụ, ranh giới transaction, quyền hạn/PII, thứ tự khóa, hợp đồng dùng chung và tương tác file nóng.
- **Changed Assets:** danh sách nhóm file mã nguồn, test, hợp đồng API sinh tự động, tài liệu thay đổi.
- **Migration / Recovery:** `none`, hoặc migration ID, kế hoạch `Down`/recovery, grants `tp_app`/`tp_readonly`, tác động dữ liệu và thứ tự deploy.
- **Pre-Freeze Evidence:**
  - Ma trận kịch bản kiểm thử (happy, bad, boundary, permission/auth, concurrency, abuse — tùy phạm vi) → tên test.
  - Các lệnh kiểm chứng đã chạy trước freeze (hoặc ghi rõ lượt kiểm chứng cuối gắn manifest nằm ở báo cáo bàn giao và `artifacts/verify-linux/`).
- **Known Limitations, Residual Risks & Follow-ups:** giới hạn đã biết, hạng mục `NOT_RUN`/`NOT_APPLICABLE`, bàn giao cho các task `P*-*` sau.
