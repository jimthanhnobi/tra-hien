# P1-13: Admin UI kho (tồn kho, sổ kho, phiếu nhập, điều chỉnh, kiểm kê, cảnh báo P1 — mobile-first)

- **Gate/status:** `READY_FOR_LOCAL_G4` (G3 triển khai hoàn tất theo kế hoạch & quyết định Owner 23/11/2026 trên `D:\tra-hien-p1-13`; chưa commit/push/PR).
- **Tier:** **B** — UI Admin tiêu thụ các API kho đã qua G4/CI (`P1-08`, `P1-09`, `P1-10`), **không** chỉnh sửa backend, database/migration, `packages/api-client`, hay auth/session/CSRF/permission guard nền (`providers.tsx`, `auth-forms.tsx`, `navigation.ts`, middleware `AdminApi`).
  - *Điều kiện nâng lên **Tier A**:* nếu owner yêu cầu sửa `apps/admin/src/lib/navigation.ts` hoặc guard tổng trong `AdminScreen` (ví dụ hỗ trợ tài khoản tùy biến có `inventory.write`/`inventory.approve` nhưng không có `inventory.read`).
- **Worktree/base:** `D:\tra-hien-p1-13`, nhánh `feature/p1-13-admin-inventory`, base `origin/main` `e63e90b99b596ba4e1d218d2a920733cbd79edbb` (merge PR #42 `feature/p1-10-inventory-alerts`). Không đụng `D:\tra-hien-p2-01`.
- **Readiness (scoped G1/G2):** `GO` (với 1 giới hạn hiển thị nhãn biến thể không chặn — xem §8).
  - Tái sử dụng G1 đầy đủ tại `cadce575597738bf40ad7c45689482de5a5fbc3d` ([spec-inventory.md](../analysis/spec-inventory.md), [spec-gaps.md](../analysis/spec-gaps.md)), G2 nền ([conformance-review.md](../architecture/conformance-review.md), [task-readiness.md](task-readiness.md) mốc B01 `dadc4435e8f115faaeb9e8511e56e09d5d517a8a`), và G1/G2 kho tại `99c6810b54d151d6a30334c9384524e3ba2b3552` ([p1-inventory-architecture.md](p1-inventory-architecture.md)).
  - Diff `docs/spec/` từ `99c6810..e63e90b` gồm 6 file (`02-architecture.md`, `03-database.md`, `04-business-rules.md`, `05-api.md`, `07-roadmap.md`, `09-seo.md`): bổ sung quyền PATCH độc bản (`P1-08`), hậu tố mã chứng từ `PN-/KK-` (`P1-09`), `GET /api/v1/admin/inventory/alerts` (`P1-10`), và Store Catalog (`P1-14`); không xung đột phạm vi P1-13.
  - Lưu ý tên file đặc tả: repo không có `docs/spec/06-security.md`; yêu cầu bảo mật/vận hành/header nằm tại `docs/spec/06-infrastructure.md` (§4, §7, §8) và `docs/spec/02-architecture.md` (§6–§7).
  - Dependencies trên `docs/spec/07-roadmap.md:62` đều đã merge trên `origin/main` (`e63e90b`): `P0-12`, `P1-05`, `P1-08` (PR #37 `667274c`), `P1-09` (PR #38 `a46ca4d`), `P1-10` (PR #42 `e63e90b`), `P1-11`, `P1-12` (PR #36 `b45558f`).

---

## 1. Truy vết đặc tả, phạm vi và ngoài phạm vi

- **ID roadmap:** `P1-13` (`docs/spec/07-roadmap.md:62`).
- **Yêu cầu chức năng (`docs/spec/01-requirements.md:77–86`):** `FR-INV-01` (xem tồn theo variant), `FR-INV-02` (phiếu nhập kho), `FR-INV-03` (nhập tồn đầu kỳ dry-run → apply), `FR-INV-04` (điều chỉnh tồn/vỡ hỏng có lý do), `FR-INV-05` (phiên kiểm kê), `FR-INV-06` (sổ kho theo variant), `FR-INV-07` (cảnh báo tồn thấp P1).
- **Luật nghiệp vụ (`docs/spec/04-business-rules.md:77–95`):** `BR-INV-01` (mọi thay đổi tạo movement), `BR-INV-02` (`available = on_hand - reserved ≥ 0`), `BR-INV-05` (giá vốn bình quân), `BR-INV-06` (tồn đầu kỳ dry-run + chưa có movement), `BR-INV-07` (phiếu nhập draft/posted/cancelled), `BR-INV-08` (điều chỉnh lý do ≥ 3 ký tự, tăng cần `inventory.approve`, `damage` âm), `BR-INV-09` (hàng độc bản `on_hand ≤ 1`), `BR-INV-10` (kiểm kê lưu `system_qty_at_count`, duyệt áp delta vào tồn hiện tại), `BR-INV-11` (ngưỡng tồn thấp > 0, `available ≤ threshold`, consumer nội bộ P1), `BR-INV-13` (đối soát sổ kho đêm — hiển thị lệch ở panel cảnh báo P1).
- **Yêu cầu phi chức năng (`docs/spec/01-requirements.md:198–208`):** `NFR-03` (idempotency trên post phiếu nhập), `NFR-04` (không lộ PII/giá vốn), `NFR-07` (cookie `AdminCookie`, CSRF `X-CSRF-TOKEN`, `no-store`/`noindex` theo `06-infrastructure.md:170–175`), `NFR-09` (unit + browser test), `NFR-10` (mobile-first), `NFR-11` (a11y cơ bản: nhãn form, tương phản, bàn phím, `axe-core`).
- **Tiêu chí nghiệm thu liên quan (`docs/spec/07-roadmap.md:71–75`):** hỗ trợ luồng thao tác người dùng cho `AC-P1-04` (phiếu nhập + sổ kho), `AC-P1-05` (từ chối `unique_piece_violation`), `AC-P1-06` (không lộ `avgCostVnd`/`unitCostVnd` khi thiếu `costs.read`), `AC-P1-08` (luồng đếm và duyệt kiểm kê). *(Chứng minh bất biến DB/JSON server đã có tại `P1-08`/`P1-09`/`P1-10`; P1-13 kiểm chứng lớp UI/client/E2E.)*
- **Quyết định kiến trúc (`docs/spec/00-README.md:85–104`):** `DEC-01`, `DEC-04`, `DEC-05`, `DEC-06`, `DEC-14`, `DEC-15`, `DEC-16`, `DEC-20`; cấu trúc FE `02-architecture.md:244–248` (§9: Next.js App Router, Tailwind, TanStack Query + TanStack Table, React Hook Form + Zod, `@teapremium/api-client`), `02-architecture.md:131–135` (§5.5 Outbox/InventoryAlert P1), `02-architecture.md:206–235` (§7 phân quyền), `05-api.md:163–175` (§4.3 Kho).
- **Trong phạm vi P1-13:** Toàn bộ Admin UI cho module Kho tại `apps/admin` (danh sách tồn + cảnh báo P1, sổ kho biến thể, nhập tồn đầu kỳ 2 bước, phiếu nhập, điều chỉnh/hư hỏng, kiểm kê), tối ưu mobile-first (`390×844` đến `1440×1000`), dùng client sinh sẵn từ `@teapremium/api-client`, kèm unit tests và Playwright browser tests.
- **Ngoài phạm vi P1-13:**
  - Dashboard tổng hợp (`FR-FIN-08`, `GET /api/v1/admin/dashboard`) thuộc **`P2-16`** (`07-roadmap.md:62,101`).
  - Thông báo Telegram/email/digest 08:00 thuộc **`P2-13`**; giữ hàng/đối soát `inventory.reservations` thuộc **`P2-05`**.
  - Mọi thay đổi trong `backend/`, `docs/spec/`, `packages/api-client/` (mã sinh), `apps/store/`, migration/DDL, hoặc `pnpm-lock.yaml` (không thêm dependency ngoài gói đã khóa).
  - Màn hình quản lý `admin/system/outbox` (chỉ đọc số `deadAlertOutboxCount` từ `GET /api/v1/admin/inventory/alerts`).

---

## 2. Danh sách màn hình và route (mobile-first)

Tất cả route dưới `/inventory` dùng chung `ApplicationShell` (sidebar desktop + drawer mobile sẵn có ở `admin-screen.tsx`) và một thanh điều hướng phụ `.catalog-nav` tự wrap trên `390 px` (chỉ hiện các tab mà tài khoản có quyền). Trên màn hình nhỏ (`≤ 800 px`), bộ lọc xếp dọc 1 cột, nút chạm tối thiểu `44 px`, bảng dữ liệu cuộn ngang có vùng `role="region"` (hoặc card tóm tắt trên mobile cho các danh sách chứng từ/dòng đếm), đảm bảo `documentElement.scrollWidth <= innerWidth`.

| # | Route | Màn hình & chức năng chính |
|---|---|---|
| 1 | `/inventory` *(qua `src/app/[section]/page.tsx` → `AdminScreen path="/inventory"`)* | **Danh sách tồn kho & Panel cảnh báo P1:** tìm `q` (debounce 250 ms, `AbortSignal`), lọc loại sản phẩm `type` (khi có `catalog.read`), lọc trạng thái `in` / `out` (thêm `low` khi có `inventory.write`), phân trang server (`DataTable`). Phía trên (chỉ khi có `inventory.read` + `inventory.write`) hiển thị **Panel cảnh báo kho P1** (`GET /api/v1/admin/inventory/alerts?limit=50`): danh sách tồn thấp (`lowStockTotal`/`lowStockItems`), lệch đối soát sổ kho (`mismatchTotal`/`mismatches`, gồm cả biến thể thiếu dòng `stock_levels`), số cảnh báo lỗi `deadAlertOutboxCount` (hiển thị cảnh báo vận hành khi `> 0`), nút làm mới và liên kết nhanh sang sổ kho / tạo phiếu nhập. |
| 2 | `/inventory/[variantId]/movements` | **Sổ kho theo biến thể:** kiểm tra UUID `variantId` (sai → `notFound()`). Bộ lọc `type` (8 loại: `opening`, `receipt`, `sale`, `return_restock`, `adjustment`, `stocktake`, `damage`, `replacement_out`), khoảng ngày VN `from`/`to` (`yyyy-MM-dd`, kiểm `from < to`), phân trang server. Hiển thị thời điểm (giờ VN), loại, biến động (+/-), `onHandAfter`, `unitCostVnd` (chỉ khi có `costs.read`), chứng từ tham chiếu (`refType`/`refId` liên kết được tới phiếu nhập/kiểm kê nếu phù hợp), lý do. Xử lý 404 khi `variantId` không tồn tại. |
| 3 | `/inventory/opening-import` | **Nhập tồn đầu kỳ (`BR-INV-06`):** chọn tệp `.csv` hoặc `.xlsx` (hướng dẫn 3 cột bắt buộc `sku,quantity,unit_cost_vnd`, ≤ 5 MiB, ≤ 500 dòng). Bước 1: bấm **Chạy thử (Dry-run)** (`dryRun=true`) → hiển thị tóm tắt `validRows`/`invalidRows` và bảng kết quả từng dòng kèm lỗi theo cột. Chỉ khi `invalidRows === 0` và nhận được `previewToken`, mới mở Bước 2: nút **Áp dụng nhập tồn đầu kỳ** (hộp thoại xác nhận, nhắc hạn token 30 phút) gửi lại **đúng tệp đã chọn** với `dryRun=false` và header `X-Inventory-Preview-Token`. Đổi tệp lập tức xóa `previewToken` cũ và khóa nút áp dụng. Kiểm tra `committed === true` (vì lỗi sau preview vẫn trả HTTP 200 kèm `committed: false` và danh sách lỗi từng dòng). |
| 4 | `/inventory/receipts` | **Danh sách phiếu nhập kho:** lọc `status` (`draft`, `posted`, `cancelled`), phân trang server, hiển thị mã `PN-yyMMdd-N`, trạng thái, nhà cung cấp, ngày nhận, ngày xác nhận, nút **Tạo phiếu nhập** và liên kết mở chi tiết. Không hiển thị tổng tiền (API list không trả giá). |
| 5 | `/inventory/receipts/new` | **Tạo phiếu nhập nháp (`draft`):** nhập `supplierName` (≤ 200), `receivedAt` (chọn ngày giờ VN, chuẩn hóa ISO có offset `+07:00`), `note` (≤ 2.000), thêm/sửa/xóa dòng biến thể qua `VariantPicker` (không trùng `variantId`, `quantity ≥ 1`, `unitCostVnd ≥ 0` nguyên VND — bắt buộc khi tạo dòng mới). Lưu thành công chuyển sang `/inventory/receipts/[id]`. |
| 6 | `/inventory/receipts/[id]` | **Chi tiết & thao tác phiếu nhập:** validate UUID `id`. Nếu `draft`: cho sửa thông tin chung và danh sách dòng (`PATCH` kèm `rowVersion`), xác nhận nhập kho (**Chốt phiếu nhập** → hộp thoại xác nhận → `POST .../post` với `Idempotency-Key` UUID + `rowVersion`), hoặc **Hủy phiếu nháp** (`POST .../cancel` với `rowVersion`). Nếu `posted` hoặc `cancelled`: chỉ đọc. Khóa chức năng post nếu đang có thay đổi chưa lưu hoặc `lines.length === 0`. |
| 7 | `/inventory/adjustments` | **Điều chỉnh tồn & báo vỡ hỏng (`BR-INV-08`):** chọn biến thể qua `VariantPicker`, chọn loại (`adjustment` — Điều chỉnh tồn, hoặc `damage` — Vỡ/hỏng trong kho), chọn chiều (Giảm tồn / Tăng tồn — chiều tăng ẩn hoặc khóa kèm chú thích quyền nếu thiếu `inventory.approve`; `damage` luôn là giảm), nhập số lượng nguyên dương (gửi dấu `-` khi giảm, `+` khi tăng), nhập lý do bắt buộc (≥ 3 ký tự Unicode sau trim, ≤ 2.000). Sau khi lưu hiển thị `onHandAfter` và liên kết xem sổ kho biến thể. |
| 8 | `/inventory/stocktakes` | **Danh sách phiên kiểm kê & tạo phiên mới:** lọc `status` (`counting`, `submitted`, `approved`, `cancelled`), phân trang server, hiển thị mã `KK-yyMMdd-N`, trạng thái, ghi chú, mốc tạo/gửi/duyệt; form/hộp thoại **Tạo phiên kiểm kê** (`POST /api/v1/admin/stocktakes` với `note` tùy chọn ≤ 2.000) rồi chuyển tới `/inventory/stocktakes/[id]`. |
| 9 | `/inventory/stocktakes/[id]` | **Chi tiết phiên kiểm kê (`BR-INV-10`):** validate UUID `id`. Ở trạng thái `counting`: chọn biến thể qua `VariantPicker`, nhập `countedQty` nguyên `≥ 0`, bấm **Lưu số đếm** (`PUT .../{id}/lines/{variantId}` kèm `rowVersion` mới nhất — chạy tuần tự, cập nhật ngay `rowVersion` sau mỗi lần đếm); bảng các dòng đã đếm hiển thị `systemQtyAtCount`, `countedQty`, `delta = countedQty - systemQtyAtCount`, nút đếm lại dòng đã có; nút **Gửi duyệt** (`POST .../submit`, bật khi có ≥ 1 dòng) và **Hủy phiên** (`POST .../cancel`). Ở trạng thái `submitted`: chỉ đọc các dòng, hiển thị nút **Duyệt kiểm kê** (chỉ khi có `inventory.approve`, có hộp thoại xác nhận áp chênh lệch vào tồn hiện tại) và **Hủy phiên** (`POST .../cancel`). Ở `approved`/`cancelled`: chỉ đọc. |

---

## 3. Bảng ánh xạ màn hình → OpenAPI → quyền → trường ẩn

> **Xác minh OpenAPI (`packages/api-client/openapi.json` & `src/generated/schema.ts`):** Toàn bộ **20 endpoint** bên dưới **đều tồn tại thật** trong bản OpenAPI đã sinh. Toàn bộ 95 operation trong `openapi.json` hiện không có thuộc tính `operationId` (ASP.NET Core Minimal API chưa gọi `.WithName(...)`), nên `openapi-fetch` định danh nghiêm ngặt bằng cặp `paths["<path>"]["<method>"]` + `tags` (giống `catalog-api.ts` và `products-api.ts`). Không có endpoint thiếu ở nhóm nghiệp vụ lõi; chỉ có giới hạn hydrat nhãn biến thể nêu ở §8.

| Màn hình | Method & Path (OpenAPI đã sinh) | Định danh OpenAPI (`schema.ts` / `openapi.json` · tag) | Quyền backend tối thiểu | Trường bị ẩn / hành vi theo quyền |
|---|---|---|---|---|
| `/inventory` (Danh sách tồn) | `GET /api/v1/admin/inventory` | `paths["/api/v1/admin/inventory"]["get"]` (`schema.ts:1374`, `openapi.json:6772` · `Inventory`, `operationId`: chưa sinh) | `inventory.read` | Thiếu `inventory.write`: chỉ hiện `availability` (`in_stock`/`out_of_stock`); ẩn bộ lọc `status=low`, ẩn cột `onHand`/`reserved`/`available`/`lowStock`, ẩn nút sổ kho. Thiếu `costs.read`: ẩn `avgCostVnd`. |
| `/inventory` (Bộ lọc loại SP) | `GET /api/v1/admin/product-types` | `paths["/api/v1/admin/product-types"]["get"]` (`schema.ts:2398`, `openapi.json:7947` · `ProductTypes`, `operationId`: chưa sinh) | `catalog.read` | Chỉ gọi khi `permissions.includes("catalog.read")`; thiếu quyền này thì ẩn dropdown lọc loại để không phát 403. |
| `/inventory` (Panel cảnh báo P1) | `GET /api/v1/admin/inventory/alerts` | `paths["/api/v1/admin/inventory/alerts"]["get"]` (`schema.ts:1616`, `openapi.json:6961` · `Inventory`, `operationId`: chưa sinh) | `inventory.read` + `inventory.write` | Thiếu 1 trong 2 quyền: không mount/không gọi endpoint. Response không bao giờ chứa giá vốn (`LowStockAlertItem`/`InventoryMismatchItem`). |
| `/inventory/[variantId]/movements` | `GET /api/v1/admin/inventory/{variantId}/movements` | `paths["/api/v1/admin/inventory/{variantId}/movements"]["get"]` (`schema.ts:1443`, `openapi.json:7133` · `Inventory`, `operationId`: chưa sinh) | `inventory.read` + `inventory.write` | Thiếu 1 trong 2 quyền: chặn 403 tại route guard, không gọi API. Thiếu `costs.read`: ẩn cột `unitCostVnd`. |
| `/inventory/opening-import` | `POST /api/v1/admin/inventory/opening-import` | `paths["/api/v1/admin/inventory/opening-import"]["post"]` (`schema.ts:1681`, `openapi.json:7026` · `Inventory`, `operationId`: chưa sinh) | `inventory.approve` *(+ `inventory.read` tại cổng `/inventory`)* | Thiếu `inventory.approve`: ẩn tab và chặn 403 tại route. Bắt buộc `query.dryRun`, `multipart/form-data` `file`, `X-CSRF-TOKEN`, và `X-Inventory-Preview-Token` khi `dryRun=false`. |
| `/inventory/receipts` | `GET /api/v1/admin/receipts` | `paths["/api/v1/admin/receipts"]["get"]` (`schema.ts:4710`, `openapi.json:10554` · `Receipts`, `operationId`: chưa sinh) | `inventory.write` | Thiếu `inventory.write`: ẩn tab, chặn 403 tại route, không gọi API. `ReceiptSummary` không có giá vốn. |
| `/inventory/receipts/new` | `POST /api/v1/admin/receipts` | `paths["/api/v1/admin/receipts"]["post"]` (`schema.ts:4710`, `openapi.json:10554` · `Receipts`, `operationId`: chưa sinh) | `inventory.write` | Thiếu `costs.read`: vẫn nhập `unitCostVnd ≥ 0` cho dòng mới khi tạo, nhưng response trả về ẩn `unitCostVnd` và UI không hiển thị lại giá vốn đã lưu. |
| `/inventory/receipts/[id]` (Xem) | `GET /api/v1/admin/receipts/{id}` | `paths["/api/v1/admin/receipts/{id}"]["get"]` (`schema.ts:4862`, `openapi.json:10735` · `Receipts`, `operationId`: chưa sinh) | `inventory.write` | Thiếu `costs.read`: ẩn cột đơn giá đã lưu / thành tiền / tổng phiếu (`ReceiptLineView.unitCostVnd` vắng trong JSON). |
| `/inventory/receipts/[id]` (Sửa nháp) | `PATCH /api/v1/admin/receipts/{id}` | `paths["/api/v1/admin/receipts/{id}"]["patch"]` (`schema.ts:4862`, `openapi.json:10735` · `Receipts`, `operationId`: chưa sinh) | `inventory.write` | Thiếu `costs.read`: dòng cũ để trống ô giá (bỏ `unitCostVnd` khỏi payload để server giữ giá cũ); dòng mới bắt buộc nhập `unitCostVnd ≥ 0`. |
| `/inventory/receipts/[id]` (Chốt phiếu) | `POST /api/v1/admin/receipts/{id}/post` | `paths["/api/v1/admin/receipts/{id}/post"]["post"]` (`schema.ts:5109`, `openapi.json:11003` · `Receipts`, `operationId`: chưa sinh) | `inventory.write` | Bắt buộc header `Idempotency-Key` (UUID) + `X-CSRF-TOKEN` + body `{ rowVersion }`. Response `ReceiptPosted` chỉ trả `{ id, status, rowVersion }`. |
| `/inventory/receipts/[id]` (Hủy nháp) | `POST /api/v1/admin/receipts/{id}/cancel` | `paths["/api/v1/admin/receipts/{id}/cancel"]["post"]` (`schema.ts:5014`, `openapi.json:10903` · `Receipts`, `operationId`: chưa sinh) | `inventory.write` | Chỉ hiện khi `status === "draft"`; gửi `{ rowVersion }` + `X-CSRF-TOKEN`. |
| `/inventory/adjustments` | `POST /api/v1/admin/inventory/adjustments` | `paths["/api/v1/admin/inventory/adjustments"]["post"]` (`schema.ts:1523`, `openapi.json:6872` · `Inventory`, `operationId`: chưa sinh) | `inventory.write` (tăng tồn: `+ inventory.approve`) | Thiếu `inventory.approve`: ẩn/khóa tùy chọn tăng tồn (`quantityDelta > 0`), chỉ cho giảm (`adjustment` âm hoặc `damage` âm). Không hiển thị giá vốn. |
| `/inventory/stocktakes` (Danh sách) | `GET /api/v1/admin/stocktakes` | `paths["/api/v1/admin/stocktakes"]["get"]` (`schema.ts:5454`, `openapi.json:11388` · `Stocktakes`, `operationId`: chưa sinh) | `inventory.write` | Thiếu `inventory.write`: ẩn tab, chặn 403 tại route, không gọi API. |
| `/inventory/stocktakes` (Tạo phiên) | `POST /api/v1/admin/stocktakes` | `paths["/api/v1/admin/stocktakes"]["post"]` (`schema.ts:5454`, `openapi.json:11388` · `Stocktakes`, `operationId`: chưa sinh) | `inventory.write` | Gửi `{ note? }` + `X-CSRF-TOKEN`. |
| `/inventory/stocktakes/[id]` (Xem) | `GET /api/v1/admin/stocktakes/{id}` | `paths["/api/v1/admin/stocktakes/{id}"]["get"]` (`schema.ts:5606`, `openapi.json:11569` · `Stocktakes`, `operationId`: chưa sinh) | `inventory.write` | Không có trường giá vốn trong `StocktakeView`. |
| `/inventory/stocktakes/[id]` (Đếm dòng) | `PUT /api/v1/admin/stocktakes/{id}/lines/{variantId}` | `paths["/api/v1/admin/stocktakes/{id}/lines/{variantId}"]["put"]` (`schema.ts:5869`, `openapi.json:11839` · `Stocktakes`, `operationId`: chưa sinh) | `inventory.write` | Chỉ hiện khi `status === "counting"`; gửi `{ rowVersion, countedQty }` + `X-CSRF-TOKEN`, cập nhật ngay `rowVersion` mới. |
| `/inventory/stocktakes/[id]` (Gửi duyệt) | `POST /api/v1/admin/stocktakes/{id}/submit` | `paths["/api/v1/admin/stocktakes/{id}/submit"]["post"]` (`schema.ts:5965`, `openapi.json:11948` · `Stocktakes`, `operationId`: chưa sinh) | `inventory.write` | Chỉ hiện khi `status === "counting"` và `lines.length > 0`; gửi `{ rowVersion }` + `X-CSRF-TOKEN`. |
| `/inventory/stocktakes/[id]` (Duyệt) | `POST /api/v1/admin/stocktakes/{id}/approve` | `paths["/api/v1/admin/stocktakes/{id}/approve"]["post"]` (`schema.ts:5679`, `openapi.json:11639` · `Stocktakes`, `operationId`: chưa sinh) | `inventory.write` + `inventory.approve` | Chỉ hiện khi `status === "submitted"` **và** có `inventory.approve`; thiếu quyền thì hiển thị ghi chú "Chờ quản lý có quyền `inventory.approve` duyệt". |
| `/inventory/stocktakes/[id]` (Hủy phiên) | `POST /api/v1/admin/stocktakes/{id}/cancel` | `paths["/api/v1/admin/stocktakes/{id}/cancel"]["post"]` (`schema.ts:5774`, `openapi.json:11739` · `Stocktakes`, `operationId`: chưa sinh) | `inventory.write` | Chỉ hiện khi `status` là `counting` hoặc `submitted`; gửi `{ rowVersion }` + `X-CSRF-TOKEN`. |
| Toàn bộ màn hình kho (Phiên & CSRF) | `GET /api/v1/admin/auth/me`, `GET /api/v1/admin/auth/csrf` | `paths["/api/v1/admin/auth/me"]["get"]` (`schema.ts:586`, `openapi.json:5922`), `paths["/api/v1/admin/auth/csrf"]["get"]` (`schema.ts:425`, `openapi.json:5802`) | Đăng nhập / mở | Tái sử dụng nguyên `AdminProvider`, `useSession`, và middleware `AdminApi` (`apps/admin/src/lib/api.ts:78–95`). |

---

## 4. Quy tắc phân quyền & chống vòng lặp 403

Mọi kiểm tra quyền trên UI **chỉ đọc `user.permissions`** trả về từ `GET /api/v1/admin/auth/me`, **tuyệt đối không suy luận từ `user.roles`** (`05-api.md:174`, `navigation.ts:20`):

1. **Tài khoản chỉ có `inventory.read` (không có `inventory.write`):**
   - Chỉ nhìn thấy tab **Tồn kho** (`/inventory`). Ẩn toàn bộ tab/nút **Phiếu nhập**, **Điều chỉnh**, **Kiểm kê**, **Tồn đầu kỳ**, nút **Sổ kho**, và **Panel cảnh báo P1**.
   - Bảng tồn kho chỉ hiển thị SKU, sản phẩm/biến thể, loại (nếu có `catalog.read`), và nhãn nhị phân **Còn hàng** (`in_stock`) / **Hết hàng** (`out_of_stock`). Không render cột `onHand`, `reserved`, `available`, `lowStock`, `avgCostVnd`.
   - Bộ lọc trạng thái chỉ có `Tất cả`, `Còn hàng (in)`, `Hết hàng (out)` — **ẩn hoàn toàn** lựa chọn `Tồn thấp (low)` và không bao giờ gửi `status=low` (tránh 403 từ `ListInventoryHandler`).
   - Điều hướng trực tiếp bằng URL vào `/inventory/[variantId]/movements`, `/inventory/receipts*`, `/inventory/adjustments`, `/inventory/stocktakes*`, `/inventory/opening-import`: `InventoryScreen` chặn ở đầu cây render bằng `<StatusPage status={403} embedded />` trước khi mount component con → **0 request** bắn ra, không tạo vòng lặp 403.
   - `QueryClient` đã cấu hình `retry: false` (`providers.tsx:11–12`); các query phụ thuộc quyền bổ sung (`alerts`, `product-types`) bắt buộc đặt `enabled: hasPermission`. Nếu backend vẫn trả 403 (do vừa bị thu hồi quyền), hiển thị `FormError` rõ ràng và không tự gọi lại.
2. **Tài khoản có `inventory.read` + `inventory.write` (thiếu `costs.read` và `inventory.approve` — ví dụ `sales`):**
   - Xem được `onHand`, `reserved`, `available`, `lowStock`, bộ lọc `low`, Panel cảnh báo P1, sổ kho, tạo/sửa/chốt/hủy phiếu nhập, điều chỉnh giảm / báo vỡ hỏng (`damage`), tạo/đếm/gửi duyệt/hủy kiểm kê.
   - **Ẩn hoàn toàn `avgCostVnd` và `unitCostVnd`** ở bảng tồn kho, sổ kho, và xem/sửa phiếu nhập (không tính hay hiển thị tổng giá trị tồn/phiếu nhập). Khi sửa phiếu nhập draft (`PATCH`), giữ trống ô đơn giá cho dòng đã lưu (bỏ `unitCostVnd` khỏi body để server giữ nguyên giá cũ theo `p1-09-implementation.md:13,69`), chỉ yêu cầu nhập giá vốn khi thêm dòng biến thể mới.
   - **Ẩn/khóa** các thao tác cần `inventory.approve`: tab & route `/inventory/opening-import`, chiều tăng tồn trên `/inventory/adjustments`, nút **Duyệt kiểm kê** trên `/inventory/stocktakes/[id]`.
3. **Tài khoản có thêm `inventory.approve` và/hoặc `costs.read` (ví dụ `owner`):**
   - `inventory.approve` mở khóa: nhập tồn đầu kỳ (`/inventory/opening-import`), điều chỉnh tăng tồn (`quantityDelta > 0`), và duyệt kiểm kê (`POST .../approve`).
   - `costs.read` độc lập với `inventory.approve`: mở khóa hiển thị `avgCostVnd` (bảng tồn), `unitCostVnd` (sổ kho, chi tiết phiếu nhập, thành tiền/tổng phiếu nhập). Giá vốn `0` hiển thị `0 đ`; giá vốn chưa có (`undefined`/`null`) hiển thị `—`.

---

## 5. Xử lý lỗi, bảo toàn bản nháp chưa lưu & CSRF / Idempotency

- **CSRF bắt buộc trên mọi request ghi:** Mọi `POST`, `PATCH`, `PUT` đi qua `AdminApi.client` (`apps/admin/src/lib/api.ts:78–87`), middleware tự gọi `GET /api/v1/admin/auth/csrf` lấy token mới vào bộ nhớ và gắn header `X-CSRF-TOKEN` trước mỗi request ghi (kể cả `multipart/form-data` của `opening-import`), `credentials: "include"`, `cache: "no-store"`. Không lưu token trong `localStorage`/`sessionStorage`, không tự động retry mutation khi lỗi (`csrf_invalid` thông báo người dùng bấm thử lại thủ công).
- **Idempotency trên chốt phiếu nhập (`POST /api/v1/admin/receipts/{id}/post`):** Sinh `Idempotency-Key` bằng `crypto.randomUUID()` theo cặp `(receiptId, rowVersion)` trong trạng thái của component; nếu người dùng bấm thử lại sau lỗi mạng/5xx khi `rowVersion` chưa đổi thì **giữ nguyên cùng UUID** để backend replay an toàn; khi `rowVersion` thay đổi (sau sửa draft hoặc tải lại từ 409) thì xoay UUID mới để tránh `409 idempotency_key_reused`.
- **Bảo toàn dữ liệu form chưa lưu trên mọi lỗi:**
  - Tuyệt đối không reset form, không remount component, không xóa danh sách dòng đang chỉnh khi API trả lỗi hoặc khi refetch chạy nền.
  - Dùng `useProductNavigationGuard(dirty, pending)` (cảnh báo `beforeunload`, click link nội bộ, nút back trình duyệt) cho các form đang có thay đổi chưa lưu (phiếu nhập, đếm kiểm kê, điều chỉnh, kết quả preview tồn đầu kỳ).
  - Khóa nút submit bằng `ref` chống gửi trùng khi request đang bay (`pending`).
- **Ma trận xử lý theo mã HTTP & `code`:**

| HTTP / `code` | Nguồn nghiệp vụ | Hành vi UI (không mất dữ liệu chưa lưu) |
|---|---|---|
| `400 validation_failed` | Sai định dạng số nguyên, thiếu lý do ≥ 3 ký tự, trùng dòng, thiếu giá dòng mới, `from >= to` | Gắn lỗi từng trường/dòng qua `ApiError.fields` + banner `FormError`; giữ nguyên input để sửa ngay. |
| `400 opening_preview_invalid` | Token preview thiếu, hết hạn > 30 phút, sai tệp hoặc đổi nhân viên | Giữ tệp đang chọn, xóa token cũ, yêu cầu bấm **Chạy thử lại** để cấp `previewToken` mới. |
| `401 unauthenticated` | Hết phiên / đổi mật khẩu | Middleware `AdminApi.onUnauthorized` xóa toàn bộ query cache kho và chuyển về `/login`. |
| `403 forbidden` / `https_required` | Thiếu quyền (ví dụ tăng tồn không có `inventory.approve`, hoặc quyền vừa bị thu hồi) | Hiển thị `FormError` rõ lý do, giữ nguyên form, không retry tự động. |
| `404 not_found` | Biến thể/phiếu nhập/kiểm kê không tồn tại | Trang chi tiết/sổ kho hiện thông báo không tìm thấy + nút quay về danh sách; trong form ghi giữ nguyên input và báo biến thể/chứng từ không còn. |
| `409 concurrency_conflict` | `rowVersion` cũ ở sửa/chốt/hủy phiếu nhập, đếm/gửi/duyệt/hủy kiểm kê | `isConcurrencyConflict(err) === true`: **giữ nguyên bản nháp trên màn hình**, khóa các nút ghi tiếp theo để tránh ghi đè âm thầm, hiển thị nút chủ động **"Bỏ thay đổi và tải phiên bản mới"** nạp lại `rowVersion` mới nhất từ server. |
| `409` *(hợp đồng thực tế backend)* **và** `422` *(phòng thủ)*: `unique_piece_violation`, `out_of_stock` (âm / `< reserved`), `stocktake_count_stale`, `receipt_lines_invalid`, `stocktake_lines_invalid`, `invalid_status_transition`, `idempotency_key_reused` | Vi phạm bất biến hàng độc bản (`BR-INV-09`), tồn âm hoặc nhỏ hơn `reserved` (`BR-INV-02/08/10`), kiểm kê bị phiên khác duyệt trước (`P1-09`), trạng thái chứng từ đã đổi | **Không** khóa vĩnh viễn như `concurrency_conflict` (trừ `invalid_status_transition` có thêm nút tải lại trạng thái mới); giữ nguyên bản nháp form, ánh xạ `errors["lines[<variantId>]"]` trực tiếp lên đúng dòng biến thể vi phạm (kèm tên/SKU), cho phép sửa và gửi lại. Riêng `stocktake_count_stale` hiển thị hướng dẫn rõ: hủy phiên kiểm kê hiện tại và tạo phiên mới để đếm lại. |
| `413` / `415` | Tệp nhập tồn đầu kỳ vượt 5 MiB / 32 MiB XLSX / > 500 dòng hoặc sai định dạng (macro/công thức/không phải CSV/XLSX) | Giữ nguyên trạng thái màn hình, hiện lỗi tiếng Việt cụ thể để chọn tệp khác. |
| `200` với `committed: false` (`opening-import` apply) | Dữ liệu kho thay đổi giữa lúc preview và apply (`opening_already_recorded`, `sku_identity_changed`, `unique_piece_violation`) | Không báo thành công; render bảng lỗi từng dòng/cột trả về, xóa `previewToken` cũ và yêu cầu sửa tệp/chạy thử lại. |

---

## 6. Kế hoạch file (`apps/admin` — tổng 26 file: 3 sửa, 23 tạo mới)

> **Ràng buộc phạm vi:** Không sửa `backend/`, `docs/spec/`, `packages/api-client/`, `apps/store/`, `apps/admin/package.json`, `pnpm-lock.yaml`, `apps/admin/src/lib/navigation.ts`, `apps/admin/src/components/providers.tsx`, hoặc `apps/admin/src/components/auth-forms.tsx`.

### File sửa đổi (3 file)
1. `apps/admin/src/lib/api.ts` — Bổ sung thông điệp tiếng Việt trong `ApiError.messages` cho toàn bộ mã lỗi kho (`unique_piece_violation`, `out_of_stock`, `invalid_status_transition`, `idempotency_key_reused`, `opening_preview_invalid`, `opening_already_recorded`, `stocktake_count_stale`, `receipt_lines_invalid`, `stocktake_lines_invalid`, `inventory_document_codes_exhausted`, `file_limit_exceeded`, `unsupported_media_type`, `stock_quantity_overflow`, `invalid_stock_movement`, `sku_identity_changed`). Không sửa middleware CSRF/cookie/401.
2. `apps/admin/src/components/admin-screen.tsx` — Thêm prop tùy chọn `inventoryPage?: InventoryPage`, render `<InventoryScreen page={inventoryPage ?? "stock"} user={session.data.user} />` khi `path === "/inventory"`, cập nhật mô tả thẻ `/inventory` ở `Welcome`. Không sửa logic `forbidden`/onboarding/session guard.
3. `apps/admin/src/app/globals.css` — Bổ sung style mobile-first `.inventory-*` (thẻ cảnh báo, bộ lọc xếp cột, card/bảng dòng phiếu nhập và kiểm kê, highlight lỗi dòng, nút chạm ≥ 44 px, đảm bảo không tràn ngang ở `390 px`).

### File tạo mới (23 file)
*Next.js App Router pages (8 file):*
4. `apps/admin/src/app/inventory/[variantId]/movements/page.tsx`
5. `apps/admin/src/app/inventory/opening-import/page.tsx`
6. `apps/admin/src/app/inventory/receipts/page.tsx`
7. `apps/admin/src/app/inventory/receipts/new/page.tsx`
8. `apps/admin/src/app/inventory/receipts/[id]/page.tsx`
9. `apps/admin/src/app/inventory/adjustments/page.tsx`
10. `apps/admin/src/app/inventory/stocktakes/page.tsx`
11. `apps/admin/src/app/inventory/stocktakes/[id]/page.tsx`

*Typed API adapter & pure helpers/Zod (2 file):*
12. `apps/admin/src/lib/inventory-api.ts`
13. `apps/admin/src/lib/inventory-forms.ts`

*UI components (11 file):*
14. `apps/admin/src/components/inventory/inventory-screen.tsx`
15. `apps/admin/src/components/inventory/stock-list-panel.tsx`
16. `apps/admin/src/components/inventory/alerts-panel.tsx`
17. `apps/admin/src/components/inventory/movements-panel.tsx`
18. `apps/admin/src/components/inventory/opening-import-panel.tsx`
19. `apps/admin/src/components/inventory/variant-picker.tsx`
20. `apps/admin/src/components/inventory/receipts-panel.tsx`
21. `apps/admin/src/components/inventory/receipt-editor.tsx`
22. `apps/admin/src/components/inventory/adjustments-panel.tsx`
23. `apps/admin/src/components/inventory/stocktakes-panel.tsx`
24. `apps/admin/src/components/inventory/stocktake-detail.tsx`

*Kiểm thử (2 file):*
25. `apps/admin/tests/inventory.test.tsx`
26. `apps/admin/tests/browser/inventory.spec.ts`

---

## 7. Kế hoạch kiểm thử (tổng 24 test: 10 unit + 14 Playwright browser)

### 7.1 Unit tests (`apps/admin/tests/inventory.test.tsx` — 10 test)
1. **Phân quyền thuần từ `permissions`:** `inventory.read`, `inventory.write`, `inventory.approve`, `costs.read`, `catalog.read` không suy luận từ `roles` (kể cả khi `roles: ["owner"]` nhưng thiếu claim).
2. **Parser giá vốn VND nguyên không âm:** chấp nhận `0` và số nguyên hợp lệ, từ chối số âm / thập phân / ký hiệu khoa học / vượt `Number.MAX_SAFE_INTEGER`; định dạng hiển thị `0 đ` vs `—`.
3. **Builder payload PATCH phiếu nhập khi thiếu `costs.read`:** bỏ `unitCostVnd` cho dòng cũ chưa đổi giá (giữ giá server theo quyết định P1-09), bắt buộc có `unitCostVnd ≥ 0` cho dòng biến thể mới, không gửi `lines: null`.
4. **Validation điều chỉnh/vỡ hỏng (`BR-INV-08`):** lý do ≥ 3 Unicode runes sau trim (kiểm ký tự có dấu & surrogate), `quantityDelta !== 0`, `damage` chỉ nhận âm, tăng tồn bị chặn khi thiếu `inventory.approve`.
5. **Tính delta & map lỗi dòng kiểm kê (`BR-INV-10`):** `countedQty ≥ 0`, `delta = countedQty - systemQtyAtCount`, ánh xạ `errors["lines[<variantId>]"]` (`unique_piece_violation`, `out_of_stock`, `stocktake_count_stale`, `not_found`).
6. **Máy trạng thái nhập tồn đầu kỳ (`BR-INV-06`):** đổi tệp xóa `previewToken` và khóa apply; `invalidRows > 0` không mở apply; HTTP 200 với `committed: false` giữ nguyên kết quả lỗi từng dòng.
7. **Adapter `InventoryApi` & CSRF / Headers:** kiểm phương thức, đường dẫn, `credentials: "include"`, `cache: "no-store"`, gọi mới `/auth/csrf` + gửi `X-CSRF-TOKEN` trên mọi `POST`/`PATCH`/`PUT`, gửi `FormData` + `X-Inventory-Preview-Token` ở `opening-import`, gửi `Idempotency-Key` ở `receipts/{id}/post`.
8. **Vòng đời `Idempotency-Key` chốt phiếu nhập:** retry cùng `(id, rowVersion)` giữ nguyên UUID; đổi `rowVersion` sinh UUID mới.
9. **Phân loại `ApiError`:** `isConcurrencyConflict` chỉ `true` với `409 concurrency_conflict`; `409`/`422` `unique_piece_violation`, `out_of_stock`, `stocktake_count_stale`, `invalid_status_transition` không bị nhầm là `concurrency_conflict` và giữ đúng `fields`.
10. **Bộ lọc ngày sổ kho & UUID guard:** kiểm `yyyy-MM-dd`, chặn `from >= to`, chặn `variantId`/`id` không đúng định dạng UUID trước khi gọi API.

### 7.2 Playwright browser tests (`apps/admin/tests/browser/inventory.spec.ts` — 14 test)
1. **Nhân viên chỉ có `inventory.read` (cả có và thiếu `catalog.read`):** chỉ hiện `Còn hàng`/`Hết hàng`, ẩn bộ lọc `low`, ẩn panel cảnh báo, ẩn số lượng & giá vốn, ẩn nút sổ kho và mọi tab ghi; vào thẳng URL `/movements`, `/receipts`, `/adjustments`, `/stocktakes`, `/opening-import` hiện 403 ngay và **0 request** tới API bị cấm (không vòng lặp 403).
2. **Nhân viên `sales` (`inventory.read` + `inventory.write`, không có `costs.read`/`inventory.approve`):** xem số lượng, bộ lọc `low`, panel cảnh báo, sổ kho không có bất kỳ trường/cột giá vốn nào; ẩn tab tồn đầu kỳ, khóa chiều tăng điều chỉnh và nút duyệt kiểm kê.
3. **Danh sách tồn kho + bộ lọc + Panel cảnh báo P1:** tìm `q` (debounce + `AbortSignal`), lọc `type` + `status=in/out/low`, phân trang; panel `GET /api/v1/admin/inventory/alerts` hiển thị tồn thấp, lệch đối soát sổ kho (gồm `hasStockLevel: false`), và cảnh báo `deadAlertOutboxCount > 0`, nút làm mới và điều hướng sang sổ kho.
4. **Sổ kho theo biến thể:** lọc `type`, lọc ngày VN `from`/`to`, phân trang, hiện `unitCostVnd` cho `owner` và ẩn cho `sales`, xử lý 404 biến thể không tồn tại.
5. **Nhập tồn đầu kỳ `dryRun` → `apply`:** tệp có dòng lỗi hiện bảng lỗi từng dòng/cột (không cho apply); tệp hợp lệ trả `previewToken`; đổi tệp hủy token; chạy thử lại → bấm Áp dụng gửi đúng `multipart/form-data`, `X-Inventory-Preview-Token`, `X-CSRF-TOKEN`; kiểm cả nhánh `200 committed: false` (`opening_already_recorded`), `400 opening_preview_invalid`, `413`, `415`.
6. **Luồng phiếu nhập đầy đủ (`draft` → sửa → `post` có `Idempotency-Key` → retry cùng key → `cancel`):** tạo phiếu nháp, sửa dòng, chốt phiếu gửi `Idempotency-Key` + `rowVersion` + fresh `X-CSRF-TOKEN`, giả lập lỗi mạng/502 ở lần post đầu rồi bấm lại xác nhận giữ cùng `Idempotency-Key`, hủy một phiếu nháp khác, phiếu đã chốt/hủy chỉ đọc.
7. **Sửa phiếu nhập nháp bởi `sales` thiếu `costs.read`:** giữ nguyên dòng cũ (payload `PATCH` không gửi `unitCostVnd`), thêm dòng mới bắt buộc nhập giá ≥ 0, không lộ giá vốn trên giao diện.
8. **Điều chỉnh tồn & báo vỡ hỏng (`BR-INV-08`/`09`):** `owner` điều chỉnh tăng thành công, `sales` báo vỡ hỏng (`damage` âm) thành công và không chọn được chiều tăng; server trả `409`/`422` `unique_piece_violation` và `out_of_stock` (âm/đang giữ `reserved`) vẫn **giữ nguyên dữ liệu form**.
9. **Luồng kiểm kê đầy đủ (`BR-INV-10`):** tạo phiên `KK-...`, đếm tuần tự 2 biến thể (kiểm lần 2 dùng `rowVersion` mới từ response lần 1), hiển thị đúng `delta`, gửi duyệt (`submit`), `owner` duyệt (`approve`), và ca hủy từ cả `counting` và `submitted`.
10. **Lỗi duyệt kiểm kê theo dòng:** server trả `409`/`422` với `errors["lines[variantId]"]` gồm `unique_piece_violation`, `out_of_stock`, `stocktake_count_stale` → đánh dấu từng dòng tương ứng và hiển thị hướng dẫn hủy/đếm lại cho `stocktake_count_stale`.
11. **Khôi phục xung đột `409 concurrency_conflict` mà không mất bản nháp:** trên cả phiếu nhập draft và phiên kiểm kê đang nhập số đếm, khi server trả `409 concurrency_conflict`, bản nháp đang sửa vẫn giữ nguyên trên màn hình, nút Lưu bị khóa, bấm "Bỏ thay đổi và tải phiên bản mới" mới nạp `rowVersion` mới và cho lưu tiếp.
12. **Bảo toàn form khi phiếu nhập gặp `400`/`403`/`409`/`422` `unique_piece_violation`:** xác minh giữ nguyên các dòng đang nhập, hiển thị lỗi đúng dòng/trường, không tự retry lặp.
13. **Viewport điện thoại `390×844` (+ `1440×1000`) & Accessibility (`axe-core`):** kiểm tra tất cả màn hình chính (tồn kho + cảnh báo, sổ kho, phiếu nhập, điều chỉnh, kiểm kê, tồn đầu kỳ) ở `390×844`: `documentElement.scrollWidth <= innerWidth`, mục tiêu chạm nút/input ≥ 44 px, `AxeBuilder` 0 vi phạm, lưu screenshot kiểm chứng vào `artifacts/`.
14. **Điều hướng bàn phím, cảnh báo rời trang khi form bẩn & hết phiên `401`:** đóng dialog xác nhận bằng `Escape`, cảnh báo rời trang khi đang sửa phiếu nhập chưa lưu, và khi nhận `401` tự xóa sạch query cache kho và chuyển về `/login`.

### 7.3 Lệnh kiểm chứng bắt buộc tại G3 (chạy trong Linux container qua `verify-linux.ps1`)
- `./scripts/verify-linux.ps1 -Mode frontend -Manifest artifacts/p1-13/candidate.manifest` (gồm `lint --max-warnings=0`, `typecheck`, `test:unit`, `test:browser`, `build` cho `apps/admin` và toàn workspace).
- *(Theo quyết định Owner 23/11/2026: bỏ lượt verify backend `verify-linux.ps1` chế độ backend vì P1-13 không sửa `backend/`, `docs/spec/`, `packages/api-client/` và `P2-01` đang dùng Docker song song; ghi rõ vào `NOT RUN`.)*
- `git diff --check` và `git diff --exit-code origin/main -- backend docs/spec packages/api-client apps/store`.
- *NOT RUN dự kiến ở P1-13:* Lượt verify backend (`./scripts/verify-linux.ps1` mặc định — không đổi backend và P2-01 dùng Docker song song theo quyết định Owner 23/11/2026), live full-stack E2E với PG17/Caddy thật (nếu không được owner yêu cầu mở rộng riêng), gửi Telegram/digest (`P2-13`), đối soát `reservations` (`P2-05`), và dashboard tổng hợp (`P2-16`).

---

## 8. Rủi ro, ASSUMPTION và GAP

| Mã | Loại | File:dòng bằng chứng | Mô tả & hậu quả | Phương án đề xuất (không sửa backend ở P1-13) |
|---|---|---|---|---|
| **GAP-P1-13-01** | `MISSING_CONTRACT` *(không chặn)* | `packages/api-client/src/generated/schema.ts:7602–7608,7686–7696`; `packages/api-client/openapi.json:6772,7133,10735,11569`; `docs/spec/01-requirements.md:198` (NFR-01) | `ReceiptLineView`, `StocktakeLineView` và `GET /inventory/{variantId}/movements` chỉ trả `variantId` (không có `sku`, `productName`, `variantName`); đồng thời không có endpoint tra cứu 1 biến thể theo `variantId` hay `GET /inventory?variantIds=...`. Khi mở trực tiếp URL chi tiết phiếu nhập, kiểm kê hoặc sổ kho (reload/bookmark), UI không có sẵn SKU/tên biến thể; với catalog lớn (tới 2.000 biến thể theo NFR-01), tải toàn bộ các trang `GET /inventory?pageSize=100` sẽ tốn nhiều request. | **Phương án A (Khuyến nghị cho P1-13):** Lưu cache nhãn biến thể trong TanStack Query từ danh sách tồn/`VariantPicker`; khi mở URL trực tiếp chỉ hydrate có giới hạn (tối đa 5 trang × 100 = 500 biến thể, dừng sớm ngay khi đủ `variantId` cần thiết); nếu vẫn chưa thấy thì hiển thị `variantId` rút gọn có nút sao chép (không bịa SKU/tên). **Phương án B:** Tạm dừng P1-13 để bổ sung `sku`/`productName`/`variantName` vào DTO phiếu nhập/kiểm kê hoặc thêm `variantIds` ở backend. |
| **GAP-P1-13-02** | `DESIGN_RISK` *(lệch mã HTTP 409 vs 422)* | `backend/src/TeaPremium.Application/Inventory/InventoryDocuments.cs:227–233`; `backend/src/TeaPremium.Api/Inventory/InventoryDocumentEndpoints.cs:199–201`; `docs/spec/05-api.md:26` | Thực tế backend (và `05-api.md:26`) trả **HTTP 409** (không phải 422) cho `unique_piece_violation`, `out_of_stock` (âm/đang giữ), `stocktake_count_stale`, `receipt_lines_invalid`, `stocktake_lines_invalid`. Nếu UI coi mọi 409 đều là stale `rowVersion` sẽ khóa nhầm form người dùng. | Phân tách chặt: chỉ `status === 409 && code === "concurrency_conflict"` mới là xung đột `rowVersion`; mọi mã lỗi nghiệp vụ kho được xử lý thống nhất trên cả **409** (thực tế) và **422** (phòng thủ), giữ nguyên form và gắn lỗi theo dòng `lines[variantId]`. |
| **GAP-P1-13-03** | `MISSING_CONTRACT` *(metadata OpenAPI, không chặn)* | `packages/api-client/openapi.json:6772–11948`; `backend/src/TeaPremium.Api/Inventory/InventoryEndpoints.cs:22–69`; `InventoryDocumentEndpoints.cs:57–146` | 0/95 operation trong `openapi.json` có `operationId` (ASP.NET Core chưa gọi `.WithName(...)`). Không thiếu endpoint nào, chỉ thiếu nhãn `operationId`. | Định danh và gọi API qua `paths[path][method]` của `openapi-fetch` (đúng pattern `catalog-api.ts` và `products-api.ts`); không sửa backend/OpenAPI ở P1-13. |
| **ASSUMPTION-01** | `ASSUMPTION` *(cổng quyền `/inventory` & Tier B)* | `apps/admin/src/lib/navigation.ts:6`; `apps/admin/src/components/admin-screen.tsx:37–38`; `docs/spec/02-architecture.md:217–219`; `backend/src/TeaPremium.Api/Inventory/InventoryEndpoints.cs:34` | Theo ma trận vai trò `02-architecture.md:217–219`, mọi vai trò có `inventory.write` hoặc `inventory.approve` đều có `inventory.read`. Kế hoạch giả định giữ nguyên `navigation.ts:6` (`permission: "inventory.read"`) để không sửa guard nền và giữ **Tier B**. | Nếu owner muốn hỗ trợ tài khoản tùy biến có `inventory.write`/`inventory.approve` nhưng **không** có `inventory.read`, cần xác nhận nâng P1-13 lên **Tier A** và cho phép sửa `navigation.ts` + `AdminScreen`. |
| **ASSUMPTION-02** | `ASSUMPTION` *(lọc loại SP cần `catalog.read`)* | `docs/spec/05-api.md:112,166`; `docs/spec/02-architecture.md:212,217` | `GET /api/v1/admin/product-types` yêu cầu `catalog.read`. Mọi vai trò mặc định đều có `catalog.read`, nhưng để tuân thủ quy tắc không hardcode role và không tạo 403 loop, UI chỉ gọi endpoint này khi `permissions.includes("catalog.read")` (thiếu thì ẩn bộ lọc loại sản phẩm). | Giữ mặc định theo phương án này; không cần thay đổi backend. |
| **RISK-01** | `DESIGN_RISK` *(đếm kiểm kê tuần tự)* | `backend/src/TeaPremium.Application/Inventory/InventoryDocuments.cs:130–148` | Mỗi lần `PUT /stocktakes/{id}/lines/{variantId}` đều đổi `rowVersion` của phiên kiểm kê. Nếu nhân viên thao tác nhanh trên điện thoại gửi 2 request song song sẽ tự gây `409 concurrency_conflict`. | Khóa nút lưu từng dòng trong lúc `pending` (tuần tự hóa thao tác đếm trên 1 phiên) và cập nhật ngay `document.rowVersion` từ response trả về. |
| **RISK-02** | `DESIGN_RISK` *(giữ nguyên tệp ở `opening-import`)* | `backend/src/TeaPremium.Api/Inventory/InventoryEndpoints.cs:105–131`; `docs/plans/p1-08-implementation.md:15` | `apply` xác minh SHA-256 đúng bytes của tệp đã `dryRun` và giới hạn 30 phút. Nếu người dùng sửa file bên ngoài hoặc chọn lại file khác trên điện thoại sẽ nhận `400 opening_preview_invalid`. | Giữ đúng đối tượng `File` trong state sau `dryRun=true`, tự xóa `previewToken` ngay khi `onChange` chọn tệp mới, hiển thị đồng hồ/mốc hết hạn 30 phút và hướng dẫn chạy thử lại rõ ràng khi gặp `opening_preview_invalid`. |
| **RISK-03** | `DESIGN_RISK` *(khác biệt `06-security.md`)* | `docs/spec/06-infrastructure.md:170–175,214–248` | Prompt nhắc `06-security.md` nhưng repo chỉ có `06-infrastructure.md`. Đã đối chiếu đúng `06-infrastructure.md` (header `no-store`, `noindex`, `X-Frame-Options: DENY`, phạm vi CI frontend) và `02-architecture.md` §6–§7. | Ghi nhận nguồn thực tế trong hồ sơ task để tránh liên kết gãy trong CI `validate_changed_docs`. |

### Quyết định đã khóa bởi Owner (23/11/2026)
- **GAP-P1-13-01 → Phương án A:** Cache nhãn biến thể trong TanStack Query; khi mở URL trực tiếp thì hydrate giới hạn tối đa `5` trang × `100` biến thể, dừng sớm ngay khi đủ các `variantId` cần thiết; nếu không tìm thấy thì hiển thị `variantId` rút gọn kèm nút sao chép; tuyệt đối không bịa SKU hay tên biến thể. Không sửa backend.
- **ASSUMPTION-01 → Phương án A:** Giữ nguyên `apps/admin/src/lib/navigation.ts` (`inventory.read` làm cổng vào `/inventory`), kiểm tra quyền chi tiết theo từng màn hình/thao tác trong `InventoryScreen`; giữ nguyên **Tier B**.
- **GAP-P1-13-02 (khóa xử lý lỗi 409/422):** Chỉ HTTP `409` kèm `code === "concurrency_conflict"` mới là xung đột `rowVersion`; toàn bộ mã lỗi nghiệp vụ kho xử lý thống nhất trên cả `409` và `422`, giữ nguyên dữ liệu form chưa lưu và gắn lỗi trực tiếp theo dòng `lines[variantId]`.
- **Mục 7.3 (phạm vi verify Docker):** Chỉ chạy `./scripts/verify-linux.ps1 -Mode frontend -Manifest artifacts/p1-13/candidate.manifest` (bỏ lượt verify backend vì không thay đổi backend và `P2-01` đang dùng Docker song song).

---

## 9. Migration/recovery, xác minh & bàn giao

- **Migration/recovery:** `none` (chỉ sửa 3 file và tạo mới 23 file trong `apps/admin`, cộng hồ sơ này — tổng 27 đường dẫn, không đụng `backend/`, `docs/spec/`, `packages/api-client/`, `apps/store/`, `package.json`, `pnpm-lock.yaml`, `navigation.ts`, `providers.tsx`, `auth-forms.tsx`). Khôi phục bằng cách hoàn tác delta của nhánh `feature/p1-13-admin-inventory`; không ảnh hưởng schema DB hay dữ liệu đã commit qua API.
- **Kết quả kiểm thử thực tế tại G3 & Remediation sau G4 (`apps/admin`):**
  - *Remediation F1–F4 (sau G4 PASS manifest `6616cbd7…`):*
    - **F1 [P2] FIXED** (`apps/admin/src/components/inventory/variant-picker.tsx`): tách `VariantPicker` để unmount `VariantPickerDialog` khi `open === false`, thêm `resetAndClose()` xóa `selectedMap`, `search`, `q`, `page` trên mọi đường đóng/xác nhận (Đóng/Escape/backdrop/chọn) và lọc `chosenList` theo `excludedIds`; bổ sung test hồi quy Playwright `P1-13-B15` kiểm cả luồng xóa dòng rồi mở lại (`POST`/`PATCH`) và đóng bằng nút Đóng.
    - **F2 [P3] FIXED** (`apps/admin/tests/browser/inventory.spec.ts` — `P1-13-B08`): bổ sung ca `sales` gửi báo vỡ/hỏng (`damage`) và assert payload `POST /api/v1/admin/inventory/adjustments` (`{ variantId, type: "damage", quantityDelta: -2, reason }`).
    - **F3 [P3] FIXED** (`apps/admin/tests/inventory.test.tsx` `P1-13-U07` & `apps/admin/tests/browser/inventory.spec.ts` `P1-13-B16`): bổ sung assert dừng sớm ở trang 2 khi đủ `variantId` (0 request khi đã cache) và fallback ngoài 500 biến thể hiển thị `variantId` rút gọn (không bịa SKU) kèm nút `Sao chép mã` hoạt động.
    - **F4 [P3] FIXED** (`apps/admin/src/lib/api.ts`, `P1-13-U09`, `P1-13-B05`): bổ sung mã `unsupported_format` (khớp `ProductImportFile.Unsupported()` 415 của backend) vào `ApiError.messages` và cập nhật mock `B05` dùng đúng `unsupported_format`.
  - `corepack pnpm --filter @teapremium/admin typecheck` (`tsc --noEmit`): **PASS** (exit 0).
  - `corepack pnpm --filter @teapremium/admin lint` (`eslint . --max-warnings=0`): **PASS** (exit 0, 0 warnings).
  - `corepack pnpm --filter @teapremium/admin test:unit`: **PASS 48/48, 0 skipped** (gồm **10/10** unit test `P1-13-U01..U10` tại `apps/admin/tests/inventory.test.tsx` + 38 unit test hồi quy).
  - `corepack pnpm --filter @teapremium/admin exec playwright test tests/browser/inventory.spec.ts`: **PASS 16/16, 0 skipped** (`P1-13-B01..B16`, gồm 2 ca hồi quy mới `B15`/`B16` + 14 ca nền với `axe-core` 0 vi phạm, viewport `390×844` & `1440×1000`).
  - Lượt kiểm chứng toàn workspace trong Linux container gắn manifest mới (`./scripts/verify-linux.ps1 -Mode frontend -Manifest artifacts/p1-13/candidate.manifest` gồm 48 unit + 106 Playwright browser + build), SHA-256 manifest mới và đường dẫn `verify.log` được báo chính xác trong báo cáo bàn giao sau khi khóa manifest (không sửa thêm bất kỳ tệp nào sau khi ghi manifest).
- **NOT RUN:**
  - `./scripts/verify-linux.ps1` chế độ `backend` / `full`: bỏ qua theo quyết định Owner (23/11/2026) vì không thay đổi backend và `P2-01` đang dùng Docker song song.
  - Live full-stack E2E với PG17/Caddy thật, gửi Telegram/digest (`P2-13`), đối soát `reservations` (`P2-05`), và dashboard tổng hợp (`P2-16`) — ngoài phạm vi P1-13.
- **Review/publish:** `READY_FOR_LOCAL_G4: YES` (Tier B). Dừng tại worktree chưa commit/push để bàn giao G4 độc lập.
