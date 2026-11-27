# P2-06: Tạo/sửa/hủy đơn Admin (BR-ORD-04/08, BR-PRC-06/07), đơn backfill (BR-ORD-09) & 2 endpoint công khai địa giới hành chính (`GAP-P214-01 = A`)

- **Trạng thái hồ sơ:** `G3 IMMUTABLE IMPLEMENTATION RECORD` (hoàn tất trước khi đóng băng candidate manifest trên approved base `origin/main` `d67c66f1c5d86382d220a7de8d23b983c2b5ecd8` — không tự tham chiếu manifest SHA-256, commit SHA chưa publish, verdict G4, kết quả CI hay trạng thái merge; sửa đặc tả đúng 2 dòng `docs/spec/05-api.md:181,183` theo `G11 = A` đã được Owner phê duyệt; **0** migration mới, **0** sửa đổi `apps/*` hay `scripts/*`).
- **Tier:** `A` — chạm tiền VND (`bigint`), chiết khấu tay theo ngưỡng quyền (`BR-PRC-06`, `AC-P2-06`), chốt chặn chống lộ giá vốn 3 lớp (`BR-PRC-07`), giữ/nhả tồn kho & hoàn voucher (`BR-INV-03/04`, `G12 = A`), PII khách hàng & HTML chống XSS (`BR-ORD-12`), idempotency tạo đơn (`NFR-03`, `ASSUMPTION-P206-04 = A`), đơn backfill lịch sử chỉ `owner` (`BR-ORD-09`), sửa nhỏ 2 dòng đặc tả `05-api.md:181,183` (`G11 = A`), và 2 endpoint công khai đọc địa giới hành chính (`GAP-P214-01 = A`).
- **Worktree/base:** `D:\tra-hien-p2-06`, nhánh `feature/p2-06-admin-orders`, approved base `origin/main` = `d67c66f1c5d86382d220a7de8d23b983c2b5ecd8` (đã bao gồm `P2-01`, `P2-02`, `P2-03` PR `#51`, `P2-05`, `P2-07` PR `#52`, `P2-04` PR `#53`, `P2-08` PR `#54`, `P2-13`, Workflow V2 PR `#49`/`#50`).
- **Readiness (scoped G1/G2):** `GO` — toàn bộ quyết định `O6a = A`, `G11 = A`, `G12 = A`, `O1 = A`, `O2 = A`, `O3a = C`, `O3b = Phương án 3`, `ASSUMPTION-P206-01/03/04/05 = A`, và `GAP-P214-01 = A` đều ở trạng thái **`OWNER_APPROVED`** (ghi nhận tại [ADR-P2-01-phase2-contracts.md](../decisions/ADR-P2-01-phase2-contracts.md) §4.5–§4.6).
  - Tái sử dụng baseline Full G1 tại `cadce575597738bf40ad7c45689482de5a5fbc3d` ([spec-inventory.md](../analysis/spec-inventory.md), [spec-gaps.md](../analysis/spec-gaps.md)), G2 nền ([conformance-review.md](../architecture/conformance-review.md)), và các hợp đồng đã merge của `P2-05`, `P2-07`, `P2-08`.
  - Phụ thuộc trực tiếp đã merge trên `d67c66f`: `P2-01` (`85218d1`), `P2-02` (`97735e7`), `P2-05` (`862fbf0`), `P2-07` (`137dca8` — cung cấp `OrderPaymentStatusCalculator` và `IOrderPaymentLedgerReader`), `P2-08` (`d67c66f` — cung cấp `IOrderShipmentQueries`).
- **Traceability:** xem Mục 1.2 và bảng ánh xạ code/test ở Mục 4–5.
- **Scope / out of scope:** xem Mục 1.3.
- **Migration/recovery:** **`none`** (`0` migration, `0` thay đổi `AppDbContextModelSnapshot`). Toàn bộ bảng `sales.*`, `crm.customers`, `pricing.vouchers`/`voucher_redemptions`, `inventory.reservations`/`stock_levels`, `platform.provinces`/`wards`, chỉ mục, `xmin` và quyền `tp_app`/`tp_readonly` đã hoàn tất ở `P0-09`, `P2-01`, `P2-05`, `P2-07`. Khôi phục bằng cách revert commit nhánh `feature/p2-06-admin-orders` (bao gồm 2 dòng `docs/spec/05-api.md` và OpenAPI/client).
- **Pre-freeze verification evidence:**
  - `./scripts/verify-linux.ps1 -Mode api -WriteBack` (`RESULT=PASS coverage=API`, `133` OpenAPI operations: `123` nền + `8` Admin Orders + `2` Store Administrative; `11` Store GET operations; không có `/extend-payment`; `POST /api/v1/admin/orders` chỉ có duy nhất phản hồi 2xx là `201`).
  - `./scripts/verify-linux.ps1 -Filter "FullyQualifiedName~AdminOrders|FullyQualifiedName~StoreAdministrative|FullyQualifiedName~OpenApiTests|FullyQualifiedName~CompiledArchitectureTests"` (`RESULT=FOCUSED_PASS coverage=FOCUSED_BACKEND`, `0` fail, `0` skip, `OpenAPI/client are current.`).
  - Kết quả manifest-bound `FULL_ALL` và Dynamic Task Status nằm ngoài hồ sơ bất biến này (tại Unified Handoff Report / PR body).

