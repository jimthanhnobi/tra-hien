# 08 — Cách giao việc cho ChatGPT (hoặc AI viết code khác)

## 1. Nguyên tắc

1. **Mỗi lần một task** trong `07-roadmap.md` (một PR). Không giao "làm cả P1".
2. **Đưa đúng ngữ cảnh**, không dán cả bộ spec: luôn gồm `00-README.md` + `02-architecture.md` + phần liên quan của `03`, `04`, `05` + đoạn DDL của bảng liên quan trong `schema.sql` + **code hiện có** của các file sẽ bị sửa.
3. **Spec thắng code.** AI thấy spec mâu thuẫn hoặc thiếu → dừng và hỏi, không tự quyết.
4. **Test đi cùng code.** Mỗi `BR-*` trong task có test mang mã luật trong tên.
5. Người giao việc **review và chạy test** trước khi merge (checklist §5). AI không tự merge.

## 2. Prompt hệ thống (dán đầu mỗi cuộc trò chuyện mới)

```
Bạn là senior engineer .NET 10 + PostgreSQL 17 + Next.js (App Router, TypeScript strict) làm cho dự án TeaPremium v2.
Tài liệu spec tôi gửi kèm là nguồn sự thật. Quy tắc bắt buộc:
- Chỉ làm đúng phạm vi task tôi giao. Không thêm tính năng, endpoint, bảng, cột, package ngoài spec.
- Spec thiếu/mâu thuẫn: liệt kê câu hỏi ở đầu câu trả lời và DỪNG, không đoán.
- Kiến trúc theo 02-architecture.md: Domain không phụ thuộc gì; Application không dùng EF Core; repository không chứa luật nghiệp vụ;
  không gọi DateTime.Now (dùng IClock); tiền là long VND; enum lưu text + CHECK; tên DB snake_case, constraint đặt tên tường minh trùng schema.sql.
- Lỗi nghiệp vụ trả Result/Error với code đúng như 05-api.md; không ném exception cho lỗi dự kiến.
- Mỗi luật BR-* trong phạm vi task phải có test; tên test bắt đầu bằng mã luật, ví dụ BR_INV_04_...
- Integration test dùng Testcontainers PostgreSQL, không dùng InMemory/SQLite.
- Không log hoặc trả PII ra ngoài phạm vi spec cho phép. Field giá vốn chỉ trả khi có quyền costs.read.
- Đầu ra: (1) danh sách file tạo/sửa, (2) NỘI DUNG ĐẦY ĐỦ từng file (không viết "...phần còn lại giữ nguyên"),
  (3) lệnh tạo migration nếu có, (4) lệnh chạy test, (5) mục "Giả định đã dùng" nếu có.
```

## 3. Mẫu prompt cho một task

```
TASK: <ID> — <tên task trong 07-roadmap.md>
MỤC TIÊU: <1–2 câu>
PHẠM VI LUẬT: <danh sách BR-..., FR-..., AC-... liên quan>
API: <các endpoint trong 05-api.md thuộc task>
DB: <các bảng; dán đoạn DDL từ schema.sql>
CODE HIỆN CÓ: <dán các file liên quan: entity, DbContext, configuration, endpoint gần nhất cùng kiểu>
KHÔNG LÀM: <những gì để task sau>
ĐỊNH NGHĨA XONG:
- Build không cảnh báo; toàn bộ test cũ + mới xanh.
- Các AC trong phạm vi có test tự động.
- Migration (nếu có) áp được từ DB rỗng và từ DB của task trước.
```

## 4. Ví dụ prompt

