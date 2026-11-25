# ADR-P2-01 — Khôi phục tri thức, hợp đồng Phase 2 và ranh giới quyết định (Knowledge Recovery)

- **Trạng thái tài liệu:** `KNOWLEDGE_RECOVERY_RECORD` (Tier B, docs-only — chờ Independent G4 & Owner publish).
- **Ngày lập:** `2026-11-25` (mốc đối chiếu `origin/main` = `591f872d7ba7377e7c5254360a59058576d5c04a`, sau PR `#47` `P2-13`).
- **Phạm vi:** Không thay đổi production source code, không thay đổi `docs/spec/`, không tự chốt nghiệp vụ mới và không thay đổi quy định trong `AGENTS.md` / `.agents/skills/` / `scripts/` / `.github/`.
- **Chính sách độc lập với công cụ/mô hình:** Mọi quyết định, cổng kiểm soát và tiêu chí nghiệm thu trong tài liệu này độc lập với công cụ, nhà cung cấp hay mức độ suy luận; tài liệu chỉ ghi nhận bằng chứng kỹ thuật và trạng thái phê duyệt thực tế.

---

## 1. Mục đích Knowledge Recovery và bối cảnh

Trong quá trình triển khai từ cuối Phase 1 (`P1-10`, `P1-13`) sang Phase 2 (`P2-01`..`P2-13` và các kế hoạch `P2-03`, `P2-04`, `P2-06`, `P2-07`, `P2-08`, `P2-12`), một phần quyết định nghiệp vụ, hợp đồng liên module, phân tích xung đột và danh sách follow-up chỉ nằm rải rác trong lịch sử hội thoại điều phối, tài liệu bàn giao tạm ngoài Git và các hồ sơ kế hoạch riêng lẻ.

Tài liệu này đưa toàn bộ tri thức đã kiểm chứng vào Git nhằm:

1. Bảo toàn các quyết định nghiệp vụ và kỹ thuật thực sự đã được Owner phê duyệt kèm tham chiếu bằng chứng.
2. Tách bạch nghiêm ngặt trạng thái phê duyệt (`approval status`), trạng thái triển khai (`implementation status`) và trạng thái xung đột (`conflict status`) — tuyệt đối không nâng nhầm quyết định đã code/merge nhưng thiếu xác nhận tường minh của Owner thành `OWNER_APPROVED`.
3. Chuẩn hóa bảng hợp đồng dùng chung (`Shared Contracts & Ownership`), xung đột đang mở (`CONF-P206-P207-01`), các vấn đề chưa chốt (`O1`–`O7`) và follow-up liên task cho Phase 2–5.
4. Cung cấp nguồn sự thật bền vững để một phiên Technical Lead mới có thể tiếp quản điều phối mà không phụ thuộc lịch sử hội thoại cũ (xem trạng thái task và snapshot worktree tại [task-readiness.md](../plans/task-readiness.md) và bảng gap tại [spec-gaps.md](../analysis/spec-gaps.md)).

---

## 2. Thẩm quyền tài liệu, quan hệ với `docs/spec/` và mốc audit đặc tả

### 2.1 Nguyên tắc thẩm quyền

Theo `AGENTS.md` và [README của `docs/decisions/`](README.md):

- `docs/spec/` (`00-README.md`, `01`–`09` Markdown và `schema.sql`) là nguồn có thẩm quyền duy nhất về yêu cầu, quy tắc nghiệp vụ, DDL và API (SEO theo `DEC-21` trong `09-seo.md`).
- **ADR này không bao giờ âm thầm ghi đè `docs/spec/`.** Mọi mục được phân loại rõ tác động đặc tả:
  - `Clarification only`: làm rõ ranh giới triển khai tương thích với `docs/spec/` hiện hành, không cần sửa spec.
  - `Amend docs/spec/<file>`: thay đổi văn bản hoặc hợp đồng đặc tả; nếu chưa có diff được Owner phê duyệt tường minh thì giữ trạng thái chưa chốt và không sửa `docs/spec/` trong PR-1.
- Mọi đề xuất phân tích (`O1`–`O7`, `PROPOSED`, `UNRESOLVED`, `CONFLICT`) trong tài liệu này **không phải đặc tả có hiệu lực**.

### 2.2 Phân loại mốc kiểm định đặc tả (`Latest Verified Spec SHA`)

Không đồng nhất "commit cuối chạm `docs/spec/`" với "SHA đã qua Full G1 `spec-audit`", và không đồng nhất commit chứa biên bản audit với snapshot spec thực tế được audit nếu khác nhau:

| Loại mốc | Commit SHA | Bằng chứng đã xác minh | Trạng thái bằng chứng |
|---|---|---|---|
| **Full G1 Audited Spec SHA** | `cadce575597738bf40ad7c45689482de5a5fbc3d` (`2026-11-16`) | [spec-inventory.md](../analysis/spec-inventory.md) dòng `5`, [spec-gaps.md](../analysis/spec-gaps.md) dòng `5` (đọc đủ 10 Markdown + `schema.sql`) | `VERIFIED_IN_CURRENT_SESSION` |
| **Targeted G1 A01** | Base `288ca8b55cab2017c9ee42bd35036e2d2e5bd87c` → PR `#20` HEAD `47f2c7b` → merge `024ea998c43baf0afde30b069ed6b4876cb96f4a` (`2026-11-18`) | [a01-amendment-evidence.md](../reviews/a01-amendment-evidence.md), [spec-gaps.md](../analysis/spec-gaps.md) dòng `71–89` | `VERIFIED_IN_CURRENT_SESSION` |
| **Targeted G1/G2 B01** | `dadc4435e8f115faaeb9e8511e56e09d5d517a8a` (`2026-11-19`) | [task-readiness.md](../plans/task-readiness.md) dòng `114–144`, [ADR-B01](ADR-B01-right-sized-operations.md) | `VERIFIED_IN_CURRENT_SESSION` |
| **Targeted G1 gần nhất đã commit trong `spec-gaps.md`** | `f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7` (`P1-04`, `2026-11-19`) + closure `P1-07` tại `ed49d11d945ec27033489d4b834dd49b8c9bb6ed` (`2026-11-20`) | [spec-gaps.md](../analysis/spec-gaps.md) dòng `91–111`, [ADR-P1-04](ADR-P1-04-publication-history-and-publish.md), [p1-07-implementation.md](../plans/p1-07-implementation.md) | `VERIFIED_IN_CURRENT_SESSION` |
| **Phase 2 Readiness Audit (phiên riêng)** | Chạy read-only trên `main @ 82710b3` (`2026-11-22`), chưa có biên bản riêng commit trong `docs/analysis/` | Tham chiếu trong phiên điều phối cũ (mã phiên `b2221439-9e6b-45ca-b9d4-abcc6d706928`); các quyết định Owner rút ra đã được xác nhận tại Sự kiện B bên dưới | `REPORTED_BY_OTHER_AGENT` |
| **Commit gần nhất thay đổi `docs/spec/` trên `origin/main`** | `57cb13fb4b03e28749e06ed8a136b6b5e6fa1748` (`P2-01`, PR `#44` merge `85218d119b36d5a1b8977a75c9fb71abf454ddde`, `2026-11-23`; liền trước đó là `35d0333` của `P1-10`) | `git log cadce575..origin/main -- docs/spec/` gồm `15` commit; `git diff 57cb13f..591f872 -- docs/spec/` bằng rỗng (`0` thay đổi). `57cb13f` đi qua task-scoped G4 của `P2-01`, **không phải Full G1 audit mới** | `VERIFIED_IN_CURRENT_SESSION` |

---

## 3. Evidence Provenance và quy tắc phân loại

### 3.1 Bộ nhãn trạng thái chuẩn

- **Trạng thái quyết định (Approval / Lifecycle Status):**
  - `OWNER_APPROVED`: có bằng chứng xác nhận tường minh của Owner (qua sự kiện hỏi–đáp trực tiếp hoặc ADR/spec đã được Owner phê duyệt hợp lệ trong repository).
  - `IMPLEMENTED_BUT_NOT_CONFIRMED`: đã có mã nguồn hoặc đã merge vào `origin/main` theo đề xuất của agent điều phối, nhưng **không có xác nhận nghiệp vụ tường minh của Owner**.
  - `CONFLICT`: hai kế hoạch, đoạn mã hoặc đặc tả đang mâu thuẫn nhau và chưa có quyết định chốt của Owner.
  - `PROPOSED`: đề xuất phân tích trong hồ sơ kế hoạch của task chưa triển khai; chưa được Owner phê duyệt.
  - `UNRESOLVED`: vấn đề thiếu hợp đồng, chưa gắn lộ trình hoặc cần xác minh pháp lý/vận hành ngoài repo; chưa chốt.
  - `SUPERSEDED`: quyết định cũ đã bị một quyết định được phê duyệt sau đó thay thế có phạm vi rõ ràng.
- **Mức độ kiểm chứng bằng chứng (Evidence Provenance Tags):** `VERIFIED_IN_CURRENT_SESSION`, `REPORTED_BY_LEAD`, `REPORTED_BY_OTHER_AGENT`, `OWNER_CONFIRMED`, `ASSUMED`, `UNKNOWN`, `NOT_RUN`.
- **Quy tắc chống nâng trạng thái sai:** Các hành động như Owner sao chép prompt sang phiên G3, cho phép agent bắt đầu implement, chạy test, mở PR hoặc merge PR **không mặc nhiên là sự phê duyệt nghiệp vụ** đối với các `GAP`/`ASSUMPTION` tự đề xuất bên trong prompt. Ghi chú *"Quyết định Owner đã khóa cứng"* xuất hiện trong một số hồ sơ `p1-13`, `p2-02`, `p2-05`, `p2-13`, `p2-03`, `p2-07` là văn bản do agent ghi lại từ prompt điều phối; qua rà soát đối chiếu trực tiếp lịch sử hội thoại điều phối, các mục đó thiếu xác nhận tường minh nên được phân loại trung thực là `IMPLEMENTED_BUT_NOT_CONFIRMED`.

### 3.2 Nguồn chứng cứ gốc đã đối chiếu

