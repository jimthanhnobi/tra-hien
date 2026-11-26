# P2-07: Payments, `OrderPaymentStatusCalculator`, Void, Refund Classification & Shipping Migration

> **Hồ sơ triển khai bất biến (Immutable Implementation Record — `docs/WORKFLOW.md` §2.2):** Tài liệu này được hoàn thiện **trước** khi đóng dấu `candidate.manifest` và không nhúng `manifest_sha256` tự tham chiếu, SHA commit chưa publish, kết quả CI tương lai hay verdict G4. Trạng thái động (`Dynamic Task Status`: `manifest_sha256`, đường dẫn `verify.log`, số ca test, verdict G4 độc lập, published HEAD, PR/CI) được ghi nhận trong Unified Handoff Report và mô tả PR.

- **Tier:** `A` — chạm trực tiếp tiền VND (`bigint`), DDL/migration fail-closed trên `sales.refunds` (`kind`, `shipping_amount_vnd`, `4` `CHECK`, `ux_refunds_return_request`), sổ thu tiền `sales.payments` và chứng từ hoàn tiền `sales.refunds` bất biến (`REVOKE DELETE, TRUNCATE`), idempotency tài chính (`AC-P2-04`, `NFR-03`), tự động xác nhận đơn web (`BR-PAY-03`), phân quyền nhạy cảm (`payments.write`, `refunds.write`, vai trò `owner`), CSRF và che PII/chuỗi nhạy cảm trong `platform.audit_logs` & `platform.idempotency_keys` (`NFR-04`).
- **Worktree / Base:** `D:\tra-hien-p2-07`, nhánh `feature/p2-07-payments-refunds`, tích hợp lên `BASE` = `origin/main` `feec578f41282112ae47aa9014433aa8e7ae4c29` (đã gồm PR `#48` `ADR-P2-01`, PR `#49` PR-2 tooling, PR `#50` Workflow V2 và PR `#51` `P2-03`).
- **Readiness & Nguồn phê duyệt tường minh (`OWNER_APPROVED 2026-11-26`):**
  - Tái sử dụng Full G1 tại `cadce575597738bf40ad7c45689482de5a5fbc3d` ([spec-inventory.md](../analysis/spec-inventory.md), [spec-gaps.md](../analysis/spec-gaps.md)), G2 nền ([conformance-review.md](../architecture/conformance-review.md)), Knowledge Recovery [ADR-P2-01](../decisions/ADR-P2-01-phase2-contracts.md), [p2-01-implementation.md](p2-01-implementation.md) và [p2-05-implementation.md](p2-05-implementation.md).
  - Toàn bộ quyết định nghiệp vụ/kỹ thuật của `P2-07` đã được Owner phê duyệt tường minh ngày `2026-11-26`: **`O1 = A`**, **`O2 = A`**, **`O3a = C`**, **`O3b = Phương án 3`**, **`ASSUMPTION-P207-01 = A`**, **`ASSUMPTION-P207-02 = A`**, **`ASSUMPTION-P207-04 = A`**, kèm quyền sửa đúng **3 file đặc tả** (`docs/spec/schema.sql`, `docs/spec/04-business-rules.md`, `docs/spec/05-api.md`). Các lựa chọn bản nháp cũ `GAP-P207-01 = B` và `ASSUMPTION-P207-03 = A` đã chuyển trạng thái `SUPERSEDED` (xem [ADR-P2-01 §4.5, §6, §9](../decisions/ADR-P2-01-phase2-contracts.md)).
- **Migration / Recovery Plan:** Thêm đúng **1 additive fail-closed migration** `202611260800_P2_07_RefundClassificationAndShipping` (tổng chuỗi migration `8 → 9`, `HasPendingModelChanges() == false`; không sửa 8 migration cũ, giữ nguyên `GrantsSql.cs`, `infra/postgres/preflight.sql`, `docs/spec/03-database.md`). Chi tiết `Up` / `Down` và quy trình khôi phục nằm ở Mục 2.
- **Verification Scope:** Đổi backend + migration + `packages/api-client` (`openapi.json` & `src/generated/schema.ts` sinh qua `./scripts/verify-linux.ps1 -Mode api -WriteBack`) → kiểm chứng cục bộ bắt buộc `FULL_ALL` qua `./scripts/verify-linux.ps1 -Mode full -Manifest artifacts/p2-07/candidate.manifest`; GitHub Actions CI & `Docker regression builds` xác minh riêng sau khi publish.

---

## 1. Quyết định `OWNER_APPROVED` (`2026-11-26`), Truy vết Đặc tả và Ranh giới Phạm vi

### 1.1 Tổng hợp các quyết định `OWNER_APPROVED` áp dụng trong `P2-07`

