# P2-05: Order aggregate + state machine + `CheckoutCalculator` dùng chung (BR-ORD, BR-CRT-05) + giữ/nhả/tiêu thụ hàng (BR-INV-03/04) + test đồng thời NFR-02; migration `inventory.reservations` + indexes + `fk_reservations_line`; bật đối soát reservation BR-INV-13; bằng chứng bán cho BR-SEO-21

- **Gate/status:** `READY_FOR_LOCAL_G4` (G3 + remediation F1–F4 sau G4 BLOCK hoàn tất trên `D:\tra-hien-p2-05`; đã fast-forward lên `origin/main` có PR #44 `P2-01`, áp dụng đầy đủ quyết định owner 22/11/2026 & 23/11/2026 gồm `G12 = A`, chưa commit/push).
- **Tier:** `A` — chạm tiền VND (`bigint`), bất biến kho/giữ hàng đồng thời (`NFR-02`), migration/DDL/grants (`inventory.reservations` + `fk_reservations_line`), PII đơn hàng trong audit/outbox và trạng thái bán SEO (`BR-SEO-21`).
- **Worktree/base:** `D:\tra-hien-p2-05`, nhánh `feature/p2-05-order-core`, base `origin/main` `85218d119b36d5a1b8977a75c9fb71abf454ddde` (PR #44 `P2-01` + PR #43 `P1-13` đã merge).
- **Readiness (scoped G1/G2):** `GO` (`P2-01` đã merge `origin/main` tại `85218d119b36d5a1b8977a75c9fb71abf454ddde`; owner đã khóa cứng `GAP-P205-01 = A`, `ASSUMPTION-P205-01 = B`, `ASSUMPTION-P205-02 = A`, `G12 = A`).
  - Tái sử dụng G1 đầy đủ tại `cadce575597738bf40ad7c45689482de5a5fbc3d` ([spec-inventory.md](../analysis/spec-inventory.md), [spec-gaps.md](../analysis/spec-gaps.md)), G2 nền ([conformance-review.md](../architecture/conformance-review.md), [task-readiness.md](task-readiness.md) mốc B01 `dadc4435e8f115faaeb9e8511e56e09d5d517a8a`), G1/G2 kho tại `99c6810b54d151d6a30334c9384524e3ba2b3552` ([p1-inventory-architecture.md](p1-inventory-architecture.md)), và `P1-10` tại `e63e90b99b596ba4e1d218d2a920733cbd79edbb` ([p1-10-implementation.md](p1-10-implementation.md)).
  - Đã đối chiếu trực tiếp code `P2-01` đã merge trên `BASE = 85218d119b36d5a1b8977a75c9fb71abf454ddde` (`202611230421_P2_01_CrmSalesFinanceFoundation`, `CrmSalesFinanceEntities.cs`, `CrmSalesFinanceModel.cs`, `GrantsSql.cs`, `infra/postgres/preflight.sql`, và các bài test `CrmSalesFinance*`).
  - Dependency trên `07-roadmap.md:90`: `P1-08` (cùng `P1-06`, `P1-10`, `P1-13`, `P1-14`) và **`P2-01` đều đã merge `origin/main`**. `P2-05` không phụ thuộc `P2-02` (nhận snapshot khách qua tham số/port và seed trực tiếp `crm.customers` trong test).
- **Traceability:** xem Mục 1.2 bên dưới.
- **Scope / out of scope:** xem Mục 1.3 bên dưới.
- **Cases:** xem Mục 8 bên dưới (`20` unit test cases mới trong 2 class `OrderStateMachineTests` (`13`) + `CheckoutCalculatorTests` (`7`), `29` integration test cases mới trong 4 class mới — gồm `3` bài test PG17 bổ sung trong remediation F1/F2/F3/F4 — + `1` test mới ở `StoreCatalogTests` + mở rộng `InventoryAlertsAndReconciliationTests`, `0` skipped).
- **Migration/recovery:** 1 migration forward `202611230721_P2_05_InventoryReservations` (sau `202611230421_P2_01_CrmSalesFinanceFoundation`, tổng `8` migrations) tạo `inventory.reservations` + 2 partial indexes + `fk_reservations_line` (`ON DELETE SET NULL`) + grants đóng băng; `Down` có pre-drop guard `$p205_down$` về `P201Head` trên DB dùng một lần (Mục 4.5).
- **Verification:** vòng tập trung remediation F1–F4 đã PASS (`15/15` IntegrationTests cho `OrderConcurrencyTests` + `OrderLifecycleAndReservationTests` + `27/27` CI scope classifier + `build -warnaserror` + `dotnet format whitespace` + `migrations.sql` + `OpenAPI/client are current`) tại `artifacts/verify-linux/20261125-102902-388-backend/verify.log`. Full suite backend cuối chạy ngay sau khi đóng dấu `artifacts/p2-05/candidate.manifest` (SHA-256 mới và đường dẫn log đầy đủ báo trong khối gửi Orchestrator để không sửa file sau khi ghi manifest).
- **Risks/follow-ups:** xem Mục 9 bên dưới.
- **Review/publish:** chờ G3 hoàn tất → ghi `artifacts/p2-05/candidate.manifest` → chạy full `./scripts/verify-linux.ps1 -Manifest artifacts/p2-05/candidate.manifest` → G4 độc lập khác hãng → xin phép owner trước khi `publish-pr`.

---

## 1. Định danh đặc tả, quyết định khóa cứng của Owner và ranh giới phạm vi

### 1.1 Quyết định owner 22/11/2026 (KHÓA CỨNG — chép nguyên văn)

1. `inventory.reservations` tạo ở P2-05 theo `schema.sql` đã sửa ở P2-01: `order_line_id` nullable khi `status='released'`, `CHECK (status = 'released' OR order_line_id IS NOT NULL)`, `fk_reservations_line` → `sales.order_lines(id)` `ON DELETE SET NULL`. `tp_app` được `DELETE sales.order_lines`; domain chỉ cho xóa dòng khi `order.status IN ('pending','confirmed')`.
2. BR-SEO-21: variant `is_unique_piece=true` có `available==0` là `"sold"` khi bán ròng trong `inventory.stock_movements` `-(Σ sale + Σ return_restock với ref_type IN ('order','return')) > 0`, đọc qua port `ICatalogInventoryRead` (không phá biên module). Mọi variant active của product đều độc bản và `"sold"` → set `products.sold_out_at = now`; `return_restock` làm `on_hand > 0` → `sold_out_at = NULL`; phát `StorefrontInvalidate`.
3. G22: mỗi loại message `Order*` đăng ký ĐÚNG 1 composite `IOutboxHandler` ngay từ P2-05 (tương thích `OutboxBatchProcessor.SingleOrDefault`). (`G16` và COD `awaiting_settlement` thuộc P2-08 — chỉ thiết kế điểm mở rộng, không làm.)
4. AC-P2-01: 20 request đồng thời tạo đơn cho chiếc ấm độc bản cuối → đúng 1 thành công, 19 `out_of_stock`, `reserved = 1`. Thứ tự khóa `product → variant → stock_levels` nhất quán với P1.
5. R-P201-04: `origin='admin' ⇒ sales_channel <> 'web'` kiểm ở Domain. R-P201-06: repository `order_status_history` chỉ append.
6. BR-INV-13: job đối soát P1-10 mở rộng kiểm `reserved = Σ reservation active` (hiện P1-10 cố ý không truy vấn `reservations`) — vẫn KHÔNG tự sửa số dư.
7. **Quyết định chốt 23/11/2026 (KHÓA CỨNG):**
   - `GAP-P205-01 = A`: `discountVnd` tính ra `0` → trả `422 voucher_min_order`, không ghi redemption.
   - `ASSUMPTION-P205-01 = B`: chỉ mở rộng `ReconcileInventory` + `InventoryAlertHandler` kiểm `Σ reservation active = reserved` (không tự sửa số dư); KHÔNG đổi `EfInventoryQueries.AlertsAsync`, KHÔNG đổi `InventoryQueries.cs`, KHÔNG đổi OpenAPI, KHÔNG đụng `packages/api-client`.
   - `ASSUMPTION-P205-02 = A`: chỉ `product.status='active'` và `variant.status='active'` (kèm `price_vnd` hợp lệ) mới thêm được vào đơn (web và admin) → `variant_not_sellable`.
   - `G12 = A` *(chốt remediation G4, 23/11/2026)*: gỡ voucher do sửa đơn (`UpdateOrderLines`) → cùng transaction chuyển redemption sang `'reverted'` + giảm `used_count`; giữ cùng mã đang `applied` → trừ chính lượt của đơn khỏi `UsedCount`/`CustomerAppliedCount` rồi đồng bộ `amount_vnd` (nếu sửa đơn làm giảm dưới `min_order_vnd` hoặc `discount == 0` → tự gỡ voucher và revert); áp mới/áp lại mã trên đơn đã có dòng `'reverted'` → kiểm `usage_limit` + `per_customer_limit` dưới khóa `pricing.vouchers FOR NO KEY UPDATE` rồi `UPDATE` dòng redemption cũ theo `order_id` (`UNIQUE(order_id)` — `R-P201-02`, không `INSERT` gây `23505`).

### 1.2 Truy vết nguồn đặc tả và định danh (`FR-*`, `BR-*`, `NFR-*`, `AC-*`, `DEC-*`)

- **Nguồn đặc tả:**
  - `docs/spec/07-roadmap.md:59,90-95,106-111,124` (`P1-10`, `P2-05..10`, `AC-P2-01..06`, `P3-02`).
  - `docs/spec/04-business-rules.md:4,35,51,72-73` (`BR-CAT-02`, `BR-CAT-06`, `BR-CAT-15`), `77-96` (`BR-INV-01..05`, `BR-INV-09`, `BR-INV-11..13`), `99-108` (`BR-PRC-01..07`), `115-132` (`BR-CRT-05`, `BR-CRT-06`), `143-179` (`BR-ORD-01..11`), `203-206` (`BR-SHP-01..04`), `299-302` (`BR-PLT-01`, `BR-PLT-02`).
  - `docs/spec/09-seo.md:74-75,176-177` (`BR-SEO-21`, `BR-SEO-81`).
  - `docs/spec/02-architecture.md:68-97` (§3–§4 biên module & use case), `100-148` (§5.1–§5.5 tiền/thời gian, giao dịch & UPDATE nguyên tử, idempotency, `[Audited]`, outbox), `170-177` (§5.8 `StorefrontInvalidate`).
  - `docs/spec/03-database.md:11,15-18,58-75,134-194` (mã đơn `TP`, `xmin`, tồn kho & reservations, đơn hàng, grants `tp_app`/`tp_readonly`, `platform.settings`, chính sách migration) và `docs/spec/schema.sql` (bản sau `P2-01`: dòng `301-302`, `421-455`, `499-513`, `531-551`, `808-922`, `1044-1054`).
  - `docs/spec/05-api.md:20-29,36-38,57,69-70,167,176-198` (bảng mã lỗi ProblemDetails, Store Catalog `sold`, `admin/inventory/alerts`, ranh giới endpoint đơn P2-06+/P3-02).
- **Định danh liên kết:**
  - `DEC-01`, `DEC-04`, `DEC-05`, `DEC-06`, `DEC-07`, `DEC-08`, `DEC-09`, `DEC-10`, `DEC-11`, `DEC-20`, `DEC-21`, `DEC-23`, `DEC-25`, `DEC-30`.
  - `FR-INV-07`, `FR-INV-08`, `FR-PRC-01..03`, `FR-CRT-02`, `FR-CRT-04`, `FR-ORD-03..05`, `FR-PLT-01`, `FR-PLT-02`.
  - `BR-CAT-02`, `BR-CAT-06`, `BR-CAT-15`, `BR-INV-01`, `BR-INV-02`, `BR-INV-03`, `BR-INV-04`, `BR-INV-05`, `BR-INV-09`, `BR-INV-11`, `BR-INV-12`, `BR-INV-13`, `BR-PRC-01`, `BR-PRC-02`, `BR-PRC-04`, `BR-PRC-05`, `BR-PRC-06`, `BR-PRC-07`, `BR-SHP-01`, `BR-CRT-05`, `BR-ORD-01`, `BR-ORD-02`, `BR-ORD-03`, `BR-ORD-04`, `BR-ORD-08` (lõi aggregate & đồng bộ dòng/reservation), `BR-ORD-10`, `BR-SEO-21`, `BR-PLT-01`, `BR-PLT-02`.
  - `NFR-02`, `NFR-03`, `NFR-04`, `NFR-08`, `NFR-09`.
  - Nghiệm thu trực tiếp tại `P2-05`: **`AC-P2-01`**, **`AC-P2-02`**, **`AC-P2-03`**, nền `xmin` cho **`AC-P2-05`** và bằng chứng bán backend cho **`AC-P3-06`/`AC-P3-12`**, giữ nguyên **`AC-P0-05`**.

### 1.3 Trong phạm vi `P2-05` và ranh giới với `P2-06` / `P2-07` / `P2-08` / `P2-10` / `P3`

- **Trong phạm vi `P2-05`:**
  - **Không mở thêm HTTP endpoint mới** (`admin/orders/*` hay `store/checkout/*`). `P2-05` xây tầng Domain + Application + Infrastructure + Migration nền tảng dùng chung cho cả Admin P2 và Storefront P3, đồng thời nâng cấp hành vi đọc của `GET /api/v1/store/products[/{slug}]` cho `BR-SEO-21` `"sold"`.
  - Migration `inventory.reservations` + 2 partial indexes + `fk_reservations_line` + `GrantsSql.Reservations()` + kiểm tra `preflight.sql`.
  - Aggregate `Order`, `OrderLineItem`, `OrderStatusHistoryEntry`, state machine đầy đủ (`BR-ORD-01..04`), đồng bộ thêm/sửa/xóa dòng ở `pending`/`confirmed` (`BR-ORD-08` lõi, Owner Decision #1), sinh mã đơn `TPyyMMddXXXX` chống trùng (`BR-ORD-10`), `[Audited]` cho `sales.orders` che PII/ghi chú (`AuditValuePolicy`), và `OrderVariantUsage : IVariantUsage` khóa sửa SKU/xóa variant đã lên đơn (`BR-CAT-02`/`06`).
  - `CheckoutCalculator` thuần trong Domain (`BR-CRT-05`, `BR-PRC-01/02/04/06/07`, `BR-SHP-01`, `BR-CAT-15`) + port `IVoucherRedemptionPort` (`BR-PRC-05` & `G12 = A` / `R-P201-02`: khóa `pricing.vouchers FOR NO KEY UPDATE`, áp/UPSERT theo `order_id`, cập nhật `amount_vnd` và hoàn `used_count` khi tạo/sửa/hủy đơn).
  - Dịch vụ kho `IInventoryReservationService` (`Reserve` / `Release` / `SyncLines` / `Consume` / `RestockReturned`), tuân thủ nghiêm ngặt thứ tự khóa toàn cục `Order → catalog.products → catalog.variants → inventory.stock_levels → pricing.vouchers`.
  - Mở rộng `ReconcileInventory` và `InventoryAlertHandler` kiểm `reserved == Σ reservations(active).quantity` (`BR-INV-13`, Owner Decision #6, `ASSUMPTION-P205-01 = B`, không đổi OpenAPI).
  - Sản xuất và đọc bằng chứng bán độc bản `BR-SEO-21` qua `ICatalogInventoryRead` + `IProductSoldOutSync` (`products.sold_out_at` set/clear + `StorefrontInvalidate`).
  - Đăng ký đúng 1 `CompositeOrderOutboxHandler : IOutboxHandler` cho mỗi một trong 6 message `Order*` kèm `IOrderOutboxStep` mở rộng (`G22`, Owner Decision #3).
- **Ngoài phạm vi `P2-05` (ranh giới rõ với các task kế tiếp):**
  - **`P2-06` (Admin Orders API & Backfill):** Các HTTP endpoints `GET/POST/PATCH /api/v1/admin/orders`, `POST .../confirm`, `POST .../cancel`, `POST .../reorder`, `GET .../packing-slip`; kiểm quyền HTTP `orders.read`/`orders.write`/`orders.override_price`/`costs.read` (`AC-P2-06`, `BR-PRC-06/07` đối chiếu `avg_cost` theo actor), tạo nhanh khách khi nhập đơn Admin, cảnh báo UI khi gỡ voucher do sửa đơn (`G12`), xác nhận gọi COD trên DTO (`G11`), và luồng **đơn backfill** `is_backfilled = true` (`BR-ORD-09`).
  - **`P2-07` (Payments & Refunds):** `OrderPaymentStatusCalculator` (`BR-PAY-02`), ghi nhận chuyển khoản/tiền mặt (`BR-PAY-01`), tự xác nhận đơn web khi đủ tiền (`BR-PAY-03` kết nối với `P3-03`), void payment (`BR-PAY-06`, `R-P201-01`), hoàn tiền (`BR-PAY-07`), `AC-P2-04`. (`P2-05` chỉ nhận `payment_status` hiện hành trên `Order` để kiểm điều kiện chuyển trạng thái).
  - **`P2-08` (Shipments & COD `awaiting_settlement` & Failed Restock):** CRUD vận đơn `sales.shipments` + `shipment_events` (`BR-SHP-02..05`), tạo `payments(cod, awaiting_settlement)` khi `delivered` (`BR-ORD-07.1` cắm qua điểm mở rộng `IOrderDeliveredHook` của `P2-05`), phân tách hạch toán `G16` khi giao thất bại trả hàng về kho (gọi port `RestockReturnedAsync` đã có của `P2-05` để giữ đúng `BR-SEO-21`).
  - **`P2-09` / `P2-10` (COD Settlement & Returns):** Đối soát COD (`BR-PAY-05`, `AC-P2-07`); yêu cầu/duyệt đổi trả `sales.return_requests`/`return_lines`, hoàn tiền hoặc `replacement_out`, và gọi `RestockReturnedAsync(refType: "return")` giữ đúng `BR-SEO-21` (`BR-RET-01..04`, `G04`/`G27`/`G29`/`G30`).
  - **`P2-11` / `P2-13` / `P3-01..06` / `P5-05`:** Subscriber `IOrderOutboxStep` cập nhật chỉ số & sở thích khách (`P2-11`), gửi Telegram/email/digest (`P2-13`, `P3-06`), giỏ hàng & checkout HTTP/VietQR (`P3-01`, `P3-02`), job `ExpireUnpaidOrders` & `extend-payment` (`P3-03`), mã giới thiệu `BR-MKT-05..08` (`P5-05`).

---

## 2. State Machine đơn hàng (`BR-ORD-01..04`, `order_status_history`)

### 2.1 Tập trạng thái và bất biến

- 6 trạng thái (`OrderStatus`, khớp `ck_orders_status`): `pending`, `confirmed`, `shipping`, `delivered`, `delivery_failed`, `cancelled`.
- 3 trạng thái cuối (`BR-ORD-03`): `delivered`, `delivery_failed`, `cancelled` — mọi lệnh chuyển tiếp từ 3 trạng thái này đều bị từ chối với lỗi `invalid_status_transition` (`ErrorType.Conflict` → HTTP `409`). Giao lại sau `delivery_failed` là tạo đơn mới ở `P2-06` (`BR-ORD-03`).
- Mọi thay đổi `status` bắt buộc đi qua method của aggregate `Order` (`PlaceWeb`, `CreateAdmin`, `Confirm`, `Cancel`, `MarkShipping`, `MarkDelivered`, `MarkDeliveryFailed`); thuộc tính `Status` chỉ có `private set` (`BR-ORD-01`).
- Bất biến kênh & hạn thanh toán kiểm ngay trong constructor/factory của `Order`:
  - `origin == "web"` ⇒ `sales_channel == "web"` (`ck_order_web_channel`); nếu `payment_method == "bank_transfer"` thì `payment_due_at` bắt buộc khác `null` (`> placed_at`, `ck_order_due`); nếu `cod` thì `payment_due_at == null`.
  - `origin == "admin"` ⇒ **`sales_channel != "web"`** (thuộc `{'zalo_personal','zalo_oa','facebook','phone','in_person','other'}`, kiểm ở Domain theo `R-P201-04` / Owner Decision #5), `payment_due_at == null` (`BR-ORD-04`).
  - `(status == "delivered") == (delivered_at != null)` (`ck_order_delivered`) và `(status == "cancelled") == (cancelled_at != null)` (`ck_order_cancelled`).

### 2.2 Bảng chuyển trạng thái, điều kiện, tác động kho, lịch sử & outbox

| Từ trạng thái | Method trên `Order` | Đích | Actor hợp lệ (`actor_type`) | Điều kiện nghiệp vụ kiểm tại Domain / Use Case | Tác động kho (`BR-INV-03/04`) & Voucher | Dòng `order_status_history` (append-only) & Outbox |
|---|---|---|---|---|---|---|
| *(khởi tạo)* | `Order.PlaceWeb(...)` | `pending` | `customer` (`actor_id` nullable khi vãng lai) | `origin = 'web'`, `sales_channel = 'web'`, `>= 1` dòng không trùng `variant_id`, tổng tiền khớp `CheckoutCalculator` | **Reserve (`BR-INV-03`):** `reserved += q`, tạo `reservations(active)` theo `variant_id` tăng dần; áp voucher nếu có (`BR-PRC-05`) | `(from: null, to: 'pending', reason: null)` · Outbox: `OrderPlaced` (+ `StorefrontInvalidate` nếu `available` `>0 → 0`, `InventoryAlert` nếu chạm ngưỡng tồn thấp) |
| *(khởi tạo)* | `Order.CreateAdmin(..., confirmNow: false)` | `pending` | `staff` (`actor_id` bắt buộc) | `origin = 'admin'`, `sales_channel <> 'web'`, `payment_due_at = null`, `>= 1` dòng hợp lệ | **Reserve (`BR-INV-03`)** như trên; áp voucher nếu có | `(from: null, to: 'pending', reason: null)` · Outbox: `OrderPlaced` (+ invalidation/alert kho nếu có) |
| *(khởi tạo)* | `Order.CreateAdmin(..., confirmNow: true)` | `confirmed` | `staff` (`actor_id` bắt buộc) | Như trên + nếu `payment_method == 'cod' && codRequiresCall` thì bắt buộc xác nhận đã gọi/ghi chú; đặt `confirmed_at = now` | **Reserve (`BR-INV-03`)** như trên; áp voucher nếu có | `(from: null, to: 'confirmed', reason: confirmNote)` · Outbox: `OrderPlaced` + `OrderConfirmed` |
| `pending` | `order.Confirm(...)` | `confirmed` | `staff` (`orders.write`) hoặc `system` (`BR-PAY-03`) | Nếu `origin == 'web' && payment_method == 'bank_transfer'`: bắt buộc `payment_status == 'paid'`. Nếu `payment_method == 'cod' && actor == 'staff' && codRequiresCall`: bắt buộc cờ đã gọi + ghi chú. Đặt `confirmed_at = now` | **Không đổi kho** (reservation giữ nguyên `active`) | `(from: 'pending', to: 'confirmed', reason: note)` · Outbox: `OrderConfirmed` |
| `pending` | `order.Cancel(...)` | `cancelled` | `staff` (lý do bắt buộc), `customer` (lý do tùy chọn), `system` (`reason = 'payment_expired'`) | `actor` là `staff`/`system` bắt buộc `reason.Trim().Length >= 1`. Đặt `cancelled_at = now`, `status_reason = reason` | **Release (`BR-INV-04`, `AC-P2-02`):** mọi reservation `active → released` (`closed_at = now`), `reserved -= q`, **0 dòng `stock_movements`**; hoàn `voucher_redemptions → reverted` & `used_count - 1` | `(from: 'pending', to: 'cancelled', reason)` · Outbox: `OrderCancelled` (+ `StorefrontInvalidate` nếu `available` `0 → >0`) |
| `confirmed` | `order.Cancel(...)` | `cancelled` | `staff` (`actor_id` & lý do bắt buộc) | `hasActiveOrderShipment == false` (`04:158` — khách không tự hủy ở `confirmed`); đặt `cancelled_at = now`, `status_reason = reason` | **Release (`BR-INV-04`, `AC-P2-02`)** + hoàn voucher y hệt trên (**0 dòng `stock_movements`**) | `(from: 'confirmed', to: 'cancelled', reason)` · Outbox: `OrderCancelled` (+ `StorefrontInvalidate` nếu `available` `0 → >0`) |
| `confirmed` | `order.MarkShipping(...)` | `shipping` | `staff` (`actor_id` bắt buộc) | Có vận đơn giao hàng hợp lệ (`hasOrderShipment == true`, `BR-SHP-02`) **và** (`payment_status == 'paid'` **hoặc** `payment_method == 'cod'`). Đặt `shipped_at = now`, chốt `order_lines.unit_cost_vnd` | **Consume (`BR-INV-04`, `AC-P2-03`):** mọi reservation `active → consumed` (`closed_at = now`), `on_hand -= q`, `reserved -= q`, ghi `stock_movements(sale, -q, unit_cost = avg_cost_vnd, ref_type = 'order', ref_id = order.Id)` + đồng bộ `BR-SEO-21` `sold_out_at` | `(from: 'confirmed', to: 'shipping', reason: note)` · Outbox: `OrderShipped` (+ `StorefrontInvalidate` khi unique piece sang `"sold"` / set `sold_out_at`) |
| `shipping` | `order.MarkDelivered(...)` | `delivered` | `staff` hoặc `system` (webhook P6) | `deliveredAt <= now` và (`shipped_at == null` hoặc `deliveredAt >= shipped_at`). Đặt `delivered_at = deliveredAt` | **Không đổi kho** (đã trừ khi `shipping`). Chạy hook `IOrderDeliveredHook` trong cùng transaction (no-op ở `P2-05`, mở rộng COD `awaiting_settlement` ở `P2-08`) | `(from: 'shipping', to: 'delivered', reason: note, occurred_at: now)` · Outbox: `OrderDelivered` |
| `shipping` | `order.MarkDeliveryFailed(...)` | `delivery_failed` | `staff` hoặc `system` (webhook P6) | Lý do bắt buộc (`reason.Trim().Length >= 1`). Đặt `failed_at = now`, `status_reason = reason` | **Không tự cộng kho ngay lúc báo giao thất bại** (hàng chỉ cộng lại khi shop thực nhận hoàn về qua `return_restock` ở `P2-08` `BR-SHP-03`) | `(from: 'shipping', to: 'delivery_failed', reason)` · Outbox: `OrderDeliveryFailed` |

### 2.3 Quy tắc ghi `sales.order_status_history` và `[Audited]` (`R-P201-06`, `BR-ORD-02`)

- **Chỉ append (`R-P201-06`):** `IOrderRepository` và `EfOrderRepository` chỉ đọc danh sách lịch sử và `db.Set<OrderStatusHistory>().AddRange(order.DrainNewHistory())`; tuyệt đối không có phương thức sửa hay xóa dòng `OrderStatusHistory` (kết hợp `REVOKE DELETE, TRUNCATE` ở PostgreSQL).
- **Audit cùng transaction (`02-architecture.md` §5.4):** Đánh dấu `[Audited]` trên persistence entity `Order` (`sales.orders`). Khai báo allowlist riêng cho `entityType == "sales.orders"` trong `AuditValuePolicy.cs`: chỉ cho phép các trường an toàn (`code`, `origin`, `sales_channel`, `status`, `payment_method`, `payment_status`, `subtotal_vnd`, `discount_vnd`, `shipping_fee_vnd`, `total_vnd`, `customer_id`, `voucher_id`, `created_by_staff_id`, `is_first_purchase`, `is_backfilled`, các cột `*_at`); toàn bộ PII/ghi chú/UTM (`contact_*`, `receiver_*`, `province_*`, `ward_*`, `address_line`, `*_note`, `status_reason`, `utm`, `referral_code`, `invoice_number`) luôn bị lọc thành `"[redacted]"`.

---

## 3. Thiết kế `CheckoutCalculator` dùng chung (`BR-CRT-05`, `BR-PRC-*`, `BR-SHP-01`)

Đặt tại `TeaPremium.Domain.Orders.CheckoutCalculator` (hàm thuần tĩnh, không phụ thuộc I/O hay EF Core) để dùng chung 100% giữa:
- **Admin (`P2-05`/`P2-06`):** tạo đơn tay, sửa dòng/giảm giá/phí ship đơn `pending`/`confirmed`.
- **Storefront (`P3-02`):** `POST /api/v1/store/checkout/quote` (chế độ `Quote` loại dòng `unavailable` khỏi tổng kèm `warnings[]`) và `POST /api/v1/store/checkout/orders` (chế độ `PlaceOrder` chặn `cart_has_unavailable_items` và đối chiếu `expected_total_vnd`).

### 3.1 Input và Output

- **Input (`CheckoutCalculationInput`):**
  - `Mode`: `WebQuote` | `WebPlaceOrder` | `AdminOrder`.
  - `EvaluatedAt`: `DateTimeOffset` (lấy một lần từ `IClock.UtcNow` cho toàn bộ phép tính).
  - `Lines`: `IReadOnlyList<CheckoutLineInput>` — gồm `VariantId`, `ProductId`, `Sku`, `ProductName`, `VariantName`, `ProductActive`, `VariantActive`, `BasePriceVnd` (`long?`), `Promotions` (`IReadOnlyList<ScheduledPrice>`), `IsUniquePiece`, `AvailableStock`, `RequestedQuantity`, và cho Admin: `OverrideUnitPriceVnd` (`long?`), `PriceOverrideReason` (`string?`).
  - `Discount`: tối đa **1 cơ chế giảm giá** (`None` | `VoucherCode(string Code, VoucherSnapshot? Voucher, Guid? CustomerId, int CustomerAppliedCount)` | `ReferralCode(string Code)` | `AdminManualDiscount(long DiscountVnd, string? DiscountNote, bool ActorHasCostsRead)`).
  - `Shipping`: `ShippingRuleInput(long FlatFeeVnd, long? FreeThresholdVnd, long? AdminOverrideShippingFeeVnd = null)`.
  - `Limits`: `MaxQuantityPerLineWeb` (`orders.max_quantity_per_line_web`, mặc định `10`), `MaxStaffDiscountPercent` (`orders.max_staff_discount_percent`, mặc định `10`).
- **Output (`CheckoutCalculationResult`):**
  - Thành công: `PricedLines` (mỗi dòng có `ListPriceVnd`, `UnitPriceVnd`, `PriceSource` ∈ `{'list','sale','override'}`, `PriceOverrideReason`, `Quantity`, `LineTotalVnd`, `IsAvailable`, `UnavailableReason?`), `UnavailableLines`, `SubtotalVnd` (`merchandiseVnd`), `DiscountVnd`, `AppliedVoucherId?`, `DiscountLabel?`, `ShippingFeeVnd`, `TotalVnd`, `Warnings[]`.
  - Thất bại (khi ở `WebPlaceOrder` / `AdminOrder` hoặc lỗi mã giảm giá): mã lỗi chuẩn hóa đúng `04-business-rules.md` & `05-api.md`.

### 3.2 Quy tắc tính dòng, làm tròn VND, Voucher và Phí ship

1. **Đơn giá hiệu lực & thành tiền từng dòng (`BR-PRC-01/02`, `BR-PRC-07`, `BR-CAT-15`, `ck_line_total`):**
   - Kiểm tra bán được: `ProductActive && VariantActive && BasePriceVnd is >= 1 and <= 10_000_000_000L` (xem `ASSUMPTION-P205-02` Mục 9).
   - Ở `WebQuote`: dòng không bán được hoặc `AvailableStock <= 0` gắn `IsAvailable = false` và **không cộng vào `SubtotalVnd`** (`BR-CRT-03`/`05`); dòng `IsUniquePiece && RequestedQuantity > 1` hoặc `RequestedQuantity > min(AvailableStock, MaxQuantityPerLineWeb)` tự cắt về mức tối đa và sinh warning.
   - Ở `WebPlaceOrder`: còn bất kỳ dòng không bán được → lỗi `cart_has_unavailable_items` (`409`); `IsUniquePiece && RequestedQuantity > 1` → `unique_piece_violation` (`409`); `RequestedQuantity > MaxQuantityPerLineWeb` → `validation_failed` (`400`); `RequestedQuantity > AvailableStock` → `out_of_stock` (`409`).
   - Ở `AdminOrder`: không bán được → `variant_not_sellable` (`422`, `BR-CAT-15`); `Quantity` ngoài `1..1000` → `validation_failed`; `IsUniquePiece && Quantity > 1` → `unique_piece_violation` (`409`).
   - Gọi `PricingCalculator.Calculate(true, BasePriceVnd, Promotions, EvaluatedAt)`:
     - Không có override: `ListPriceVnd = BasePriceVnd`; nếu `EffectivePriceVnd < BasePriceVnd` → `UnitPriceVnd = EffectivePriceVnd`, `PriceSource = "sale"`, ngược lại `UnitPriceVnd = BasePriceVnd`, `PriceSource = "list"`.
     - Có `OverrideUnitPriceVnd` (chỉ hợp lệ ở `AdminOrder`): yêu cầu `OverrideUnitPriceVnd >= 0` và `PriceOverrideReason?.Trim().Length >= 3` (`ck_line_override`, thiếu → `validation_failed`), đặt `UnitPriceVnd = OverrideUnitPriceVnd`, `PriceSource = "override"`. *(Kiểm quyền `orders.override_price` và ngưỡng `unit_price < avg_cost` theo `BR-PRC-07` thực hiện ở use case Admin `P2-06`).*
   - **`LineTotalVnd = checked((long)Quantity * UnitPriceVnd)`** — khớp tuyệt đối `ck_line_total` (`line_total_vnd = quantity * unit_price_vnd`).
   - **`SubtotalVnd = checked(Σ LineTotalVnd)`** trên các dòng hợp lệ (`>= 0`).

2. **Voucher & Giảm giá & Quy tắc làm tròn VND (`BR-PRC-04..06`, `ck_order_discount`):**
   - Mỗi đơn chỉ nhận **tối đa 1 cơ chế**: truyền đồng thời voucher và referral hoặc giảm tay → `validation_failed`.
   - Nếu truyền `ReferralCode` ở giai đoạn P2/P3 (trước `P5-05`): trả ngay `referral_not_available` (`422`, theo `07-roadmap.md:124`).
   - **Phần Voucher được phép ở `P2-05` (`BR-PRC-04` + `BR-PRC-05`):**
     - Kiểm tra `VoucherSnapshot` theo **đúng thứ tự ưu tiên cứng của `BR-PRC-04` (`04-business-rules.md:102-104`)**, dừng ở lỗi đầu tiên gặp:
       1. `Voucher is null` → `voucher_not_found`
       2. `Voucher.Status != "active"` → `voucher_disabled`
       3. `EvaluatedAt < Voucher.StartsAt` → `voucher_not_started`; `EvaluatedAt >= Voucher.EndsAt` → `voucher_expired`
       4. `Voucher.UsageLimit is { } cap && Voucher.UsedCount >= cap` → `voucher_usage_exhausted`
       5. `Voucher.BoundCustomerId is { } bound && bound != CustomerId` → `voucher_not_for_customer`
       6. `CustomerId is not null && CustomerAppliedCount >= Voucher.PerCustomerLimit` → `voucher_customer_limit`
       7. `SubtotalVnd < Voucher.MinOrderVnd` → `voucher_min_order`
     - **Làm tròn VND:** Tiền đều là `long` VND nguyên (`DEC-20`), phép nhân trung gian dùng `Int128` tránh tràn số. Riêng công thức voucher `%` áp dụng đúng biểu thức đặc thù của `BR-PRC-04` (`04-business-rules.md:104` ưu tiên hơn quy tắc chung `half-up` đầu file):
       - `fixed`: `discountVnd = Math.Min(Voucher.DiscountValue, SubtotalVnd)`.
       - `percent`: `raw = (long)((Int128)SubtotalVnd * Voucher.DiscountValue / 100)` (= `floor(merchandise × value / 100)` với số nguyên không âm); `capped = Voucher.MaxDiscountVnd is { } max ? Math.Min(raw, max) : raw`; `discountVnd = Math.Min(capped, SubtotalVnd)`.
       - Nếu `discountVnd == 0` (trường hợp biên đơn cực nhỏ với voucher `%` và `min_order_vnd == 0` không thỏa `ck_voucher_redemptions_amount_vnd > 0` tại `schema.sql:1049`): trả `voucher_min_order` (xem `GAP-P205-01` Mục 9).
     - Khi đặt đơn thành công có `VoucherId`, `IVoucherRedemptionPort.ApplyAsync` chạy `UPDATE pricing.vouchers SET used_count = used_count + 1 WHERE id = @id AND status = 'active' AND (usage_limit IS NULL OR used_count < usage_limit)` (`0` dòng → rollback trả `voucher_usage_exhausted`) và thêm `pricing.voucher_redemptions(status = 'applied', amount_vnd = discountVnd)`. Khi đơn hủy (`Cancel`), `RevertForOrderAsync` đổi `status = 'reverted'` và giảm `used_count = used_count - 1` (`BR-PRC-05`). *(CRUD quản trị voucher và gỡ/áp lại voucher khi PATCH đơn giảm dưới `min_order` thuộc `P5` / `P2-06`).*
   - **Giảm giá tay Admin (`BR-PRC-06`, dùng lại ở `P2-06`):**
     - `0 <= DiscountVnd <= SubtotalVnd` (`ck_order_discount`). Nếu `DiscountVnd > 0` bắt buộc `DiscountNote?.Trim().Length >= 1`.
     - Nếu `!ActorHasCostsRead` và `(Int128)DiscountVnd * 100 > (Int128)SubtotalVnd * MaxStaffDiscountPercent` (so sánh nhân chéo chính xác tuyệt đối, không sai số làm tròn) → trả `discount_requires_owner` (`422`, `AC-P2-06`).

3. **Phí vận chuyển & Tổng thanh toán (`BR-SHP-01`, `ck_order_total`):**
   - `afterDiscountVnd = checked(SubtotalVnd - DiscountVnd)` (`>= 0`).
   - Theo công thức chuẩn `BR-SHP-01`: `computedShipping = Shipping.FreeThresholdVnd is { } threshold && afterDiscountVnd >= threshold ? 0L : Shipping.FlatFeeVnd`.
   - Ở `AdminOrder` có `AdminOverrideShippingFeeVnd is { } customFee`: dùng `customFee` (`>= 0`); ngược lại dùng `computedShipping`.
   - **`TotalVnd = checked(SubtotalVnd - DiscountVnd + ShippingFeeVnd)`** — khớp tuyệt đối `ck_order_total` (`total_vnd = subtotal_vnd - discount_vnd + shipping_fee_vnd`) và `ck_order_discount` (`discount_vnd <= subtotal_vnd`).

---

## 4. Kho & Reservation: giữ/nhả/tiêu thụ, thứ tự khóa, hàng độc bản, idempotency và Migration

### 4.1 Thứ tự khóa chống deadlock toàn hệ thống (`AC-P2-01`, `p1-inventory-architecture.md:32`)

Mọi thao tác chạm tồn kho/reservation của đơn hàng (`Reserve`, `Release`, `SyncOrderLines`, `Consume`, `RestockReturned`) bắt buộc lấy khóa trong transaction `IUnitOfWork` hiện hành theo **đúng thứ tự một chiều nhất quán với P1**:

1. **L1 — Khóa chứng từ đơn hàng trước (nếu đơn đã tồn tại):** `SELECT *, xmin FROM sales.orders WHERE id = @orderId FOR UPDATE` (tương tự `P1-09` khóa `receipts`/`stocktakes` trước khi khóa Catalog/Inventory).
2. **L2 — Khóa `catalog.products` cha theo UUID tăng dần:** `SELECT *, xmin FROM catalog.products WHERE id = ANY(@parentIds) ORDER BY id FOR NO KEY UPDATE`.
3. **L3 — Khóa `catalog.variants` theo UUID tăng dần:** `SELECT *, xmin FROM catalog.variants WHERE id = ANY(@variantIds) AND product_id = ANY(@productIds) ORDER BY id FOR UPDATE`.
4. **L4 — Khóa `inventory.stock_levels` theo `variant_id` tăng dần:** `SELECT * FROM inventory.stock_levels WHERE variant_id = ANY(@ids) ORDER BY variant_id FOR UPDATE`.
5. **L5 — Khóa `pricing.vouchers` theo `id` tăng dần (khi đơn có áp/sửa/hủy voucher):** `SELECT *, xmin FROM pricing.vouchers WHERE id = ANY(@voucherIds) ORDER BY id FOR NO KEY UPDATE` (hoặc `WHERE code = @code FOR NO KEY UPDATE` khi tạo đơn). Luôn lấy **sau** `L1..L4`, tuần tự hóa mọi kiểm tra `usage_limit` và `per_customer_limit` mà không xung đột `FOR KEY SHARE` của FK `sales.orders.voucher_id` hay tạo chu trình với Catalog/Inventory P1.

*Lý do kỹ thuật:* `INSERT INTO inventory.reservations` và `INSERT INTO sales.order_lines` đều lấy khóa FK `FOR KEY SHARE` lên `catalog.variants` (và `catalog.products`). Việc lấy khóa `products → variants` trước `stock_levels` triệt tiêu hoàn toàn vòng chờ `40P01` với `EfProductStore`, `RebuildProductSearchText`, `EfSalePriceStore` và `EfInventoryStockStore` của P1, đồng thời tuần tự hóa mọi đơn đặt nhiều biến thể trùng nhau bất kể thứ tự dòng người dùng gửi lên (`(A, B)` vs `(B, A)`).

### 4.2 Vòng đời tạo / sửa dòng / nhả / tiêu thụ reservation (`BR-INV-03/04`, Owner Decision #1)

1. **Tạo reservation (`ReserveForOrderAsync` — `BR-INV-03`, `02-architecture.md` §5.2):**
   - Dưới khóa L1 → L2 → L3, kiểm tra bán được (`BR-CAT-15`) và hàng độc bản (`variant.IsUniquePiece && qty > 1` → `unique_piece_violation`, `BR-INV-09`).
   - Gom **toàn bộ** biến thể không đủ khả dụng (`available < qty`, biến thể chưa có dòng `stock_levels` tính `available = 0`); nếu danh sách khác rỗng → trả `out_of_stock` kèm đầy đủ mảng `{ variantId, sku, available }` theo thứ tự ổn định, rollback toàn bộ đơn.
   - Với từng dòng theo **`variant_id` tăng dần**, chạy SQL nguyên tử có điều kiện:
     ```sql
     UPDATE inventory.stock_levels
        SET reserved = reserved + @qty, updated_at = @now
      WHERE variant_id = @variantId AND on_hand - reserved >= @qty;
     ```
     (`rowsAffected != 1` → rollback trả `out_of_stock`), chèn `inventory.reservations(id, variant_id, order_line_id, quantity, status='active', created_at=@now)`, ghi `AuditLog` cho `inventory.stock_levels` (`reserved` trước/sau), phát `StorefrontInvalidate(["product:" + slug, "catalog"])` nếu `available` chuyển `> 0 → 0` (`BR-INV-12`), và cập nhật dedup `InventoryAlert(low_stock)` nếu `StockRules.IsLowStock(afterAvailable, threshold)` (`BR-INV-11`).

2. **Sửa / xóa dòng khi đơn ở `pending` hoặc `confirmed` (`SyncOrderLinesAsync` — `BR-ORD-08`, `BR-INV-04`, Owner Decision #1):**
   - Domain chặn cứng mọi thay đổi `order_lines` nếu `order.Status` không thuộc `{'pending','confirmed'}` (`invalid_status_transition`).
   - **Xóa dòng (`DELETE FROM sales.order_lines`):**
     - **Thứ tự ghi bắt buộc để không đụng `23514`:** Do `fk_reservations_line` (`ON DELETE SET NULL`) không deferrable và bảng `inventory.reservations` có `CHECK (status = 'released' OR order_line_id IS NOT NULL)`, nếu xóa dòng `sales.order_lines` khi reservation còn `status = 'active'` thì trigger `ON DELETE SET NULL` sẽ gán `order_line_id = NULL` ngay lập tức và nổ lỗi `23514`!
     - Vì vậy, `SyncOrderLinesAsync` bắt buộc: **(Bước 1)** cập nhật `reservation.Status = "released"`, `ClosedAt = now`, `CloseReason = "line_removed"`, giảm `stock_levels.reserved -= oldQty` và **`SaveChangesAsync` flush xuống DB trước**; **(Bước 2)** xóa dòng `sales.order_lines` (`db.Remove(orderLine)` → `SaveChangesAsync`), lúc này `fk_reservations_line` tự động gán `inventory.reservations.order_line_id = NULL` hợp lệ trên dòng đã `status = 'released'`. *(Cơ chế này đồng thời là chốt chặn cấp DB ngăn mọi lệnh SQL vô tình xóa `order_lines` đang `active` hoặc đã `consumed`!).*
   - **Giảm số lượng dòng (`newQty < oldQty`):** Đánh dấu reservation cũ `status = 'released'`, `closed_at = now`, `close_reason = 'quantity_reduced'` và flush (để giải phóng partial unique index `ux_reservation_active_line`), giảm `stock_levels.reserved -= (oldQty - newQty)`, rồi chèn reservation `active` mới với `quantity = newQty` (`BR-INV-04`).
   - **Tăng số lượng dòng (`newQty > oldQty`, `delta = newQty - oldQty`):** Kiểm `IsUniquePiece` (`newQty <= 1`), giữ thêm `delta` bằng câu `UPDATE` nguyên tử `BR-INV-03` (thiếu → rollback trả `out_of_stock`), cập nhật `reservation.Quantity = newQty`.

3. **Nhả toàn bộ khi hủy đơn (`ReleaseForOrderAsync` — `BR-INV-04`, `AC-P2-02`):**
   - Dưới khóa `Order → products → variants → stock_levels`, giảm `stock_levels.reserved -= q` (`WHERE variant_id = @id AND reserved >= @q`), chuyển toàn bộ reservation `active` của đơn sang `status = 'released'`, `closed_at = now`, `close_reason = 'order_cancelled'` (hoặc `'payment_expired'`). **Tuyệt đối không tạo `stock_movements`** (`AC-P2-02`). Phát `StorefrontInvalidate` nếu `available` chuyển `0 → > 0` và xóa dedup `InventoryAlert` nếu vượt ngưỡng.

4. **Tiêu thụ khi xuất giao hàng (`ConsumeForOrderAsync` — `BR-INV-04`, `AC-P2-03`):**
   - Dưới khóa `Order → products → variants → stock_levels`, xác nhận từng dòng đơn có đúng 1 reservation `active` khớp `quantity`.
   - Theo `variant_id` tăng dần: đọc `avgCostVnd = level.AvgCostVnd` (`long?`), giảm đồng thời `on_hand -= q` và `reserved -= q` (`WHERE variant_id = @id AND on_hand >= @q AND reserved >= @q`), chèn 1 dòng `inventory.stock_movements(movement_type = 'sale', quantity = -q, unit_cost_vnd = avgCostVnd, on_hand_after = newOnHand, ref_type = 'order', ref_id = order.Id, actor_type, actor_id, occurred_at = now)`, gán `order_line.UnitCostVnd = avgCostVnd`, chuyển reservation sang `status = 'consumed'`, `closed_at = now`, `close_reason = 'shipped'`.
   - Flush thay đổi kho rồi gọi `IProductSoldOutSync.SyncSoldOutStateAsync(productIds, ct)` trong cùng transaction để cập nhật `products.sold_out_at` và phát `StorefrontInvalidate` theo `BR-SEO-21`.

### 4.3 Bảo vệ chống ghi lặp (Idempotency)

- **Tầng giao dịch/API (`NFR-03`, `02-architecture.md` §5.3):** Mọi command handler của `P2-05` chạy qua `IUnitOfWork.ExecuteAsync`, tương thích trực tiếp với `EfUnitOfWork.ExecuteRequestAsync` của `IdempotencyEndpoint` đã có từ `P0-07` khi `P2-06`/`P3-02` gắn endpoint.
- **Tầng DB & Aggregate:** `ux_reservation_active_line` (`UNIQUE(order_line_id) WHERE status = 'active'`) chặn tạo 2 reservation active cho cùng dòng đơn; `ux_order_lines_order_id_variant_id` chặn trùng biến thể trong cùng đơn; state machine + `xmin` (`uint Version`) chặn hủy kép (trừ `reserved` 2 lần) hoặc giao kép (trừ `on_hand` 2 lần).

### 4.4 Mở rộng đối soát kho đêm `BR-INV-13` (Owner Decision #6)

- **`ReconcileInventory.cs` (`TeaPremium.Infrastructure.Inventory`):** Mở rộng truy vấn LINQ (dưới `JobExecutionGuard`) trên toàn bộ `catalog.variants` (kể cả biến thể chưa có dòng `stock_levels` với `onHand = 0, reserved = 0`):
  - `ledgerQty = SUM(stock_movements.quantity) ?? 0L`
  - `activeReservedQty = SUM(reservations.quantity WHERE status == "active") ?? 0L`
  - Phát `InventoryAlert(ReconciliationMismatch, variant.Id, vietnamDate)` khi **`ledgerQty != onHand || activeReservedQty != reserved`** (tối đa 1 alert/variant/ngày lịch VN; các reservation `consumed` và `released` kể cả `order_line_id IS NULL` không tính vào `activeReservedQty`). **Tuyệt đối không tự sửa `stock_levels` hay `reservations`.**
- **`InventoryAlertHandler.cs` (theo `ASSUMPTION-P205-01 = B`):** Consumer truy vấn lại cả `ledgerQty != onHand` và `activeReservedQty != reserved` một cách idempotent, ghi log JSON có cấu trúc không chứa PII hay giá vốn; giữ nguyên `EfInventoryQueries.AlertsAsync`, `InventoryQueries.cs`, OpenAPI và `packages/api-client`.

### 4.5 Thiết kế Migration `inventory.reservations`, `Down` guard, Grants và Preflight

- **Tên migration:** `<yyyyMMddHHmm>_P2_05_InventoryReservations` (chạy ngay sau `P2-01`, tổng cộng migration thứ `8`).
- **DDL khớp 100% `schema.sql:499-513, 909-910` (bản đã sửa ở `P2-01`):**
  - Bảng `inventory.reservations`: `id uuid PK` (`pk_reservations`), `variant_id uuid NOT NULL` (`fk_reservations_variant_id → catalog.variants(id)` `NO ACTION`), `order_line_id uuid NULL` (**`fk_reservations_line → sales.order_lines(id)` `ON DELETE SET NULL`**), `quantity integer NOT NULL`, `status text NOT NULL DEFAULT 'active'`, `created_at timestamptz NOT NULL DEFAULT now()`, `closed_at timestamptz NULL`, `close_reason text NULL`.
  - 4 CHECK constraints tường minh: `ck_reservations_quantity` (`quantity > 0`), `ck_reservations_status` (`status IN ('active','consumed','released')`), `ck_reservations_active_closed` (`(status = 'active') = (closed_at IS NULL)`), `ck_reservations_order_line_required` (`status = 'released' OR order_line_id IS NOT NULL`).
  - 2 partial indexes tường minh: `ux_reservation_active_line` (`UNIQUE (order_line_id) WHERE status = 'active'`) và `ix_reservation_variant_active` (`(variant_id) WHERE status = 'active'`). Không có cột `xmin` hay index thừa.
- **Thứ tự `Up`:** (1) SQL literal đóng băng `GrantsSql.Preflight` → (2) `CreateTable("reservations", "inventory", ...)` kèm PK, 4 CHECK, 2 FK → (3) 2 `CreateIndex` → (4) SQL literal đóng băng `GrantsSql.Reservations()` (giữ nguyên các hằng `PlatformAndIam()`, `CatalogAndInventory()`, `CrmSalesFinance()` để không phá test lịch sử của `P1-01`/`P2-01`; cấp thêm `REVOKE ALL ON inventory.reservations FROM PUBLIC,tp_app,tp_readonly; GRANT SELECT,INSERT,UPDATE,DELETE ON inventory.reservations TO tp_app; GRANT SELECT ON inventory.reservations TO tp_readonly;` + hậu kiểm `Preflight`).
- **Thứ tự `Down` về `P201Head`:**
  1. Khối guard `DO $p205_down$ ... END $p205_down$;`: bắt buộc `current_user = 'tp_owner'`; `LOCK TABLE inventory.reservations IN ACCESS EXCLUSIVE MODE`; từ chối ngay (`P2-05 Down refused: data in inventory.reservations...`) nếu có `>= 1` dòng (kể cả dòng `released`); kiểm `pg_attribute`, `pg_constraint`, `pg_trigger`, `pg_class`, `pg_depend` để từ chối nếu có cột/constraint/trigger/view/function/FK ngoài phạm vi tham chiếu `inventory.reservations`.
  2. `DropTable("reservations", "inventory")` — gỡ sạch `fk_reservations_line`, 2 index và bảng `inventory.reservations`, trả ACL toàn DB về khớp 100% snapshot `P201Head`.
- **Preflight (`infra/postgres/preflight.sql`):** Bản `preflight.sql` của `P2-01` đã bao phủ cả 11 schema (gồm `'inventory'` và `'sales'`), nên tự động kiểm quyền `PUBLIC`, grant-option, `TRUNCATE`/`MAINTAIN`, column-grant và owner của `inventory.reservations` cả trước lẫn sau khi `P2-05` `Up`.

---

## 5. Sinh mã đơn hàng `^TP[0-9]{6}[A-Z0-9]{4}$` và chống trùng (`BR-ORD-10`, `03-database.md:11`)

- **Quy tắc sinh (`OrderCode` + `RandomOrderCodeGenerator : IOrderCodeGenerator`):**
  - Tiền tố ngày: `"TP"` + `BusinessCalendar.ToVietnamDate(clock.UtcNow).ToString("yyMMdd", CultureInfo.InvariantCulture)` (6 chữ số theo múi giờ `Asia/Ho_Chi_Minh` UTC+7 qua `IClock`, tuyệt đối không gọi `DateTime.Now`).
  - Hậu tố ngẫu nhiên: 4 ký tự lấy phân phối đều bằng `RandomNumberGenerator.GetInt32(Alphabet.Length)` từ bảng **31 ký tự** `"23456789ABCDEFGHJKMNPQRSTUVWXYZ"` (đã loại bỏ `0`, `O`, `1`, `I`, `L` theo `03-database.md:11`). Không gian mã mỗi ngày Việt Nam = $31^4 = 923.521$ mã, luôn thỏa mãn `ck_orders_code` (`^TP[0-9]{6}[A-Z0-9]{4}$`) và dùng trực tiếp làm nội dung chuyển khoản không dấu, không khoảng trắng (`BR-ORD-10`).
- **Cơ chế chống trùng bằng PostgreSQL `SAVEPOINT` (theo đúng mẫu `EfProductStore.InsertGenerated`):**
  - Trong transaction đang mở của `IUnitOfWork`, nếu va chạm `ux_orders_code` (`SqlState == "23505"`, `ConstraintName == "ux_orders_code"`), PostgreSQL sẽ đánh dấu abort transaction hiện hành trừ khi bọc trong `SAVEPOINT`.
  - Khi thêm mới `Order`, `EfOrderRepository` lặp tối đa `MaxCodeAttempts = 10` lần:
    1. Chụp tập entity đang được track trước lần thử (`priorTracked`).
    2. `await tx.CreateSavepointAsync("order_code_candidate", ct);`
    3. Sinh mã ứng viên qua `IOrderCodeGenerator.NextCode(vietnamDate)`, gán `orderRow.Code`, `db.Set<Order>().Add(orderRow)` và gọi `await db.SaveChangesAsync(ct);`.
    4. Nếu thành công: `await tx.ReleaseSavepointAsync("order_code_candidate", ct);` rồi tiếp tục ghi `OrderLine`, `OrderStatusHistory`, `Reservation`.
    5. Nếu bắt đúng `DbUpdateException` có `InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_orders_code" }`: gọi `RollbackToSavepointAsync("order_code_candidate")`, detach mọi entity phát sinh trong lần thử lỗi (gồm cả dòng `AuditLog` vừa được `AuditSaveChangesInterceptor` thêm vào ChangeTracker), `ReleaseSavepointAsync`, rồi sinh mã kế tiếp. Quá 10 lần trả `Error("order_code_exhausted", ..., ErrorType.Conflict)` để rollback sạch. Không bao giờ catch nhầm lỗi `23505`/`23503`/`23514` của cột khác.

---

## 6. Outbox `Order*` + Composite Handler (`G22`), bằng chứng bán `BR-SEO-21` và `StorefrontInvalidate`

### 6.1 Outbox `Order*` và đúng 1 Composite `IOutboxHandler` mỗi loại (`G22`, Owner Decision #3)

- `OutboxBatchProcessor.cs:71` dùng `handlers.SingleOrDefault(x => x.MessageType == message.Type)`: nếu có `0` handler sẽ ném `handler_unavailable` → retry 10 lần → `dead`; nếu có `>= 2` handler cùng `MessageType` sẽ ném `InvalidOperationException` làm kẹt hàng đợi.
- **6 loại message `Order*` chuẩn (`OrderOutboxTypes`, khớp `BR-CRT-06` & `BR-ORD-02`):**
  `OrderPlaced`, `OrderConfirmed`, `OrderCancelled`, `OrderShipped`, `OrderDelivered`, `OrderDeliveryFailed`.
- **Payload an toàn tuyệt đối (`NFR-04`):** `OrderOutboxPayload(Guid OrderId, string OrderCode, string Status, DateTimeOffset OccurredAt, string? ReasonCode = null)` — chỉ chứa định danh và mã lý do hệ thống đóng (ví dụ `"payment_expired"`), **không bao giờ chứa tên, SĐT, email, địa chỉ, ghi chú người dùng hay giá vốn**.
- **Thiết kế Composite:**
  - Khai báo interface mở rộng nội bộ `IOrderOutboxStep` (`string MessageType`, `int Order`, `Task HandleAsync(Guid messageId, OrderOutboxPayload payload, CancellationToken ct)`).
  - Trong `OrdersRegistration.AddOrders()`, đăng ký **đúng 6 instance `CompositeOrderOutboxHandler : IOutboxHandler`** (mỗi message type đúng 1 `IOutboxHandler` duy nhất).
  - Khi chạy trong `before_delivery` savepoint của `OutboxBatchProcessor`, `CompositeOrderOutboxHandler`:
    1. Kiểm chặt JSON bằng `OrderOutboxMessage.TryParse` (sai định dạng/thuộc tính lạ → ném lỗi để theo luồng backoff 10 lần → `dead`).
    2. Kiểm tra sự tồn tại của `sales.orders` theo `OrderId` (nếu không còn/đã xử lý thì log cảnh báo an toàn và hoàn tất idempotent).
    3. Thực thi tuần tự các `IOrderOutboxStep` khớp `MessageType` theo `Order` tăng dần (`P2-05` có `0` step phụ nên xử lý hoàn tất ngay sang `status = 'processed'`; `P2-11` thêm step chỉ số/sở thích khách, `P2-13`/`P3-06` thêm step Telegram/email, `P5-05` thêm step referral mà **không bao giờ đăng ký thêm `IOutboxHandler` thứ hai**).
  - **Điểm mở rộng cho `P2-08` (không triển khai ở `P2-05`):**
    - `IOrderDeliveredHook` chạy đồng bộ trong cùng transaction `MarkDelivered` để `P2-08` cắm tạo `sales.payments(cod, awaiting_settlement)` (`BR-ORD-07.1`).
    - `IInventoryReservationService.RestockReturnedAsync` nhận `refType` (`"order"` cho giao thất bại hoàn kho ở `P2-08` vs `"return"` cho đổi trả sau giao ở `P2-10`) để `P2-08`/`P4-02` xử lý `G16` mà không sửa lại lõi `P2-05`.

### 6.2 Bằng chứng bán hàng độc bản `BR-SEO-21` và `StorefrontInvalidate` (Owner Decision #2)

- **Công thức bằng chứng bán tại port `ICatalogInventoryRead` (`TeaPremium.Application.Inventory` → hiện thực tại `EfCatalogInventoryRead`):**
  - Bổ sung `Task<IReadOnlySet<Guid>> SoldUniqueVariantIdsAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken ct)`.
  - Một biến thể thuộc `variantIds` được trả trong tập `"sold"` khi và chỉ khi đồng thời thỏa:
    1. `variant.is_unique_piece == true`,
    2. Tồn khả dụng hiện tại `available == (on_hand - reserved) == 0` (thiếu dòng `stock_levels` tính `0`), **và**
    3. Bán ròng nghiệp vụ trong `inventory.stock_movements`:
       $$-\sum_{\substack{\text{movement\_type} \in \{\text{'sale'}, \text{'return\_restock'}\} \\ \text{ref\_type} \in \{\text{'order'}, \text{'return'}\}}} \text{quantity} > 0$$
  - *Hệ quả chính xác trên mọi kịch bản:*
    - Đang giữ hàng (`pending`/`confirmed`, `on_hand = 1, reserved = 1`): `available = 0`, bán ròng `= 0` → **`"out_of_stock"`** (chưa bán thật), `sold_out_at` vẫn `NULL`.
    - Vỡ/hỏng hoặc kiểm kê giảm về 0 (`damage`/`adjustment`/`stocktake`): `available = 0`, bán ròng `= 0` → **`"out_of_stock"`**, `sold_out_at` vẫn `NULL`.
    - Chuyển `shipping` (`sale = -1, ref_type = 'order'`): `available = 0`, bán ròng `= -(-1) = +1 > 0` → **`"sold"`**.
    - Giao thất bại hoàn kho (`P2-08`, `return_restock = +1, ref_type = 'order'`) hoặc khách trả hàng nhập lại kho (`P2-10`, `return_restock = +1, ref_type = 'return'`): `on_hand = 1 > 0` (`available = 1`) và bán ròng `= -(-1 + 1) = 0` → quay về **`"in_stock"`** (và nếu sau đó bị vỡ trong kho `damage = -1` thì `available = 0`, bán ròng vẫn `= 0` → `"out_of_stock"`, không bị nhận nhầm là `"sold"`).
- **Cập nhật Store Catalog (`EfStoreCatalogRead.cs` & `EfStoreCatalogRead.Products.cs`):**
  - Trong cùng transaction snapshot `RepeatableRead`, với các biến thể `is_unique_piece == true` đang có `available <= 0` trên trang danh sách và trang chi tiết, gọi `await inventory.SoldUniqueVariantIdsAsync(...)` để trả `"sold"` thay vì `"out_of_stock"` (khớp enum `"in_stock" | "out_of_stock" | "sold"` đã sinh sẵn trong OpenAPI từ `P1-14` và đảm bảo đồng nhất danh sách ↔ chi tiết theo `BR-SEO-81`).
- **Đồng bộ `catalog.products.sold_out_at` và `StorefrontInvalidate` (`EfProductSoldOutSync : IProductSoldOutSync`):**
  - Gọi trong cùng transaction ngay sau khi flush biến động `sale` (ở `ConsumeForOrderAsync`) hoặc `return_restock` (ở `RestockReturnedAsync`), dưới khóa `catalog.products FOR NO KEY UPDATE` đã giữ sẵn:
  - Đọc tất cả `catalog.variants` có `status == 'active'` của sản phẩm, đối chiếu qua `ICatalogInventoryRead`:
    - **Set `sold_out_at`:** Nếu `activeVariants.Length > 0` (chống bẫy vacuous truth khi `0` active variants) **và** mọi variant `active` đều có `is_unique_piece == true` và thuộc tập `"sold"`: nếu `product.SoldOutAt is null` thì đặt `product.SoldOutAt = now`, `product.UpdatedAt = now`.
    - **Clear `sold_out_at`:** Khi `return_restock` làm `on_hand > 0` (hoặc sản phẩm không còn thỏa "mọi variant active đều độc bản và sold") mà `product.SoldOutAt is not null`: đặt `product.SoldOutAt = null`, `product.UpdatedAt = now`.
  - **Phát `StorefrontInvalidate`:** Lưu ý khi chuyển `confirmed → shipping`, `available` trước và sau đều bằng `0` (`1 - 1 = 0` → `0 - 0 = 0`) nên `InventoryMutation` cũ sẽ không tự phát invalidation! Do đó `EfProductSoldOutSync` chủ động phát `StorefrontInvalidate(["product:" + slug, "catalog", "sitemap", "feed"])` (kèm các `category:{slug}` của sản phẩm) bất cứ khi nào trạng thái `"sold"` của biến thể độc bản hoặc `products.sold_out_at` thay đổi.

---

## 7. Danh sách file triển khai G3 (`48` file)

Tổng cộng **48 file** (`22` tracked `M` + `26` untracked `??`; **`0` file trong `docs/spec/`**, **`0` file trong `apps/`**, **`0` file trong `packages/`** theo `ASSUMPTION-P205-01 = B`):

### 7.1 `TeaPremium.Domain` (4 file tạo mới)
1. `backend/src/TeaPremium.Domain/Orders/OrderConstants.cs` *(tạo mới)* — hằng số `OrderOrigin`, `SalesChannel`, `OrderStatus`, `OrderPaymentMethod`, `OrderPaymentStatus`, `OrderPriceSource`, `OrderActorType`, `ReservationStatus`, `ReservationCloseReason`, `OrderAcquisitionSource`, `OrderEventTypes`.
2. `backend/src/TeaPremium.Domain/Orders/OrderCode.cs` *(tạo mới)* — kiểm tra định dạng và bảng chữ cái 31 ký tự `TPyyMMddXXXX` (`BR-ORD-10`).
3. `backend/src/TeaPremium.Domain/Orders/Order.cs` *(tạo mới)* — aggregate `Order`, `OrderLineItem`, `OrderStatusHistoryEntry`, state machine `BR-ORD-01..04`, sửa/xóa dòng `pending`/`confirmed`, kiểm `R-P201-04` và toàn bộ bất biến `ck_order_*`/`ck_line_*`.
4. `backend/src/TeaPremium.Domain/Orders/CheckoutCalculator.cs` *(tạo mới)* — bộ tính giá/voucher/ship/tổng tiền dùng chung Admin P2 & Storefront P3 (`BR-CRT-05`, `BR-PRC-01/02/04/06`, `BR-SHP-01`, `GAP-P205-01 = A`, `ASSUMPTION-P205-02 = A`).

### 7.2 `TeaPremium.Application` (7 file: 6 tạo mới, 1 sửa; giữ nguyên `InventoryQueries.cs`)
5. `backend/src/TeaPremium.Application/Inventory/ICatalogInventoryRead.cs` *(sửa)* — thêm `SoldUniqueVariantIdsAsync` (`BR-SEO-21`).
6. `backend/src/TeaPremium.Application/Catalog/IProductSoldOutSync.cs` *(tạo mới)* — port đồng bộ `products.sold_out_at` + `StorefrontInvalidate`.
7. `backend/src/TeaPremium.Application/Inventory/IInventoryReservationService.cs` *(tạo mới)* — port `LockVariantsForOrderAsync`, `ReserveForOrderAsync`, `SyncOrderLinesAsync`, `ReleaseForOrderAsync`, `ConsumeForOrderAsync`, `RestockReturnedAsync`.
8. `backend/src/TeaPremium.Application/Pricing/IVoucherRedemptionPort.cs` *(tạo mới)* — port đọc snapshot voucher, áp `used_count + 1` và hoàn `reverted` (`BR-PRC-04/05`).
9. `backend/src/TeaPremium.Application/Orders/OrderContracts.cs` *(tạo mới)* — `IOrderRepository` (append-only history `R-P201-06`), `IOrderCodeGenerator`, `IOrderPricingRead`, `IOrderDeliveredHook`, DTOs/inputs.
10. `backend/src/TeaPremium.Application/Orders/OrderOutbox.cs` *(tạo mới)* — `OrderOutboxPayload` không PII/giá vốn, `TryParse`/`Serialize`, `IOrderOutboxStep` (`G22`).
11. `backend/src/TeaPremium.Application/Orders/OrderCoreUseCases.cs` *(tạo mới)* — các command/handler + validator lõi cho tạo đơn, xác nhận, hủy, sửa/xóa dòng, chuyển giao hàng, giao thành công, giao thất bại.

### 7.3 `TeaPremium.Infrastructure` (20 file: 9 tạo mới, 11 sửa; giữ nguyên `EfInventoryQueries.cs`)
12. `backend/src/TeaPremium.Infrastructure/Persistence/CatalogInventoryEntities.cs` *(sửa)* — thêm POCO `Reservation`.
13. `backend/src/TeaPremium.Infrastructure/Persistence/CrmSalesFinanceEntities.cs` *(sửa)* — gắn `[TeaPremium.Domain.Audited]` lên `Order`.
14. `backend/src/TeaPremium.Infrastructure/Persistence/AuditValuePolicy.cs` *(sửa)* — thêm allowlist tường minh cho `sales.orders`, che toàn bộ PII/ghi chú/UTM.
15. `backend/src/TeaPremium.Infrastructure/Persistence/ReservationModel.cs` *(tạo mới)* — cấu hình EF Core cho `inventory.reservations` + 4 CHECK + 2 partial indexes + `fk_reservations_line` (`ON DELETE SET NULL`).
16. `backend/src/TeaPremium.Infrastructure/Persistence/AppDbContext.cs` *(sửa)* — gọi `ReservationModel.Configure(builder)`.
17. `backend/src/TeaPremium.Infrastructure/Persistence/GrantsSql.cs` *(sửa)* — thêm `GrantsSql.Reservations()` (giữ nguyên các hàm cũ của P0/P1/P2-01).
18. `backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611230721_P2_05_InventoryReservations.cs` *(tạo mới)* — migration forward + `Down` guard `$p205_down$`.
19. `backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611230721_P2_05_InventoryReservations.Designer.cs` *(tạo mới)* — Designer metadata.
20. `backend/src/TeaPremium.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs` *(sửa)* — cập nhật snapshot EF Core.
21. `backend/src/TeaPremium.Infrastructure/Inventory/EfCatalogInventoryRead.cs` *(sửa)* — hiện thực `SoldUniqueVariantIdsAsync` (`BR-SEO-21`).
22. `backend/src/TeaPremium.Infrastructure/Inventory/EfInventoryReservationService.cs` *(tạo mới)* — hiện thực khóa `product → variant → stock_levels`, giữ/sửa/nhả/tiêu thụ/nhập hoàn kho + UPDATE nguyên tử.
23. `backend/src/TeaPremium.Infrastructure/Inventory/ReconcileInventory.cs` *(sửa)* — mở rộng kiểm `SUM(reservations active) != reserved` (`BR-INV-13`).
24. `backend/src/TeaPremium.Infrastructure/Inventory/InventoryAlertHandler.cs` *(sửa)* — mở rộng hậu kiểm & log cảnh báo lệch `reserved` không PII/giá vốn.
25. `backend/src/TeaPremium.Infrastructure/Catalog/EfProductSoldOutSync.cs` *(tạo mới)* — set/clear `products.sold_out_at` và phát `StorefrontInvalidate`.
26. `backend/src/TeaPremium.Infrastructure/Catalog/EfStoreCatalogRead.cs` *(sửa)* — ánh xạ `availability = "sold"` trên chi tiết sản phẩm.
27. `backend/src/TeaPremium.Infrastructure/Catalog/EfStoreCatalogRead.Products.cs` *(sửa)* — ánh xạ `availability = "sold"` trên danh sách thẻ sản phẩm.
28. `backend/src/TeaPremium.Infrastructure/Pricing/EfVoucherRedemptionStore.cs` *(tạo mới)* — hiện thực `IVoucherRedemptionPort` (`BR-PRC-04/05`).
29. `backend/src/TeaPremium.Infrastructure/Orders/EfOrderRepository.cs` *(tạo mới)* — hiện thực `IOrderRepository`, `RandomOrderCodeGenerator` có `SAVEPOINT` chống trùng, `EfOrderPricingRead`, và `OrderVariantUsage : IVariantUsage`.
30. `backend/src/TeaPremium.Infrastructure/Orders/CompositeOrderOutboxHandler.cs` *(tạo mới)* — composite handler cho 6 loại message `Order*` (`G22`).
31. `backend/src/TeaPremium.Infrastructure/Orders/OrdersRegistration.cs` *(tạo mới)* — đăng ký DI module Orders & 6 composite outbox handlers.

### 7.4 `TeaPremium.Api` (1 file sửa; không đổi OpenAPI/`packages/api-client`)
32. `backend/src/TeaPremium.Api/Program.cs` *(sửa)* — đăng ký `builder.Services.AddOrders();` (không thêm route HTTP mới).

### 7.5 Tests & Hồ sơ kế hoạch (16 file: 6 test mới, 9 test sửa giữ tương thích migration/P1-10/StoreCatalog, 1 hồ sơ)
33. `backend/tests/TeaPremium.UnitTests/OrderStateMachineTests.cs` *(tạo mới)* — 11 method (`13` ca thực thi) cho `Order` aggregate, state machine, line edit rules, `R-P201-04`, `OrderCode`.
34. `backend/tests/TeaPremium.UnitTests/CheckoutCalculatorTests.cs` *(tạo mới)* — 7 method (`7` ca thực thi) cho `CheckoutCalculator`, làm tròn VND, thứ tự lỗi `BR-PRC-04`, `GAP-P205-01 = A`, `ASSUMPTION-P205-02 = A`, phí ship `BR-SHP-01`.
35. `backend/tests/TeaPremium.IntegrationTests/ReservationsMigrationTests.cs` *(tạo mới)* — 6 method (`10` ca thực thi) cho parity PG17, semantic drift, pending-model-changes, idempotent script, `Down`/`Up` + guard `$p205_down$`, quyền thật `tp_app`/`tp_readonly` & `preflight.sql`.
36. `backend/tests/TeaPremium.IntegrationTests/OrderConcurrencyTests.cs` *(tạo mới)* — 5 method (`AC-P2-01` 20 request đồng thời, chống deadlock đa biến thể ngược chiều, khóa đồng thời với `EfProductStore`, savepoint retry trùng `ux_orders_code`, và F2: 12 đơn đồng thời cùng khách cùng voucher `per_customer_limit=1` → đúng 1 đơn áp mã, `used_count=1`, 0 deadlock).
37. `backend/tests/TeaPremium.IntegrationTests/OrderLifecycleAndReservationTests.cs` *(tạo mới)* — 10 method (shortfall rollback, `AC-P2-02`, `AC-P2-03`, xóa/sửa dòng `pending`/`confirmed` + `order_line_id IS NULL`, chặn xóa sau `shipping` + chốt chặn `23514`, `xmin` 409 + audit redaction + `OrderVariantUsage` + `42501` append-only, 6 composite outbox handlers, step order + retry→`dead`, F1: đồng bộ voucher trong `UpdateOrderLines` gồm áp/giữ/auto-drop/áp lại trên `reverted`/đổi mã/gỡ mã + hủy không trừ kép, và F3+F4: lọc active shipment khớp `ux_shipment_active_order` + clamp int64 setting an toàn).
38. `backend/tests/TeaPremium.IntegrationTests/UniquePieceSoldLifecycleTests.cs` *(tạo mới)* — 4 method (`BR-SEO-21` set/clear `products.sold_out_at`, Store API `"in_stock"`/`"out_of_stock"`/`"sold"`, vacuous truth, `RestockReturnedAsync` + `StorefrontInvalidate`).
39. `backend/tests/TeaPremium.IntegrationTests/InventoryAlertsAndReconciliationTests.cs` *(sửa)* — thay bảng bẫy P1 bằng kiểm thử đối soát `SUM(reservations active) = reserved` thật (`BR-INV-13`) kèm trigger cấm sửa cả `stock_levels` lẫn `reservations`.
40. `backend/tests/TeaPremium.IntegrationTests/StoreCatalogTests.cs` *(sửa)* — bổ sung 1 test HTTP `GET /api/v1/store/products[/{slug}]` phân biệt `"sold"` vs `"out_of_stock"`.
41. `backend/tests/TeaPremium.IntegrationTests/CrmSalesFinanceFixture.cs` *(sửa)* — thêm hằng `P201Head` và `P205Head`.
42. `backend/tests/TeaPremium.IntegrationTests/CrmSalesFinanceParityTests.cs` *(sửa)* — ghim mốc kiểm parity P2-01 tại `P201Head`.
43. `backend/tests/TeaPremium.IntegrationTests/CrmSalesFinanceDatabaseTests.cs` *(sửa)* — cập nhật tổng số migration ở head (`7 → 8`), loại `inventory.reservations` khỏi `P0P1FingerprintAsync`.
44. `backend/tests/TeaPremium.IntegrationTests/CrmSalesFinanceRollbackTests.cs` *(sửa)* — cập nhật chuỗi rollback `head → P201Head → P1Head → head` và ghim guard test P2-01 tại `P201Head`.
45. `backend/tests/TeaPremium.IntegrationTests/CatalogInventoryDatabaseTests.cs` *(sửa)* — cập nhật tổng số migration ở head (`7 → 8`), loại `inventory.reservations` khỏi đếm 25 bảng P1-01.
46. `backend/tests/TeaPremium.IntegrationTests/CatalogInventoryRollbackTests.cs` *(sửa)* — loại `inventory.reservations` khỏi đếm 25 bảng P1-01 sau `MigrateAsync()` lên head.
47. `backend/tests/TeaPremium.IntegrationTests/DatabaseRolesTests.cs` *(sửa)* — cập nhật số dòng `public."__EFMigrationsHistory"` tại head (`7L → 8L`).
48. `docs/plans/p2-05-implementation.md` *(file hiện tại)*.

---

## 8. Kế hoạch kiểm thử (`0` skipped) và cách test khi chưa có HTTP endpoint `admin/orders`

**Cách test khi `P2-05` chưa mở HTTP endpoint đơn hàng:**
- Tầng Domain & `CheckoutCalculator` kiểm bằng unit test thuần trong `TeaPremium.UnitTests`.
- Tầng Application + Infrastructure kiểm tích hợp trên **PostgreSQL 17 thật (`Testcontainers`)** bằng cách mở `AsyncServiceScope` từ `WebApplicationFactory<Program>` chạy kết nối runtime **`tp_app` thật** (`h.App.RuntimeConnection`), gọi trực tiếp `ICommandHandler<TCommand, TResult>` qua pipeline thật (`ValidationDecorator` → `TransactionCommandDecorator` / `EfUnitOfWork` → `AuditSaveChangesInterceptor` → `EfOutboxWriter` → `OutboxBatchProcessor`), giống hệt mẫu `P1-08`/`P1-10`.
- Hai mặt phẳng HTTP sẵn có chịu tác động của `P2-05` (`GET /api/v1/store/products[/{slug}]` và `GET /api/v1/admin/inventory/alerts`) kiểm trực tiếp qua `HttpClient` + JSON thô.

### 8.1 Unit Tests dự kiến (`TeaPremium.UnitTests` — 2 class, ~`32` test cases, `0` skipped)

| Mã | Nhóm test trong `OrderStateMachineTests` & `CheckoutCalculatorTests` | Số ca dự kiến | Luật & hành vi chứng minh |
|---|---|---:|---|
| `U01` | `BR_ORD_01_03_ValidAndInvalidTransitions_EnforceStateMachineAndTerminalStates` | 10 | Mọi chuyển hợp lệ (`pending→confirmed/cancelled`, `confirmed→shipping/cancelled`, `shipping→delivered/delivery_failed`) và mọi tổ hợp chuyển cấm (từ `delivered`/`delivery_failed`/`cancelled`, `pending→shipping`, `pending→delivered`, `confirmed→delivered`) trả `invalid_status_transition`; mỗi chuyển sinh đúng 1 `OrderStatusHistoryEntry` và 1 `OrderDomainEvent`. |
| `U02` | `BR_ORD_04_R_P201_04_OriginAndChannelInvariants_RejectAdminWithWebChannel` | 5 | `origin='admin'` với `sales_channel='web'` bị chặn tại Domain; `origin='web'` với `sales_channel!='web'` bị chặn; `origin='web' + bank_transfer` bắt buộc `payment_due_at`; `origin='admin'` luôn `payment_due_at == null`, hỗ trợ `confirmNow: false/true`; kiểm điều kiện `Confirm` (`web bank_transfer` chưa `paid`, `cod` thiếu `callConfirmed`/note khi `codRequiresCall=true`, `confirmed→cancelled` bị chặn khi có active shipment, `MarkShipping` bị chặn khi `bank_transfer` chưa `paid` hoặc thiếu shipment, `MarkDelivered` kiểm `shipped_at <= delivered_at <= now`). |
| `U03` | `BR_ORD_08_LineMutations_AllowedOnlyInPendingAndConfirmed` | 3 | Thêm/sửa/xóa dòng chỉ cho phép ở `pending`/`confirmed`; từ chối ở `shipping`/`delivered`/`delivery_failed`/`cancelled` hoặc khi xóa hết về `0` dòng hoặc trùng `variant_id`. |
| `U04` | `BR_ORD_10_OrderCode_UsesVietnamDateAndUnambiguousAlphabet` | 2 | Mã sinh khớp `^TP[0-9]{6}[A-Z0-9]{4}$`, không bao giờ chứa `0,O,1,I,L`; mốc `2026-11-23T16:59:59Z` dùng `261123` còn `17:00:00Z` (`00:00 +07:00`) sang `261124`. |
| `U05` | `BR_CRT_05_BR_PRC_01_02_CheckoutCalculator_ComputesLinesAndUnavailableModes` | 4 | Khớp `PricingCalculator` (`list` vs `sale` vs Admin `override` có lý do `>= 3` ký tự); `WebQuote` bỏ dòng `unavailable` khỏi `SubtotalVnd` kèm warning; `WebPlaceOrder` trả `cart_has_unavailable_items`; `AdminOrder` trả `variant_not_sellable` (`BR-CAT-15`); khớp `ck_line_total`. |
| `U06` | `BR_PRC_04_06_VoucherErrorPrecedenceFloorRoundingAndStaffDiscountCap` | 5 | Kiểm đúng thứ tự 7 mã lỗi `BR-PRC-04` từ `voucher_not_found` tới `voucher_min_order`; `percent` làm tròn `floor` (`155 × 10% = 15`), áp `max_discount_vnd` và trần `SubtotalVnd`; biên `discount == 0` trả `voucher_min_order`; `ReferralCode` trả `referral_not_available`; giảm tay Admin kiểm `discount_note` và `discount_requires_owner` (`AC-P2-06`). |
| `U07` | `BR_SHP_01_ShippingFeeAndOrderTotal_MatchDatabaseCheckConstraints` | 3 | `base = SubtotalVnd - DiscountVnd` so với `FreeThresholdVnd` (`null`, `base == threshold - 1`, `base == threshold`), Admin override phí ship, và bất biến `TotalVnd == SubtotalVnd - DiscountVnd + ShippingFeeVnd` (`ck_order_total`), `DiscountVnd <= SubtotalVnd` (`ck_order_discount`). |

### 8.2 Integration Tests dự kiến trên PostgreSQL 17 (`TeaPremium.IntegrationTests` — `26` test methods, `0` skipped)

| Mã | Test method dự kiến | Số method | Nội dung kiểm chứng trên PostgreSQL 17 thật |
|---|---|---:|---|
| `I01`–`I06` | `ReservationsMigrationTests` (`Parity`, `SemanticDrift`, `PendingModelChanges`, `IdempotentScript`, `DownAndGuard`, `TpAppTpReadonlyAndPreflight`) | 6 | Parity 100% `inventory.reservations` + 2 partial indexes + `fk_reservations_line` (`ON DELETE SET NULL`) với `schema.sql`; detector bắt lệch ngữ nghĩa; `HasPendingModelChanges() == false`; script idempotent chạy 2 lần giữ nguyên dữ liệu; `Down` về `P201Head` sạch bảng/ACL + từ chối `Down` (`P0001`) khi `reservations` có dòng (kể cả `released`) hoặc có view/FK ngoài; đăng nhập thật `tp_app`/`tp_readonly`, chạy `preflight.sql` trước/sau `Up` PASS, probe 4 CHECK constraints trả `23514` và `ux_reservation_active_line` trả `23505`. |
| `I07` | `OrderConcurrencyTests.AC_P2_01_TwentyConcurrentOrdersOnLastUniquePiece_ExactlyOneSucceedsAndNineteenReturnOutOfStock` | 1 | **AC-P2-01 / NFR-02:** Ấm độc bản `is_unique_piece = true`, `on_hand = 1, reserved = 0`. 20 scope `tp_app` đồng thời xuất phát qua barrier gọi tạo đơn → đúng `1` đơn thành công, `19` kết quả lỗi `out_of_stock` kèm `{variantId, sku, available: 0}`, `0` deadlock `40P01`, DB có đúng `1` đơn, `1` reservation `active`, `stock_levels.on_hand = 1`, `stock_levels.reserved = 1`, `0` dòng `sale` movement. |
| `I08`–`I10` | `OrderConcurrencyTests` (`ReverseMultiVariantOrderNoDeadlock`, `CatalogAndOrderShareLockOrder`, `OrderCodeCollisionRetriesViaSavepoint`) | 3 | 10 cặp đơn đồng thời đặt `(VariantA, VariantB)` vs `(VariantB, VariantA)` không bao giờ deadlock `40P01` và tổng `reserved` khớp tuyệt đối; khóa đồng thời với `EfProductStore` / `RebuildProductSearchText` tuân thủ `product → variant → stock_levels`; giả lập trùng `ux_orders_code` bằng `IOrderCodeGenerator` chứng minh rollback savepoint + detach `AuditLog` thử lỗi + cấp mã kế tiếp thành công không dính `25P02`. |
| `I11`–`I13` | `OrderLifecycleAndReservationTests` (`ReserveShortfallRollsBackAllLines`, `AC_P2_02_CancelPendingAndConfirmedReleasesReservedWithoutMovements`, `AC_P2_03_MarkShippingConsumesReservationWritesSaleMovementAndLocksUnitCost`) | 3 | Đơn nhiều dòng có 2 dòng thiếu tồn → trả đủ cả 2 dòng trong `out_of_stock` và rollback sạch; **AC-P2-02:** hủy đơn `pending` và `confirmed` giảm `reserved` về `0`, reservation sang `released` (`closed_at != null`), sổ `stock_movements` không sinh dòng mới, hoàn `voucher_redemptions` sang `reverted` & giảm `used_count`; **AC-P2-03:** chuyển `shipping` đổi reservation sang `consumed`, giảm `on_hand` & `reserved`, ghi `stock_movements(sale, -q, unit_cost_vnd = avg_cost_vnd, ref_type = 'order')`, chốt `order_lines.unit_cost_vnd` (và không đổi khi nhập lô mới giá khác sau đó). |
| `I14`–`I16` | `OrderLifecycleAndReservationTests` (`DeleteAndAdjustLinesInPendingAndConfirmedSetsReservationOrderLineNull`, `ForbidLineDeleteAfterShippingAndVerifyDbCheckSafetyNet`, `XminConcurrencyAuditRedactionVariantUsageAndAppendOnlyHistory`) | 3 | Xóa dòng đơn ở `pending` và `confirmed`: nhả `reserved` trước rồi `DELETE FROM sales.order_lines` bằng `tp_app` → dòng đơn biến mất, dòng `inventory.reservations` giữ nguyên với `status = 'released'` và **`order_line_id IS NULL`**; giảm số lượng dòng tạo reservation `active` mới; tăng số lượng giữ thêm phần chênh; chặn xóa dòng ở `shipping`/`delivered`/`cancelled` tại Domain **và** chứng minh lệnh SQL `DELETE FROM sales.order_lines` trực tiếp bằng `tp_app` khi reservation đang `active`/`consumed` cũng bị PostgreSQL chặn bởi `23514` (`ck_reservations_order_line_required`); kiểm `xmin` stale trả `concurrency_conflict`, `platform.audit_logs` của `sales.orders` che sạch SĐT/tên/địa chỉ/ghi chú, `OrderVariantUsage` chặn sửa SKU/xóa variant đã có dòng đơn (`BR-CAT-02`), và `tp_app` không thể `DELETE` `sales.orders` hay `sales.order_status_history` (`42501`). |
| `I17`–`I18` | `OrderLifecycleAndReservationTests` (`CompositeOrderOutboxHandlers_RegisterSingleHandlerPerTypeAndProcessCleanly`, `OrderOutbox_MalformedPayloadAndStepFailureFollowBoundedRetryToDead`) | 2 | Xác nhận DI có đúng `1` `IOutboxHandler` cho từng loại trong 6 message `Order*`; chạy trọn vòng đời đơn qua `OutboxBatchProcessor` → cả 6 loại chuyển `processed`, payload và log không chứa PII/giá vốn; gắn thử 2 `IOrderOutboxStep` thực thi đúng thứ tự `Order` trong savepoint, lỗi/payload hỏng backoff đúng 10 lần → `dead`. |
| `I19`–`I22` | `UniquePieceSoldLifecycleTests` (`ReservedUniquePieceRemainsOutOfStockUntilShipped`, `ShippedUniquePieceMarksSoldSetsSoldOutAtAndEmitsInvalidation`, `MultiVariantAndDamagedUniquePieceDoNotFalselySetSoldOutAt`, `ReturnRestockClearsSoldOutAtRestoresInStockAndEmitsInvalidation`) | 4 | **BR-SEO-21:** (1) Khi mới giữ hàng (`pending`/`confirmed`), `GET /api/v1/store/products/{slug}` và `GET /api/v1/store/products` trả `"out_of_stock"`, `sold_out_at == null`; (2) Khi chuyển `shipping`, cả list card và detail trả `"sold"`, `products.sold_out_at != null`, phát `StorefrontInvalidate`; (3) Product có 2 variant độc bản mới bán 1, hoặc có variant thường `active`, hoặc `0` variant `active`, hoặc hết hàng do `damage`/`adjustment` đều giữ `sold_out_at == null` và variant vỡ trả `"out_of_stock"`; (4) Chạy `RestockReturnedAsync` (`ref_type = 'order'` và `'return'`) đưa `on_hand = 1` → `products.sold_out_at` về `NULL`, Store API trả `"in_stock"`, phát `StorefrontInvalidate`; bán lại lần 2 → `"sold"` và set `sold_out_at` mới. |
| `I23`–`I26` | `InventoryAlertsAndReconciliationTests` & `StoreCatalogTests` *(mở rộng 4 kịch bản)* | 4 | **BR-INV-13:** `ReconcileInventory` + `InventoryAlertHandler` + `GET /api/v1/admin/inventory/alerts` phát hiện lệch `reserved != SUM(reservations active)` (kể cả khi `on_hand == ledgerQty`, thiếu dòng `stock_levels`, và bỏ qua dòng `released`/`consumed` có `order_line_id IS NULL`), chứng minh bằng trigger PostgreSQL rằng **không bao giờ tự sửa `stock_levels` hay `reservations`**, và JSON response không lộ trường `cost`. |

### 8.3 Chuỗi lệnh kiểm chứng bắt buộc khi thực thi G3

```powershell
./scripts/verify-linux.ps1 -Filter "FullyQualifiedName~Order|FullyQualifiedName~CheckoutCalculator|FullyQualifiedName~Reservation|FullyQualifiedName~UniquePieceSold|FullyQualifiedName~P110"
./scripts/verify-linux.ps1 -Mode api -WriteBack
./scripts/candidate-manifest.ps1 -Write -Base <origin/main SHA sau khi P2-01 merge> -Out artifacts/p2-05/candidate.manifest
./scripts/verify-linux.ps1 -Manifest artifacts/p2-05/candidate.manifest
./scripts/verify-linux.ps1 -Mode frontend -Manifest artifacts/p2-05/candidate.manifest
git diff --check
```

---

## 9. Rủi ro kỹ thuật, `ASSUMPTION` và `GAP` đối chiếu đặc tả (`file:dòng`)

### 9.1 Rủi ro thiết kế đã nhận diện và biện pháp khóa chặn trong `P2-05`

| Mã | Nguồn (`file:dòng`) | Mô tả rủi ro | Biện pháp triệt tiêu trong `P2-05` |
|---|---|---|---|
| `R-P205-01` | `docs/spec/schema.sql:509,909-910` (bản `P2-01`) | `DELETE FROM sales.order_lines` kích hoạt `fk_reservations_line` `ON DELETE SET NULL` tức thì; nếu dòng `inventory.reservations` chưa chuyển sang `status = 'released'` trước lệnh `DELETE`, biểu thức `CHECK (status = 'released' OR order_line_id IS NOT NULL)` sẽ nổ `23514`. | Trong `SyncOrderLinesAsync`, bắt buộc chuyển reservation sang `status = 'released'` (`closed_at = now`), giảm `reserved` và `SaveChangesAsync` flush trước khi xóa dòng `sales.order_lines`; có integration test `I14`/`I15` kiểm cả luồng hợp lệ lẫn chốt chặn SQL thô. |
| `R-P205-02` | `backend/src/TeaPremium.Application/Inventory/InventoryMutation.cs:49` ↔ `docs/spec/09-seo.md:74-75` (`BR-SEO-21`) | Khi đơn chuyển `confirmed → shipping`, `available` trước (`1 - 1 = 0`) và sau (`0 - 0 = 0`) đều bằng `0`, nên điều kiện `(before.Available > 0) != (after.Available > 0)` không phát `StorefrontInvalidate` dù trang sản phẩm cần đổi từ `"out_of_stock"` sang `"sold"`. | `EfProductSoldOutSync` phát tường minh `StorefrontInvalidate(["product:" + slug, "catalog", "sitemap", "feed"])` mỗi khi trạng thái `"sold"` của biến thể độc bản hoặc `products.sold_out_at` thay đổi. |
| `R-P205-03` | `docs/spec/09-seo.md:74` & `docs/spec/04-business-rules.md:50` | Lỗi vacuous truth `.All(...) == true` trên tập rỗng nếu product đang có `0` variant `active` (tất cả `inactive`), làm set nhầm `products.sold_out_at`. | Điều kiện bắt buộc `activeVariants.Length > 0 && activeVariants.All(...)`, kiểm bằng integration test `I21`. |
| `R-P205-04` | `backend/tests/TeaPremium.IntegrationTests/InventoryAlertsAndReconciliationTests.cs:378-388,524` | Bài test `P1-10` hiện tự chạy `CREATE TABLE inventory.reservations` giả để bẫy `42501` và assert SQL không chứa chuỗi `"reservations"`; khi `P2-05` tạo bảng thật và bật đối soát `BR-INV-13`, đoạn bẫy P1 này sẽ đụng `42P07`. | Cập nhật bài test này tại `P2-05` sang kiểm đối soát `reservations` thật theo Owner Decision #6, giữ nguyên trigger cấm tự sửa số dư. |
| `R-P205-05` | `D:\tra-hien-p2-01` & `D:\tra-hien-p1-13` (chưa merge `origin/main`) | `P2-01` chứa 26 bảng nền `crm`/`sales`/`pricing`/`finance`; `P1-13` tiêu thụ `InventoryAlertsView` từ `packages/api-client`. | Chờ `P2-01` (và `P1-13` nếu đi trước) merge vào `main`, chạy `base-integration` trên `D:\tra-hien-p2-05` trước khi tạo migration `P2-05` và sinh lại OpenAPI. |

### 9.2 `GAP` / `ASSUMPTION` biên nhỏ đã được Owner khóa cứng (23/11/2026)

1. **`GAP-P205-01 = A (ĐÃ CHỐT)`** — `docs/spec/04-business-rules.md:102-105` ↔ `docs/spec/schema.sql:538,549,1049`: Khi `discountVnd` tính ra `0` → trả `422 voucher_min_order`, không tăng `used_count`, không ghi `voucher_redemptions`.
2. **`ASSUMPTION-P205-01 = B (ĐÃ CHỐT)`** — `docs/spec/04-business-rules.md:95` & `docs/spec/05-api.md:167`: Chỉ mở rộng `ReconcileInventory` + `InventoryAlertHandler` kiểm `Σ reservation active = reserved` (không tự sửa số dư); KHÔNG đổi `EfInventoryQueries.AlertsAsync`, KHÔNG đổi `InventoryQueries.cs`, KHÔNG đổi OpenAPI, KHÔNG đụng `packages/api-client`.
3. **`ASSUMPTION-P205-02 = A (ĐÃ CHỐT)`** — `docs/spec/04-business-rules.md:72-73,112,166`: Chỉ `product.status='active'` và `variant.status='active'` (kèm `price_vnd` hợp lệ) mới thêm được vào đơn (web và admin) → `variant_not_sellable` / `cart_has_unavailable_items`.
4. **`G12 = A (ĐÃ CHỐT — remediation G4, 23/11/2026)`** — `docs/spec/04-business-rules.md:104-105,163-167` ↔ `docs/spec/schema.sql:548-551` (`R-P201-02`): Gỡ voucher khi sửa đơn (`UpdateOrderLines`) chuyển redemption sang `'reverted'` + giảm `used_count` trong cùng transaction; áp lại voucher trên đơn đã có dòng `'reverted'` kiểm giới hạn dưới khóa `pricing.vouchers FOR NO KEY UPDATE` rồi `UPDATE` dòng hiện có theo `order_id` (không `INSERT`).