1. **Tài liệu bàn giao nguồn ngoài Git:** `D:\tra-hien\artifacts\handoff\pr1-evidence-handoff-20261125.md` (`32.974` bytes), SHA-256 đã xác minh khớp tuyệt đối: `de3858f944c1b0469e4da0d9c209ff8f0b759473994cb7f0fdd363bb4f0411ab` (`VERIFIED_IN_CURRENT_SESSION`).
2. **Đối chiếu trực tiếp transcript nguồn của phiên Lead cũ (`d0f0ef21-5717-4368-83de-bdbb73883106`, `871` dòng, `22–25/11/2026`):**
   - Đã rà soát toàn bộ sự kiện `ask_question` và toàn bộ thông điệp đầu vào của Owner trong phiên này (`VERIFIED_IN_CURRENT_SESSION`; tuyệt đối không sao chép raw transcript vào Git).
   - Xác nhận có đúng **3 sự kiện `ask_question`** chứa câu trả lời phê duyệt nghiệp vụ/kỹ thuật tường minh của Owner:
     - **Sự kiện A:** `2026-11-22 22:09–22:13 +07:00`, `STEP 438` (câu hỏi tại dòng `436`, trả lời tại dòng `437`). Gồm `3` quyết định kỹ thuật/nghiệp vụ cho `P1-10` (`A2`–`A4`) và `1` lựa chọn phân vai phiên điều phối (`A1`, không phải quyết định nghiệp vụ).
     - **Sự kiện B:** `2026-11-22 22:36 +07:00`, `STEP 462` (câu hỏi tại dòng `460`, trả lời tại dòng `461`). Gồm `10` quyết định nghiệp vụ/kỹ thuật thuộc `3` nhóm câu hỏi cho Phase 2 (`A1`: 3 mục, `A2`: 3 mục, `A3`: 4 mục) và `1` lựa chọn lịch chạy song song `P1-13`/`P2-01` (`A4`, lựa chọn điều phối tiến độ).
     - **Sự kiện C:** `2026-11-23 14:10–14:11 +07:00`, `STEP 598` (câu hỏi tại dòng `598`, trả lời tại dòng `599`). Gồm `2` quyết định hợp đồng trước `P2-06` (`A1`: `G11`, `A2`: `G12`).
   - Ngoài 3 sự kiện trên, không tìm thấy xác nhận tường minh riêng nào của Owner cho các nhóm `GAP`/`ASSUMPTION` của `P1-13`, `P2-02`, `P2-05`, `P2-13`, `P2-03` và `P2-07` trong toàn bộ phiên điều phối `22–25/11/2026`.

### 3.3 Đối chiếu số lượng quyết định `OWNER_APPROVED`

Báo cáo tóm tắt trước đó của Lead nêu con số `"13"`, trong khi đối chiếu trực tiếp 3 sự kiện `ask_question` cho thấy các cách đếm cụ thể như sau (`VERIFIED_IN_CURRENT_SESSION`):

- **Theo mục quyết định kỹ thuật/nghiệp vụ khôi phục từ 3 sự kiện (`A` + `B` + `C`):** **`15` mục quyết định** = `3` mục của `P1-10` (`DEC-P110-01..03`) + `10` mục của Sự kiện B + `2` mục của Sự kiện C *(chưa tính `2` lựa chọn thuần điều phối tiến độ `A.A1` và `B.A4`)*.
- **Theo riêng phạm vi Phase 2 (loại trừ `3` mục `P1-10`):** **`12` mục quyết định** (`10` từ Sự kiện B + `2` từ Sự kiện C); nếu tách riêng từng mã ID spec/gap (`G04`/`G29`, `G17b`/`G28`) thì gồm **`14` ID riêng lẻ** (hoặc **`13` ID** nếu gộp `G17a` và `G17b` thành một mã `G17` — đây là nguồn gốc khả dĩ của con số `"13"` trước đó).
- **Theo mã ID riêng lẻ trên cả 3 sự kiện:** **`17` ID riêng lẻ** (`DEC-P110-01`, `DEC-P110-02`, `DEC-P110-03`, `G03`, `G21`, `G17a`, `BR-SEO-21`, `G16`, `G22`, `G04`, `G29`, `G27`, `G30`, `G17b`, `G28`, `G11`, `G12`), phân bổ trong **`8` nhóm trả lời nghiệp vụ** (`A.A2`, `A.A3`, `A.A4`, `B.A1`, `B.A2`, `B.A3`, `C.A1`, `C.A2`).
- **Quyết định `OWNER_APPROVED` đã commit sẵn từ trước (không bị hạ cấp):** Toàn bộ quyết định có bằng chứng phê duyệt hợp lệ trong repo trước `22/11/2026` vẫn giữ nguyên trạng thái `OWNER_APPROVED` (xem Mục 4.4).

---

## 4. Các quyết định `OWNER_APPROVED`

### 4.1 Nhóm Sự kiện A — `P1-10` (`2026-11-22 22:13 +07:00`, `STEP 438`, `OWNER_CONFIRMED` / `VERIFIED_IN_CURRENT_SESSION`)

| Decision ID | Nội dung chính xác | Bằng chứng xác nhận Owner & Nguồn | Approval / Impl Status | Task áp dụng & Dependency | Shared Contract / Follow-up | Spec Impact & Xung đột |
|---|---|---|---|---|---|---|
| `DEC-P110-01` | Không tạo bảng mới (`0` migration); bổ sung `GET /api/v1/admin/inventory/alerts` (yêu cầu `inventory.read + inventory.write`, không trả giá vốn) trả tồn thấp hiện hành + lệch sổ kho hiện thời + số outbox `InventoryAlert` trạng thái `dead`, kèm bổ sung 1 dòng vào `05-api.md` §4.3. *(Phương án đã cân nhắc: tạo bảng `inventory.alerts` mới hoặc chỉ ghi log server.)* | Trả lời `A2` tại `STEP 438` (`2026-11-22 22:13 +07:00`); lưu chứng cứ tại [p1-10-implementation.md](../plans/p1-10-implementation.md) dòng `10–12` và commit `35d0333`. | `OWNER_APPROVED` · **Merged** (`35d0333` / PR `#42` `e63e90b`) | `P1-10` (dep `P0-06`, `P1-08`) → tiêu thụ bởi `P1-13`, `P2-13`. | `P1-13` (đã merge) và `P2-13` (đã merge) đọc trạng thái hiện tại, không replay backlog P1. | `Amend docs/spec/05-api.md` dòng `167` (**đã áp tại `35d0333`**). Không xung đột. |
| `DEC-P110-02` | Phát message outbox `InventoryAlert` cùng transaction ở cả hai đường: biến động tồn kho (`InventoryMutation`) lẫn khi sửa `low_stock_threshold` của variant mà trạng thái sau ghi thỏa `threshold > 0 && available <= threshold`. *(Phương án đã cân nhắc: chỉ phát trong `InventoryMutation`.)* | Trả lời `A3` tại `STEP 438` (`2026-11-22 22:13 +07:00`); [p1-10-implementation.md](../plans/p1-10-implementation.md) dòng `13–15`. | `OWNER_APPROVED` · **Merged** (`35d0333` / PR `#42` `e63e90b`) | `P1-10` → kế thừa bởi `P2-05`, `P2-13`. | `InventoryAlert` outbox producer/consumer (`BR-INV-11`). | `Clarification only`. Không xung đột. |
| `DEC-P110-03` | `ReconcileInventory` kế thừa `PeriodicJob` kiểm định kỳ, nhưng chỉ thực hiện đối soát tối đa `1` lần/ngày theo lịch Việt Nam sau `01:30` sáng (giữ advisory lock riêng, hỗ trợ `RunOnceAsync` cho integration test). *(Phương án đã cân nhắc: chu kỳ cố định 24h tính từ lúc khởi động process.)* | Trả lời `A4` tại `STEP 438` (`2026-11-22 22:13 +07:00`); [p1-10-implementation.md](../plans/p1-10-implementation.md) dòng `16–17`. | `OWNER_APPROVED` · **Merged** (`35d0333` / PR `#42` `e63e90b`) | `P1-10` → mở rộng đối soát `inventory.reservations` ở `P2-05` (đã merge `862fbf0`). | `ReconcileInventory` + `BR-INV-13`. | `Clarification only`. Không xung đột. |

### 4.2 Nhóm Sự kiện B — Phase 2 Readiness (`2026-11-22 22:36 +07:00`, `STEP 462`, `OWNER_CONFIRMED` / `VERIFIED_IN_CURRENT_SESSION`)