| Mã quyết định | Trạng thái | Nội dung cốt lõi áp dụng |
|---|---|---|
| `R-P201-01` & `ASSUMPTION-P207-01 = A` | `OWNER_APPROVED` (`2026-11-26`) | `sales.payments` có `ck_payments_confirmed` (`(status = 'confirmed') = (confirmed_at IS NOT NULL)`). Void `confirmed` đặt đồng thời `status = 'voided'`, `confirmed_at = NULL`, `void_reason = reason` (mốc `confirmed_at` cũ bảo lưu trong `platform.audit_logs`); chặn void khi `cod_settlement_id IS NOT NULL` (`409 payment_settled_cod`), đã `voided` (`409 payment_already_voided`) hoặc làm `net < 0` (`409 payment_has_refunds`). |
| `R-P201-06` & Grants `P2-01` | `OWNER_APPROVED` (`P2-01`) | `tp_app` bị `REVOKE DELETE, TRUNCATE, MAINTAIN` trên `sales.payments` và `sales.refunds`. `IRefundRepository` / `EfRefundRepository` strictly append-only (chỉ thêm mới + đọc). Gắn `[Audited]` + `AuditValuePolicy` fail-closed cho `Payment`, `Refund`, `ReturnRequest`. |
| `G04 + G29` | `OWNER_APPROVED` (`STEP 462` & `2026-11-26`) | Luồng đổi trả hoàn tiền 2 bước: `approve` (`returns.write`, `P2-10`) → `owner` ghi `POST /api/v1/admin/orders/{id}/refunds` (`refunds.write`, `Idempotency-Key`, CSRF, `kind = 'return'`, `returnRequestId`) chốt `return_requests.status = 'resolved'` trong cùng transaction. |
| `O1 = A` | `OWNER_APPROVED` (`2026-11-26`) | `delivery_failed && net > 0` bật `NeedsRefund = true` (hoàn qua `kind = 'cancellation'`). **`P2-07` sở hữu duy nhất `OrderPaymentStatusCalculator`** (`07-roadmap.md:92`). |
| `O2 = A` | `OWNER_APPROVED` (`2026-11-26`) | Trên đơn còn chạy, `NeedsRefund` bao gồm `IsOverpaid` (`overpaid > 0`) và đồng bộ nhánh `return_requests(status = 'approved', resolution = 'refund')`. |
| `O3a = C` | `OWNER_APPROVED` (`2026-11-26`) | `BR-PAY-02` 3 nhánh (`is_backfilled`, `cancelled \| delivery_failed`, active/delivered dùng `effective_paid = paid - refunded_overpayment` & `refunded_commercial`). Hoàn tiền thừa giữ `'paid'` nên `Order.MarkShipping` giữ nguyên; đơn `0đ` mặc định `'paid'`, không sinh payment giả, không auto-confirm (`BR-PAY-03`), không VietQR (`BR-PAY-04`), `cod_amount = 0` (`BR-SHP-02`); tính lại `payment_status` ở tạo đơn, sửa dòng đơn, payment, refund, `cancelled` và `delivery_failed`. |
| `O3b = Phương án 3` | `OWNER_APPROVED` (`2026-11-26`) | 1 fail-closed additive migration `P2_07_RefundClassificationAndShipping`: thêm `kind text NOT NULL` (không `DEFAULT`), `shipping_amount_vnd bigint NOT NULL DEFAULT 0`, `4` `CHECK`, `ux_refunds_return_request` (`1 return_request ↔ tối đa 1 refund`). Áp ma trận 4 `kind × order_status`, trần tiền hàng cộng dồn **half-up ở bước cuối** (`04-business-rules.md:4`), chỉ cho `shipping_amount_vnd > 0` trên `return` khi hoàn tất trả 100% mọi dòng đơn (kể cả dòng `0đ`) qua `resolution = 'refund'` (loại trừ `replacement`), và cập nhật `BR-ORD-07.2`/`BR-RET-04`/`BR-MKT-07`/`BR-FIN-02/04/08` tách hoàn thương mại (`return \| adjustment`) khỏi `overpayment`/`cancellation`. |
| `ASSUMPTION-P207-02 = A` | `OWNER_APPROVED` (`2026-11-26`) | `GET /api/v1/admin/payments/awaiting` bắt buộc `type ∈ {bank_transfer, cod}`, phân trang `<= 100`, trả `AwaitingPaymentsPage` rõ kiểu; `type=bank_transfer` lọc đúng đơn còn nghĩa vụ thu theo `(ConfirmedPaidVnd - OverpaymentRefundedVnd) < TotalVnd` (trả thêm `effectivePaidVnd`, loại đơn `0đ`), `type=cod` join shipment `purpose='order' AND status='delivered'` mới nhất tránh nhân đôi dòng. |
| `ASSUMPTION-P207-04 = A` | `OWNER_APPROVED` (`2026-11-26`) | `BR-PAY-03` auto-confirm phát `OrderConfirmed(ReasonCode = "auto_confirmed_on_full_payment")` qua `CompositeOrderOutboxHandler`; không tự chèn email khách hàng trước `P3-06`. |
| Spec Amendments (3 file) | `OWNER_APPROVED` (`2026-11-26`) | Chỉ sửa đúng `docs/spec/schema.sql`, `docs/spec/04-business-rules.md`, `docs/spec/05-api.md` có ghi rõ provenance `Owner-approved 2026-11-26`. Không sửa `docs/spec/03-database.md` hay bất kỳ file spec nào khác. |

### 1.2 Truy vết định danh (`FR-*`, `BR-*`, `NFR-*`, `AC-*`, `DEC-*`)

- **Spec đã áp amendment (`Owner-approved 2026-11-26`):**
  - [schema.sql](../spec/schema.sql) (`sales.refunds` dòng `993–1014`).
  - [04-business-rules.md](../spec/04-business-rules.md) (`BR-ORD-07.2`, `BR-PAY-01..04`, `BR-PAY-06..07`, `BR-SHP-02..03`, `BR-RET-02`, `BR-RET-04`, `BR-MKT-07`, `BR-FIN-02`, `BR-FIN-04`, `BR-FIN-08`).
  - [05-api.md](../spec/05-api.md) (§4.4 dòng `191–195` và ghi chú provenance cuối §4.4).
- **Định danh liên kết:**
  - `DEC`: `DEC-01`, `DEC-07`, `DEC-08`, `DEC-12`, `DEC-14`, `DEC-15`, `DEC-16`, `DEC-20`, `DEC-30`.
  - `FR`: `FR-PAY-01`, `FR-PAY-03`, `FR-PAY-04`, `FR-PAY-05`, phối hợp `FR-ORD-04`, `FR-RET-02`, `FR-PLT-01`.
  - `BR`: `BR-PAY-01..04`, `BR-PAY-06..07`, `BR-SHP-02..03`, `BR-RET-02..04`, `BR-ORD-01..03`, `BR-ORD-07.2`, `BR-ORD-08..09`, `BR-MKT-07`, `BR-FIN-02`, `BR-FIN-04`, `BR-FIN-08`, `BR-PLT-01..03`.
  - `NFR`: `NFR-01`, `NFR-03`, `NFR-04`, `NFR-07`, `NFR-08`, `NFR-09`, `NFR-12`.
  - `AC`: nghiệm thu trực tiếp `AC-P2-04`, nền backend cho `AC-P3-03`, giữ xanh `AC-P0-03` (CSRF), `AC-P0-04` (audit redaction), `AC-P0-05` (kiến trúc tầng & `IClock`).

### 1.3 Ranh giới phạm vi (tách bạch với `P2-06`, `P2-08`, `P2-09`, `P2-10`, `P2-11`, `P3-06`, `P4-02`)

