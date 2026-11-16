# Trà Hiên V2 – workflow Codex dựa trên spec đã chốt

**Nguồn sự thật:** `docs/spec/00-README.md` + `01`–`09` Markdown + `schema.sql` ở commit đã kiểm tra. Giữ mã FR/BR/NFR/AC/DEC và `P0-01`…`P6-*`. Quyết định SEO có quy tắc ưu tiên riêng (DEC-21).

## 1. Import / cập nhật spec

- Có thể sửa và push `docs/spec/` như bình thường. Không có repository-guard CI.
- Xem `git status`, `git log -1 --oneline` để đảm bảo dùng đúng phiên bản.
- Không tự ý sửa spec bằng agent. Nếu có conflict, ghi riêng vào gap register; thay đổi spec phải được người dùng duyệt.

## 2. Audit (đọc-only đối với spec; chưa code)

Trong Codex chat:
```text
Use $spec-audit. Have spec_analyst review the authoritative V2 specifications:
docs/spec/00-README.md through 09-seo.md and schema.sql.
Preserve FR/BR/NFR/AC/DEC and P0-P6 IDs. Check cross-file consistency,
SQL model compatibility, auth/CSRF, SEO and roadmap dependencies.
Write docs/analysis/spec-inventory.md, spec-gaps.md, traceability.md.
Separate blockers for P0-01 from P3+ go-live decisions.
Do not edit docs/spec or implement code.
```

Thẩm định kết quả audit; quyết định các blocker ảnh hưởng P0 trước khi bắt đầu P0-01. Không buộc mọi Q1–Q8 phải chốt ngay khi chưa liên quan.

## 3. Architecture conformance review (chưa code)

```text
Use $architecture-plan with planner. Review existing DEC-01..DEC-24,
02-architecture.md, schema.sql and 07-roadmap.md.
Do not create an alternative architecture or duplicate task IDs.
Create docs/architecture/conformance-review.md and
docs/plans/task-readiness.md with specific gaps and dependencies.
Return GO/HOLD for P0-01; no implementation.
```

Nếu cần đổi thiết kế đã chốt, viết đề xuất nhỏ và lý do, để chủ dự án phê duyệt.

## 4. Implement ONE roadmap task

Tạo branch từ main sạch: `git switch -c feature/p0-01-foundation`. Sau khi chủ dự án duyệt, trong Codex:

```text
Use $implementation-slice. Implement only P0-01 in docs/spec/07-roadmap.md.
Use existing architectural decisions and business requirements.
Delegate the bounded coding work to implementer.
Run real build/tests available for the new scaffold; report commands and results.
Do not modify docs/spec. Do not implement P0-02 or merge.
```

## 5. Review trước merge

```text
Use $review-gate. Have reviewer inspect the diff of this branch versus main
against P0-01, FR/BR/NFR/AC/DEC and actual tests.
Report findings P0–P3 with file/line and reproduction; don't modify code
or claim unrun tests passed. Human review before merge.
```

Gắn phát hiện vào PR; tự chạy lại build/tests; chỉ merge khi không còn P0/P1 và đã xác nhận đủ bằng chứng. Một task ≈ một PR.

## 6. Sau khi spec thay đổi

Chạy lại spec-audit với `git diff` giữa hai phiên bản spec và đánh giá tác động lên task/code/test đã triển khai. Update traceability; đừng tự đổi code không liên quan.

## Công cụ tùy chọn

`github/spec-kit`, `obra/superpowers`, `CodeRabbit` có thể hỗ trợ, nhưng không phải điều kiện bắt buộc. Đừng bật đồng thời nhiều bộ workflow tạo bản sao spec và roadmap.