| Decision ID | Nội dung chính xác | Bằng chứng xác nhận Owner & Nguồn | Approval / Impl Status | Task áp dụng & Dependency | Shared Contract / Follow-up | Spec Impact & Xung đột |
|---|---|---|---|---|---|---|
| `G03` | Hoãn toàn bộ bảng và khóa ngoại `marketing.*` sang `P5` (`P5-01`/`P5-02`); giữ các cột UUID nullable (`campaign_id`, `tracking_link_id`) và chỉ mục `ix_orders_campaign` tại `P2-01`. *(Phương án đã cân nhắc: tạo sớm 2 bảng `marketing.campaigns`/`tracking_links` ngay ở `P2-01`.)* | Trả lời `A1 (1)` tại `STEP 462` (`2026-11-22 22:36 +07:00`); [p2-01-implementation.md](../plans/p2-01-implementation.md) dòng `37`, commit `57cb13f`. | `OWNER_APPROVED` · **Merged** (`57cb13f` / PR `#44` `85218d1`) | `P2-01` → follow-up `P5-01`, `P5-02`. | `P5-01`/`P5-02` phải tạo bảng `marketing.*` và bổ sung 4 FK đã hoãn (`crm.interactions.campaign_id`, `sales.orders.tracking_link_id`, `sales.orders.campaign_id`, `finance.expenses.campaign_id`). | `Amend docs/spec/schema.sql` (**đã áp tại `57cb13f`**). Không xung đột. |
| `G21` (+ `sales.order_lines` DELETE) | `P2-01` tạo `26` bảng nền (`crm`, `sales`, `pricing.vouchers/redemptions`, `finance.expenses`); cho phép `tp_app` `DELETE` trên `sales.order_lines` trước khi xuất kho (gỡ `sales.order_lines` khỏi `REVOKE DELETE` tại `03-database.md:156`); `P2-05` tạo `inventory.reservations` và đặt `fk_reservations_line ON DELETE SET NULL` kèm `CHECK (status = 'released' OR order_line_id IS NOT NULL)`. *(Phương án đã cân nhắc: giữ `REVOKE DELETE` và xóa mềm bằng `quantity = 0`.)* | Trả lời `A1 (2)` tại `STEP 462` (`2026-11-22 22:36 +07:00`); [p2-01-implementation.md](../plans/p2-01-implementation.md) dòng `32–40`, [p2-05-implementation.md](../plans/p2-05-implementation.md) dòng `412`. | `OWNER_APPROVED` · **Merged** (`57cb13f` PR `#44` & `5e6f6b8` PR `#46`) | `P2-01`, `P2-05` → tiêu thụ bởi `P2-06` (`PATCH` sửa dòng đơn). | `SyncOrderLinesAsync` phải chuyển reservation sang `'released'` và flush trước khi `DELETE` `sales.order_lines`; `catalog.product_reviews` vẫn hoãn sang `P5-07`. | `Amend docs/spec/schema.sql` & `docs/spec/03-database.md` dòng `156` (**đã áp tại `57cb13f`**). Không xung đột. |
| `G17a` (`ck_customer_contact`) | Bổ sung điều kiện `OR merged_into_customer_id IS NOT NULL` vào `ck_customer_contact` (`schema.sql:586`) để khi gộp khách phone-only (`phone_e164 = NULL`, `zalo_name = NULL`) không vi phạm ràng buộc `CHECK` (`23514`). | Trả lời `A1 (3)` tại `STEP 462` (`2026-11-22 22:36 +07:00`); `docs/spec/schema.sql:586`, commit `57cb13f`. | `OWNER_APPROVED` · **Merged** (`57cb13f` / PR `#44` `85218d1`) | `P2-01` → tiêu thụ bởi `P2-12`. | Hỗ trợ luồng gộp khách `BR-CRM-05` ở `P2-12`. | `Amend docs/spec/schema.sql` dòng `586` (**đã áp tại `57cb13f`**). Không xung đột. |
| `BR-SEO-21` (Sold Evidence) | Biến thể độc bản được tính là `"sold"` khi tổng biến động bán ròng (`sale` trừ `return_restock` có `ref_type IN ('order','return')`) `> 0` và `available == 0`, đọc qua cổng `ICatalogInventoryRead`; khi `return_restock` đưa `on_hand > 0` thì xóa `products.sold_out_at` và phát `StorefrontInvalidate`. *(Phương án đã cân nhắc: tính `"sold"` sớm ngay khi đơn `confirmed`.)* | Trả lời `A2 (1)` tại `STEP 462` (`2026-11-22 22:36 +07:00`); [p2-05-implementation.md](../plans/p2-05-implementation.md) dòng `390, 413`. | `OWNER_APPROVED` · **Implemented (phần bán & port ở `P2-05` PR `#46` `862fbf0`)**; đảo ngược ở `P2-08`/`P2-10` chưa implement | `P2-05` → `P2-08` (khi shipment `returned`), `P2-10` (khi restock đổi trả), `P3-13` (vòng đời SEO/archive). | `ICatalogInventoryRead` & `IProductSoldOutSync` (sở hữu bởi `P2-05`). | `Clarification only`. Không xung đột. |
| `G16` | Khi giao hàng thất bại ở `P2-08` và hàng hoàn về kho (`shipment = returned`), ghi `movement_type = 'return_restock'` với `ref_type = 'order'`, `ref_id = order.id` (tách khỏi `ref_type = 'return'` của đổi trả `P2-10` để không làm lệch `C2` trong công thức P&L `BR-FIN-02` tại `P4-02`). | Trả lời `A2 (2)` tại `STEP 462` (`2026-11-22 22:36 +07:00`); đối chiếu `04-business-rules.md:205` (`BR-SHP-03`) & `251–265` (`BR-FIN-02`). | `OWNER_APPROVED` · **Not implemented** (`P2-08` `PLAN_READY`, `P4-02` chưa bắt đầu) | `P2-08` → `P4-02`. | `RestockReturnedAsync` của `P2-05` đã chuẩn bị hỗ trợ cả `ref_type = 'order'` và `'return'`. | `Clarification only`. Không xung đột. |
| `G22` | Mỗi loại sự kiện outbox `Order*` đăng ký đúng `1` `CompositeOrderOutboxHandler : IOutboxHandler` từ `P2-05` (tương thích `OutboxBatchProcessor.SingleOrDefault`); các module sau chỉ đăng ký `IOrderOutboxStep`. Phần tạo `payments(cod, awaiting_settlement)` khi `delivered` thực hiện đồng bộ cùng transaction `delivered` tại `P2-08` theo `BR-ORD-07.1` (`04-business-rules.md:169–170`). | Trả lời `A2 (3)` tại `STEP 462` (`2026-11-22 22:36 +07:00`) cho `CompositeOrderOutboxHandler`; `04-business-rules.md:169–170` cho COD `awaiting_settlement`. | `OWNER_APPROVED` · **Core merged (`P2-05` `862fbf0` & step đầu tiên ở `P2-13` `591f872`)**; các step sau chưa implement | `P2-05` → `P2-08`, `P2-11`, `P2-13`, `P3-06`, `P5-05`. | `CompositeOrderOutboxHandler` & `IOrderOutboxStep` (sở hữu bởi `P2-05`). | `Clarification only`. Không xung đột. |
| `G04` + `G29` | Luồng đổi trả hoàn tiền tách 2 bước: (1) `approve` bằng quyền `returns.write` (`sales`/`owner`, chưa tạo `sales.refunds`) → (2) `owner` ghi nhận hoàn tiền qua `POST admin/orders/{id}/refunds` (`refunds.write`, bắt buộc `Idempotency-Key`, truyền `returnRequestId`) mới chuyển `return_requests.status = 'resolved'` trong cùng transaction. | Trả lời `A3 (1)` tại `STEP 462` (`2026-11-22 22:36 +07:00`); đối chiếu `02-architecture.md:214–215`, `04-business-rules.md:212–215`, `05-api.md:195–197`. | `OWNER_APPROVED` · **In-flight một phần (`P2-07` chưa merge)**; `P2-10` chưa implement | `P2-07` (hook & `returnRequestId`), `P2-10` (`approve`/`returns`). | `IRefundRecordedHook` & `returnRequestId` flow (dự kiến `P2-07` sở hữu). | `Clarification only` (có thể cân nhắc chỉnh câu chữ `04-business-rules.md:213` tại `P2-10` — diff chưa duyệt). Không xung đột ở luồng 2 bước; phần phí ship thuộc `O3` chưa chốt. |
| `G27` | Giữ đủ 3 lý do lỗi đổi trả (`damaged_in_transit`, `defective`, `wrong_item`) và bắt buộc `>= 1` bằng chứng ảnh/video cho cả 3 lý do (`owner` được phép override có lý do). *(Phương án đã cân nhắc: chỉ bắt bằng chứng cho `damaged_in_transit` theo chữ cũ `BR-RET-01`.)* | Trả lời `A3 (2)` tại `STEP 462` (`2026-11-22 22:36 +07:00`); đối chiếu `00-README.md:96` (`DEC-12`), `04-business-rules.md:211` (`BR-RET-01`). | `OWNER_APPROVED` **(nguyên tắc)** · **Not implemented**; diff cụ thể trên `docs/spec/` chưa duyệt | `P2-10`, `P3-07`, `P3-12`. | Chính sách đổi trả công khai và validation `POST admin/returns`. | `Amend docs/spec/00-README.md` (`DEC-12`) & `docs/spec/04-business-rules.md:211` (**chưa duyệt diff cụ thể**, xử lý tại `P2-10`). |
| `G30` | Đổi hàng thay thế (`replacement`) xuất đúng cùng `variant_id`, kiểm `active` & `available >= q`, và trừ kho nguyên tử (`movement_type = 'replacement_out'`, `ref_type = 'return'`) khi tạo vận đơn thay thế (`purpose = 'replacement'`). | Trả lời `A3 (3)` tại `STEP 462` (`2026-11-22 22:36 +07:00`); đối chiếu `04-business-rules.md:214` (`BR-RET-02`). | `OWNER_APPROVED` · **Not implemented** (`P2-08` seam `PLAN_READY`, `P2-10` chưa bắt đầu) | `P2-08` (seam vận đơn), `P2-10`. | Phối hợp `sales.shipments(purpose='replacement')` và `InventoryMutation`. | `Clarification only`. Không xung đột. |
| `G17b` + `G28` | Nguyên tắc Merge/Erase khách hàng bảo toàn `BR-IAM-04` (reset `history_unlocked_at`, kẹp mốc các địa chỉ chuyển sang khi chưa unlock), xoay `security_stamp`, và xóa/ẩn danh các trường ghi chú cũ (`note`, `follow_up_result`). *(Lưu ý: chỉ chốt nguyên tắc này; `GAP-P212-01` và `ASSUMPTION-P212-01..03` không nằm trong xác nhận này và giữ `UNRESOLVED`/`PROPOSED`.)* | Trả lời `A3 (4)` tại `STEP 462` (`2026-11-22 22:36 +07:00`); đối chiếu `04-business-rules.md:13–18, 224, 234`. | `OWNER_APPROVED` **(nguyên tắc)** · **Not implemented** (`P2-12` `PLAN_READY`) | `P2-12`, `P3-05`, `P5-01`/`P5-02`/`P5-05`/`P5-07`. | Hợp đồng quyền riêng tư mở rộng sang `P3-05` (`history_unlocked_at`), `P5-*` (`IMarketingCustomerPrivacyPort`, `G17` `campaign_recipients` và `G20` reviews). | `Clarification only`. Chi tiết biên ở `O6b`/`O7` chưa chốt. |

### 4.3 Nhóm Sự kiện C — Hợp đồng trước `P2-06` (`2026-11-23 14:11 +07:00`, `STEP 598`, `OWNER_CONFIRMED` / `VERIFIED_IN_CURRENT_SESSION`)