- **Trong phạm vi `P2-07`:**
  1. Spec amendments được phê duyệt trên đúng 3 file (`schema.sql`, `04-business-rules.md`, `05-api.md`) + cập nhật [ADR-P2-01](../decisions/ADR-P2-01-phase2-contracts.md) & [spec-gaps.md](../analysis/spec-gaps.md).
  2. Migration fail-closed `202611260800_P2_07_RefundClassificationAndShipping` + cập nhật EF model, snapshot và bộ test migration parity/rollback/roles.
  3. `OrderPaymentStatusCalculator` 3 nhánh + `CalculateReturnRefundCeiling` cộng dồn half-up + khởi tạo đơn `0đ`, self-heal/tính lại `payment_status` khi `ConfirmOrder` / `MarkOrderShipping` / `UpdateOrderLines` / `CancelOrder` / `MarkOrderDeliveryFailed`, và đồng bộ đếm đơn `0đ` trong `EfDailyDigestRead`.
  4. Đúng **4 endpoint Admin API** (§4.4 dòng `191–193, 195`): `POST admin/orders/{id}/payments`, `POST admin/payments/{id}/void`, `GET admin/payments/awaiting`, `POST admin/orders/{id}/refunds` (cùng toàn bộ 10 bất biến tài chính, `415 unsupported_media_type` ProblemDetails, `[Audited]` + `AuditValuePolicy`, OpenAPI & TypeScript client).
- **Ngoài phạm vi `P2-07`:**
  - `POST /api/v1/admin/cod-settlements` (`05-api.md:194` → **`P2-09`**).
  - HTTP Admin Orders (`P2-06`), Shipments & `payments(cod, awaiting_settlement)` (`P2-08`), Returns CRUD/approve (`P2-10`), LTV aggregation (`P2-11`), VietQR (`P3-02`), `ExpireUnpaidOrders` (`P3-03`), email khách hàng `payment_received_customer` (`P3-06`), P&L/Cashflow/Excel reports (`P4-02`), Admin UI (`P2-15`), SePay webhook (`P6-01`).

---

## 2. Fail-Closed Additive Migration (`202611260800_P2_07_RefundClassificationAndShipping`) & Kế hoạch Khôi phục

### 2.1 Cấu trúc `Up` và `Down`

1. **DDL bổ sung trên `sales.refunds` (đồng nhất 100% với `docs/spec/schema.sql:993–1014`):**
   - `kind text NOT NULL` — **không có `DEFAULT`** (`pg_attrdef` = `NULL`), buộc mọi lệnh `INSERT` phải khai báo tường minh.
   - `shipping_amount_vnd bigint NOT NULL DEFAULT 0`.
   - `ck_refunds_kind`: `kind IN ('overpayment','return','cancellation','adjustment')`.
   - `ck_refunds_shipping_amount_vnd`: `shipping_amount_vnd >= 0 AND shipping_amount_vnd <= amount_vnd`.
   - `ck_refunds_kind_return_request`: `(kind = 'return') = (return_request_id IS NOT NULL)`.
   - `ck_refunds_kind_shipping`: `kind IN ('return','adjustment') OR shipping_amount_vnd = 0`.
   - `ux_refunds_return_request`: `CREATE UNIQUE INDEX ux_refunds_return_request ON sales.refunds(return_request_id) WHERE return_request_id IS NOT NULL;`.
2. **Bảo vệ fail-closed trong `Up`:**
   - Chạy frozen literal `GrantsSql.Preflight` đầu migration.
   - Khối `DO $p207_up$`: xác nhận `current_user = 'tp_owner'`, khóa `LOCK TABLE sales.refunds IN ACCESS EXCLUSIVE MODE`, và **`RAISE EXCEPTION 'P2-07 Up refused: existing rows in sales.refunds...'` ngay nếu `EXISTS (SELECT 1 FROM sales.refunds)`** — tuyệt đối không tự phân loại hay gán default cho dữ liệu hoàn tiền cũ.
   - Thêm 2 cột, 4 `CHECK`, `ux_refunds_return_request`, rồi chạy lại frozen literal `GrantsSql.Preflight` hậu kiểm.
   - Không động vào 8 migration trước, không động vào `GrantsSql.cs`, `infra/postgres/preflight.sql`, hay quyền `REVOKE DELETE, TRUNCATE, MAINTAIN ON sales.refunds FROM tp_app` (các quyền bảng giữ nguyên cho `tp_app` và `tp_readonly`).
3. **Bảo vệ fail-closed trong `Down` & Kế hoạch khôi phục:**
   - Khối `DO $p207_down$`: xác nhận `current_user = 'tp_owner'`, khóa `LOCK TABLE sales.refunds IN ACCESS EXCLUSIVE MODE`, từ chối bằng `RAISE EXCEPTION` nếu `EXISTS (SELECT 1 FROM sales.refunds)`, hoặc `sales.ux_refunds_return_request` thiếu/bị drift sang bảng/cột/predicate khác (`pg_index.indrelid`, `indisunique`, `indkey`, `indpred`), hoặc bất kỳ một trong 4 `CHECK` của `P2-07` (`ck_refunds_kind`, `ck_refunds_shipping_amount_vnd`, `ck_refunds_kind_return_request`, `ck_refunds_kind_shipping`) thiếu hoặc lệch định nghĩa chuẩn PostgreSQL 17 (`pg_constraint.contype = 'c'` và `pg_get_constraintdef(oid)`), hoặc có column ACL tùy biến trên `kind`/`shipping_amount_vnd`, hoặc có object/dependency ngoài phạm vi `P2-07` trong `pg_depend` (view/trigger/index/constraint lạ tham chiếu 2 cột hoặc `ux_refunds_return_request`).
   - Sau khi qua toàn bộ guard: drop `ux_refunds_return_request`, 4 `CHECK`, và 2 cột `shipping_amount_vnd`, `kind` → khôi phục nguyên trạng schema & ACL của mốc `202611230721_P2_05_InventoryReservations`.
   - **Khôi phục khi DB đã có dữ liệu thật:** Tuyệt đối không chạy `Down` trên DB có dữ liệu `sales.refunds`; khôi phục bằng point-in-time backup trước khi deploy hoặc bằng forward corrective migration được Owner phê duyệt riêng.