---

## 1. Định danh đặc tả, quyết định Owner và phạm vi

### 1.1 Quyết định Owner đã phê duyệt (`OWNER_APPROVED`)

1. **`O6a = A` (`2026-11-27`):** Giữ `ExpireUnpaidOrders` (`BR-ORD-05`), `Order.ExtendPaymentDue`, và `POST /api/v1/admin/orders/{id}/extend-payment` (`BR-ORD-06`) ở **`P3-03`** ([07-roadmap.md](../spec/07-roadmap.md#L125)). `P2-06` mở đúng **8** Admin Orders endpoints và không thêm job nền hay route `/extend-payment`.
2. **`G11 = A` (phê duyệt `STEP 598`, tái xác nhận `2026-11-27` — Spec Amendment Authorized):** Sửa duy nhất đúng 2 dòng `181` và `183` của [05-api.md](../spec/05-api.md#L181-L183) bổ sung `callConfirmed?, callNote?` vào cả hai đường xác nhận đơn COD (`POST admin/orders` khi `confirmNow = true` và `POST admin/orders/{id}/confirm`). Đọc setting `orders.cod_requires_call` từ `SettingsCatalog`; khi đang `true` và đơn là `cod` mà thiếu `callConfirmed == true` hoặc thiếu ghi chú cuộc gọi thì trả `422 cod_confirmation_required`.
3. **`G12 = A` & `AC-P2-05`:** `PATCH /api/v1/admin/orders/{id}` (`BR-ORD-08`) gọi đồng bộ giữ/nhả kho và `pricing.voucher_redemptions` + `used_count`; nếu giảm dòng hàng khiến tổng tụt dưới `min_order_vnd` của voucher đang áp thì tự gỡ voucher, hoàn `used_count`, và trả cảnh báo `{ code: "voucher_removed_min_order" }` trong `warnings[]`. Bảo vệ cạnh tranh bằng `rowVersion` (`xmin` → `409 concurrency_conflict` trên `PATCH`, `confirm`, `cancel`).
4. **`ASSUMPTION-P206-01 = A` (`2026-11-27`):**
   - `PATCH /api/v1/admin/orders/{id}` hỗ trợ `discountNote?`: khi `discountVnd > 0`, bắt buộc có `discountNote` không rỗng (mới gửi hoặc giữ lại `order.DiscountNote` hiện có; thiếu → `400 validation_failed`); khi `discountVnd == 0`, tự động xóa `discountNote = null`.
   - Kiểm `BR-PRC-06` (`AC-P2-06`): nhân viên thiếu `costs.read` tạo hoặc sửa làm tỷ lệ giảm tay vượt `orders.max_staff_discount_percent` (mặc định `10%`) nhận `422 discount_requires_owner`; tuy nhiên `sales` vẫn được sửa `shipping` hoặc `internalNote` trên đơn đã có giảm giá tay do `owner` duyệt nếu không chạm `lines`/`discountVnd`.
   - Phân biệt **3 chiều** cho `shippingFeeVnd` trong JSON `PATCH` bằng parser `JsonDocument` có cờ `HasShippingFeeVnd`:
     1. *Omitted (`HasShippingFeeVnd == false`):* trên `origin == "admin"`, giữ nguyên `order.ShippingFeeVnd`; trên `origin == "web"`, tự tính lại theo `BR-SHP-01` nếu `lines` hoặc `discountVnd` thay đổi (nếu không thì giữ nguyên).
     2. *Explicit `null` (`HasShippingFeeVnd == true && ShippingFeeVnd == null`):* luôn tự tính lại phí ship theo `BR-SHP-01` (`CheckoutCalculator`).
     3. *Explicit number (`>= 0`):* áp dụng phí ship tường minh (âm → `400 validation_failed`).
5. **`ASSUMPTION-P206-02` giữ `SUPERSEDED` (`O1 = A`, `O2 = A`, `O3a = C`, `O3b = Phương án 3`):**
   - Tái sử dụng duy nhất `TeaPremium.Domain.Orders.OrderPaymentStatusCalculator`, `IOrderPaymentLedgerReader` (`P2-07`), và `IOrderShipmentQueries` (`P2-08`) trên `d67c66f`; không tạo calculator thứ hai.
   - Khi `PATCH` cập nhật tổng tiền, đọc tổng sổ thanh toán qua `IOrderPaymentLedgerReader.GetOrderLedgerTotalsAsync(order.Id, ct)`, tính bằng `OrderPaymentStatusCalculator.Calculate(...)` và áp vào `order.ApplyPaymentCalculation(calc, now)`.
   - Công thức SQL `?flag=needs_refund|overpaid|expiring` trên `GET admin/orders` và các cờ tính trên `GET admin/orders/{id}` đồng nhất toán học 100%:
     - `confirmedPaid = SUM(payments.amount_vnd WHERE status = 'confirmed')`
     - `refunded = SUM(refunds.amount_vnd)`, `overpaymentRefunded = SUM(refunds.amount_vnd WHERE kind = 'overpayment')`
     - `netPaid = confirmedPaid - refunded`, `effectivePaid = confirmedPaid - overpaymentRefunded`
     - `hasApprovedRefundReturn = EXISTS(return_requests WHERE order_id = o.id AND status = 'approved' AND resolution = 'refund')`
     - `isOverpaid = NOT o.is_backfilled AND o.status NOT IN ('cancelled', 'delivery_failed') AND effectivePaid > o.total_vnd AND netPaid > 0`
     - `needsRefund = NOT o.is_backfilled AND ((o.status IN ('cancelled', 'delivery_failed') AND netPaid > 0) OR isOverpaid OR hasApprovedRefundReturn)`
     - `isExpiring = NOT o.is_backfilled AND o.status = 'pending' AND o.payment_method = 'bank_transfer' AND o.total_vnd > 0 AND o.payment_status = 'unpaid' AND o.payment_due_at IS NOT NULL AND o.payment_due_at > nowUtc AND o.payment_due_at <= nowUtc + 24h` (đồng nhất 100% với `BR-ORD-05`, chỉ mục `ix_orders_due`, và `EfDailyDigestRead` của `P2-13`; loại trừ đơn `partially_paid` và đơn `0đ`).
6. **`ASSUMPTION-P206-03 = A` (`2026-11-27`):** `POST /api/v1/admin/orders/{id}/reorder` cho phép đặt lại từ cả 3 trạng thái cuối (`delivered`, `cancelled`, `delivery_failed`), từ chối `pending`/`confirmed`/`shipping` với `409 invalid_status_transition`; body tùy chọn `{ salesChannel?, paymentMethod?, internalNote? }` (chấp nhận `{}` hoặc body rỗng); map `salesChannel == "web"` → `"other"` khi không ghi đè; luôn tính giá và kiểm tồn kho theo **catalog hiện hành** qua `CheckoutCalculator`, tạo đơn `admin`/`pending` mới kèm mã `TP` và reservation mới, không sao chép giá tay/voucher/giảm giá/thanh toán/hoàn tiền/vận đơn cũ.
7. **`ASSUMPTION-P206-04 = A` (`2026-11-27`):** `POST /api/v1/admin/orders` (`[idem]`) trả `201 Created` `AdminOrderCreatedView` (`18` trường không chứa PII và không chứa giá vốn) kèm bộ kiểm whitelist đóng `AdminOrderIdempotencyPolicy.SafeCreateOrderResponse` để `platform.idempotency_keys.response_body` tuyệt đối sạch PII/giá vốn.
8. **`ASSUMPTION-P206-05 = A` (`2026-11-27`, `BR-ORD-09` Backfill):** Chỉ vai trò `owner` (`sales`/`content` → `403 forbidden`); `Order.CreateBackfilled` tạo `status = 'delivered'`, `is_backfilled = true`, `payment_status = 'unpaid'` (khớp nhánh 1 của `O3a = C`), `0` reservations, `0` `stock_movements`, `0` `payments`, `0` `IProductSoldOutSync`, `0` `OrderPlaced`, chỉ phát duy nhất sự kiện outbox `OrderDelivered(ReasonCode = "backfilled")`.
9. **`GAP-P214-01 = A` (`2026-11-27`):** Mở 2 endpoint công khai đọc địa giới hành chính (`AllowAnonymous`, `0` migration, `0` PII) dùng lại `IAdministrativeReferenceData` / `EfAdministrativeReferenceData` cho `P2-14` & `P2-15`:
   - `GET /api/v1/store/provinces`: nếu `!IsReadyAsync` → `422 administrative_data_not_ready`; ngược lại `200 OK` `StoreProvinceListView { items: AdministrativeProvinceItem[] }` chỉ gồm tỉnh `is_active = true`, sắp `SortOrder ASC, Name ASC, Code ASC`.
   - `GET /api/v1/store/provinces/{code}/wards`: kiểm hợp lệ `{code}` trước (`1..16` ký tự ASCII chữ/số/`-`/`_` sau trim, không `\0`/ký tự điều khiển → `400 validation_failed`); nếu `!IsReadyAsync` → `422 administrative_data_not_ready`; nếu tỉnh không tồn tại hoặc `is_active = false` → `404 not_found`; ngược lại `200 OK` `StoreWardListView { provinceCode, provinceName, items: AdministrativeWardItem[] }` chỉ gồm phường/xã `is_active = true`, sắp `Name ASC, Code ASC`.

### 1.2 Truy vết nguồn đặc tả và mã định danh

- **Task ID:** `P2-06` ([07-roadmap.md](../spec/07-roadmap.md#L91)) + `GAP-P214-01 = A`.
- **Nguồn đặc tả:**
  - [00-README.md](../spec/00-README.md) (`DEC-01`, `DEC-03`, `DEC-05..11`, `DEC-14..16`, `DEC-18`, `DEC-20`, `DEC-30`).
  - [01-requirements.md](../spec/01-requirements.md) (`FR-ORD-01..07`, `FR-PLT-01`, `FR-PLT-02`, `NFR-01..04`, `NFR-07..09`, `NFR-12`).
  - [02-architecture.md](../spec/02-architecture.md) (§3 biên module `Orders ↔ CRM ↔ Inventory`, §4 use cases, §5.2–§5.5 `IUnitOfWork`/`xmin`/`Idempotency-Key`/`[Audited]`/outbox, §6–§7 `AdminCookie`/CSRF/ma trận quyền & lọc giá vốn theo `costs.read`).
  - [03-database.md](../spec/03-database.md) & [schema.sql](../spec/schema.sql) (`sales.*`, `crm.customers`, `pricing.vouchers`/`voucher_redemptions`, `inventory.reservations`/`stock_levels`, `platform.provinces`/`wards`, `platform.settings`).
  - [04-business-rules.md](../spec/04-business-rules.md) (`BR-CAT-15`, `BR-INV-03/04/11/12`, `BR-PRC-01/02/04..07`, `BR-ORD-01..04/08/09/10/12`, `BR-PAY-02`, `BR-SHP-01/02/04`, `BR-CRM-01/02/14`, `BR-FIN-01`).
  - [05-api.md](../spec/05-api.md#L179-L190) (§1 quy ước chung, §4.4 Admin Orders — riêng dòng `181` & `183` đã cập nhật theo `G11 = A`).
- **Tiêu chí nghiệm thu trực tiếp:** `AC-P2-05` (`xmin` 409 trên `PATCH`/`confirm`/`cancel`), `AC-P2-06` (`sales` giảm 15% → `422 discount_requires_owner`, `owner` → thành công); củng cố mặt HTTP cho `AC-P2-01`, `AC-P2-02`, `AC-P0-03..05`, `AC-P1-06`.

### 1.3 Trong phạm vi / Ngoài phạm vi

- **Trong phạm vi:**
  - `8` Admin Orders endpoints (`/api/v1/admin/orders*`): `GET ""`, `GET "/{id}"`, `POST ""` (`[idem]`), `PATCH "/{id}"`, `POST "/{id}/confirm"`, `POST "/{id}/cancel"`, `POST "/{id}/reorder"`, `GET "/{id}/packing-slip"`.
  - `2` Store Administrative endpoints (`GAP-P214-01 = A`): `GET /api/v1/store/provinces`, `GET /api/v1/store/provinces/{code}/wards`.
  - Sửa tối thiểu 2 dòng `docs/spec/05-api.md:181,183` (`G11 = A`) và cập nhật `docs/decisions/ADR-P2-01-phase2-contracts.md` (§4.6, §6, §7, §10–§13).
  - Tái sinh `packages/api-client/openapi.json` & `packages/api-client/src/generated/schema.ts`, bổ sung kiểm tra kiểu TypeScript `admin-orders-contract.test-d.ts`.
- **Ngoài phạm vi:**
  - `ExpireUnpaidOrders` (`BR-ORD-05`), `Order.ExtendPaymentDue`, `POST /api/v1/admin/orders/{id}/extend-payment` (`BR-ORD-06`) → **`P3-03`** (`O6a = A`).
  - Thanh toán/hoàn tiền/đối soát COD (`P2-07` đã merge, `P2-09`); vận đơn (`P2-08` đã merge); đổi trả (`P2-10`); subscriber `OrderDelivered` cập nhật chỉ số khách (`P2-11`); thông báo (`P2-13` đã merge, `P3-06`); giao diện Admin UI (`P2-14`, `P2-15`).

---

## 2. Hợp đồng 10 HTTP Endpoints triển khai trong `P2-06`

### 2.1 Nhóm Admin Orders (`8` endpoints dưới `/api/v1/admin/orders`)

Mọi endpoint dưới `/api/v1/admin/orders` đi qua `AdminTransportMiddleware` (bắt buộc HTTPS `400 https_required`, `Cache-Control: no-store`), `AdminCookie` (`AdminSecurity.Ready`), phân quyền rõ ràng (`orders.read` / `orders.write` / kiểm động `orders.override_price`, `costs.read`, vai trò `owner`), `X-CSRF-TOKEN` trên mọi `POST`/`PATCH`, và parser JSON nghiêm ngặt (`AllowDuplicateProperties = false`, `UnmappedMemberHandling = Disallow`, `NumberHandling = Strict`, `415` khi sai Content-Type).

| # | Method & Path | Quyền & Bảo vệ | Đầu vào chính | Đầu ra thành công | Mã lỗi chính |
|---:|---|---|---|---|---|
| 1 | `GET /api/v1/admin/orders` | `orders.read` · `no-store` | `page`, `pageSize` (`1..100`), `sort`, `status`, `paymentStatus`, `channel`, `origin`, `from`, `to` (VN date `[from, to+1d)` hoặc ISO offset), `q` (mã đơn, đuôi SĐT, tên không dấu `unaccent` escape `%_\`), `customerId`, `flag` (`needs_refund\|overpaid\|expiring`) | `200 OK` `CatalogPage<AdminOrderListItemView>` (`0` giá vốn) | `400 validation_failed`, `401`, `403` |
| 2 | `GET /api/v1/admin/orders/{id}` | `orders.read` (+ `costs.read` cho `lines[].unitCostVnd`) · `no-store` | `id:guid` | `200 OK` `AdminOrderDetailView` (đọc thật 6 bảng con: `lines`, `payments`, `refunds`, `shipments+events`, `returns+lines`, `history`, cộng `warnings[]`) | `401`, `403`, `404 not_found` |
| 3 | `POST /api/v1/admin/orders` **[idem]** | `orders.write` (+ `orders.override_price` nếu có `unitPriceVnd`; `owner` nếu `isBackfilled`) · CSRF · `Idempotency-Key: <uuid>` | `CreateAdminOrderRequest` (`customerId ^ newCustomer`, `lines[]`, `discountVnd?`, `discountNote?`, `shippingFeeVnd?`, `shipping`, `internalNote?`, `confirmNow`, `callConfirmed?`, `callNote?`, `isBackfilled?`, `deliveredAt?`) | `201 Created` `AdminOrderCreatedView` (`18` trường, `0` PII/giá vốn, kiểm bởi `SafeCreateOrderResponse`) | `400`, `401`, `403`, `404`, `409`, `415`, `422` |
| 4 | `PATCH /api/v1/admin/orders/{id}` | `orders.write` (+ `orders.override_price` nếu có `unitPriceVnd`) · CSRF | Partial JSON (`JsonDocument`): `rowVersion` + ≥ 1 trường trong `lines`, `shipping`, `discountVnd`, `discountNote`, `shippingFeeVnd` (3-way omitted/null/value), `internalNote` | `200 OK` `AdminOrderDetailView` (`rowVersion` mới, `paymentStatus` tính lại theo `O3a = C`, `warnings[]` khi tự gỡ voucher theo `G12 = A`) | `400`, `401`, `403`, `404`, `409 concurrency_conflict`, `415`, `422` |
| 5 | `POST /api/v1/admin/orders/{id}/confirm` | `orders.write` · CSRF | `{ rowVersion, callConfirmed?, callNote?, note? }` | `200 OK` `AdminOrderDetailView` (`status = "confirmed"`) | `400`, `401`, `403`, `404`, `409`, `415`, `422 cod_confirmation_required` |
| 6 | `POST /api/v1/admin/orders/{id}/cancel` | `orders.write` · CSRF | `{ rowVersion, reason }` (`1..500`) | `200 OK` `AdminOrderDetailView` (`status = "cancelled"`, nhả reservation, hoàn voucher, bật `needsRefund` nếu `netPaid > 0`) | `400`, `401`, `403`, `404`, `409 invalid_status_transition` (gồm khi đã có active shipment `BR-SHP-04`), `415` |
| 7 | `POST /api/v1/admin/orders/{id}/reorder` | `orders.write` · CSRF | Tùy chọn `{ salesChannel?, paymentMethod?, internalNote? }` (hoặc `{}` / rỗng) | `201 Created` `AdminOrderDetailView` + `Location` (đơn mới `admin`/`pending`, giá/tồn kho catalog hiện hành) | `400`, `401`, `403`, `404`, `409`, `415`, `422` |
| 8 | `GET /api/v1/admin/orders/{id}/packing-slip` | `orders.read` · `no-store` | `id:guid`, `hidePrices=false` | `200 OK` `text/html; charset=utf-8` (inline SVG Code 128B, `WebUtility.HtmlEncode`, `hidePrices` ẩn giá nhưng giữ COD, CSP + `X-Content-Type-Options: nosniff` + `X-Frame-Options: SAMEORIGIN`) | `400 validation_failed`, `401`, `403`, `404 not_found` |

### 2.2 Nhóm Store Administrative Data (`2` endpoints theo `GAP-P214-01 = A`)

| # | Method & Path | Quyền | Đầu vào | Đầu ra thành công | Mã lỗi chính |
|---:|---|---|---|---|---|
| 9 | `GET /api/v1/store/provinces` | `AllowAnonymous` | Không | `200 OK` `StoreProvinceListView` (`items[]` `is_active = true`, `SortOrder ASC, Name ASC, Code ASC`, `0` PII) | `422 administrative_data_not_ready` |
| 10 | `GET /api/v1/store/provinces/{code}/wards` | `AllowAnonymous` | `code` (`1..16` trimmed ASCII `[A-Za-z0-9_-]`) | `200 OK` `StoreWardListView` (`provinceCode`, `provinceName`, `items[]` `is_active = true`, `Name ASC, Code ASC`, `0` PII) | `400 validation_failed`, `422 administrative_data_not_ready`, `404 not_found` |

---

## 3. Bảo mật, ranh giới module và bất biến chính

1. **Ranh giới module `Orders ↔ CRM` (`CrmModuleBoundaryRule`):**
   - `Orders` chỉ gọi `ICustomerOrderResolver` (`Application/Crm/Contracts/`); hiện thực `EfCustomerOrderResolver` nằm trong `Infrastructure/Crm` và tuyệt đối chỉ thao tác `crm.customers` (không chạm bất kỳ entity nào ngoài `crm`), vượt qua kiểm tra IL `Mono.Cecil` của `CompiledArchitectureTests`.
   - Kiểm tra `HasAnyDeliveredOrderAsync` cho `isFirstPurchase` đặt tại `IOrderRepository` / `EfOrderRepository` (`Infrastructure/Orders`).
   - Quick-create `newCustomer` dùng `SAVEPOINT p206_quick_customer` và chỉ detach entity mới thêm nếu bắt `23505` trùng SĐT (`ux_customers_phone`), bảo toàn entity `IdempotencyKey` của transaction bao ngoài (không bao giờ gọi `ChangeTracker.Clear()`).
   - Khách chỉ có `zalo_name` (`phone_e164 IS NULL`) tự động fallback sang `receiverPhone` đã chuẩn hóa cho `orders.contact_phone` để thỏa `ck_orders_contact_phone`.
2. **Bảo vệ giá vốn 3 lớp (`BR-PRC-07`):**
   - Kiểm `orders.override_price` trước khi đọc tồn kho/giá vốn (`403 forbidden`).
   - Kiểm `variant_not_sellable` trước khi đối chiếu `avg_cost_vnd` để không tạo oracle phân biệt giá vốn của biến thể không bán được.
   - `422 price_below_allowed` chỉ trả thông điệp hằng tĩnh + `data.variantId`, không bao giờ xuất `avg_cost_vnd` ra response hay log; `AdminOrderLineView.UnitCostVnd` gắn `[JsonIgnore(WhenWritingNull)]` và chỉ điền khi `actor.Has("costs.read")`; `AdminOrderListItemView`, `AdminOrderCreatedView` và `packing-slip` HTML không có trường giá vốn.
3. **Bảo vệ HTML `packing-slip` (`BR-ORD-12`):**
   - Xác thực `OrderCode.IsValid(code)` trước khi mã hóa mã vạch SVG Code 128B nội tuyến (ISO/IEC 15417: 107 mẫu, kiểm parity & checksum mod 103).
   - Mọi chuỗi động đi qua `WebUtility.HtmlEncode`; tuyệt đối không xuất `internalNote`, `discountNote`, `priceOverrideReason`, `unitCostVnd`/`avgCostVnd`.
   - Gắn cứng `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN`, và `Content-Security-Policy: default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'`.

---

## 4. Danh mục tệp thay đổi (`31` tệp, `0` migration)

- **Đặc tả & Quyết định (`3` tệp):**
  - `docs/spec/05-api.md` *(sửa đúng dòng `181` & `183` theo `G11 = A`)*
  - `docs/decisions/ADR-P2-01-phase2-contracts.md` *(ghi nhận phê duyệt `2026-11-27` cho `P2-06` + `GAP-P214-01 = A` và trạng thái merge thực tế `P2-07`/`P2-04`/`P2-08`)*
  - `docs/plans/p2-06-implementation.md` *(hồ sơ bất biến này)*
- **`TeaPremium.Domain` (`2` tệp sửa):**
  - `backend/src/TeaPremium.Domain/Orders/OrderConstants.cs` (`OrderDeliveredReasonCodes.Backfilled`)
  - `backend/src/TeaPremium.Domain/Orders/Order.cs` (`Order.CreateBackfilled`, `Order.UpdateAdminDetails`, `Order.UpdateAdminAdjustments`)
- **`TeaPremium.Application` (`7` tệp: `3` sửa, `4` mới):**
  - `backend/src/TeaPremium.Application/AdministrativeData/IAdministrativeReferenceData.cs` *(mở rộng DTO & query danh sách tỉnh/phường + `AdministrativeProvinceCode.TryNormalize`)*
  - `backend/src/TeaPremium.Application/Crm/Contracts/ICustomerOrderResolver.cs` *(mới)*
  - `backend/src/TeaPremium.Application/Orders/OrderOutbox.cs` *(cho phép `ReasonCode = "backfilled"`)*
  - `backend/src/TeaPremium.Application/Orders/OrderContracts.cs` (`HasAnyDeliveredOrderAsync`)
  - `backend/src/TeaPremium.Application/Orders/AdminOrderContracts.cs` *(mới)*
  - `backend/src/TeaPremium.Application/Orders/AdminOrderUseCases.cs` *(mới)*
  - `backend/src/TeaPremium.Application/Orders/PackingSlipHtmlRenderer.cs` *(mới)*
- **`TeaPremium.Infrastructure` (`7` tệp: `5` sửa, `2` mới):**
  - `backend/src/TeaPremium.Infrastructure/AdministrativeData/EfAdministrativeReferenceData.cs`
  - `backend/src/TeaPremium.Infrastructure/Crm/EfCustomerOrderResolver.cs` *(mới)*
  - `backend/src/TeaPremium.Infrastructure/Crm/CrmRegistration.cs`
  - `backend/src/TeaPremium.Infrastructure/Orders/EfOrderRepository.cs`
  - `backend/src/TeaPremium.Infrastructure/Orders/EfAdminOrderQueries.cs` *(mới)*
  - `backend/src/TeaPremium.Infrastructure/Orders/OrdersRegistration.cs`
  - `backend/src/TeaPremium.Infrastructure/Persistence/AuditSaveChangesInterceptor.cs` *(bổ sung `or Order` để lưu đủ 6 cột `OnAdd` khi tạo `sales.orders` trong `platform.audit_logs` với cơ chế che PII fail-closed)*
- **`TeaPremium.Api` (`4` tệp: `1` sửa, `3` mới):**
  - `backend/src/TeaPremium.Api/Program.cs`
  - `backend/src/TeaPremium.Api/AdministrativeData/StoreAdministrativeEndpoints.cs` *(mới)*
  - `backend/src/TeaPremium.Api/Orders/AdminOrderEndpoints.cs` *(mới)*
  - `backend/src/TeaPremium.Api/Orders/AdminOrdersOpenApi.cs` *(mới)*
- **Hợp đồng OpenAPI / TS client (`3` tệp: `2` tái sinh tự động, `1` mới):**
  - `packages/api-client/openapi.json` *(tái sinh qua `-Mode api -WriteBack`)*
  - `packages/api-client/src/generated/schema.ts` *(tái sinh qua `-Mode api -WriteBack`)*
  - `packages/api-client/src/admin-orders-contract.test-d.ts` *(mới — kiểm chứng kiểu TypeScript cho 8 Admin Orders + 2 Store Administrative endpoints và xác nhận không có `/extend-payment`)*
- **Kiểm thử tự động (`5` tệp: `1` sửa, `4` mới):**
  - `backend/tests/TeaPremium.UnitTests/AdminOrdersAndPackingSlipTests.cs` *(mới — `12` test methods)*
  - `backend/tests/TeaPremium.IntegrationTests/OpenApiTests.cs` *(sửa — khẳng định `133` total ops, `11` Store GET ops, `8` Admin Orders + `2` Store Administrative paths, không có `/extend-payment`)*
  - `backend/tests/TeaPremium.IntegrationTests/StoreAdministrativeEndpointsTests.cs` *(mới — `2` PG17 integration test methods)*
  - `backend/tests/TeaPremium.IntegrationTests/AdminOrdersPricingSecurityAndBackfillTests.cs` *(mới — `4` PG17 integration test methods)*
  - `backend/tests/TeaPremium.IntegrationTests/AdminOrdersApiTests.cs` *(mới — `14` PG17 integration test methods, gồm null JSON guards, `create` audit log 6 cột `OnAdd` + redaction, `flag=expiring` loại `partially_paid`/`0đ`, và 2 kịch bản đua song song thật bằng `Task.WhenAll` cho `AC-P2-05` & `SAVEPOINT p206_quick_customer`)*

---

## 5. Bản đồ bao phủ kiểm thử

| Nhóm kiểm thử | Phạm vi xác minh chính |
|---|---|
| `AdminOrdersAndPackingSlipTests` (UnitTests) | `Order.CreateBackfilled` (`unpaid`, `is_backfilled`, lịch sử `backfill`, chỉ phát `OrderDelivered(ReasonCode="backfilled")`, từ chối ngày tương lai/kênh `web`/actor rỗng/`unitCost != null`); `OrderOutboxMessage` nhận `"backfilled"` & từ chối free-text; `UpdateAdminDetails`/`UpdateAdminAdjustments`; bảng Code 128 ISO/IEC 15417 (107 mẫu), render SVG, `hidePrices` giữ COD, chống XSS, từ chối mã đơn không hợp lệ; `SafeCreateOrderResponse` 18 trường; `AdministrativeProvinceCode.TryNormalize`; `AdminOrderFilterParser` ngày VN half-open & ISO offset. |
| `StoreAdministrativeEndpointsTests` (PG17) | `422 administrative_data_not_ready` trước import; `400 validation_failed` kiểm trước readiness; danh sách tỉnh & phường chỉ trả bản ghi `is_active = true`, đúng thứ tự xác định, trim `{code}`, `404 not_found` cho tỉnh thiếu/inactive, `0` PII. |
| `AdminOrdersPricingSecurityAndBackfillTests` (PG17) | `AC-P2-06` (`sales` 15% → `422 discount_requires_owner`, `10%` → `201`, `owner` 15% → `201`, `sales` sửa `shipping`/`internalNote` trên đơn giảm 15% thành công nhưng giảm số lượng tụt tỷ lệ bị chặn); `BR-PRC-07` (`sales` dưới `avg_cost_vnd` → `422 price_below_allowed` 0 rò giá vốn cả JSON & log, `owner` thành công, lọc `unitCostVnd` trên GET/list/PATCH, thiếu `orders.override_price` → `403 forbidden`); `BR-ORD-09` backfill (`sales` → `403`, `owner` → `201 delivered/unpaid`, `0` reservations/`stock_movements`/`payments`, outbox `OrderDelivered(backfilled)` xử lý thành công); ma trận `401`/`400 https_required`/`403 content`/`400 csrf_invalid` trên đủ 8 endpoints. |
| `AdminOrdersApiTests` (PG17) | Tạo đơn với `customerId`, `newCustomer` (chuẩn hóa SĐT, `409 customer_phone_exists`), khách chỉ có Zalo; `[idem]` phát lại nguyên byte `201`, `409 idempotency_key_reused`, kiểm giải mã `idempotency_keys.response_body` 0 PII/giá vốn; `G11 = A` trên cả `confirmNow` và `/confirm` + bật/tắt `orders.cod_requires_call`; `AC-P2-05` `409 concurrency_conflict` trên `PATCH`/`confirm`/`cancel`; `G12 = A` tự gỡ voucher + cảnh báo; `ASSUMPTION-P206-01 = A` 3 chiều `shippingFeeVnd`, `discountNote`, tính lại `payment_status` & che PII trong `audit_logs`; hủy đơn chặn khi có active shipment (`BR-SHP-04`), nhả kho & bật `needsRefund`; `ASSUMPTION-P206-03 = A` đặt lại từ cả 3 trạng thái cuối với giá/tồn kho catalog hiện hành và `0` sao chép tài chính/vận đơn; `GET list/detail` đồng nhất 100% SQL vs detail flags (`O1`/`O2`/`O3a`/`O3b`/`P2-13`), đọc đủ 6 bảng con thật, tìm kiếm `q` không dấu/đuôi SĐT/escape `%`, lọc ngày VN; `BR-ORD-12` `packing-slip` headers CSP/`no-store`/XSS/`hidePrices`. |
| `OpenApiTests` & `CompiledArchitectureTests` & `admin-orders-contract.test-d.ts` | Khẳng định `133` total operations, `11` Store GET operations, `8` Admin Orders + `2` Store Administrative endpoints, không tồn tại `/extend-payment`, schema khớp; biên IL `CrmModuleBoundaryRule` xanh; kiểu TypeScript sinh tự động khớp hợp đồng frontend `P2-14`/`P2-15`. |