| Decision ID | Nội dung chính xác | Bằng chứng xác nhận Owner & Nguồn | Approval / Impl Status | Task áp dụng & Dependency | Shared Contract / Follow-up | Spec Impact & Xung đột |
|---|---|---|---|---|---|---|
| `G11` (`= A`) | Bổ sung `callConfirmed: bool` + `callNote?: string` vào cả hai đường xác nhận đơn: `POST admin/orders` (khi `confirmNow = true`) và `POST admin/orders/{id}/confirm`. Khi `orders.cod_requires_call = true` mà đơn COD có `callConfirmed != true` (hoặc thiếu note theo rule) thì trả `422`. Được phép sửa tối thiểu 1–2 dòng tại `docs/spec/05-api.md` (dòng `181`, `183`) khi triển khai `P2-06`. *(Phương án đã cân nhắc: chỉ thêm ở `POST confirm`, không cho `confirmNow` tạo thẳng `confirmed`.)* | Trả lời `A1` tại `STEP 598` (`2026-11-23 14:11 +07:00`); đối chiếu `04-business-rules.md:156`, `05-api.md:181,183`. | `OWNER_APPROVED` · **Not implemented** (`P2-06` `PLAN_READY`, hiện `HOLD G3` do `CONF-P206-P207-01`) | `P2-06` → `P2-15` (UI tạo/xác nhận đơn). | API contract `/api/v1/admin/orders` & `.../confirm`. | `Amend docs/spec/05-api.md:181,183` (**đã duyệt nguyên tắc, áp tại `P2-06`**, không sửa trong PR-1). Không xung đột. |
| `G12` (`= A`) | Khi sửa đơn làm tổng tụt dưới `min_order` của voucher, trong cùng transaction chuyển `pricing.voucher_redemptions` sang `'reverted'` và giảm `used_count`. Nếu áp lại voucher trên cùng đơn, kiểm tra lại giới hạn rồi `UPDATE` dòng redemption hiện có, không `INSERT` dòng mới (do `order_id UNIQUE`, xem `R-P201-02`). *(Phương án đã cân nhắc: không tự gỡ, trả `422` buộc nhân viên gỡ tay.)* | Trả lời `A2` tại `STEP 598` (`2026-11-23 14:11 +07:00`); đối chiếu `04-business-rules.md:104–105, 175`, [p2-05-implementation.md](../plans/p2-05-implementation.md) dòng `423`. | `OWNER_APPROVED` · **Core merged (`P2-05` `5e6f6b8` / PR `#46` `862fbf0`)**; HTTP `PATCH` + cảnh báo chờ `P2-06` | `P2-05` (đã merge) → `P2-06`, `P5-05`. | `OrderCoreUseCases.UpdateOrderLines` & `pricing.voucher_redemptions`. | `Clarification only`. Không xung đột. |

### 4.4 Bảo toàn các quyết định `OWNER_APPROVED` đã commit trước `22/11/2026`

Các quyết định sau đã có bằng chứng phê duyệt hợp lệ trong repository trước phiên điều phối `22–25/11/2026` và **vẫn giữ nguyên trạng thái `OWNER_APPROVED`** (không bị hạ cấp vì không nằm trong 3 sự kiện `ask_question` trên):

- `G26` / `G01` / `F-01` (nền `P0-04`): [task-readiness.md](../plans/task-readiness.md) dòng `77, 91, 98`, [p0-04-readiness.md](../plans/p0-04-readiness.md).
- `F-02` (`P0-09` địa giới hành chính): [p0-09-f02-readiness.md](../plans/p0-09-f02-readiness.md), [task-readiness.md](../plans/task-readiness.md) dòng `7, 79`.
- `A01` (`D1`–`D14` và 2 bổ sung backup/điều tra + owner API; trừ phần `D8`/`D9`/`D12` được `B01` điều chỉnh): [ADR-PROPOSAL-A01](ADR-PROPOSAL-A01-p0-closure-p1-owner-decisions.md), [a01-amendment-evidence.md](../reviews/a01-amendment-evidence.md).
- `B01` (thu gọn vận hành P0, phân định gate DB giả vs dữ liệu thật/go-live): [ADR-B01](ADR-B01-right-sized-operations.md).
- `P1-04` (`first_published_at`, `/publish`, cảnh báo `< 400` từ không chặn publish, SEO fallback): [ADR-P1-04](ADR-P1-04-publication-history-and-publish.md).
- `P1-07` (`BR-CAT-13` định danh variant, create-only, transaction theo Product) & `AC-P1-07` (luật tìm kiếm sản phẩm ngày `20/11/2026`): [spec-gaps.md](../analysis/spec-gaps.md) dòng `109–111`, [07-roadmap.md](../spec/07-roadmap.md) dòng `74`, [p1-07-implementation.md](../plans/p1-07-implementation.md).
- `P1-14` (hợp đồng Store Catalog API A–F & amendment ngày `21/11/2026`): [p1-14-implementation.md](../plans/p1-14-implementation.md) dòng `3, 9`.

---

## 5. Các quyết định `IMPLEMENTED_BUT_NOT_CONFIRMED`

Các mục dưới đây **đã được triển khai trong code hoặc đã merge vào `origin/main`**, nhưng đối chiếu trực tiếp transcript gốc của phiên điều phối (`22–25/11/2026`) xác nhận **không có xác nhận nghiệp vụ tường minh của Owner** (`VERIFIED_IN_CURRENT_SESSION`). Theo chỉ đạo `O5`, giữ nguyên trạng thái `IMPLEMENTED_BUT_NOT_CONFIRMED` cho tới khi Owner xác nhận hoặc yêu cầu điều chỉnh.

| Decision Group / IDs | Nội dung chính xác đã triển khai | Nguồn & Trạng thái Repo | Approval / Impl / Conflict | Task áp dụng & Follow-up | Spec Impact |
|---|---|---|---|---|---|
| **`P1-13`** (`GAP-P1-13-01..03`, `ASSUMPTION-01..02`) | Hydrate nhãn biến thể tối đa `5 × 100` trang khi mở URL trực tiếp (ngoài 500 hiện `variantId` rút gọn + sao chép); tách `409 concurrency_conflict` khỏi các lỗi kho `409`/`422`; giữ cổng `/inventory` theo `inventory.read` (Tier B); ẩn filter product-type khi thiếu `catalog.read`. | Đề xuất của Lead trong chat (`23/11`); [p1-13-implementation.md](../plans/p1-13-implementation.md) dòng `208–221`; **Merged** PR `#43` (`be3f7b6`). | Approval: `IMPLEMENTED_BUT_NOT_CONFIRMED` · Impl: **Merged** · Conflict: none | `P1-13` (đã merge). Không có blocker. | `Clarification only` |
| **`P2-02`** (`GAP-P202-01=C`, `GAP-P202-02=B`, `GAP-P202-03=A`, `ASSUMPTION-P202-01..03`, `R-P201-04`) | `correct-identity` từ chối đổi/xóa SĐT (`409 account_exists`) khi khách đã có `crm.customer_accounts` (follow-up `P3-05`); giữ `customers.write` cho `GET/POST/PATCH/DELETE addresses` và `GET/POST/PATCH admin/tags[/{id}]` theo `05-api.md:209–210`; mở `GET admin/customers/{id}/orders` đọc thật `sales.orders`; `rowVersion` tùy chọn trên sub-resource `tags`/`addresses`; tìm `q` literal substring + đuôi SĐT; mã `KH` 6 chữ số + `data.existingCustomerId`; siết đầu số di động VN tại Domain nhưng giữ sentinel `+84000000000` cho `BR-CRM-11`. | Đề xuất của Lead (`23/11`); [p2-02-implementation.md](../plans/p2-02-implementation.md) dòng `361–371`; **Merged** PR `#45` (`97735e7`). | Approval: `IMPLEMENTED_BUT_NOT_CONFIRMED` · Impl: **Merged** · Conflict: none | `P2-02` (đã merge) → Follow-up **`P3-05`** (luồng đổi SĐT khi có account), **`P2-14`** (lưu ý quyền `GET admin/tags` đòi `customers.write`). | `Clarification only` |
| **`P2-05`** (`GAP-P205-01=A`, `ASSUMPTION-P205-01=B`, `ASSUMPTION-P205-02=A`) | Voucher tính ra `0đ` → `422 voucher_min_order` (không tăng `used_count`/redemption); chỉ mở rộng `ReconcileInventory` và `InventoryAlertHandler` đối soát `inventory.reservations` mà không đổi DTO/OpenAPI `GET admin/inventory/alerts`; chỉ cho bán biến thể có `product.status = 'active' && variant.status = 'active'`. | Đề xuất của Lead (`23/11`); [p2-05-implementation.md](../plans/p2-05-implementation.md) dòng `420–422`; **Merged** PR `#46` (`862fbf0`). | Approval: `IMPLEMENTED_BUT_NOT_CONFIRMED` · Impl: **Merged** · Conflict: none | `P2-05` (đã merge) → nền cho `P2-06`..`P2-10`, `P3-02`. | `Clarification only` |
| **`P2-13`** (`GAP-01=1B`, `GAP-02=2A`, `GAP-03=3A`, `ASSUMPTION-01=A`, `ASSUMPTION-02`) | Digest `08:00` đọc trực tiếp 6 chỉ số từ bảng `P2-01`; cửa sổ `expiring` = `24h` (`now < payment_due_at <= now + 24h`) và loại trừ khỏi nhóm chờ xác nhận; Telegram tức thời có watermark cho `reconciliation_mismatch` và outbox `dead` (trừ `NotificationDelivery`), `low_stock` chỉ đưa vào digest `08:00`; `notify.telegram_chat_id` rỗng/`null` = tắt kênh Telegram; SMTP thuần BCL không thêm NuGet (từ chối cổng `465`, dùng STARTTLS `587`). | Đề xuất của Lead (`23/11`); [p2-13-implementation.md](../plans/p2-13-implementation.md) dòng `10–15`; **Merged** PR `#47` (`591f872`). | Approval: `IMPLEMENTED_BUT_NOT_CONFIRMED` · Impl: **Merged** · Conflict: none | `P2-13` (đã merge) → `P2-06` (tái dùng `OrderPaymentExpiringWindow = 24h`), `P3-06` (email khách hàng), `P3-10` (xem xét cổng `465` nếu SMTP provider yêu cầu). | `Clarification only` |
| **`P2-03`** (`ASSUMPTION-P203-01..03 = A`) | `dryRun=false` không đòi preview token, re-validate dưới transaction + `pg_advisory_xact_lock`, import nguyên tử các dòng hợp lệ và bỏ qua dòng `duplicate_in_file`/`existing_phone_skipped`/`error` (gắn warning `zalo_only_reimport_risk` cho dòng chỉ có `zalo_name`); cột `note` tạo `crm.interactions(kind='note', channel='other')` không cập nhật `last_contacted_at`; cột `tags` tách theo `,`/`;`, tự tạo tag mới; `.xlsx` đọc sheet duy nhất hoặc sheet `KhachHang`. | Đề xuất của Lead (`25/11`); mã trên `D:\tra-hien-p2-03` (`CustomerImport.cs`, `p2-03-implementation.md:385–387`, **chưa merge**, đang G4). | Approval: `IMPLEMENTED_BUT_NOT_CONFIRMED` · Impl: **In-flight (unmerged)** · Conflict: xem rủi ro re-import `O4` | `P2-03` (in-flight) → `P2-04` (kế thừa `[Audited] Interaction`), `P2-14`. | `Clarification only` |
| **`P2-07`** (`GAP-P207-01=B`, `ASSUMPTION-P207-01..04 = A`) | Giữ nguyên công thức chữ `BR-PAY-02` và điều kiện `Order.MarkShipping` (`Order.cs:569` đòi `PaymentStatus == Paid` cho chuyển khoản); chặn `void` làm `net < 0` (`409 payment_has_refunds`); `GET admin/payments/awaiting` bắt buộc `type`, phân trang `<= 100`, join shipment `delivered` mới nhất; trần refund gắn `returnRequestId` đúng chữ `BR-RET-02` (không cộng `shipping_fee_vnd`, không trừ `returned_quantity`); `BR-PAY-03` phát `OrderConfirmed(ReasonCode = "auto_confirmed_on_full_payment")` qua `CompositeOrderOutboxHandler`. | Lead tự điền vào prompt G3 (`25/11`); mã trên `D:\tra-hien-p2-07` (`OrderPaymentStatusCalculator.cs`, `p2-07-implementation.md:36–41, 369–399`, **chưa merge**, đang G4 + **HOLD publish**). | Approval: `IMPLEMENTED_BUT_NOT_CONFIRMED` · Impl: **In-flight (unmerged)** · Conflict: **`CONFLICT` (`CONF-P206-P207-01` & `O1`–`O3`)** | `P2-07` (**HOLD publish**) → `P2-06`, `P2-08`, `P2-09`, `P2-10`, `P3-06`. | `Clarification only` (chỉ cần `Amend docs/spec/04-business-rules.md:189` nếu Owner chọn phương án C cho `GAP-P207-01`). |