### 4.1 P1-08 — Kho cơ bản
```
TASK: P1-08 — Kho: stock_levels, movements, giá vốn bình quân, tồn đầu kỳ import
MỤC TIÊU: Có tầng domain + application + persistence cho tồn kho theo variant, sổ kho append-only, giá vốn bình quân, import tồn đầu kỳ có dry-run.
PHẠM VI LUẬT: BR-INV-01, 02, 05, 06, 09, 12 · FR-INV-01, 03, 06 · AC-P1-04, AC-P1-05, AC-P1-06
API: GET admin/inventory, GET admin/inventory/{variantId}/movements, POST admin/inventory/opening-import?dryRun=
DB: inventory.stock_levels, inventory.stock_movements (dán DDL)
CODE HIỆN CÓ: (dán Variant.cs, VariantConfiguration.cs, AppDbContext.cs, một endpoint admin mẫu, PermissionNames.cs)
KHÔNG LÀM: reservations (P2-05), phiếu nhập/kiểm kê (P1-09), UI.
Yêu cầu thêm:
- Thiết kế StockLevel là aggregate với method ApplyMovement(type, qty, unitCost, ref, actor, clock) đảm bảo BR-INV-01/05/09.
- avg_cost làm tròn half-up đến đồng (viết helper và unit test các ca biên: on_hand=0, avg=null, số lẻ .5).
- Import file: cột sku, quantity, unit_cost_vnd; trả kết quả từng dòng; chạy thật validate lại; chỉ cho variant chưa có movement.
- Test integration kiểm JSON của user role content KHÔNG có avgCostVnd.
```

### 4.2 P2-05 — Đơn hàng và giữ hàng
```
TASK: P2-05 — Order aggregate + state machine + CheckoutCalculator + giữ/nhả/tiêu thụ hàng
PHẠM VI LUẬT: BR-ORD-01..03, 05 (chỉ logic, job làm ở P3-03), 07 (phần kho + payment COD), 10 · BR-CRT-05 · BR-INV-03, 04 · BR-SHP-01 · NFR-02 · AC-P2-01..03
...
Yêu cầu thêm:
- Giữ hàng bằng UPDATE có điều kiện nguyên tử theo 02 §5.2, xử lý theo variant_id tăng dần.
- Test đồng thời: 20 Task song song đặt cùng 1 variant độc bản → đúng 1 thành công.
- Order chỉ đổi trạng thái qua method; test bảng chuyển trạng thái đầy đủ (mọi cặp không hợp lệ → invalid_status_transition).
```

### 4.3 Prompt sửa lỗi
```
BUG: <mô tả, bước tái hiện, kết quả mong đợi theo BR-...>
TEST ĐANG FAIL: <dán output>
CODE LIÊN QUAN: <dán file>
Yêu cầu: viết test tái hiện trước, sửa tối thiểu, không đổi hành vi khác. Giải thích nguyên nhân gốc trong 3–5 câu.
```

## 5. Checklist review PR (người giao việc tự kiểm)

- [ ] Chỉ chạm các file trong phạm vi task; không thêm package lạ.
- [ ] Không có `DateTime.Now`, `DateTimeOffset.UtcNow`, `decimal` cho tiền bán lẻ, `float/double` cho tiền.
- [ ] Endpoint có `[Authorize(Policy=...)]` đúng bảng 02 §7; có test 403 cho vai trò không được phép.
- [ ] DTO không lộ giá vốn/PII sai quyền (có test kiểm JSON).
- [ ] Thao tác ghi quan trọng có `Idempotency-Key` / `rowVersion` theo 05.
- [ ] Mọi thay đổi `on_hand` có movement; mọi chuyển trạng thái đơn có history.
- [ ] Migration: tên constraint/index trùng `schema.sql`; chạy `dotnet ef migrations script` đọc lại SQL.
- [ ] Test tên có mã `BR_*`; `dotnet test` và `pnpm test` xanh ở máy mình (không chỉ tin lời AI).
- [ ] Không có TODO bị "quên"; TODO hợp lệ phải ghi `TODO(spec): ...` và đưa lên danh sách câu hỏi.

## 6. Khi AI "trôi" khỏi spec

Dấu hiệu: tự đổi tên bảng/cột, thêm MediatR/AutoMapper/Repository chung chung, dùng InMemory DB, gộp nhiều task, bỏ test.
Xử lý: trả lời "Không đúng spec mục <x>. Làm lại theo đúng mục đó, giữ nguyên phần còn lại." Nếu cuộc trò chuyện quá dài → mở cuộc mới, dán prompt hệ thống + task.

## 7. Cập nhật spec

Phát hiện thiếu sót khi làm → sửa spec **trước**, ghi vào `00-README.md §6` (DEC mới) hoặc thêm BR, rồi mới giao task. Đánh số tiếp, không đánh lại số cũ.