---

## 3. Thiết kế `OrderPaymentStatusCalculator` 3 nhánh (`O3a = C`) & Trần Hoàn tiền (`O3b = Phương án 3`)

### 3.1 `OrderPaymentStatusCalculator.Calculate` (`TeaPremium.Domain.Orders`)

Chữ ký thuần BCL (không I/O, dùng `Int128` chống tràn):
`Calculate(long confirmedPaidVnd, long refundedVnd, long overpaymentRefundedVnd, long totalVnd, string orderStatus, bool isBackfilled = false)`
→ trả `OrderPaymentCalculation(Status, ConfirmedPaidVnd, RefundedVnd, OverpaymentRefundedVnd, CommercialRefundedVnd, EffectivePaidVnd, NetPaidVnd, TotalVnd, RemainingToPayVnd, OverpaidVnd, IsOverpaid, NeedsRefund)`.

1. **Kiểm bất biến đầu vào:** mọi số âm, `overpaymentRefundedVnd > refundedVnd`, hoặc `orderStatus` lạ → `DomainException("validation_failed")`; `net = confirmedPaidVnd - refundedVnd < 0` → `DomainException("negative_net_payment")`.
2. **Nhánh 1 — `isBackfilled == true`:** fail-closed (`backfilled_order_immutable`) nếu `confirmedPaidVnd != 0 || refundedVnd != 0 || overpaymentRefundedVnd != 0`; luôn trả `Status = 'unpaid'`, `RemainingToPayVnd = 0`, `OverpaidVnd = 0`, `IsOverpaid = false`, `NeedsRefund = false`.
3. **Nhánh 2 — `cancelled` hoặc `delivery_failed`:** ưu tiên `refundedVnd > 0` (`net == 0 ? 'refunded' : 'partially_refunded'`), kế tiếp `confirmedPaidVnd == 0 ? 'unpaid'`, `confirmedPaidVnd < totalVnd ? 'partially_paid'`, còn lại `'paid'`. Luôn đặt `RemainingToPayVnd = 0`, `OverpaidVnd = 0`, `IsOverpaid = false`, **`NeedsRefund = (net > 0)`** (`O1 = A`).
4. **Nhánh 3 — `pending` / `confirmed` / `shipping` / `delivered`:**
   - `effectivePaid = confirmedPaidVnd - overpaymentRefundedVnd`, `commercialRefunded = refundedVnd - overpaymentRefundedVnd`.
   - `Status`: nếu `commercialRefunded > 0` → `net == 0 ? 'refunded' : 'partially_refunded'`; ngược lại nếu `net >= totalVnd` → **`'paid'`** (gồm cả đơn `0đ` và đơn hoàn đúng phần tiền thừa về `total`, nên `Order.MarkShipping` giữ nguyên); ngược lại nếu `effectivePaid <= 0` → `'unpaid'`; còn lại `'partially_paid'`.
   - `RemainingToPayVnd = max(0, totalVnd - effectivePaid)`.
   - `OverpaidVnd = effectivePaid > totalVnd ? min(net, effectivePaid - totalVnd) : 0`; `IsOverpaid = OverpaidVnd > 0`; `NeedsRefund = IsOverpaid` (`O2 = A`; ở tầng use case/truy vấn nối `OR EXISTS return_requests(status = 'approved' AND resolution = 'refund')`).
5. **Khởi tạo & đồng bộ trên `Order` và `OrderCoreUseCases`:**
   - `Order.PlaceWeb` và `Order.CreateAdmin` khởi tạo `PaymentStatus` qua `OrderPaymentStatusCalculator.Calculate(0, 0, 0, totalVnd, OrderStatus.Pending, isBackfilled: false)` → đơn `0đ` có `PaymentStatus = 'paid'` ngay khi tạo (giữ `Status = 'pending'`, không sinh payment giả, không auto-confirm).
   - `ConfirmOrder`, `MarkOrderShipping` (mọi `!order.IsBackfilled`), `UpdateOrderLines`, `CancelOrder` và `MarkOrderDeliveryFailed` đều đọc `IOrderPaymentLedgerReader.GetOrderLedgerTotalsAsync` dưới khóa `sales.orders FOR UPDATE` và gọi `OrderPaymentStatusCalculator.Calculate` → `order.ApplyPaymentCalculation(calc, now)` trong cùng transaction (tự chữa lành đơn `0đ` lưu từ base còn `'unpaid'`); `EfDailyDigestRead` loại trừ đơn `TotalVnd == 0` khỏi điều kiện chờ thanh toán / sắp hết hạn thanh toán.

### 3.2 `OrderPaymentStatusCalculator.CalculateReturnRefundCeiling` (`BR-RET-02` cộng dồn half-up)

- Kiểm tra `currentReturnLines` không rỗng (`return_request_has_no_lines`), mọi `orderLineId` thuộc đơn, tổng cộng dồn `priorRefundReturnQty[line] + currentQty[line] <= orderLine.Quantity` (`return_quantity_exceeds_order`, không dựa vào `order_lines.returned_quantity` vì có thể chứa `replacement`).
- Tính trần tiền hàng cộng dồn bằng số học `Int128` làm tròn **half-up ở bước cuối** (`04-business-rules.md:4`):
  - `netMerch = order.SubtotalVnd - order.DiscountVnd`.
  - Nếu `order.SubtotalVnd == 0` → `cumCeiling = 0`; nếu `cumGross >= order.SubtotalVnd` → `cumCeiling = netMerch`; ngược lại `cumCeiling = (long)((((Int128)netMerch * cumGross * 2) + order.SubtotalVnd) / ((Int128)order.SubtotalVnd * 2))` (ví dụ `800k/3 → 266_667 + 266_666 + 266_667 = 800_000`; `700k/3 → 233_333 + 233_334 + 233_333 = 700_000`).
  - `remainingMerchCeiling = max(0, netMerch - priorAdjustmentGoodsRefundedVnd - priorReturnGoodsRefundedVnd)`.
  - `maxGoodsRefundVnd = min(max(0, cumCeiling - priorReturnGoodsRefundedVnd), remainingMerchCeiling)`.