---

## 6. Các quyết định `CONFLICT`

### `CONF-P206-P207-01` — Xung đột hợp đồng thanh toán dùng chung giữa `P2-06` và `P2-07`

- **Trạng thái:** `CONFLICT` (Approval: chưa chốt · Implementation: `P2-07` đã code in-flight chưa merge, `P2-06` mới là `PLAN_READY` chưa G3).
- **Bằng chứng đã kiểm chứng trực tiếp (`VERIFIED_IN_CURRENT_SESSION`):**
  - `D:\tra-hien-p2-07\backend\src\TeaPremium.Domain\Orders\OrderPaymentStatusCalculator.cs` dòng `5–63` (snapshot tại `2026-11-25 18:47 +07:00`).
  - `D:\tra-hien-p2-06\docs\plans\p2-06-implementation.md` dòng `108–112`, `204–246`, `374–376`.
  - [07-roadmap.md](../spec/07-roadmap.md) dòng `91–92`; [04-business-rules.md](../spec/04-business-rules.md) dòng `159, 165, 183–194`; [05-api.md](../spec/05-api.md) dòng `179`; `backend/src/TeaPremium.Domain/Orders/Order.cs:569`.
- **Chi tiết 3 điểm mâu thuẫn (cộng liên hệ `O3`):**
  1. **Trùng quyền sở hữu & khác chữ ký `OrderPaymentStatusCalculator`:** [07-roadmap.md](../spec/07-roadmap.md) dòng `92` đặt `OrderPaymentStatusCalculator` ở `P2-07`, nhưng plan `P2-06` (`p2-06-implementation.md:208–235`) cũng dự kiến tự tạo lớp cùng tên với kiểu trả về `PaymentStatusCalculation` (`4` trường), trong khi mã thực tế của `P2-07` (`OrderPaymentStatusCalculator.cs:5–14`) dùng `OrderPaymentCalculation` (`9` trường: `Status`, `ConfirmedPaidVnd`, `RefundedVnd`, `NetPaidVnd`, `TotalVnd`, `RemainingToPayVnd`, `OverpaidVnd`, `IsOverpaid`, `NeedsRefund`) và bổ sung `CalculateReturnLinesMaxRefundVnd`.
  2. **Xử lý `delivery_failed` (`O1`):** `BR-ORD-03` (`04-business-rules.md:165`) khẳng định `delivery_failed` là trạng thái cuối (giao lại = tạo đơn mới). Mã `P2-07` (`OrderPaymentStatusCalculator.cs:43, 51`) định nghĩa `isUnsuccessfulTerminal = orderStatus is Cancelled or DeliveryFailed` và bật `NeedsRefund = true` khi `delivery_failed && netPaidVnd > 0`; ngược lại plan `P2-06` (`p2-06-implementation.md:110, 230–232`, `ASSUMPTION-P206-02`) chỉ xét `isCancelled && net > 0`.
  3. **Quan hệ giữa `NeedsRefund` và `IsOverpaid` (`O2`):** Mã `P2-07` (`OrderPaymentStatusCalculator.cs:50–51`) đặt `NeedsRefund = netPaidVnd > totalVnd` cho đơn còn hoạt động, khiến `NeedsRefund` và `IsOverpaid` **đồng thời `true`** khi thu thừa và cùng về `false` sau khi hoàn đủ phần dư; trong khi plan `P2-06` (`p2-06-implementation.md:110–111, 231–232`) đặt hai cờ loại trừ nhau. Ngoài ra, pure calculator không truy cập `sales.return_requests`, nên ở `P2-10` bộ lọc `GET admin/orders?flag=needs_refund` sẽ cần nối thêm nhánh kiểm `return_requests(status = 'approved', resolution = 'refund')`.
  4. **Hệ quả liên quan với xuất kho (`O3` / `GAP-P207-01`):** Xem Mục 10 (`O3`) về tương tác giữa `BR-PAY-02` (`partially_refunded`) và `Order.MarkShipping` (`Order.cs:569`).
- **Task bị ảnh hưởng & HOLD:**
  - **HOLD publish `P2-07`** (chờ G4 verdict cuối và Owner chốt `O1`–`O3`).
  - **HOLD G3 `P2-06`** (chờ chốt `O1`–`O3`, `O6a`, quyền sở hữu `OrderPaymentStatusCalculator` và rebase sau `P2-07` nếu `P2-07` sở hữu calculator).
  - **Cảnh báo phụ thuộc `P2-08`:** cần chốt `O3` (`Order.MarkShipping`) và tiêu thụ `OrderPaymentStatusCalculator` thống nhất trước khi vào G3 `P2-08`.
- **Spec Impact:** `Clarification only` (hoặc `Amend docs/spec/04-business-rules.md` nếu Owner sau này chọn sửa công thức `BR-PAY-02` hoặc ghi rõ định nghĩa hai cờ).

---

## 7. Các quyết định `PROPOSED`

Tất cả các mục dưới đây mới là đề xuất phân tích trong hồ sơ kế hoạch của các task chưa vào G3; **KHÔNG PHẢI `OWNER_APPROVED`** và phải được xác minh lại trên baseline `origin/main` mới nhất trước khi task tương ứng vào G3 (`O6a`, `O7`).

| Decision ID | Nội dung đề xuất | Nguồn bằng chứng (`VERIFIED_IN_CURRENT_SESSION`) | Task áp dụng & Dependency | Spec Impact |
|---|---|---|---|---|
| `PROP-P206-SCOPE` (`O6a`) | Plan `P2-06` đề xuất kéo `ExpireUnpaidOrders` (`BR-ORD-05`) và `POST .../extend-payment` (`BR-ORD-06`) từ `P3-03` (`07-roadmap.md:125`) lên làm sớm tại `P2-06`. Đối chiếu `BR-ORD-04` (`04-business-rules.md:166`): đơn Admin không có `payment_due_at`, không tự hủy; chỉ đơn web (`P3-02`) mới tạo `payment_due_at` → **không có dependency kỹ thuật bắt buộc phải kéo sớm**. Hướng đề xuất hiện xem xét: ưu tiên giữ ở `P3-03` theo roadmap trừ khi Owner duyệt ngoại lệ. | `D:\tra-hien-p2-06\docs\plans\p2-06-implementation.md:31, 67`; [07-roadmap.md](../spec/07-roadmap.md) dòng `91, 125`; [04-business-rules.md](../spec/04-business-rules.md) dòng `166–168`. | `P2-06` (HOLD G3), `P3-03`. | `Clarification only` (giữ nguyên `07-roadmap.md`). |
| `P2-04` (`GAP-P204-01`, `ASSUMPTION-P204-01..03`) | Cấm tạo/sửa thủ công `kind = 'campaign_message'` trên `POST/PATCH .../interactions`; định nghĩa tích lũy giờ VN cho `due = overdue \| today \| week`; `POST` dùng `GREATEST(last_contacted_at, occurred_at)` và `PATCH` tính lại `MAX`; chặn ghi trên khách `erased`/`merged`, kiểm `orderId` thuộc đúng `customerId`, không dung sai clock skew, giữ `GET .../campaigns` ở `P5-02`. | `D:\tra-hien-p2-04\docs\plans\p2-04-implementation.md:304–307`. | `P2-04` (`PLAN_READY`; dep `P2-02` đã merge, phối hợp `[Audited] Interaction` với `P2-03`). | `Clarification only` |
| `P2-06` (`ASSUMPTION-P206-01, 03, 04, 05`) | `PATCH` nhận thêm `discountNote?` và giữ `shippingFeeVnd` của đơn Admin khi tỉnh lược; `reorder` cho phép từ cả 3 trạng thái cuối (`delivered`, `cancelled`, `delivery_failed`) và đổi `salesChannel='web'` sang `'other'`; `POST admin/orders` trả `AdminOrderCreatedView` `0` PII cho bảng idempotency; đơn backfill giữ `payment_status = 'unpaid'` đúng dẫn xuất `BR-PAY-02`. *(Riêng `ASSUMPTION-P206-02` đã chuyển thành xung đột `CONF-P206-P207-01` ở Mục 6.)* | `D:\tra-hien-p2-06\docs\plans\p2-06-implementation.md:371–385`. | `P2-06` (`PLAN_READY` / HOLD G3). | `Clarification only` |
| `P2-08` (`GAP-P208-01..02`, `ASSUMPTION-P208-01`) | Giữ seam Domain `Shipment.Cancel()` nhưng không mở HTTP `cancelled` ở `P2-08`; cho phép chuyển tiến không lùi tới `in_transit \| delivered \| failed`; cho phép `returnCostVnd?` trong `POST admin/shipments/{id}/events` khi `status = 'returned'`; ghi dòng khởi tạo `shipment_events('created')` lúc tạo vận đơn (`201 Created`). | `D:\tra-hien-p2-08\docs\plans\p2-08-implementation.md:300–319`. | `P2-08` (`PLAN_READY`; dep `P2-05` đã merge; phối hợp `O3` và `OrderPaymentStatusCalculator` của `P2-07`). | `Clarification only` |
| `P2-12` (`ASSUMPTION-P212-01..03`) | Cho phép `rowVersion` tùy chọn trên `PUT consents`, `POST merge`, `POST erase` (có gửi thì kiểm `xmin`); `GET .../export` chỉ trả `application/json` và ghi `audit_logs(action='access')` (không tự sinh `crm.data_requests`); khi `erase` khách `A` thì ẩn danh luôn mọi dòng stub `S` có `S.merged_into_customer_id == A`. *(Cũng lưu ý bàn giao `R-P204-01` về ẩn danh `follow_up_result`.)* | `D:\tra-hien-p2-12\docs\plans\p2-12-implementation.md:328–330`; `D:\tra-hien-p2-04\docs\plans\p2-04-implementation.md:314–316`. | `P2-12` (`PLAN_READY`; dep `P2-02` đã merge). | `Clarification only` |

---

## 8. Các quyết định `UNRESOLVED`

| Decision ID | Nội dung chưa giải quyết | Bằng chứng & Mức kiểm chứng | Task chịu tác động / Gate | Spec Impact |
|---|---|---|---|---|
| `GAP-P212-01` (`O6b`) | `BR-CRM-12` (`DataRetentionJob`, `04-business-rules.md:235`, `03-database.md:193, 226–233`) chưa được gán vào dòng task nào trong [07-roadmap.md](../spec/07-roadmap.md). Các bảng đã có gồm `sales.carts`, `platform.notification_logs`, `platform.idempotency_keys`, `media.upload_sessions`; chưa có `platform.web_vitals_samples` (`P3-16`) và `marketing.link_clicks` (`P5-01`). **Chưa chốt task sở hữu và tuyệt đối không chạy retention xóa dữ liệu thật** khi chưa chốt điều kiện nghiệp vụ và vận hành. | `D:\tra-hien-p2-12\docs\plans\p2-12-implementation.md:327`; `04-business-rules.md:235`; `03-database.md:193`; `07-roadmap.md` (`VERIFIED_IN_CURRENT_SESSION`). | Chốt trước G3 `P2-12`; liên quan `P3-01`, `P3-12`, `P3-16`, `P5-01`. | `Amend docs/spec/07-roadmap.md` (hoặc gán rõ khi Owner quyết định — chưa duyệt). |
| `G24` / `Q1–Q8` / P0 Production Acceptance / Closure `#2` | Xác minh pháp lý Việt Nam (Nghị định 13/2023/NĐ-CP, Luật Bảo vệ dữ liệu cá nhân 2025, thời hạn phản hồi `export`/`erase`, tính hợp lệ bằng chứng `consent`, nghĩa vụ lưu chứng từ thuế/kế toán, DPIA/chuyển dữ liệu ra nước ngoài); chốt vận hành `Q1`–`Q8` (`00-README.md:72–79`); nghiệm thu thực tế HTTPS, tài khoản owner thật đổi mật khẩu + TOTP, backup mã hóa + restore drill (P0); MFA recovery CLI (`Closure #2`, đã hoãn theo `ADR-B01`). | [spec-gaps.md](../analysis/spec-gaps.md) dòng `59, 63–65`; [ADR-B01](ADR-B01-right-sized-operations.md); [task-readiness.md](../plans/task-readiness.md) dòng `105–107`; `p2-12-implementation.md:346–350` (`VERIFIED_IN_CURRENT_SESSION`, runtime `NOT_RUN`). | Gate bắt buộc trước dữ liệu thật / go-live (`P3-10`, `P3-12`); không chặn lập trình cục bộ trên DB giả. | External / Legal / Operational verification (không tự kết luận tuân thủ pháp lý). |

---

## 9. Các quyết định `SUPERSEDED`

- **Trong phạm vi Phase 2 Knowledge Recovery:** Chưa có quyết định `OWNER_APPROVED` nào của Phase 2 bị `SUPERSEDED` (`0` mục). Bản nháp `OrderPaymentStatusCalculator` trong plan `P2-06` đang ở trạng thái **`CONFLICT`** với mã `P2-07` (`CONF-P206-P207-01`), **chưa** chuyển sang `SUPERSEDED` cho tới khi Owner chốt `O1`–`O3`.
- **Lịch sử trước Phase 2 (ghi nhận để tham chiếu đầy đủ):** Phần `D8` (MFA recovery CLI / `>= 2` owner bắt buộc), `D9` (chờ Closure `#2` và manual HTTPS trước DDL giả `P1-01`) và điều chỉnh phạm vi `D12` của [ADR-PROPOSAL-A01](ADR-PROPOSAL-A01-p0-closure-p1-owner-decisions.md) đã được `SUPERSEDED` có phạm vi bởi [ADR-B01](ADR-B01-right-sized-operations.md) (Owner duyệt ngày `18/11/2026`, merge PR `#23`/`#24`).

---

## 10. Tổng hợp các quyết định Owner chưa chốt `O1`–`O7` (KHÔNG PHẢI `OWNER_APPROVED`)

Mọi mục `O1`–`O7` dưới đây đang ở trạng thái **chưa chốt**; PR-1 chỉ ghi nhận phân tích kỹ thuật đã kiểm chứng để Owner ra quyết định riêng, **không chặn PR-1** và không tự biến đề xuất thành đặc tả.

1. **`O1` — `NeedsRefund` và `delivery_failed` (`CONFLICT`):**
   - `BR-ORD-03` (`04-business-rules.md:165`) xác nhận `delivery_failed` là trạng thái cuối (muốn giao lại phải tạo đơn mới qua `reorder`).
   - Đề xuất đang xem xét: bật `NeedsRefund = true` cho cả `cancelled` và `delivery_failed` khi `netPaidVnd > 0`; `NeedsRefund` chỉ là cờ cảnh báo nghiệp vụ để nhân viên xử lý hoàn tiền, không tự tạo lệnh hoàn tiền.
   - Trạng thái: chưa chốt; xung đột trực tiếp giữa `P2-07` (`OrderPaymentStatusCalculator.cs:43, 51`) và plan `P2-06` (`p2-06-implementation.md:110, 230–232`).
2. **`O2` — `NeedsRefund` và `IsOverpaid` đồng thời + follow-up `P2-10` (`CONFLICT`):**
   - Đề xuất đang xem xét: trên đơn còn hiệu lực có `netPaidVnd > totalVnd`, cả `NeedsRefund` và `IsOverpaid` cùng `true` và cùng tắt về `false` sau khi hoàn đủ phần thu thừa (`netPaidVnd == totalVnd`).
   - Follow-up bắt buộc nếu chọn hướng này: tại `P2-10`, pure calculator không đọc `sales.return_requests`, nên bộ lọc `GET admin/orders?flag=needs_refund` phải nối thêm nhánh `EXISTS` cho đơn `delivered` có `return_requests(status = 'approved', resolution = 'refund')`.
   - Trạng thái: chưa chốt; đối nghịch với công thức loại trừ nhau trong plan `P2-06` (`p2-06-implementation.md:110–111, 231–232`).
3. **`O3` — `PaymentStatus`, hoàn tiền thừa trước khi xuất kho và trần hoàn phí vận chuyển (`IMPLEMENTED_BUT_NOT_CONFIRMED` / `CONFLICT` biên):**
   - **Vấn đề `MarkShipping` (`GAP-P207-01`):** `Order.MarkShipping` (`backend/src/TeaPremium.Domain/Orders/Order.cs:569`) kiểm `PaymentMethod != Cod && PaymentStatus != Paid`. Vì `BR-PAY-02` (`04-business-rules.md:189`) đặt `payment_status = 'partially_refunded'` ngay khi `refunded > 0` (kể cả khi khách trả thừa rồi shop hoàn phần thừa làm `netPaidVnd == totalVnd`), việc hoàn tiền thừa **trước khi xuất kho** sẽ bị `Order.MarkShipping` chặn nếu giữ nguyên cả hai. Cần Owner chốt giữa: (A) giữ `BR-PAY-02`, nới `Order.MarkShipping` ở `P2-08` chấp nhận `PaymentStatus == Paid || netPaidVnd >= TotalVnd`; (B) giữ nguyên cả hai (quy ước vận hành: xuất kho trước, hoàn phần thừa sau — hiện `P2-07` đang code theo B); hoặc (C) amend `BR-PAY-02` (`04-business-rules.md:189`).
   - **Vấn đề trần hoàn tiền & phí vận chuyển (`ASSUMPTION-P207-03`):** `BR-RET-02` (`04-business-rules.md:213`) giới hạn trần hoàn theo giá trị dòng hàng trả sau phân bổ giảm giá (không cộng `shipping_fee_vnd`). Cần xác nhận chính sách hoàn phí ship khi trả 100% đơn (tách lệnh refund độc lập không gắn `returnRequestId` như `P2-07` đang code, hay mở rộng trần khi trả toàn bộ dòng).
   - **Các giả định khác của `P2-07`:** `ASSUMPTION-P207-01` (chặn void làm `net < 0`), `ASSUMPTION-P207-02` (`awaiting` bắt buộc `type`), `ASSUMPTION-P207-04` (`OrderConfirmed` outbox) đều ở trạng thái `IMPLEMENTED_BUT_NOT_CONFIRMED`.
4. **`O4` — `P2-03` Customer Import & rủi ro re-import dòng chỉ có `zalo_name` (`IMPLEMENTED_BUT_NOT_CONFIRMED`):**
   - Kiểm chứng trực tiếp tại `D:\tra-hien-p2-03\backend\src\TeaPremium.Application\Crm\CustomerImport.cs:252–304`: các dòng hợp lệ có `phone` khi import lại sẽ bị bỏ qua (`existing_phone_skipped`, không tạo trùng khách hay trùng interaction).
   - **Rủi ro đã xác minh:** Dòng chỉ có `zalo_name` (`phone == null`) không có khóa duy nhất ở DB; khi nhân viên tải lại cùng tệp (ví dụ sau khi sửa vài dòng lỗi), các dòng zalo-only sẽ **tạo trùng hồ sơ khách hàng và tạo trùng `crm.interactions(kind='note')`**, dù kết quả có gắn `warnings = ["zalo_only_reimport_risk"]`.
   - Không tự thêm `UNIQUE` lên `zalo_name` và không tự chế cơ chế deduplication; giữ `ASSUMPTION-P203-01..03` ở trạng thái chưa được Owner xác nhận để Owner/G4 cân nhắc trước publish `P2-03`.