- `IsFullRefundReturnCompleted`: `true` **chỉ khi mọi dòng hàng trong `order.Lines` (kể cả dòng đơn giá `0đ`)** đều đạt `priorRefundReturnQty[line] + currentQty[line] == line.Quantity` tính riêng trên các phiếu `resolution = 'refund'` (loại trừ hoàn toàn `replacement`).

---

## 4. Chi tiết 4 Endpoint & 10 Bất biến Tài chính

### 4.1 Ma trận 4 Endpoint (`docs/spec/05-api.md` §4.4 dòng `191–193, 195`)

Mọi endpoint dưới `/api/v1/admin/...` chịu `AdminTransportMiddleware` (`https_required` `400`, `AdminHost` `403`, `Cache-Control: no-store`) và `AdminCookie` (`AdminSecurity.Ready`). Mọi `POST` gắn `RequireCsrfHeader` (`400 csrf_invalid`), `.WithJsonRequest<T>()` + `.RequireJsonContentType()` (trả RFC 9457 `415 unsupported_media_type` trước idempotency khi sai `Content-Type`), và đọc JSON strict (bắt buộc offset múi giờ ISO-8601 `Z` hoặc `±HH:MM` trên `receivedAt`/`refundedAt`).

| # | Method & Path | Quyền & Bảo vệ | Hợp đồng chính |
|---:|---|---|---|
| 1 | `POST /api/v1/admin/orders/{id:guid}/payments` | `payments.write` (`owner`, `sales`) + CSRF + `Idempotency-Key` (`payments.order.record.v1`) | Ghi `bank_transfer \| cash`; `SAVEPOINT` bắt `23505 ux_payments_bank_ref → 409 duplicate_bank_reference`; cho phép ghi trên `cancelled`/`delivery_failed` (bật `needsRefund`), chặn `is_backfilled`; `BR-PAY-03` auto-confirm phát `OrderConfirmed(ReasonCode = "auto_confirmed_on_full_payment")`. |
| 2 | `POST /api/v1/admin/payments/{id:guid}/void` | `AdminSecurity.Owner` (chỉ `owner`) + CSRF | Khóa `L1 sales.orders` → `L2 sales.payments` chống deadlock `40P01`; chặn đơn `is_backfilled` (`422 backfilled_order_immutable`); đặt `status='voided', confirmed_at=NULL, void_reason=reason` (`R-P201-01`); chặn `payment_already_voided`, `payment_settled_cod`, `payment_has_refunds` (`net < 0`); tính lại `payment_status`. |
| 3 | `GET /api/v1/admin/payments/awaiting` | `payments.write` (`owner`, `sales`) | Bắt buộc `type ∈ {bank_transfer, cod}`, `pageSize <= 100`. `bank_transfer` cấm `carrier`, lọc `status IN ('pending','confirmed','shipping') AND (ConfirmedPaidVnd - OverpaymentRefundedVnd) < TotalVnd` (loại đơn `0đ`), trả `effectivePaidVnd` & `remainingVnd`. `cod` join shipment `purpose='order' AND status='delivered'` mới nhất. Không lộ `cost`/`profit`. |
| 4 | `POST /api/v1/admin/orders/{id:guid}/refunds` | `refunds.write` (chỉ `owner`) + CSRF + `Idempotency-Key` (`payments.order.refund.v1`) | Nhận `{ kind, amountVnd, shippingAmountVnd?, method, bankReference?, reason, returnRequestId?, refundedAt }` (`shippingAmountVnd` optional mặc định `0` trong cả C# & TypeScript). Khóa `L1 sales.orders` → `L2 ledger` → `L3 sales.return_requests`, thực thi đủ 10 bất biến tài chính, `SAVEPOINT` bắt `23505 ux_refunds_return_request → 409 return_already_refunded`, trả `RefundRecordedView` (18 trường an toàn `SafeRefundRecordedResponse`, gồm `kind`, `shippingAmountVnd`). |

### 4.2 Danh mục 10 Bất biến Tài chính tại `CreateOrderRefund` (dưới khóa `L1 sales.orders FOR UPDATE`)

1. **Chặn đơn lịch sử:** `order.IsBackfilled` → `422 backfilled_order_immutable`.
2. **Validate `kind` & `shippingAmountVnd`:** `kind` bắt buộc ∈ `{overpayment, return, cancellation, adjustment}` (`400`), `shippingAmountVnd ∈ [0, amountVnd]` (`400`); `overpayment | cancellation` có `shippingAmountVnd > 0` → `422 shipping_refund_not_allowed`.
3. **Ma trận `kind × order.Status`:**
   - `adjustment` CHỈ trên `delivered` (`422 invalid_refund_kind_for_order_status`).
   - `cancellation` CHỈ trên `cancelled | delivery_failed` (`422 invalid_refund_kind_for_order_status`).
   - `overpayment` không trên `cancelled | delivery_failed` (`422 invalid_refund_kind_for_order_status`).
   - `return` CHỈ trên `delivered` (`422 invalid_refund_kind_for_order_status`).
4. **Trần thu ròng:** `amountVnd <= currentNetVnd` (`422 refund_exceeds_net_paid`) — bảo vệ đồng thời bằng khóa `L1` chống race.
5. **Trần `overpayment`:** `amountVnd <= currentCalc.OverpaidVnd` (`422 refund_exceeds_overpayment`).
6. **Liên kết `returnRequestId` (`1:1`):** `kind == "return"` thiếu `returnRequestId` → `422 return_request_required`, `kind != "return"` truyền `returnRequestId` → `422 return_request_not_allowed`; kiểm thuộc đúng `orderId` (`422 return_request_order_mismatch`), đang `status == 'approved' && resolution == 'refund'` (nếu đã `resolved` hoặc không hợp lệ → `409 invalid_return_status`), chống trùng `1:1` ở tầng DB qua `SAVEPOINT` bắt `23505 ux_refunds_return_request → 409 return_already_refunded`, và phải có `>= 1` dòng `return_lines` (`422 return_request_has_no_lines`).
7. **Trần phí vận chuyển cộng dồn (`return` & `adjustment`):** `priorShippingRefundedVnd + shippingAmountVnd <= order.ShippingFeeVnd` (`422 refund_exceeds_shipping_fee`) — chặn double-refund ship qua cả `adjustment` + `return`.
8. **Điều kiện hoàn phí ship trên `return`:** `shippingAmountVnd > 0` chỉ hợp lệ khi `ceiling.IsFullRefundReturnCompleted == true` (`422 shipping_refund_requires_full_return`, loại trừ `replacement` và yêu cầu trả đủ cả dòng `0đ`).
9. **Trần tiền hàng cộng dồn (`goodsAmountVnd = amountVnd - shippingAmountVnd`):**
   - `adjustment`: `goodsAmountVnd <= remainingMerchandiseVnd` (`422 refund_exceeds_order_merchandise`).
   - `return`: `goodsAmountVnd <= ceiling.MaxGoodsRefundVnd` (`422 refund_exceeds_return_lines`, bao gồm cộng dồn half-up và trừ `priorAdjustmentGoodsRefundedVnd`).
10. **Cập nhật nguyên tử & Audit fail-closed:** chèn `sales.refunds`, chuyển `return_requests.status = 'resolved'` (khi `kind == 'return'`), tính lại `sales.orders.payment_status`, tính `needsRefund = calc.NeedsRefund || hasOtherApprovedRefundReturns`, ghi `[Audited]` với `AuditValuePolicy` allowlist `kind` & `shipping_amount_vnd` (che cứng `bank_reference`, `reason`, `note`, `void_reason`), và gọi `IRefundRecordedHook`.

---

## 5. Danh mục File Thay đổi (`47` file: `17` tạo mới, `30` sửa/tái sinh)

*(Không có file nào trong `apps/`, không sửa 8 migration cũ, `GrantsSql.cs`, `infra/postgres/preflight.sql`, hay `docs/spec/03-database.md`.)*

### 5.1 Đặc tả được phê duyệt & Hồ sơ Quản trị (`6` file: `1` tạo mới, `5` sửa)
1. `docs/spec/schema.sql` *(sửa — Owner-approved 2026-11-26)*
2. `docs/spec/04-business-rules.md` *(sửa — Owner-approved 2026-11-26)*
3. `docs/spec/05-api.md` *(sửa — Owner-approved 2026-11-26)*
4. `docs/decisions/ADR-P2-01-phase2-contracts.md` *(sửa — nâng `O3a=C`, `O3b=Phương án 3`, `ASSUMPTION-P207-01/02/04=A` sang `OWNER_APPROVED`, đóng `CONF-P206-P207-01`, giữ nguyên `O5`/`O6`/`O7`/`G27`/`G24`/`Q1–Q8`)*
5. `docs/analysis/spec-gaps.md` *(sửa — đóng `CONF-P206-P207-01` & `GAP-P207-01`, cập nhật `G04`/`G29`)*
6. `docs/plans/p2-07-implementation.md` *(tạo mới — hồ sơ hiện tại)*

### 5.2 `TeaPremium.Domain` (`3` file: `2` tạo mới, `1` sửa)
7. `backend/src/TeaPremium.Domain/Orders/OrderPaymentStatusCalculator.cs` *(tạo mới)*
8. `backend/src/TeaPremium.Domain/Orders/PaymentConstants.cs` *(tạo mới — thêm `RefundKind`)*
9. `backend/src/TeaPremium.Domain/Orders/Order.cs` *(sửa — khởi tạo `PaymentStatus` cho đơn `0đ`, `ApplyPaymentCalculation`, `Confirm` system `reasonCode`)*

### 5.3 `TeaPremium.Application` (`5` file: `2` tạo mới, `3` sửa)
10. `backend/src/TeaPremium.Application/Orders/OrderContracts.cs` *(sửa)*
11. `backend/src/TeaPremium.Application/Orders/OrderCoreUseCases.cs` *(sửa — tính lại `payment_status` trong `ConfirmOrder`, `MarkOrderShipping`, `UpdateOrderLines`, `CancelOrder`, `MarkOrderDeliveryFailed`)*
12. `backend/src/TeaPremium.Application/Orders/OrderOutbox.cs` *(sửa)*
13. `backend/src/TeaPremium.Application/Payments/PaymentContracts.cs` *(tạo mới)*
14. `backend/src/TeaPremium.Application/Payments/PaymentUseCases.cs` *(tạo mới — bao gồm tính `wasAlreadyPaid` từ `OrderPaymentStatusCalculator` theo sổ thanh toán cho `BR-PAY-03` và guard `backfilled_order_immutable` trên cả `RecordOrderPayment`, `VoidPayment`, `CreateOrderRefund`)*

### 5.4 `TeaPremium.Infrastructure` (`12` file: `4` tạo mới, `8` sửa)
15. `backend/src/TeaPremium.Infrastructure/Persistence/CrmSalesFinanceEntities.cs` *(sửa — `Refund.Kind`, `Refund.ShippingAmountVnd`, `[Audited]` trên `Payment`/`Refund`/`ReturnRequest` giữ nguyên `[Audited]` trên `Interaction` của `P2-03`)*
16. `backend/src/TeaPremium.Infrastructure/Persistence/CrmSalesFinanceModel.cs` *(sửa — ánh xạ `kind`, `shipping_amount_vnd`, 4 `CHECK`, `ux_refunds_return_request`)*
17. `backend/src/TeaPremium.Infrastructure/Persistence/AuditSaveChangesInterceptor.cs` *(sửa — gộp `Interaction or Payment or Refund or ReturnRequest`)*
18. `backend/src/TeaPremium.Infrastructure/Persistence/AuditValuePolicy.cs` *(sửa — giữ nhánh `crm.interactions` của `P2-03` + thêm allowlist `sales.payments`/`sales.refunds`/`sales.return_requests` gồm `kind` & `shipping_amount_vnd`)*
19. `backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611260800_P2_07_RefundClassificationAndShipping.cs` *(tạo mới)*
20. `backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611260800_P2_07_RefundClassificationAndShipping.Designer.cs` *(tạo mới — khớp 1:1 `AppDbContextModelSnapshot.cs`)*
21. `backend/src/TeaPremium.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs` *(sửa)*
22. `backend/src/TeaPremium.Infrastructure/Notifications/EfDailyDigestRead.cs` *(sửa — loại trừ đơn `TotalVnd == 0` khỏi điều kiện chờ thanh toán / sắp hết hạn thanh toán cho dữ liệu base)*
23. `backend/src/TeaPremium.Infrastructure/Orders/EfOrderRepository.cs` *(sửa)*
24. `backend/src/TeaPremium.Infrastructure/Orders/OrdersRegistration.cs` *(sửa — giữ cả `ICustomerOrderQueries` của `P2-03` và `IOrderPaymentLedgerReader` của `P2-07`)*
25. `backend/src/TeaPremium.Infrastructure/Payments/EfPaymentStores.cs` *(tạo mới)*
26. `backend/src/TeaPremium.Infrastructure/Payments/PaymentsRegistration.cs` *(tạo mới)*

### 5.5 `TeaPremium.Api` & `packages/api-client` (`7` file: `3` tạo mới, `4` sửa/tái sinh)
27. `backend/src/TeaPremium.Api/Iam/AdminSecurity.cs` *(sửa — thêm policy `AdminSecurity.Owner`)*
28. `backend/src/TeaPremium.Api/Payments/PaymentEndpoints.cs` *(tạo mới — `.WithJsonRequest<T>()` + `.RequireJsonContentType()`, `415` ProblemDetails, `SafeRefundRecordedResponse` 18 trường)*
29. `backend/src/TeaPremium.Api/Payments/PaymentOpenApi.cs` *(tạo mới — xóa OpenAPI `default` trên `shippingAmountVnd` để TS sinh optional `shippingAmountVnd?: number`)*
30. `backend/src/TeaPremium.Api/Program.cs` *(sửa)*
31. `packages/api-client/openapi.json` *(tái sinh qua `./scripts/verify-linux.ps1 -Mode api -WriteBack`)*
32. `packages/api-client/src/generated/schema.ts` *(tái sinh qua `./scripts/verify-linux.ps1 -Mode api -WriteBack`)*
33. `packages/api-client/src/payments-contract.test-d.ts` *(tạo mới)*

### 5.6 `backend/tests` (`14` file: `5` tạo mới, `9` sửa)
34. `backend/tests/TeaPremium.UnitTests/OrderPaymentStatusCalculatorTests.cs` *(tạo mới — `8` methods: `5` `[Fact]` + `3` `[Theory]` = `36` test cases thực thi cho 3 nhánh `O3a = C`, đơn `0đ`, cộng dồn half-up `800k`/`700k`/`.5`, dòng `0đ`, `adjustment` và `Order.Confirm`/`MarkShipping`)*
35. `backend/tests/TeaPremium.IntegrationTests/RefundsMigrationTests.cs` *(tạo mới — `7` `[Fact]`: authoring contract + `HasPendingModelChanges() == false` + `IMigrationsModelDiffer` 0 khác biệt giữa `migration.TargetModel` và `IDesignTimeModel` + kiểm trực tiếp `TargetModel` + `pg_attrdef` `kind` không default; `Up` fail-closed khi `sales.refunds` có dòng cũ; idempotent script chạy 2 lần giữ nguyên dữ liệu đã phân loại; `Down` guard dữ liệu/dependency ngoài + `Down → Up` khôi phục ACL; `Down` từ chối schema drift `ux_refunds_return_request` và giữ nguyên index trên bảng khác; `Down` từ chối khi thiếu hoặc drift định nghĩa 4 `CHECK` qua `pg_get_constraintdef` và giữ nguyên constraint ngoài phạm vi; `tp_app`/`tp_readonly`/preflight + probe `23502`, đủ 4 `CHECK` `23514`, `23505 ux_refunds_return_request`)*
36. `backend/tests/TeaPremium.IntegrationTests/PaymentRecordingAndIdempotencyTests.cs` *(tạo mới — `11` `[Fact]`: `AC-P2-04` idempotency + 20 concurrent requests, `SAVEPOINT` `ux_payments_bank_ref` + reuse sau void, validation + `is_backfilled`, `BR-PAY-03` auto-confirm + outbox `OrderConfirmed`, ghi nhận payment trên `cancelled`/`delivery_failed`, đơn `0đ` + tính lại `payment_status` khi `CancelOrder`/`MarkOrderDeliveryFailed`, `GET .../awaiting` lọc `EffectivePaidVnd < TotalVnd` & `cod` theo carrier không trùng dòng và không lộ `cost`/`profit`, đơn web `0đ` kiểu base `unpaid` tự chữa lành khi `ConfirmOrder`/`MarkOrderShipping` + đếm đúng trong `IDailyDigestRead`, và `BR-PAY-03` tính `wasAlreadyPaid` từ `OrderPaymentStatusCalculator` theo sổ thanh toán cho đơn `0đ` cũ/mới, đơn có `payment_status` lệch `unpaid`/`paid` và chuyển trạng thái chuẩn `unpaid → partial → paid`)*
37. `backend/tests/TeaPremium.IntegrationTests/PaymentVoidAndRefundTests.cs` *(tạo mới — `11` `[Fact]`: `R-P201-01` void `confirmed` + `awaiting_settlement` + audit `confirmed_at.before`, chặn void `settled`/`voided`/`has_refunds`/`is_backfilled`, `cancellation` refund + ma trận trạng thái + idempotency, 10 concurrent refunds serial hóa trên `L1`, `O3a = C` hoàn tiền thừa giữ `'paid'` cho `MarkShipping` + return-then-overpayment, `O3b` nhiều phiếu trả một phần `800k`/`700k` cộng dồn half-up + `replacement` không mở khóa ship + đơn có dòng `0đ` + `NeedsRefund`, chặn double-refund ship qua `adjustment` + `return` + `refund_exceeds_order_merchandise` + `SAVEPOINT 23505 ux_refunds_return_request → 409 return_already_refunded`, 6 concurrent `return` refunds cho cùng `returnRequestId` chỉ tạo đúng `1` refund, `G04 + G29` `returnRequestId` kiểm `422 return_request_required`/`422 return_request_not_allowed` + resolve nguyên tử, `UpdateOrderLines` tính lại `payment_status`, audit redaction + idempotency safety + reflection append-only + `tp_app` `DELETE` `42501`)*
38. `backend/tests/TeaPremium.IntegrationTests/PaymentAuthorizationAndCsrfTests.cs` *(tạo mới — `4` `[Fact]`: HTTPS `400`, anonymous `401`, CSRF `400`, `text/plain` → RFC 9457 `415 unsupported_media_type` trên cả 3 `POST`, RBAC `content`/`sales`/`owner` trên cả 4 endpoint)*
39. `backend/tests/TeaPremium.IntegrationTests/AuditValuePolicyTests.cs` *(sửa — giữ đủ test `crm.interactions` của `P2-03` + bổ sung `sales.payments`/`sales.refunds` gồm `kind` & `shipping_amount_vnd`/`sales.return_requests`)*
40. `backend/tests/TeaPremium.IntegrationTests/OpenApiTests.cs` *(sửa — tổng operation `111 → 115`, xác nhận 4 route `P2-07` + `POST .../customers/import` của `P2-03`, `kind` required, `shippingAmountVnd` optional, chưa có `POST admin/cod-settlements`)*
41–47. `CrmSalesFinanceFixture.cs`, `CrmSalesFinanceParityTests.cs`, `CrmSalesFinanceDatabaseTests.cs`, `CrmSalesFinanceRollbackTests.cs`, `DatabaseRolesTests.cs`, `CatalogInventoryDatabaseTests.cs`, `ReservationsMigrationTests.cs` *(sửa — cập nhật `P207Head`, tổng số migration `8 → 9`, bảng `sales.refunds` có thêm `kind` & `shipping_amount_vnd` trong test hiện hữu)*

---

## 6. Rủi ro Kỹ thuật & Bàn giao Liên Task

| Mã | Nguồn | Rủi ro | Biện pháp đã khóa chặn |
|---|---|---|---|
| `R-P207-01` | `05-api.md:192` vs `195` | `POST .../payments/{id}/void` không có `orderId` trên URL; khóa `sales.payments` trước `sales.orders` gây deadlock `40P01`. | Đọc nhẹ `AsNoTracking()` lấy `order_id`, rồi luôn khóa `L1 sales.orders FOR UPDATE` trước `L2 sales.payments FOR UPDATE`. |
| `R-P207-02` | `schema.sql:954` & `AuditValuePolicy.cs` | Void `confirmed` quên `confirmed_at = NULL` nổ `23514`; fallback audit chung thiếu `"voided"`/`"awaiting_settlement"`/`"resolved"` hoặc làm lọt PII. | Đặt đồng thời `status='voided', confirmed_at=NULL, void_reason=reason` và khai báo allowlist tường minh theo `entityType` (`kind`, `shipping_amount_vnd`, `confirmed_at` Safe; `bank_reference`/`note`/`void_reason`/`reason` Redacted). |
| `R-P207-03` | `schema.sql:869` (`ck_order_due`) | Xóa `payment_due_at` khi auto-confirm nổ `23514`. | Dùng `Order.Confirm(...)` giữ nguyên `payment_due_at`. |
| `R-P207-04` | `IdempotencyEndpoint.cs` & `NFR-04` | Lộ dữ liệu nhạy cảm trong `platform.idempotency_keys.response_body`. | `PaymentRecordedView` và `RefundRecordedView` không chứa `note`/`reason`/`bankReference`; kiểm chặt qua `SafePaymentRecordedResponse` / `SafeRefundRecordedResponse` (18 trường). |
| `R-P207-05` | `202611260800_P2_07_RefundClassificationAndShipping` | Tự gán default cho `kind` trên DB đã có refund sẽ làm sai lệch doanh thu/LTV/P&L. | `kind text NOT NULL` không có `DEFAULT`; `Up` khóa `ACCESS EXCLUSIVE` và `RAISE EXCEPTION` nếu `sales.refunds` không rỗng; `Down` khóa tương tự và chặn nếu có dữ liệu hoặc object/dependency ngoài phạm vi. |
| `R-P207-06` | `schema.sql:1007–1030` | Đơn có nhiều shipment (`failed` rồi `delivered`) gây nhân đôi dòng ở `GET .../awaiting?type=cod`. | Chọn duy nhất shipment `purpose='order' AND status='delivered'` mới nhất theo `(delivered_at DESC, created_at DESC)`. |

### Bàn giao sang các task kế tiếp
- **`P2-06` (Admin Orders API):** Rebase sau khi `P2-07` merge; tái dùng `OrderPaymentStatusCalculator` và đồng bộ truy vấn SQL `?flag=needs_refund|overpaid` theo đúng 3 nhánh `O3a = C` + `EXISTS return_requests(approved, refund)` (`O2 = A`).
- **`P2-08` (Shipments):** Dùng `calc.RemainingToPayVnd` cho `cod_amount_vnd` (`BR-SHP-02`, đơn `0đ` → `0`) và tái dùng `MarkOrderDeliveryFailed` đã tự tính lại `payment_status` (`BR-SHP-03`).
- **`P2-09` (COD Settlements):** Tái dùng `OrderPaymentStatusCalculator` khi xác nhận hàng loạt `payments(cod, awaiting_settlement → confirmed)`.
- **`P2-10` (Returns):** Dùng chung `CalculateReturnRefundCeiling` và luồng 2 bước (`1 return_request ↔ tối đa 1 refund` qua `ux_refunds_return_request`).
- **`P2-11` (CRM LTV), `P4-02` (P&L/Cashflow/Excel), `P5-05` (Referral):** Đăng ký `IRefundRecordedHook` / truy vấn `sales.refunds` lọc `kind IN ('return','adjustment')` cho doanh thu/LTV/referral (`BR-ORD-07.2`, `BR-FIN-02`, `BR-FIN-08`, `BR-MKT-07`), giữ mọi `kind` cho Cashflow (`BR-FIN-04`).
- **`P3-02` (Checkout/VietQR) & `P3-06` (Customer Emails):** VietQR dùng `calc.RemainingToPayVnd` (`= 0` gồm đơn `0đ` không sinh QR, `BR-PAY-04`); `P3-06` đăng ký `IOrderOutboxStep` nhận `OrderConfirmed(ReasonCode = "auto_confirmed_on_full_payment")` để gửi `payment_received_customer`.