5. **`O5` — Các quyết định đã merge thiếu xác nhận tường minh (`IMPLEMENTED_BUT_NOT_CONFIRMED`):**
   - Không nâng đồng loạt các nhóm quyết định của `P1-13`, `P2-02`, `P2-05`, `P2-13` (đã merge) sang `OWNER_APPROVED`; lưu giữ đúng trạng thái `IMPLEMENTED_BUT_NOT_CONFIRMED` kèm commit/PR tại Mục 5.
6. **`O6` — Phạm vi và lộ trình (`PROPOSED` / `UNRESOLVED`):**
   - `O6a` (`PROP-P206-SCOPE`): Đề xuất giữ `ExpireUnpaidOrders` và `extend-payment` ở `P3-03` theo `07-roadmap.md:125`; chỉ kéo sớm lên `P2-06` khi có dependency bắt buộc và phê duyệt tường minh của Owner.
   - `O6b` (`GAP-P212-01`): `DataRetentionJob` (`BR-CRM-12`) chưa gán dòng roadmap và chưa chốt điều kiện nghiệp vụ/vận hành; không chạy xóa dữ liệu thật.
7. **`O7` — Các `GAP`/`ASSUMPTION` của task chưa vào G3 (`PROPOSED` / `UNRESOLVED`):**
   - Giữ đúng trạng thái `PROPOSED`/`UNRESOLVED` cho toàn bộ mục biên của `P2-04`, `P2-06`, `P2-08`, `P2-12` (Mục 7 & Mục 8); xác minh lại trên baseline `origin/main` phù hợp khi từng task thực sự vào G3.

---

## 11. Bảng Shared Contracts & Ownership

Kiểm chứng trực tiếp từ mã nguồn trên `origin/main` (`591f872`) và hai worktree in-flight (`D:\tra-hien-p2-03`, `D:\tra-hien-p2-07`) tại `2026-11-25 18:48 +07:00` (`VERIFIED_IN_CURRENT_SESSION`):

| # | Shared Contract / Asset | Đường dẫn thực tế đã kiểm chứng | Task sở hữu (Owner) | Trạng thái Ownership & Merge | Task tiêu thụ (Consumers) | Ranh giới & Điều kiện phối hợp |
|---:|---|---|---|---|---|---|
| 1 | `OrderPaymentStatusCalculator` + `Order.ApplyPaymentCalculation` + `IOrderPaymentLedgerReader` + `CalculateReturnLinesMaxRefundVnd` | `D:\tra-hien-p2-07\backend\src\TeaPremium.Domain\Orders\OrderPaymentStatusCalculator.cs`, `PaymentContracts.cs` | **`P2-07`** *(theo `07-roadmap.md:92`, đang **xung đột** với plan `P2-06`)* | **`CONFLICT` / In-flight (chưa merge, HOLD publish)** | `P2-06`, `P2-08`, `P2-09`, `P2-10` | `P2-06` không được tạo bản `OrderPaymentStatusCalculator` thứ hai; công thức SQL cho `?flag=needs_refund\|overpaid` phải đồng nhất toán học với Domain calculator sau khi Owner chốt `O1`–`O3`. |
| 2 | `CompositeOrderOutboxHandler` & `IOrderOutboxStep` | `backend/src/TeaPremium.Infrastructure/Orders/CompositeOrderOutboxHandler.cs`, `Application/Orders/OrderOutbox.cs` | **`P2-05`** | **`OWNER_APPROVED` (`G22`) · Merged (`862fbf0`)** | `P2-06`, `P2-07`, `P2-08`, `P2-11`, `P2-13` (đã gắn), `P3-06`, `P5-05` | Mỗi sự kiện `Order*` chỉ có đúng `1` `IOutboxHandler` tổng; module khác tuyệt đối không đăng ký `IOutboxHandler` trùng mà chỉ đăng ký `IOrderOutboxStep`. |
| 3 | `ICatalogInventoryRead` & `IProductSoldOutSync` (`BR-SEO-21`) | `backend/src/TeaPremium.Application/Inventory/ICatalogInventoryRead.cs`, `Application/Catalog/IProductSoldOutSync.cs`, `Infrastructure/Catalog/EfProductSoldOutSync.cs` | **`P2-05`** | **`OWNER_APPROVED` (`BR-SEO-21`) · Merged (`862fbf0`)** | `P2-08`, `P2-10`, `P3-13` | `P2-08` (khi shipment `returned`, `ref_type='order'`) và `P2-10` (khi `return_restock`, `ref_type='return'`) gọi đồng bộ xóa `sold_out_at` + phát `StorefrontInvalidate`. |
| 4 | `[Audited] Interaction` + `AuditValuePolicy("crm.interactions")` | `D:\tra-hien-p2-03\backend\src\TeaPremium.Infrastructure\Persistence\{CrmSalesFinanceEntities.cs, AuditValuePolicy.cs}` | **`P2-03`** *(dự kiến nếu merge trước `P2-04`)* | **Đề xuất / In-flight (chưa merge)** | `P2-04`, `P2-12`, `P5-02` | Allowlist fail-closed che cứng `content` và `follow_up_result`; `P2-04` tái sử dụng trực tiếp nếu `P2-03` merge trước (tránh trùng chỉnh sửa `AuditValuePolicy`). |
| 5 | `TabularImportFile` (`TeaPremium.Application.Import`) | `D:\tra-hien-p2-03\backend\src\TeaPremium.Application\Import\TabularImportFile.cs` | **`P2-03`** *(dự kiến)* | **Đề xuất / In-flight (chưa merge)** | `P1-07` / `P1-08` (qua facade `ProductImportFile`), `P2-03` | Giữ nguyên giới hạn `500` dòng cho `P1-07`/`P1-08`, mở `2.000` dòng cho `P2-03`; bảo toàn toàn bộ chốt chặn formula/macro/zip-bomb/XML-bomb. |
| 6 | `CrmModuleBoundaryRule` (IL `Mono.Cecil`) | `D:\tra-hien-p2-03\backend\tests\TeaPremium.ArchitectureTests\CrmModuleBoundaryRule.cs` | **`P2-03`** *(dự kiến)* | **Đề xuất / In-flight (chưa merge)** | Toàn bộ `TeaPremium.Infrastructure.Crm` (`P2-04`, `P2-11`, `P2-12`) | Đóng carry-over `[P2]` của G4 `P2-02`; mọi mã mới trong `Infrastructure.Crm` không được chạm trực tiếp entity ngoài schema `crm` (trừ `AuditLog`). |
| 7 | `IRefundRecordedHook` & `returnRequestId` flow (`G04` + `G29`) | `D:\tra-hien-p2-07\backend\src\TeaPremium.Application\Payments\PaymentContracts.cs` | **`P2-07`** *(dự kiến)* | **Nguyên tắc `OWNER_APPROVED` (`G04+G29`) · Asset In-flight (chưa merge, HOLD publish)** | `P2-10`, `P2-11`, `P5-05` | Chuyển `return_requests.status = 'resolved'` cùng transaction ghi `sales.refunds`; điểm mở rộng cho cập nhật chỉ số khách (`P2-11`) và loại referral (`P5-05`). |
| 8 | `INotificationChannel` & `NotificationTemplateNames` | `backend/src/TeaPremium.Application/Notifications/{INotificationChannel.cs, NotificationContracts.cs}` | **`P2-13`** | **Merged (`591f872`); chi tiết `GAP-01..03` `IMPLEMENTED_BUT_NOT_CONFIRMED`** | `P3-06` (email khách hàng), `P5-05` (`referral_reward_customer`), `P2-06` (`OrderPaymentExpiringWindow = 24h`) | `P2-13` chỉ gửi kênh nội bộ shop (`telegram`, `shop_emails`); các khóa template khách hàng đã đặt chỗ sẵn trong `NotificationTemplateNames` cho `P3-06`. |

---

## 12. Cross-Task Dependencies và Follow-Ups (Phase 2–5 & Workflow V2)

- **Phase 2 (`P2-03`..`P2-16`):**
  - `P2-03` → cần hoàn tất G4 độc lập và Owner xem xét rủi ro `O4` (zalo-only re-import) trước publish; nếu merge trước `P2-04`, `P2-04` rebase để tái sử dụng `[Audited] Interaction` và `CrmModuleBoundaryRule`.
  - `P2-07` → **HOLD publish** chờ G4 verdict cuối và Owner chốt `O1`–`O3`.
  - `P2-06` → **HOLD G3** chờ chốt `O1`–`O3`, `O6a`, `O7` (`ASSUMPTION-P206-01,03,04,05`) và bỏ bản `OrderPaymentStatusCalculator` trùng với `P2-07`; áp sửa 2 dòng `05-api.md:181,183` (`G11 = A`) và đồng bộ `OrderPaymentExpiringWindow = 24h` với `P2-13`.
  - `P2-08` → cần chốt `O3` (`Order.MarkShipping` tại `Order.cs:569`), đồng bộ `OrderPaymentStatusCalculator` từ `P2-07` cho `cod_amount_vnd`, áp `G16` (`ref_type='order'`), `BR-SEO-21` (xóa `sold_out_at` khi `returned`), `G22` (`payments(cod, awaiting_settlement)` cùng transaction `delivered`), seam `G30` và `O7` (`GAP-P208-01..02`, `ASSUMPTION-P208-01`).
  - `P2-09` → chờ `P2-07` và `P2-08` merge; tái dùng `OrderPaymentStatusCalculator` khi xác nhận hàng loạt COD.
  - `P2-10` → chờ `P2-07` và `P2-08` merge; áp `G04+G29`, `G27` (cần chốt diff sửa spec trước), `G30`, `BR-SEO-21` (khi `return_restock`), nối `EXISTS return_requests(approved, refund)` vào bộ lọc `flag=needs_refund` (`O2`), và làm rõ hoàn phí ship (`O3`).
  - `P2-11` → chờ `P2-08` merge; đăng ký `IOrderOutboxStep` cho `OrderDelivered` (bao gồm đơn backfill `ReasonCode="backfilled"` từ `P2-06`) và hook hoàn tiền `IRefundRecordedHook`.
  - `P2-12` → chốt `GAP-P212-01` (`O6b`) và `ASSUMPTION-P212-01..03` (`O7`), kết hợp `G17a`/`G17b+G28` và bàn giao `R-P204-01` (ẩn danh `follow_up_result`).
  - `P2-14` → chờ `P2-02` (merged), `P2-03`, `P2-04`; lưu ý quyền `GET admin/tags` đòi `customers.write` theo `GAP-P202-02=B` (đang `IMPLEMENTED_BUT_NOT_CONFIRMED`).
  - `P2-15` → chờ `P2-05..P2-10`; `P2-16` → chờ `P2-15`.
- **Phase 3 (`P3-01`, `P3-03`, `P3-05`, `P3-06`, `P3-10`, `P3-12`, `P3-13`, `P3-16`):**
  - `P3-01`/`P3-16`: điểm tiếp nhận tiềm năng cho `DataRetentionJob` (`GAP-P212-01`) nếu Owner chọn giữ `P2-12` gọn 4 API.
  - `P3-03`: sở hữu `ExpireUnpaidOrders` & `extend-payment` theo roadmap (`O6a`), trừ khi Owner duyệt ngoại lệ.
  - `P3-05`: xử lý đổi/đồng bộ SĐT khi khách đã có `crm.customer_accounts` (follow-up `GAP-P202-01=C`) và tôn trọng `history_unlocked_at` sau merge (`G17b+G28`).
  - `P3-06`: tái dùng `INotificationChannel`/`NotificationTemplateNames` từ `P2-13` và gắn `IOrderOutboxStep` nhận `OrderConfirmed`/`OrderPlaced`/`OrderShipped`/`OrderCancelled`.
  - `P3-10`/`P3-12`: cổng nghiệm thu dữ liệu thật/go-live (`B01`, `G24`, `Q1–Q8`), đối chiếu cổng SMTP `465` nếu provider yêu cầu, và quy trình tránh hồi sinh PII khi restore backup cũ sau `erase`.
  - `P3-13`: vòng đời SEO 180 ngày / archive + 301 cho hàng độc bản đã bán trên bằng chứng `BR-SEO-21`.
- **Phase 4 (`P4-02`):** Công thức P&L `BR-FIN-02` tách bạch `return_restock` `ref_type='order'` (`G16`) với `ref_type='return'` (`P2-10`) và loại mặc định đơn `is_backfilled = true` (`BR-ORD-09`).
- **Phase 5 (`P5-01`, `P5-02`, `P5-05`, `P5-07`):** Tạo schema `marketing.*` + bổ sung 4 FK đã hoãn (`G03`); triển khai `IMarketingCustomerPrivacyPort` xử lý va chạm `UNIQUE(campaign_id, customer_id)` khi merge (`G17`); tái dùng `G12` và `IRefundRecordedHook` cho referral (`P5-05`); tạo `catalog.product_reviews`/`review_media` (`G21`) và ẩn danh review khi erase (`G20` tại `P5-07`).
- **Workflow V2 (`PR-2` & `PR-3`):** Chỉ ghi nhận phạm vi follow-up tại [task-readiness.md](../plans/task-readiness.md), chưa triển khai trong PR-1.

---

## 13. Danh mục Spec Amendments (đã áp / chờ áp / chưa phê duyệt)

PR-1 **không chỉnh sửa bất kỳ file nào trong `docs/spec/`**. Hiện trạng mọi amendment liên quan:

| Hạng mục | File mục tiêu trong `docs/spec/` | Trạng thái phê duyệt & áp dụng |
|---|---|---|
| `DEC-P110-01` (route `admin/inventory/alerts`) | `docs/spec/05-api.md:167` | `OWNER_APPROVED` (`STEP 438`) — **Đã áp tại `35d0333` (`P1-10`)** |
| `G03`, `G21` (`order_lines` DELETE & `fk_reservations_line`), `G17a` (`ck_customer_contact`) | `docs/spec/schema.sql`, `docs/spec/03-database.md:156` | `OWNER_APPROVED` (`STEP 462`) — **Đã áp tại `57cb13f` (`P2-01`)** |
| `G11 = A` (`callConfirmed`, `callNote?` trên 2 đường confirm) | `docs/spec/05-api.md:181, 183` | `OWNER_APPROVED` (`STEP 598`, cho phép sửa 1–2 dòng tại `P2-06`) — **Chưa áp (chờ G3 `P2-06`)** |
| `G27` (3 lý do đổi trả + bắt buộc `>= 1` bằng chứng cho cả 3, owner override) | `docs/spec/00-README.md:96` (`DEC-12`), `docs/spec/04-business-rules.md:211` (`BR-RET-01`) | `OWNER_APPROVED` **về nguyên tắc** (`STEP 462`) — **Chưa duyệt diff cụ thể**, xử lý tại `P2-10` |
| `G04 + G29` (cân chỉnh câu chữ luồng hoàn tiền 2 bước nếu cần) | `docs/spec/04-business-rules.md:213` (`BR-RET-02`) | **Chưa phê duyệt** (tùy chọn tại `P2-10`) |
| `O1` / `O2` / `O3` (nếu Owner chọn sửa `BR-PAY-02` hoặc ghi rõ định nghĩa cờ) | `docs/spec/04-business-rules.md:159, 184–194, 213` | **Chưa phê duyệt** (phụ thuộc quyết định Owner cho `O1`–`O3`) |
| `GAP-P212-01` (`O6b` — gán `BR-CRM-12` `DataRetentionJob` vào roadmap) | `docs/spec/07-roadmap.md` | **Chưa phê duyệt** (phụ thuộc quyết định Owner cho `O6b`) |

---

## 14. Giới hạn bằng chứng và điều kiện đánh giá lại (Re-evaluation Triggers)

### 14.1 Những giới hạn bằng chứng chưa xác minh được trong phiên PR-1

1. **Phase 2 Readiness Audit (`2026-11-22`):** Không có biên bản commit trong `docs/analysis/`; chỉ có bằng chứng gián tiếp qua phiên điều phối và câu trả lời xác nhận của Owner tại `STEP 462` → ghi nhận là `REPORTED_BY_OTHER_AGENT`.
2. **Trạng thái GitHub Actions CI cho PR `#42`–`#47`:** Phiên PR-1 chỉ xác minh merge commit trên `origin/main` cục bộ qua Git, không truy vấn trực tiếp GitHub Actions API cho từng PR `#42`–`#47` → giữ trạng thái CI của các PR đó là `UNKNOWN` trong phiên này (không tự ghi `CI PASS`).
3. **Verdict Independent G4 của `P2-03` và `P2-07`:** Cả hai worktree mới có log `verify-linux` cục bộ, chưa có báo cáo verdict G4 cuối đã chốt → giữ `UNKNOWN` (tuyệt đối không suy ra G4 PASS từ log verify).
4. **Nghiệm thu pháp lý, kế toán và vận hành thực (`G24`, `Q1–Q8`, `P0` real-data acceptance):** Vẫn là `NOT_RUN` ngoài repo cho tới khi có xác nhận chuyên môn và bằng chứng thực thi trước go-live.

### 14.2 Hệ quả (Consequences) và Phương án thay thế đã cân nhắc (Alternatives Considered)

- **Phương án thay thế đã cân nhắc cho gói Knowledge Recovery này:**
  1. *Chỉ giữ tri thức ở tài liệu tạm ngoài Git (`artifacts/handoff/`)* → **Loại bỏ**, vì `artifacts/` bị `.gitignore`, không đi qua G4/CI và không bền vững khi chuyển phiên Technical Lead hoặc mở máy/worktree mới.
  2. *Sửa trực tiếp `docs/spec/` và chốt luôn `O1`–`O7` trong PR-1* → **Loại bỏ**, vì vi phạm nguyên tắc thẩm quyền `AGENTS.md` (không sửa `docs/spec/` hay tự chốt nghiệp vụ khi chưa có phê duyệt tường minh của Owner) và làm phình phạm vi Tier B sang Tier A.
  3. *Ghi bền vững vào đúng 3 tài liệu `docs/decisions/ADR-P2-01-phase2-contracts.md`, `docs/plans/task-readiness.md`, `docs/analysis/spec-gaps.md` kèm tách bạch trạng thái phê duyệt / triển khai / xung đột* → **Phương án đã chọn**.
- **Hệ quả tích cực và đánh đổi:**
  - Mọi phiên Technical Lead, G3 và G4 mới đều có một nguồn tra cứu đã kiểm chứng trong Git mà không phụ thuộc lịch sử hội thoại cũ.
  - Ngăn việc nâng nhầm các quyết định `IMPLEMENTED_BUT_NOT_CONFIRMED` hoặc các đề xuất `O1`–`O7` thành đặc tả chính thức.
  - **Đánh đổi:** Snapshot worktree trong [task-readiness.md](../plans/task-readiness.md) là trạng thái tĩnh theo thời điểm (`2026-11-25 18:46 +07:00`), đòi hỏi phiên kế tiếp luôn tự chạy lệnh Git read-only để xác minh lại trước khi hành động; đồng thời `P2-07` (publish) và `P2-06` (G3) giữ nguyên trạng thái `HOLD` cho tới khi Owner chốt riêng `O1`–`O3`.

### 14.3 Điều kiện đánh giá lại (Re-evaluation Triggers)

Tài liệu này cần được cập nhật hoặc bổ sung ADR kế tiếp khi xảy ra bất kỳ sự kiện nào sau đây:

1. Owner chốt một hoặc nhiều mục trong `O1`–`O7` (đặc biệt `O1`–`O3` để gỡ HOLD cho `P2-07` và `P2-06`, hoặc `O4` cho `P2-03`).
2. Owner xác nhận hoặc yêu cầu điều chỉnh bất kỳ mục nào đang mang trạng thái `IMPLEMENTED_BUT_NOT_CONFIRMED` (`P1-13`, `P2-02`, `P2-05`, `P2-13`, `P2-03`, `P2-07`).
3. Có một vòng Full G1 `spec-audit` mới hoặc có amendment mới được merge vào `docs/spec/` làm thay đổi mốc `57cb13f`.
4. `P2-03` hoặc `P2-07` hoàn tất G4/publish/merge làm thay đổi trạng thái sở hữu hoặc hợp đồng dùng chung.
