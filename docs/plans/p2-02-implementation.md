# P2-02: Customers: CRUD, chuẩn hóa SĐT, tìm không dấu, tags, địa chỉ, correct-identity, 360° (BR-CRM-01..04, 14)

- **Gate/status:** `READY_FOR_LOCAL_G4` (G3 hoàn tất trên worktree `D:\tra-hien-p2-02`, base `origin/main` `85218d1` có `P2-01`; áp dụng đầy đủ các quyết định owner khóa cứng `GAP-P202-01 = C`, `GAP-P202-02 = B`, `GAP-P202-03 = A`, `ASSUMPTION-P202-01..03`, `R-P201-04`; chưa commit/push).
- **Tier:** `A` — xử lý trực tiếp PII khách hàng (họ tên, SĐT, email, Zalo, địa chỉ nhận hàng), phân quyền Admin (`owner` / `sales` / `content`, `AC-P2-09`), thao tác nhạy cảm `correct-identity` có lý do + audit không lộ PII thô (`FR-CRM-09`, `BR-CRM-04`, `02-architecture.md` §5.4), và khóa lạc quan `xmin` trên `crm.customers`.
- **Worktree/base:** `D:\tra-hien-p2-02`, nhánh `feature/p2-02-customers`, base `origin/main` `85218d119b36d5a1b8977a75c9fb71abf454ddde` (`P2-01` đã merge).
- **Readiness (scoped G1/G2):** `GO` cho `G3`.
  - Baseline G1 đầy đủ tại `cadce575597738bf40ad7c45689482de5a5fbc3d` ([spec-inventory.md](../analysis/spec-inventory.md), [spec-gaps.md](../analysis/spec-gaps.md)); G2 nền tại [conformance-review.md](../architecture/conformance-review.md) và [task-readiness.md](task-readiness.md) (mốc B01 `dadc4435e8f115faaeb9e8511e56e09d5d517a8a`).
  - Đã đối chiếu trực tiếp mã `P2-01` đã merge trên `85218d1` (`CrmSalesFinanceEntities.cs`, `CrmSalesFinanceModel.cs`, `GrantsSql.cs`, `AppDbContext.cs`): `crm.customers` (kèm `xmin`, `ck_customer_contact` có `merged_into_customer_id IS NOT NULL`, `ux_customers_phone`, `ix_customers_search`), `crm.tags`, `crm.customer_tags`, `crm.customer_addresses` (`ux_customer_default_address`), `crm.customer_accounts`, `crm.customer_interests`, `crm.consents`, `sales.orders`, `crm.customer_code_seq` (`START 1000`) và grants `tp_app`/`tp_readonly` (không có `DELETE`/`TRUNCATE` trên `crm.customers`, có `USAGE` trên `crm.customer_code_seq` và `EXECUTE` trên `public.f_unaccent`) khớp hoàn toàn với kế hoạch; không có độ lệch DDL/model.
  - Quyết định owner đã khóa cứng trước khi code: `GAP-P202-01 = C`, `GAP-P202-02 = B`, `GAP-P202-03 = A`, `ASSUMPTION-P202-01 = A`, `ASSUMPTION-P202-02 = A`, `ASSUMPTION-P202-03 = Đã duyệt`, `R-P201-04` siết đầu số VN ở Domain/Application không phá giá trị ẩn danh của `BR-CRM-11`.
- **Traceability:** xem Mục 1.1 và ma trận ánh xạ code/test ở Mục 5–6.
- **Scope / out of scope:** xem Mục 1.2.
- **Cases:** happy, bad, boundary, permission (`401`/`403`), concurrency (`xmin` `409`, đua SĐT trùng `23505` → `409`), audit redaction → tên test cụ thể ở Mục 6.
- **Migration/recovery:** `none` tại `P2-02` — toàn bộ 10 bảng `crm.*`, `sales.orders`, `crm.customer_code_seq`, indexes (`ux_customers_phone`, `ix_customers_search`, `ux_customer_default_address`, `ux_tags_name`) và grants `tp_app`/`tp_readonly` đã thuộc migration của `P2-01`. `P2-02` chỉ gắn `[Audited]` ở tầng C# runtime, không sửa DDL hay snapshot EF. Khôi phục bằng cách revert commit code + OpenAPI/client của nhánh.
- **Verification:**
  - `./scripts/verify-linux.ps1 -Mode api -WriteBack` → **PASS** (`artifacts/verify-linux/20261123-143626-839-api/verify.log`, `0` warning, `0` error; `packages/api-client/openapi.json` sha256 `b8a8fb11fa7bfdf8dc8fdc73fd369f91680d8437bfb535561993dff078dbdb1e`, `packages/api-client/src/generated/schema.ts` sha256 `588431ed989e1439915cecf937ecb16d3ac20ec597e8f8eaa92c330a1c8fe50e`, hash verified).
  - Vòng test tập trung `./scripts/verify-linux.ps1 -Filter "FullyQualifiedName~Customer|FullyQualifiedName~AuditValuePolicy|FullyQualifiedName~OpenApi|FullyQualifiedName~CompiledArchitectureTests"` → **PASS** (`artifacts/verify-linux/20261123-143748-663-backend/verify.log`: CI scope `27/27`, `TeaPremium.UnitTests` `92/92`, `TeaPremium.ArchitectureTests` `29/29`, `TeaPremium.IntegrationTests` `99/99`, `0` skipped, `build -warnaserror` `0` warnings, `dotnet format whitespace` PASS, `OpenAPI/client are current`).
  - Lần chạy full `-Mode backend -Manifest` và `-Mode frontend -Manifest` trên bản đóng dấu `artifacts/p2-02/candidate.manifest` chạy ngay sau khi chốt manifest (SHA-256 manifest và đường dẫn log cuối ghi trong báo cáo gửi Orchestrator để không sửa bất kỳ file nào sau khi ghi manifest).
- **Risks/follow-ups:** xem Mục 7 (follow-up `P3-05`: quy trình đổi/đồng bộ SĐT cho khách đã có `crm.customer_accounts` theo `GAP-P202-01 = C`; `P2-03`: import; `P2-04`: interactions/follow-ups; `P2-11`: manual interests + recompute stats; `P2-12`: consents/merge/erase/export).
- **Review/publish:** `READY_FOR_LOCAL_G4` — chờ G4 độc lập khác hãng kiểm tra `artifacts/p2-02/candidate.manifest` và chờ owner cho phép `publish-pr`.

---

## 1. Định danh liên kết và ranh giới phạm vi

### 1.1 Nguồn đặc tả và mã định danh (`ID`, `FR`, `BR`, `NFR`, `AC`, `DEC`)

- **Task ID:** `P2-02` ([07-roadmap.md](../spec/07-roadmap.md) dòng `87`, phụ thuộc `P2-01` dòng `86`).
- **Tài liệu đặc tả tham chiếu:**
  - [00-README.md](../spec/00-README.md) dòng `40-46` (quy ước VND `bigint`, UTC `timestamptz`, `Guid.CreateVersion7()`), dòng `85-115` (`DEC-*`).
  - [01-requirements.md](../spec/01-requirements.md) dòng `35-43` (3 vai trò Admin), dòng `136-150` (`FR-CRM-01..12`), dòng `190` (`FR-PLT-01`), dòng `196-210` (`NFR-*`).
  - [02-architecture.md](../spec/02-architecture.md) dòng `68-77` (§3 ranh giới module), dòng `78-97` (§4 use case), dòng `105-109` (§5.2 transaction & `xmin`), dòng `122-129` (§5.4 `[Audited]` & `AuditValuePolicy`), dòng `178-181` (§5.9 ProblemDetails & log che PII), dòng `189-236` (§6–§7 xác thực, CSRF, ma trận quyền).
  - [03-database.md](../spec/03-database.md) dòng `10-18` (§1 mã `KH` + sequence, `xmin`), dòng `76-80` (§3.5 khách hàng), dòng `82-92` (§3.6 địa giới 2 cấp & không tự sửa địa chỉ cũ), dòng `94-99` (§3.7 tìm kiếm không dấu `f_unaccent` & tìm đuôi SĐT), dòng `104-107` (§3.9 audit), dòng `134-158` (§4 DB roles & `REVOKE DELETE, TRUNCATE ON crm.customers`).
  - [schema.sql](../spec/schema.sql) dòng `14-22` (`unaccent`, `pg_trgm`, `public.f_unaccent`), dòng `555-713` (khối `crm` + `crm.customer_code_seq`).
  - [04-business-rules.md](../spec/04-business-rules.md) dòng `12-20` (`BR-IAM-03`, `BR-IAM-04`, `BR-IAM-06`), dòng `218-237` (`BR-CRM-01..14`).
  - [05-api.md](../spec/05-api.md) dòng `3-29` (§1 quy ước chung, phân trang, `rowVersion`, CSRF, bảng mã lỗi), dòng `199-218` (§4.5 Khách hàng).
  - [06-infrastructure.md](../spec/06-infrastructure.md) dòng `15`, `173`, `216-230` (không gửi PII vào log/telemetry, `Cache-Control: no-store`, CI scope).
- **Mã định danh áp dụng trực tiếp cho `P2-02`:**
  - `DEC`: `DEC-01`, `DEC-03`, `DEC-13`, `DEC-14`, `DEC-15`, `DEC-16`, `DEC-20`, `DEC-30`.
  - `FR`: `FR-CRM-01` (hồ sơ khách + tags), `FR-CRM-02` (sổ địa chỉ), `FR-CRM-03` (màn hình 360° — phần API đọc tổng hợp tại mốc `P2-02`), `FR-CRM-09` (`correct-identity` có lý do + audit), `FR-PLT-01` (audit thay đổi nhạy cảm che PII).
  - `BR`: `BR-CRM-01` (chuẩn hóa SĐT di động VN → E.164), `BR-CRM-02` (chống trùng SĐT trên hồ sơ chưa gộp → `409 customer_phone_exists` + `existingCustomerId`), `BR-CRM-04` (khóa sửa `phone_e164`/`origin`/`acquisition_source`/`had_legacy_purchase` trên `PATCH` thường; tách sang `correct-identity`), `BR-CRM-14` (`customers.search_text = f_unaccent(...)`), phối hợp `BR-CRM-11` (không làm gãy giá trị ẩn danh của `P2-12`) và `BR-IAM-03`/`BR-IAM-04`/`BR-IAM-06` (nếu `correct-identity` chạm hồ sơ đã có `customer_accounts`). *(Lưu ý: tiêu đề `07-roadmap.md:87` ghi gộp `BR-CRM-01..04, 14`, trong đó `BR-CRM-03` là Import khách cũ đã tách riêng cho `P2-03` ở `07-roadmap.md:88`).*
  - `NFR`: `NFR-01` (API admin p95 < 500 ms với 5.000 khách), `NFR-04` (không rò PII ra log/URL/response không phép), `NFR-07` (HTTPS, cookie `AdminCookie`, CSRF, phân quyền), `NFR-08` (không tự migrate lúc start), `NFR-09` (test mọi `BR-*` và integration test trên PostgreSQL 17 thật), `NFR-12` (audit thao tác dữ liệu cá nhân).
  - `AC`: trực tiếp nghiệm thu **`AC-P2-09`** ([07-roadmap.md](../spec/07-roadmap.md) dòng `114`: tài khoản `content` gọi `admin/customers` → `403`); kiểm chứng cơ chế `xmin` → `409` tương ứng mẫu `AC-P2-05` cho aggregate `Customer`; giữ xanh `AC-P0-04` (audit che PII) và `AC-P0-05` (kiến trúc tầng/module & cấm gọi giờ trực tiếp).

### 1.2 Trong phạm vi / Ngoài phạm vi

- **Trong phạm vi (`P2-02`):**
  1. Domain rules CRM (`TeaPremium.Domain.Crm`): chuẩn hóa SĐT di động VN sang E.164 (`BR-CRM-01` + `R-P201-04`), dựng nguồn `search_text` kèm SĐT dạng `0xxxxxxxxx` (`BR-CRM-14`), rút chữ số tìm kiếm đuôi SĐT (`03-database.md:98`), sinh mã `KH` từ `crm.customer_code_seq`, kiểm bất biến liên hệ (`ck_customer_contact`, `ck_customer_not_self_ref`).
  2. CRUD khách hàng Admin (`GET / POST /api/v1/admin/customers`, `GET / PATCH /api/v1/admin/customers/{id}`): lọc đa tiêu chí, phân trang, sắp xếp, tìm kiếm không dấu tiếng Việt + SĐT, khóa lạc quan `xmin` (`rowVersion` mờ).
  3. Hiệu chỉnh định danh (`POST /api/v1/admin/customers/{id}/correct-identity`): tách quyền `customers.write` (đổi SĐT) và `owner` (đổi `origin`, `acquisitionSource`, `hadLegacyPurchase`) theo `BR-CRM-04`, bắt buộc `reason`, ghi audit chuyên biệt không lộ PII/lý do thô.
  4. Quản lý nhãn khách (`GET / POST / PATCH /api/v1/admin/tags[/{id}]`, `PUT /api/v1/admin/customers/{id}/tags`).
  5. Quản lý sổ địa chỉ khách (`GET / POST / PATCH / DELETE /api/v1/admin/customers/{id}/addresses[/{aid}]`): đảm bảo tối đa 1 địa chỉ `is_default = true` mỗi khách (`ux_customer_default_address`), kiểm tra địa giới hành chính qua `IAdministrativeReferenceData` (`03-database.md` §3.6), hỗ trợ `ward_name_text` dự phòng.
  6. Dữ liệu 360° khách hàng (`GET /api/v1/admin/customers/{id}` và endpoint đọc danh sách đơn của khách `GET /api/v1/admin/customers/{id}/orders` theo Phương án A `GAP-P202-03`).
  7. Đánh dấu `[Audited]` + cấu hình `AuditValuePolicy` cho các entity CRM (`crm.customers`, `crm.customer_addresses`, `crm.tags`, `crm.customer_accounts`) theo `02-architecture.md:123-126`; sinh lại `openapi.json` + `schema.ts` qua `./scripts/verify-linux.ps1 -Mode api -WriteBack`.
- **Ngoài phạm vi (bàn giao đúng task kế tiếp, tuyệt đối không viết trước):**
  - Import khách cũ CSV/XLSX có `dryRun` (`BR-CRM-03`, `POST admin/customers/import`) → **`P2-03`**.
  - Interactions, hẹn chăm sóc, hoàn thành hẹn và danh sách "Cần liên hệ hôm nay" (`BR-CRM-06/07`, `GET/POST/PATCH admin/customers/{id}/interactions*`, `GET admin/follow-ups`) → **`P2-04`**.
  - Aggregate `Order`, tạo/sửa/hủy đơn, thanh toán, vận đơn, đổi trả → **`P2-05`..`P2-10`**.
  - Tự suy sở thích từ đơn đã giao, thêm/xóa sở thích `manual` (`POST/DELETE admin/customers/{id}/interests[/{iid}]`, `BR-CRM-08`) và job đêm `RecomputeCustomerStats` → **`P2-11`**.
  - Ghi/rút consent Admin (`PUT admin/customers/{id}/consents`), gộp khách (`merge`), xóa/ẩn danh dữ liệu (`erase`), xuất dữ liệu (`export`) (`BR-CRM-05, 09, 11, 13`) → **`P2-12`**.
  - Chiến dịch marketing & lịch sử chiến dịch khách nhận (`GET admin/customers/{id}/campaigns`) → **`P5-02`** (bảng `marketing.*` hoãn tới `P5-01`/`P5-02`).
  - Đăng ký/đăng nhập tài khoản khách web và nhân viên bấm xác minh SĐT mở khóa lịch sử (`POST admin/customers/{id}/verify-account-phone`, `BR-IAM-04 b`) → **`P3-05`**.
  - Giao diện Admin UI khách hàng (`apps/admin`) → **`P2-14`**.

---

## 2. Hợp đồng API chi tiết đối chiếu `05-api.md` §4.5

Mọi endpoint dưới đây đặt dưới `/api/v1/admin/...`, bắt buộc HTTPS (`400 https_required`), đúng `AdminHost` nếu cấu hình (`403 forbidden`), cookie `AdminCookie` đã hoàn tất onboarding `AdminSecurity.Ready` (`401 unauthenticated` / `403 forbidden`), `Cache-Control: no-store`, và header `X-CSRF-TOKEN` hợp lệ cho mọi `POST`/`PATCH`/`PUT`/`DELETE` (`400 csrf_invalid`). JSON nghiêm ngặt (`camelCase`, `AllowDuplicateProperties = false`, `UnmappedMemberHandling = Disallow`, `NumberHandling = Strict`).

### 2.1 Bảng endpoint triển khai trong `P2-02`

| # | Method & Path (`/api/v1/...`) | Dòng `05-api.md` | Quyền tối thiểu | Request (query / body chính) | Response thành công | Lỗi đặc thù (`ProblemDetails`) |
|---:|---|---|---|---|---|---|
| 1 | `GET admin/customers` | `202` | `customers.read` | Query: `page=1`, `pageSize=20` (`1..100`), `sort` (`createdAt:desc` mặc định \| `createdAt:asc` \| `lastDeliveredAt:desc` \| `lastDeliveredAt:asc` \| `lifetimeValueVnd:desc` \| `lifetimeValueVnd:asc` \| `fullName:asc` \| `fullName:desc`), `q`, `group`, `tag` (`Guid`), `interest` (`<type>:<id>`), `lastDeliveredBefore`, `lastDeliveredAfter` (`yyyy-MM-dd` giờ VN hoặc ISO UTC), `minLtv` (`long >= 0`), `contactStatus`, `hasConsent` (`<channel>:<purpose>`) | `200` `CatalogPage<CustomerSummaryView>` (`{ items, page, pageSize, totalItems }`; mặc định chỉ gồm hồ sơ `mergedIntoCustomerId == null`) | `400 validation_failed`, `401`, `403` |
| 2 | `POST admin/customers` | `203` | `customers.write` | Body: `{ fullName, phone?, email?, zaloName?, customerGroup?, isVip?, acquisitionSource?, referredByCustomerId?, contactStatus?, noteSummary?, hadLegacyPurchase?, tagIds?[] }` (`origin` luôn gán `"admin"`) | `201` `CustomerDetailView` + header `Location` | `400 validation_failed` / `invalid_phone` / `contact_required`, `409 customer_phone_exists` (`data: { existingCustomerId }`), `401`, `403`, `415` |
| 3 | `GET admin/customers/{id}` | `204` | `customers.read` | Path: `id:guid` | `200` `CustomerDetailView` (360°: hồ sơ + `rowVersion` + thống kê đơn + `tags[]` + `addresses[]` + `interests[]` + `consents[]` + `accountSummary`) | `401`, `403`, `404 not_found` |
| 4 | `PATCH admin/customers/{id}` | `204` | `customers.write` | Body JSON partial bắt buộc `rowVersion`; cho sửa `fullName`, `email`, `zaloName`, `customerGroup`, `isVip`, `referredByCustomerId`, `contactStatus` (`active`\|`do_not_contact`), `noteSummary`. **Cấm** gửi `phone`/`phoneE164`/`origin`/`acquisitionSource`/`hadLegacyPurchase` (`BR-CRM-04`) | `200` `CustomerDetailView` (`rowVersion` mới) | `400 validation_failed` / `identity_field_requires_correction` / `contact_required`, `404 not_found`, `409 concurrency_conflict` / `customer_erased` / `customer_merged`, `401`, `403`, `415` |
| 5 | `POST admin/customers/{id}/correct-identity` | `205` | `customers.write` (sửa `phone`); **vai trò `owner`** nếu gửi `origin`, `acquisitionSource` hoặc `hadLegacyPurchase` (`BR-CRM-04`) | Body: `{ rowVersion, phone?, origin?, acquisitionSource?, hadLegacyPurchase?, reason }` (phải có ≥ 1 trường định danh xuất hiện trong JSON; `phone: null` chỉ hợp lệ khi khách có `zaloName`; nếu khách đã có dòng `crm.customer_accounts` thì từ chối đổi/xóa `phone` theo `GAP-P202-01 = C`) | `200` `CustomerDetailView` (`rowVersion` mới) | `400 validation_failed` / `invalid_phone` / `contact_required`, `403 forbidden` (`sales` đụng trường chỉ `owner`), `404 not_found`, `409 concurrency_conflict` / `customer_phone_exists` (`data: { existingCustomerId }`) / `account_exists` / `customer_erased` / `customer_merged`, `401`, `415` |
| 6 | `GET admin/customers/{id}/orders` *(`GAP-P202-03 = A`)* | `206` | `customers.read` | Path: `id:guid`; Query: `page=1`, `pageSize=20` (`1..100`) | `200` `CatalogPage<CustomerOrderSummaryView>` (đọc thật `sales.orders` qua `ICustomerOrderQueries`; trước `P2-05` trả `{ items: [], page, pageSize, totalItems: 0 }`) | `400 validation_failed`, `401`, `403`, `404 not_found` |
| 7 | `GET admin/tags` | `210` | `customers.write` *(`GAP-P202-02 = B`, đúng `05-api.md:210`)* | Không cần tham số (sắp `name ASC, id ASC`) | `200` `IReadOnlyList<CustomerTagView>` (`{ id, name, color }`) | `401`, `403` |
| 8 | `POST admin/tags` | `210` | `customers.write` | Body: `{ name, color? }` | `201` `CustomerTagView` + `Location` | `400 validation_failed`, `409 tag_name_exists`, `401`, `403`, `415` |
| 9 | `PATCH admin/tags/{id}` *(`GAP-P202-02 = B`)* | `210` | `customers.write` | Path: `id:guid`; Body partial: `{ name?, color? }` | `200` `CustomerTagView` | `400 validation_failed`, `404 not_found`, `409 tag_name_exists`, `401`, `403`, `415` |
| 10 | `PUT admin/customers/{id}/tags` | `210` | `customers.write` | Body: `{ tagIds: Guid[], rowVersion?: string }` (`ASSUMPTION-P202-01 = A`) | `200` `CustomerTagsResult` (`{ customerId, rowVersion, tags[] }`) | `400 validation_failed`, `404 not_found`, `409 concurrency_conflict` / `customer_erased` / `customer_merged`, `401`, `403`, `415` |
| 11 | `GET admin/customers/{id}/addresses` | `209` | `customers.write` *(`GAP-P202-02 = B`, đúng `05-api.md:209`)* | Path: `id:guid` | `200` `IReadOnlyList<CustomerAddressView>` (`isDefault DESC, createdAt ASC, id ASC`) | `401`, `403`, `404 not_found` |
| 12 | `GET admin/customers/{id}/addresses/{aid}` | `209` | `customers.write` *(`GAP-P202-02 = B`, đúng `05-api.md:209`)* | Path: `id:guid`, `aid:guid` | `200` `CustomerAddressView` | `401`, `403`, `404 not_found` |
| 13 | `POST admin/customers/{id}/addresses` | `209` | `customers.write` | Body: `{ label?, receiverName, receiverPhone, provinceCode, wardCode?, wardNameText?, addressLine, isDefault?, rowVersion? }` | `201` `CustomerAddressView` + `Location` | `400 validation_failed` / `invalid_phone` / `invalid_address_location`, `404 not_found`, `409 concurrency_conflict` / `customer_erased`, `422 administrative_data_not_ready`, `401`, `403`, `415` |
| 14 | `PATCH admin/customers/{id}/addresses/{aid}` | `209` | `customers.write` | Body partial: `{ label?, receiverName?, receiverPhone?, provinceCode?, wardCode?, wardNameText?, addressLine?, isDefault?, rowVersion? }` | `200` `CustomerAddressView` | `400 validation_failed` / `invalid_phone` / `invalid_address_location`, `404 not_found`, `409 concurrency_conflict` / `customer_erased`, `422 administrative_data_not_ready`, `401`, `403`, `415` |
| 15 | `DELETE admin/customers/{id}/addresses/{aid}` | `209` | `customers.write` | Path: `id:guid`, `aid:guid` (không bắt body bắt buộc; nếu có body `{ rowVersion }` thì kiểm `xmin` theo `ASSUMPTION-P202-01`) | `204 No Content` | `400 validation_failed`, `404 not_found`, `409 concurrency_conflict` / `customer_erased`, `401`, `403` |

### 2.2 Đối chiếu các dòng còn lại trong `05-api.md` §4.5 (không làm ở `P2-02`, không bỏ sót)

- `GET admin/customers/{id}/interactions`, `POST/PATCH admin/customers/{id}/interactions[/{iid}]`, `POST .../complete-follow-up`, `GET admin/follow-ups` (`05-api.md:206-208`) → thuộc `P2-04`.
- `GET admin/customers/{id}/campaigns` (`05-api.md:206`) → thuộc `P5-02`.
- `POST/DELETE admin/customers/{id}/interests[/{iid}]` (`05-api.md:211`) → thuộc `P2-11` (`BR-CRM-08`, xác nhận tại `GAP-P202-03`).
- `PUT admin/customers/{id}/consents` (`05-api.md:212`) → thuộc `P2-12`.
- `POST admin/customers/{id}/verify-account-phone` (`05-api.md:213`) → thuộc `P3-05` (`BR-IAM-04`).
- `POST admin/customers/import?dryRun=` (`05-api.md:214`) → thuộc `P2-03`.
- `POST admin/customers/{id}/merge`, `.../erase`, `GET .../export` (`05-api.md:215-217`) → thuộc `P2-12`.
- **Điểm thiếu/lệch trong `05-api.md` §4.5 đã ghi nhận thành GAP ở Mục 7:** `PATCH admin/tags` thiếu `[/{id}]` (`05-api.md:210`), quyền `GET` của `addresses`/`tags` ghi gộp `customers.write` (`05-api.md:209-210` — `GAP-P202-02`), và hành vi `correct-identity` khi đụng `customer_accounts` chưa mô tả (`05-api.md:205` — `GAP-P202-01`).

---

## 3. Quy tắc nghiệp vụ chi tiết

### 3.1 Chuẩn hóa SĐT (`BR-CRM-01` + `R-P201-04` + tương thích `BR-CRM-11`)

- **Nguồn:** [04-business-rules.md](../spec/04-business-rules.md) dòng `220` (`BR-CRM-01`), dòng `234` (`BR-CRM-11`), [schema.sql](../spec/schema.sql) dòng `561`, `614`, `629` và mục `R-P201-04` trong `docs/plans/p2-01-implementation.md`.
- **Thuật toán tại `CustomerRules.TryNormalizePhone(string? raw, out string? e164)`:**
  1. Nếu `raw` là `null` → `e164 = null` (hợp lệ cho trường tùy chọn `customers.phone_e164`; không hợp lệ cho trường bắt buộc `customer_addresses.receiver_phone`). Nếu chứa `\0` hoặc rỗng/chỉ khoảng trắng → trả lỗi `invalid_phone`.
  2. Loại bỏ đúng tập ký tự phân cách cho phép: khoảng trắng Unicode (`char.IsWhiteSpace`), `.`, `-`, `(`, `)`.
  3. Kiểm tra chỉ gồm chữ số ASCII `'0'..'9'` (cấm chữ số Unicode hệ khác) và đầu số thứ 2 bắt buộc thuộc tập di động VN `{ '3', '5', '7', '8', '9' }`:
     - Mẫu `0[35789][0-9]{8}` (10 ký tự) → `"+84" + cleaned[1..]`.
     - Mẫu `84[35789][0-9]{8}` (11 ký tự) → `"+" + cleaned`.
     - Mẫu `\+84[35789][0-9]{8}` (12 ký tự) → `cleaned`.
  4. Mọi mẫu khác (máy bàn `024...`/`028...`, đầu `01/04/06`, thiếu/thừa chữ số, `+840901234567`, hoặc người dùng cố tình gửi giá trị ẩn danh `"+84000000000"`) → từ chối `400` với `code = "invalid_phone"`, `errors = { "<field>": ["invalid_phone"] }`.
- **Bảo toàn `BR-CRM-11` (`P2-12`):**
  - DB giữ nguyên `CHECK (... ~ '^\+84[0-9]{9}$')` từ `P2-01`.
  - Khi đọc dữ liệu từ DB ra DTO, ứng dụng giữ nguyên chuỗi đã lưu, không chạy lại `TryNormalizePhone` trên dữ liệu đọc; đồng thời `CustomerRules.AnonymizedOrderPhone = "+84000000000"` được công bố riêng cho use case `EraseCustomer` ở `P2-12` để ghi lên `sales.orders` mà không nới lỏng validation cho input người dùng.

### 3.2 Mã khách hàng & chống trùng SĐT (`BR-CRM-02` + `ux_customers_phone`)

- **Sinh mã `KH`:** gọi `SELECT nextval('crm.customer_code_seq')` trong transaction ghi của `tp_app`, định dạng `"KH" + seq.ToString("D6", CultureInfo.InvariantCulture)` → bắt đầu từ `KH001000` (`schema.sql:555,559`), từ `1000000` trở lên giữ nguyên đủ chữ số (`KH1000000`). Mã đã cấp là bất biến.
- **Bất biến liên hệ (`ck_customer_contact`):** khi tạo mới hoặc cập nhật hồ sơ chưa gộp (`merged_into_customer_id IS NULL`) và chưa xóa (`contact_status <> 'erased'`), bắt buộc có ít nhất một trong `phone_e164` hoặc `zalo_name` (đã `Trim()`, không rỗng). Thiếu cả hai → `400 contact_required`.
- **Chống trùng SĐT (`BR-CRM-02`):**
  1. Sau khi chuẩn hóa `phone_e164` khác `null`, kiểm tra trong transaction xem có dòng `crm.customers` nào thỏa `phone_e164 == normalized && merged_into_customer_id == null && id != currentId` hay không. Nếu có → trả `409 Conflict` với `code = "customer_phone_exists"` và `data = { existingCustomerId = existing.Id }` (`05-api.md:17,26`). Hồ sơ đã gộp (`merged_into_customer_id IS NOT NULL`) hoặc đã ẩn danh (`phone_e164 IS NULL`) không gây chặn.
  2. **Xử lý đua đồng thời (`23505` trên `ux_customers_phone`):** nếu hai request song song cùng vượt qua bước kiểm tra đọc và một request đụng `PostgresException { SqlState = "23505", ConstraintName = "ux_customers_phone" }`, tầng Infrastructure bắt ngoại lệ, cho rollback transaction ghi hiện tại, mở truy vấn đọc sạch `AsNoTracking()` lấy `id` của hồ sơ vừa thắng cuộc (`WHERE phone_e164 = @p AND merged_into_customer_id IS NULL`) và trả đúng `409 customer_phone_exists` kèm `existingCustomerId` thay vì `500`.

### 3.3 Tìm kiếm không dấu tiếng Việt & đuôi SĐT (`BR-CRM-14` + `03-database.md` §3.7)

- **Dựng `customers.search_text` khi ghi (`BR-CRM-14`, [04-business-rules.md](../spec/04-business-rules.md) dòng `237`):**
  - Chuyển `phone_e164` (`+84xxxxxxxxx`) sang dạng nội địa `0xxxxxxxxx` (`"0" + phoneE164[3..]`; `null` nếu không có SĐT).
  - Ghép chuỗi nguồn `source = string.Join(' ', new[] { fullName, zaloName, code, localPhone0x, email }.Where(s => !string.IsNullOrWhiteSpace(s)))`.
  - Gọi hàm PostgreSQL `SELECT public.f_unaccent({source}) AS "Value"` ngay trong transaction trước `SaveChangesAsync` và gán trực tiếp vào `entity.SearchText` của `Customer`. **Tuyệt đối không dùng raw `UPDATE` hay `ExecuteUpdate`** để tuân thủ `02-architecture.md:127-128` (không đi vòng qua `AuditSaveChangesInterceptor` và không làm tăng `xmin` hai lần).
- **Truy vấn `GET /api/v1/admin/customers?q=` ([03-database.md](../spec/03-database.md) dòng `95-98`, `ASSUMPTION-P202-02`):**
  - `q` rỗng hoặc toàn khoảng trắng → bỏ qua điều kiện tìm kiếm.
  - Tính `norm = await db.Database.SqlQuery<string>($"SELECT public.f_unaccent({q.Trim()}) AS \"Value\"").SingleAsync(ct)`.
  - Áp literal substring `x.SearchText.Contains(norm)` (Npgsql tự escape `%`, `_`, `\`, sinh `search_text LIKE '%' || @p || '%' ESCAPE '\'` khai thác chỉ mục GIN `ix_customers_search` `gin_trgm_ops`).
  - Nếu `q` chỉ chứa các ký tự SĐT hợp lệ (`0-9`, `+`, khoảng trắng, `.`, `-`, `(`, `)`) và rút ra được chuỗi chữ số ASCII `digits` (độ dài ≥ 3): đổi tiền tố `84...` hoặc `0...` sang phần đuôi tương ứng `tailDigits` và nối `OR (x.PhoneE164 != null && x.PhoneE164.EndsWith(tailDigits))` theo đúng `03-database.md:98`, giúp nhân viên tìm được cả bằng `"0901 234 567"`, `"+84901-234-567"` lẫn 4 số cuối `"4567"`.
- **Các bộ lọc còn lại của `GET /api/v1/admin/customers` (`05-api.md:202`):**
  - `group`: khớp `customer_group` (`retail | wholesale | reseller`).
  - `tag`: `EXISTS` trên `crm.customer_tags` với `tag_id = @tag`.
  - `interest`: phân tích `<type>:<value>`; với `type ∈ {material, artisan, product_type, category}` thì `<value>` phải là `Guid` khớp `crm.customer_interests(interest_type, ref_id)`; với `type = price_band` thì `<value> ∈ {under_1m, 1m_3m, 3m_10m, over_10m}` khớp `crm.customer_interests(interest_type, price_band)`. Sai cú pháp → `400 validation_failed`.
  - `lastDeliveredAfter` / `lastDeliveredBefore`: chấp nhận `yyyy-MM-dd` (quy đổi đầu ngày theo `BusinessCalendar` múi giờ `Asia/Ho_Chi_Minh`) hoặc ISO 8601 có offset UTC.
  - `minLtv`: `lifetime_value_vnd >= @minLtv` (`minLtv >= 0`).
  - `contactStatus`: `contact_status = @contactStatus` (`active | do_not_contact | erased`).
  - `hasConsent`: phân tích `<channel>:<purpose>` hợp lệ theo enum `crm.consents`, lọc `EXISTS (SELECT 1 FROM crm.consents c WHERE c.customer_id = x.id AND c.channel = @ch AND c.purpose = @pu AND c.status = 'granted')`.

### 3.4 Quản lý Tags (`crm.tags`, `crm.customer_tags`, `05-api.md:210`)

- `Tag.Name`: `Trim()`, độ dài `1..60`, không chứa `\0`; trùng `ux_tags_name` → `409 tag_name_exists`. `Tag.Color`: tùy chọn, `null` hoặc mã màu hex `#RRGGBB` (`^#[0-9A-Fa-f]{6}$`).
- `PUT /api/v1/admin/customers/{id}/tags`: khóa cha `SELECT *, xmin FROM crm.customers WHERE id = {id} FOR NO KEY UPDATE`, từ chối nếu khách `erased` hoặc đã `merged`, khử trùng `tagIds`, kiểm toàn bộ `tagIds` tồn tại trong `crm.tags` (thiếu → `400 validation_failed`), tính tập thêm/bớt trên `crm.customer_tags` (chỉ xóa dòng thừa và chèn dòng thiếu, idempotent khi gửi lại cùng mảng), cập nhật `customer.UpdatedAt = clock.UtcNow.UtcDateTime` để nâng `xmin` của `Customer`.

### 3.5 Sổ địa chỉ khách hàng (`crm.customer_addresses`, `03-database.md` §3.6, `05-api.md:209`)

- **Chuẩn hóa & kiểm tra địa giới:**
  - `receiverName` (`1..150`), `addressLine` (`1..255`), `label` (`<= 80`), `wardNameText` (`<= 150`), không chứa `\0`.
  - `receiverPhone` bắt buộc đi qua `CustomerRules.TryNormalizePhone` (`BR-CRM-01` → `+84[35789]...`).
  - Bắt buộc `wardCode != null || !string.IsNullOrWhiteSpace(wardNameText)` (`ck_customer_addresses_row`).
  - Khi tạo mới địa chỉ hoặc khi `PATCH` có thay đổi `provinceCode` / `wardCode`: kiểm tra `await adminRef.IsReadyAsync(ct)` (chưa sẵn sàng → `422 administrative_data_not_ready`) và `await adminRef.IsActiveSelectionAsync(provinceCode, wardCode, ct)` (mã tỉnh/phường không tồn tại, `is_active = false` hoặc phường không thuộc tỉnh → `400 invalid_address_location`).
  - Khi `PATCH` chỉ sửa `label` / `receiverName` / `receiverPhone` / `addressLine` / `isDefault` mà không đổi mã hành chính đã lưu: **không tự động ghi đè mã cũ** (`03-database.md:90`). Khi đọc DTO `CustomerAddressView`, trả kèm `provinceName`, `wardName` (tra qua port `IAdministrativeReferenceData.ResolveLocationsAsync`) và cờ `isLocationActive: bool` để UI cảnh báo nếu địa giới đã thay đổi trước khi dùng cho đơn hàng.
- **Đảm bảo duy nhất 1 địa chỉ mặc định (`ux_customer_default_address`):**
  - Do `ux_customer_default_address` (`UNIQUE(customer_id) WHERE is_default`) là chỉ mục non-deferrable, khi thêm/sửa một địa chỉ với `isDefault = true`:
    1. Khóa dòng khách hàng cha `FOR NO KEY UPDATE`.
    2. Nếu khách đang có địa chỉ khác `oldDefault.IsDefault == true`, đặt `oldDefault.IsDefault = false`, `oldDefault.UpdatedAt = now` và gọi `await db.SaveChangesAsync(ct)` trước (bước 1 trong cùng transaction `IUnitOfWork`).
    3. Thêm/cập nhật địa chỉ mục tiêu với `IsDefault = true` và `SaveChangesAsync(ct)` ở bước 2 trong cùng transaction.
  - Khi xóa địa chỉ hoặc đặt `isDefault = false`, không tự ý gán `isDefault = true` sang địa chỉ khác (khách được phép có `0` hoặc `1` địa chỉ mặc định).

### 3.6 Hiệu chỉnh định danh `correct-identity` (`BR-CRM-04`, `FR-CRM-09`, `GAP-P202-01`)

- **Ai được làm (`BR-CRM-04`, [04-business-rules.md](../spec/04-business-rules.md) dòng `223`):**
  - Mọi người gọi phải có `customers.write` (`owner` và `sales`).
  - `sales` (không có role `owner`) **chỉ** được gửi trường `phone` (kèm `rowVersion` và `reason`). Nếu JSON request có mặt bất kỳ khóa nào trong `{ "origin", "acquisitionSource", "hadLegacyPurchase" }` mà người gọi không có role `owner` → trả ngay `403 forbidden` trước khi chạm dữ liệu.
  - `owner` được sửa cả 4 trường (`phone`, `origin`, `acquisitionSource`, `hadLegacyPurchase`).
- **Ràng buộc dữ liệu:**
  - `rowVersion` bắt buộc và phải khớp `xmin` hiện tại của `Customer` (lệch → `409 concurrency_conflict`).
  - `reason` bắt buộc, sau `Trim()` dài `3..500` ký tự, không chứa `\0`.
  - Không cho thao tác trên hồ sơ `contact_status == 'erased'` (`409 customer_erased`) hoặc `merged_into_customer_id != null` (`409 customer_merged`).
  - Nếu sửa `phone`: chuẩn hóa theo `BR-CRM-01`, kiểm tra `ck_customer_contact` (nếu xóa `phone = null` thì `zalo_name` phải có giá trị), kiểm trùng `ux_customers_phone` (`409 customer_phone_exists` + `existingCustomerId`), và cập nhật lại `search_text` (`BR-CRM-14`) trong cùng transaction.
- **Ảnh hưởng tới `crm.customer_accounts` (`GAP-P202-01 = C`, khóa cứng bởi Owner):**
  - Nếu khách **đã có dòng `crm.customer_accounts`** và request `correct-identity` có gửi trường `phone` (đổi hoặc xóa SĐT): từ chối ngay **`409 Conflict` (`code = "account_exists"`)**, không sửa `crm.customers` hay `crm.customer_accounts`. (Nếu `owner` chỉ sửa `origin`, `acquisitionSource` hoặc `hadLegacyPurchase` mà không gửi trường `phone` thì vẫn thực hiện bình thường).
  - Nếu khách **chưa có dòng `crm.customer_accounts`**: cập nhật bình thường (chuẩn hóa `BR-CRM-01`, kiểm `ck_customer_contact`, kiểm trùng `ux_customers_phone`, dựng lại `search_text`, ghi audit + `reason` mã hóa).
  - **Follow-up `P3-05`:** khi xây dựng luồng tài khoản khách hàng web, bổ sung quy trình đổi SĐT đồng bộ `crm.customer_accounts` (xác minh lại `phone_verified_at`/`history_unlocked_at` và xoay `security_stamp` theo `BR-IAM-04/06`).
- **Audit chuyên biệt:** ngoài bản ghi `action = "update"` do `AuditSaveChangesInterceptor` tự sinh trên `crm.customers`, `CustomerIdentityAudit` ghi thêm 1 bản ghi `AuditLog` (`entity_type = "crm.customers"`, `entity_id = id`, `action = "state_change"`) trong cùng transaction, lưu `changed_fields`, before/after của các trường Safe (`origin`, `acquisition_source`, `had_legacy_purchase`), `phone_changed: bool` (không lưu SĐT thô) và `reason` đã mã hóa bằng `IDataProtectionProvider` (`purpose = "teapremium.crm.correct-identity-reason.v1"`, `reason_format = "data_protection_v1"` — cùng mẫu `ProductAudit` ở `P1-05`).

### 3.7 Hồ sơ 360° khách hàng và xử lý dữ liệu đơn hàng trước `P2-05` (`FR-CRM-03`, `05-api.md:204,206`)

- **`GET /api/v1/admin/customers/{id}` (`CustomerDetailView`) gom từ các bảng nội bộ schema `crm`:**
  1. **Thông tin định danh & phân loại:** `id`, `code`, `fullName`, `phoneE164`, `email`, `zaloName`, `customerGroup`, `isVip`, `origin`, `acquisitionSource`, `referredByCustomerId`, `contactStatus`, `noteSummary`, `mergedIntoCustomerId`, `erasedAt`, `createdAt`, `updatedAt`, `rowVersion`.
  2. **Thống kê mua hàng & chăm sóc (`stats`):** đọc trực tiếp từ các cột tổng hợp đã có sẵn trên `crm.customers` (`hadLegacyPurchase`, `deliveredOrdersCount`, `lifetimeValueVnd`, `firstDeliveredAt`, `lastDeliveredAt`, `lastContactedAt`).
  3. **Danh sách nhãn (`tags[]`):** join `crm.customer_tags` → `crm.tags` (`id`, `name`, `color`).
  4. **Sổ địa chỉ (`addresses[]`):** từ `crm.customer_addresses`, kèm `provinceName`, `wardName`, `isLocationActive` qua `IAdministrativeReferenceData`.
  5. **Sở thích (`interests[]`):** từ `crm.customer_interests` (`id`, `interestType`, `refId`, `priceBand`, `source`, `score`, `updatedAt`, sắp `score DESC, updatedAt DESC`).
  6. **Trạng thái đồng ý nhận tin (`consents[]`):** từ `crm.consents` (`id`, `channel`, `purpose`, `status`, `source`, `capturedAt` — **ẩn trường free-text `evidence`** trong response tóm tắt nếu không cần, hoặc chỉ trả cho `customers.read` theo nhu cầu Admin; xem Mục 4).
  7. **Tóm tắt tài khoản web (`accountSummary`):** từ `crm.customer_accounts` nếu có (`hasAccount`, `status`, `phoneVerifiedAt`, `emailVerifiedAt`, `historyUnlockedAt`, `lastLoginAt` — **tuyệt đối không trả `passwordHash`, `securityStamp`**).
- **Xử lý dữ liệu đơn hàng trước `P2-05`:**
  - Trên `GET /api/v1/admin/customers/{id}`: các chỉ số `deliveredOrdersCount = 0`, `lifetimeValueVnd = 0`, `firstDeliveredAt = null`, `lastDeliveredAt = null` có sẵn ngay trên `crm.customers` mà không cần gọi sang module `Orders` hay tạo dữ liệu giả.
  - Trên `GET /api/v1/admin/customers/{id}/orders` (theo Phương án A `GAP-P202-03`): module `Orders` công bố port đọc `ICustomerOrderQueries` (`TeaPremium.Application.Orders.Contracts`), Infrastructure truy vấn chỉ-đọc bảng `sales.orders` (đã được tạo ở `P2-01` cùng index `ix_orders_customer(customer_id, placed_at DESC)`). Trước `P2-05` bảng `sales.orders` rỗng nên endpoint trả tự nhiên `{ items: [], page: 1, pageSize: 20, totalItems: 0 }` (và trả `404 not_found` nếu `customerId` không tồn tại); ngay khi `P2-05`/`P2-06` ghi đơn, endpoint trả danh sách đơn thật mà không phải sửa lại CRM.

---

## 4. Phân quyền, ẩn PII theo quyền (`02-architecture.md` §7) và Audit

### 4.1 Ma trận quyền truy cập CRM (trích đối chiếu [02-architecture.md](../spec/02-architecture.md) dòng `210-235` & `StaffPermissions.cs`)

| Thao tác / Nhóm dữ liệu | Permission / Role bắt buộc | `owner` | `sales` | `content` | Khách / Chưa đăng nhập |
|---|---|:-:|:-:|:-:|:-:|
| Xem danh sách khách, 360°, đơn của khách (`GET admin/customers*`) | `customers.read` | ✓ | ✓ | **✗ (`403`, `AC-P2-09`)** | **✗ (`401` / `403`)** |
| Tạo/sửa khách, gắn tags, thêm/sửa/xóa địa chỉ, quản lý `admin/tags` | `customers.write` | ✓ | ✓ | **✗ (`403`)** | **✗ (`401` / `403`)** |
| `POST .../correct-identity` chỉ đổi `phone` | `customers.write` | ✓ | ✓ | **✗ (`403`)** | **✗ (`401` / `403`)** |
| `POST .../correct-identity` có `origin` / `acquisitionSource` / `hadLegacyPurchase` | `customers.write` + role `owner` (`BR-CRM-04`) | ✓ | **✗ (`403`)** | **✗ (`403`)** | **✗ (`401` / `403`)** |
| Xem lịch sử audit (`GET admin/audit`) | `audit.read` | ✓ | **✗ (`403`)** | **✗ (`403`)** | **✗ (`401` / `403`)** |
| Gộp / Xóa ẩn danh / Xuất dữ liệu khách (`P2-12`) | `customers.merge` / `erase` / `export` | ✓ | **✗ (`403`)** | **✗ (`403`)** | **✗ (`401` / `403`)** |

### 4.2 Quy tắc ẩn PII, bí mật xác thực và giá vốn theo quyền

1. **Chặn toàn phần PII với vai trò không có quyền CRM (`01-requirements.md:41`, `02-architecture.md:225`, `AC-P2-09`):**
   - Authorization chạy ở middleware/endpoint metadata **trước** khi bind body hoặc truy vấn DB. Tài khoản `content` (chỉ có `catalog.*`, `content.*`, `inventory.read` hạn chế) nhận `403 forbidden` đồng nhất trên mọi đường dẫn `/api/v1/admin/customers*` và `/api/v1/admin/tags*` — không phân biệt ID khách có tồn tại hay không (chống dò UUID/SĐT).
2. **Không lộ trường giá vốn / lợi nhuận (`02-architecture.md:235`):**
   - Mọi DTO của `P2-02` (`CustomerSummaryView`, `CustomerDetailView`, `CustomerOrderSummaryView`) chỉ chứa doanh thu giao hàng tích lũy `lifetimeValueVnd` / `totalVnd` phục vụ nghiệp vụ bán hàng & chăm sóc khách (`05-api.md:202` `?minLtv=`); **không có bất kỳ trường giá vốn hay lãi gộp nào** (`unitCostVnd`, `avgCostVnd`, `grossProfitVnd`), nên `sales` không bao giờ thấy giá vốn qua đường 360°.
3. **Không lộ thông tin xác thực tài khoản khách:**
   - Ngay cả với `owner` và `sales`, DTO 360° không bao giờ serialize `password_hash`, `security_stamp`, `provider_key` OAuth.
4. **Không rò PII qua Log, URL, Cache và ProblemDetails (`NFR-04`, `02-architecture.md:180`, `06-infrastructure.md:15,173`):**
   - Không đặt SĐT/email/tên trong route path; `Program.cs` đã tắt log scope (`IncludeScopes = false`) và tắt log SQL EF Core (`LogLevel.None`).
   - Mọi `Error.Message` và `ValidationErrors` chỉ dùng thông điệp tĩnh tiếng Việt + mã lỗi (`invalid_phone`, `customer_phone_exists`), tuyệt đối không nội suy giá trị SĐT/tên/email/địa chỉ người dùng gửi lên vào `ProblemDetails` hay `ILogger`.

### 4.3 Chính sách `[Audited]` và `AuditValuePolicy` cho khối CRM (`02-architecture.md:123-129`)

- **Entity đánh dấu `[Audited]` trong `CrmSalesFinanceEntities.cs`:**
  - Gắn `[Audited]` cho `Customer` (`crm.customers`), `CustomerAddress` (`crm.customer_addresses`), `Tag` (`crm.tags`), `CustomerAccount` (`crm.customer_accounts`).
  - **Không** gắn `[Audited]` cho `CustomerTag` (`crm.customer_tags`) và `CustomerLogin` (`crm.customer_logins`) vì hai bảng này dùng khóa chính ghép, trong khi `AuditSaveChangesInterceptor.Identity` chỉ chấp nhận khóa đơn đã duyệt (`02-architecture.md:125`). Thay đổi gắn tag khách vẫn làm đổi `Customer.UpdatedAt` trong cùng transaction. Các bảng `Interaction` (`P2-04`), `CustomerInterest` (`P2-11`), `Consent` & `DataRequest` (`P2-12`) sẽ bật audit tại đúng task sở hữu nghiệp vụ.
- **Bảng phân loại `AuditValuePolicy.Filter` (fail-closed, mặc định `"[redacted]"`):**

| `entity_type` | Trường `Safe` (ghi giá trị thực khi đúng kiểu runtime) | Trường `Pii` / `Secret` (bắt buộc `"[redacted]"`) |
|---|---|---|
| `crm.customers` | `code` (`^KH[0-9]{6,}$`), `customer_group` (enum), `is_vip` (`bool`), `origin` (enum), `acquisition_source` (enum), `referred_by_customer_id` (`Guid`), `merged_into_customer_id` (`Guid`), `contact_status` (enum), `had_legacy_purchase` (`bool`), `delivered_orders_count` (`int`), `lifetime_value_vnd` (`long`), `first_delivered_at` / `last_delivered_at` / `last_contacted_at` / `erased_at` / `created_at` / `updated_at` (`DateTime`), `created_by` (`Guid`) | `full_name`, `phone_e164`, `email`, `zalo_name`, `note_summary`, **`search_text`** *(chứa họ tên/Zalo/SĐT/email không dấu — bắt buộc che!)* |
| `crm.customer_addresses` | `customer_id` (`Guid`), `province_code` (`^[0-9]{2}$`), `ward_code` (`^[0-9]{5}$`), `is_default` (`bool`), `created_at` / `updated_at` (`DateTime`) | `label`, `receiver_name`, `receiver_phone`, `ward_name_text`, `address_line` |
| `crm.tags` | `color` (`^#[0-9A-Fa-f]{6}$`) | `name` *(nhãn tự nhập có thể chứa tên người/ghi chú)* |
| `crm.customer_accounts` | `customer_id` (`Guid`), `status` (`active`\|`locked`\|`disabled`), `failed_login_count` (`int`), `email_verified_at` / `phone_verified_at` / `history_unlocked_at` / `lockout_end` / `created_at` / `last_login_at` (`DateTime`) | `login_phone`, `password_hash`, `email`, `security_stamp` |

---

## 5. Kế hoạch file chính xác theo từng tầng & tái sinh OpenAPI

Tổng cộng **29 file** dự kiến trong G3 (**16 file mới**, **11 file sửa** — tính cả cập nhật báo cáo vào chính file plan này, **2 file sinh tự động**; không tạo migration mới, không sửa `docs/spec/`, không chạm `apps/`):

### 5.1 `TeaPremium.Domain` (1 file mới — chỉ dùng BCL)
1. `backend/src/TeaPremium.Domain/Crm/CustomerRules.cs` *(mới)* — hằng số enum CRM, `TryNormalizePhone` (`BR-CRM-01`) + `AnonymizedOrderPhone` (`BR-CRM-11`), `ToLocalSearchPhone` & `BuildSearchSource` (`BR-CRM-14`), `ExtractPhoneSearchDigits` (`03-database.md:98`), `FormatCustomerCode` (`KH001000`), kiểm tra email, màu hex tag và bất biến `HasValidContact`.

### 5.2 `TeaPremium.Application` (6 file: 4 mới, 2 sửa — không tham chiếu EF Core/Npgsql)
2. `backend/src/TeaPremium.Application/Results/Error.cs` *(sửa)* — bổ sung thuộc tính tùy chọn `IReadOnlyDictionary<string, object?>? Data = null` để trả `data: { existingCustomerId }` chuẩn `05-api.md:17`.
3. `backend/src/TeaPremium.Application/AdministrativeData/IAdministrativeReferenceData.cs` *(sửa)* — thêm `ResolveLocationsAsync` trả tên hiển thị và trạng thái active của tập mã `province_code`/`ward_code` (giữ đúng ranh giới module `02-architecture.md` §3.5).
4. `backend/src/TeaPremium.Application/Crm/CustomerContracts.cs` *(mới)* — toàn bộ DTO đọc/ghi, `CustomerActor`, commands/queries và ports `ICustomerStore`, `ICustomerQueries`.
5. `backend/src/TeaPremium.Application/Crm/CustomerValidators.cs` *(mới)* — bộ `AbstractValidator<T>` (FluentValidation) cho từng command/query, kiểm NUL byte, độ dài, cú pháp `interest`/`hasConsent`, ngày lọc, `rowVersion`, `reason`.
6. `backend/src/TeaPremium.Application/Crm/CustomerHandlers.cs` *(mới)* — các handler thực thi CRUD khách, chặn sửa trường định danh ở `PATCH` (`BR-CRM-04`), phân quyền & xử lý `correct-identity`, thay thế tags, thêm/sửa/xóa địa chỉ có đổi `is_default` nguyên tử, tổng hợp 360°.
7. `backend/src/TeaPremium.Application/Orders/Contracts/ICustomerOrderQueries.cs` *(mới — theo Phương án A `GAP-P202-03`)* — contract đọc liên module `ICustomerOrderQueries` + `CustomerOrderSummaryView` (không có trường giá vốn).

### 5.3 `TeaPremium.Infrastructure` (9 file: 5 mới, 4 sửa)
8. `backend/src/TeaPremium.Infrastructure/Persistence/CrmSalesFinanceEntities.cs` *(sửa trên nền `P2-01` sau `base-integration`)* — gắn `[Audited]` cho `Customer`, `CustomerAddress`, `Tag`, `CustomerAccount`.
9. `backend/src/TeaPremium.Infrastructure/Persistence/AuditSaveChangesInterceptor.cs` *(sửa)* — bổ sung `Customer` và `CustomerAddress` vào nhánh ghi nhận các cột có default SQL do app gán tường minh khi `EntityState.Added`.
10. `backend/src/TeaPremium.Infrastructure/Persistence/AuditValuePolicy.cs` *(sửa)* — thêm phân loại chi tiết cho `crm.customers`, `crm.customer_addresses`, `crm.tags`, `crm.customer_accounts` theo bảng Mục 4.3.
11. `backend/src/TeaPremium.Infrastructure/AdministrativeData/EfAdministrativeReferenceData.cs` *(sửa)* — hiện thực `ResolveLocationsAsync`.
12. `backend/src/TeaPremium.Infrastructure/Crm/CustomerIdentityAudit.cs` *(mới)* — ghi dòng `AuditLog` `action = "state_change"` cùng transaction cho `correct-identity` với `IDataProtectionProvider` bảo vệ `reason`.
13. `backend/src/TeaPremium.Infrastructure/Crm/EfCustomerStore.cs` *(mới)* — thực thi `ICustomerStore`: cấp số `crm.customer_code_seq`, tính `public.f_unaccent` gán vào `Customer.SearchText`, khóa `SELECT *, xmin FROM crm.customers WHERE id = {id} FOR NO KEY UPDATE`, flush 2 bước cho `ux_customer_default_address`, bắt `DbUpdateConcurrencyException` → `concurrency_conflict` và `PostgresException` `23505` (`ux_customers_phone`, `ux_tags_name`, `ux_customer_accounts_login_phone`).
14. `backend/src/TeaPremium.Infrastructure/Crm/EfCustomerQueries.cs` *(mới)* — thực thi `ICustomerQueries` (`AsNoTracking`): danh sách khách có lọc/sắp xếp/phân trang, tìm kiếm không dấu + đuôi SĐT, đọc chi tiết 360°, danh sách tags và địa chỉ.
15. `backend/src/TeaPremium.Infrastructure/Orders/EfCustomerOrderQueries.cs` *(mới — theo Phương án A `GAP-P202-03`)* — đọc `sales.orders` theo `customer_id`, sắp `placed_at DESC, id DESC`.
16. `backend/src/TeaPremium.Infrastructure/Crm/CrmRegistration.cs` *(mới)* — extension `AddCrm()` đăng ký DI cho các store/queries/audit CRM.

### 5.4 `TeaPremium.Api` (4 file: 2 mới, 2 sửa)
17. `backend/src/TeaPremium.Api/Middleware/ProblemMapping.cs` *(sửa)* — ánh xạ `error.Data` sang `problem.Extensions["data"]`.
18. `backend/src/TeaPremium.Api/Crm/CustomerEndpoints.cs` *(mới)* — định tuyến mỏng cho 15 endpoint tại `/api/v1/admin/customers*` và `/api/v1/admin/tags*` (bind JSON nghiêm ngặt, gắn `RequireCsrfHeader` và policy quyền).
19. `backend/src/TeaPremium.Api/Crm/CustomerOpenApi.cs` *(mới)* — OpenAPI document/operation transformers mô tả chính xác enum, query filter và body partial `PATCH`.
20. `backend/src/TeaPremium.Api/Program.cs` *(sửa)* — đăng ký `CustomerOpenApi.TransformAsync`, `builder.Services.AddCrm()` và `app.MapCustomers()`.

### 5.5 Hợp đồng OpenAPI & TypeScript client (2 file sinh tự động)
21. `packages/api-client/openapi.json` *(tái sinh)*
22. `packages/api-client/src/generated/schema.ts` *(tái sinh)*
- **Quy trình bắt buộc:** chạy `./scripts/verify-linux.ps1 -Mode api -WriteBack` trong Docker Linux (Windows chặn công cụ sinh OpenAPI `0x800711C7`), xác nhận cả hai file được ghi về workspace và hash kiểm tra khớp tuyệt đối; **tuyệt đối không sửa tay** hai file này.

### 5.6 Kiểm thử & hồ sơ task (7 file: 4 test mới, 2 test sửa, 1 hồ sơ plan cập nhật ở G3)
23. `backend/tests/TeaPremium.UnitTests/CustomerPhoneAndRulesTests.cs` *(mới)*
24. `backend/tests/TeaPremium.UnitTests/CustomerValidatorsTests.cs` *(mới)*
25. `backend/tests/TeaPremium.IntegrationTests/AuditValuePolicyTests.cs` *(sửa)*
26. `backend/tests/TeaPremium.IntegrationTests/CustomersApiTests.cs` *(mới)*
27. `backend/tests/TeaPremium.IntegrationTests/CustomerTagsAddressesAndSecurityTests.cs` *(mới)*
28. `backend/tests/TeaPremium.IntegrationTests/OpenApiTests.cs` *(sửa)*
29. `docs/plans/p2-02-implementation.md` *(tạo ở phiên này; điền số liệu chạy thật và manifest SHA-256 ở G3)*

---

## 6. Kế hoạch kiểm thử chi tiết (`0` skipped)

Mọi tên test gắn mã `BR-CRM-*` / `AC-P2-*` theo [00-README.md](../spec/00-README.md) dòng `35-36`. Tổng dự kiến: **14 unit test methods** (`~64` ca thực thi `[Fact]`/`[Theory]`) + **25 integration/contract test methods** trên PostgreSQL 17 Testcontainers + `WebApplicationFactory` (`~62` ca thực thi, `0` skipped).

### 6.1 Unit tests (`TeaPremium.UnitTests` — 14 methods)

- **`CustomerPhoneAndRulesTests.cs` (7 methods):**
  1. `BR_CRM_01_NormalizePhone_AcceptsValidVietnameseMobileFormatsAndSeparators` (`[Theory]`: `0901234567`, `032.123.4567`, `056-123-4567`, `(070) 123 4567`, `84811234567`, `+84981234567`, khoảng trắng đầu/cuối/NBSP → đều ra đúng `+84...`).
  2. `BR_CRM_01_NormalizePhone_RejectsLandlineInvalidPrefixWrongLengthUnicodeDigitsAndSentinel` (`[Theory]`: `02438223344`, `02838223344`, `0123456789`, `0412345678`, `0612345678`, `+84000000000`, `+840901234567`, `090123456`, `09012345678`, chữ số Ả Rập, chuỗi rỗng, chứa `\0` → đều trả `invalid_phone`).
  3. `BR_CRM_11_AnonymizedOrderPhoneConstant_RemainsValidForDbRegexWithoutWeakeningInputNormalization` (`[Fact]`: hằng `AnonymizedOrderPhone == "+84000000000"` khớp `^\+84[0-9]{9}$` nhưng vẫn bị `TryNormalizePhone` từ chối ở đầu vào).
  4. `BR_CRM_14_BuildSearchSource_IncludesFullNameZaloCodeLocalZeroPhoneAndEmail` (`[Fact]`: đổi `+84901234567` → `0901234567`, bỏ qua trường `null`/rỗng).
  5. `CustomerSearch_ExtractPhoneSearchDigits_HandlesLocalE164AndTailFragments` (`[Theory]`: `"0901 234"`, `"+84 901-234"`, `"84901234"`, `"4567"`, chuỗi có chữ cái trả `null`).
  6. `CustomerCode_FormatsSixDigitMinimumAndDoesNotTruncateAboveMillion` (`[Theory]`: `1000 → KH001000`, `999999 → KH999999`, `1000000 → KH1000000`).
  7. `CustomerContactInvariant_EnforcesPhoneOrZaloUnlessErasedOrMerged` (`[Theory]`: kiểm đủ các tổ hợp của `ck_customer_contact` và tự tham chiếu `referredByCustomerId == id`).
- **`CustomerValidatorsTests.cs` (7 methods):**
  8. `CreateCustomerValidator_RejectsBlankNameNulBytesInvalidEnumsAndErasedStatus` (`[Theory]`).
  9. `BR_CRM_04_PatchCustomerValidator_RejectsIdentityFieldsAndRequiresValidRowVersion` (`[Theory]`: gửi `phone`, `origin`, `acquisitionSource`, `hadLegacyPurchase` hoặc thiếu/sai `rowVersion` → `validation_failed`).
  10. `BR_CRM_04_CorrectCustomerIdentityValidator_RequiresRowVersionReasonAndAtLeastOneTargetField` (`[Theory]`: thiếu `reason`, `reason < 3` ký tự, `reason > 500`, không truyền trường định danh nào).
  11. `ListCustomersValidator_ValidatesPaginationSortInterestHasConsentAndDateFilters` (`[Theory]`: `pageSize > 100`, `interest` sai mẫu, `price_band` sai enum, `hasConsent` sai kênh/mục đích, ngày sai định dạng).
  12. `TagValidators_EnforceTrimmedNameBoundsHexColorAndDistinctTagIds` (`[Theory]`).
  13. `CustomerAddressValidators_EnforceRequiredFieldsPhoneAndWardFallbackRule` (`[Theory]`: thiếu cả `wardCode` lẫn `wardNameText` → lỗi).
  14. `CustomerInput_RejectsDuplicateJsonPropertiesAndUnmappedMembers` (`[Fact]`).

### 6.2 Integration & Policy tests trên PostgreSQL 17 thật (`TeaPremium.IntegrationTests` — 25 methods)

- **`AuditValuePolicyTests.cs` (bổ sung 2 methods):**
  1. `CrmEntitiesAllowlistOnlySafeFieldsAndRedactEveryPiiFieldIncludingSearchText` (`[Theory]`: kiểm toàn bộ cột của `crm.customers`, `crm.customer_addresses`, `crm.tags`, `crm.customer_accounts`; đặc biệt khẳng định `search_text`, `full_name`, `phone_e164`, `email`, `zalo_name`, `note_summary`, `receiver_name`, `receiver_phone`, `address_line`, `ward_name_text`, `label`, `name` của tag, `login_phone`, `password_hash`, `security_stamp` đều trả `"[redacted]"`).
  2. `CrmSafeEnumAndCodeFieldsRejectSpoofedPiiStrings` (`[Theory]`: nhét SĐT/email vào cột `code`, `customer_group`, `province_code`, `color` → đều bị chặn về `"[redacted]"`).
- **`CustomersApiTests.cs` (12 methods):**
  3. `CreateAndGetCustomer_AllocatesKhSequenceNormalizesPhoneAndPopulates360Defaults` (`POST` bởi `sales` → `201`, mã `KH001000` tăng dần dưới login `tp_app`, `origin = "admin"`, `phoneE164 = "+84..."`, 360° trả `stats` = 0, `orders` rỗng trước `P2-05`).
  4. `CreateCustomer_WithZaloOnlyAndNoPhone_SucceedsWhileMissingBothFails400` (`ck_customer_contact` ở tầng API/DB).
  5. `BR_CRM_02_CreateCustomer_DuplicateActivePhoneReturns409WithExistingCustomerId_IncludingConcurrentRace` (tạo trùng trả `409 customer_phone_exists` + `data.existingCustomerId`; bắn 8 request đồng thời cùng SĐT → đúng 1 request `201`, 7 request `409` kèm đúng UUID thắng cuộc, `0` lỗi `500`; hồ sơ đã set `merged_into_customer_id` không chặn tạo mới).
  6. `BR_CRM_14_ListCustomers_VietnameseUnaccentPhoneTailAndEscapedMetacharactersWorkWithGinIndex` (tạo `"Đặng Trần Hiên"` / Zalo `"Trà Cụ Tử Sa"` / `"0901234567"`: tìm `"dang tran"`, `"ĐẶNG"`, `"tu sa"`, `"KH001"`, `"0901 234 567"`, `"+84901234567"`, `"4567"` đều trúng; tìm `"100%"` và `"a_b"` không khớp nhầm ký tự đại diện LIKE).
  7. `ListCustomers_SupportsGroupTagInterestConsentLtvContactStatusAndVietnamDateFilters` (kiểm từng filter đơn và kết hợp + sắp xếp + phân trang ổn định, loại hồ sơ đã gộp).
  8. `BR_CRM_04_PatchCustomer_UpdatesAllowedFieldsRebuildsSearchTextAndRejectsIdentityFields` (`PATCH` sửa tên/email/Zalo/nhóm/VIP/ghi chú cập nhật ngay `search_text`; gửi `phone`/`origin`/`acquisitionSource`/`hadLegacyPurchase` bị từ chối `400`).
  9. `CustomerPatchAndCorrectIdentity_StaleOrMalformedRowVersion_Return409And400` (2 nhân viên cùng đọc 1 `rowVersion` `xmin`: người 1 lưu `200`, người 2 nhận `409 concurrency_conflict`; `rowVersion` rác nhận `400`).
  10. `BR_CRM_04_CorrectIdentity_SalesCanChangePhoneOnly_OwnerCanChangeOriginAcquisitionAndLegacyFlag` (`sales` đổi SĐT + `reason` → `200`, `sales` gửi `origin`/`acquisitionSource`/`hadLegacyPurchase` → `403 forbidden`; `owner` sửa cả 4 trường → `200`; đổi SĐT trùng khách khác → `409 customer_phone_exists` kèm `existingCustomerId`).
  11. `CorrectIdentity_WhenCustomerAccountExists_BlocksPhoneChangeOrClearWith409AccountExists` (chèn dòng `crm.customer_accounts` giả lập `P3-05`: đổi SĐT hoặc xóa `phone = null` đều trả `409 account_exists` và không sửa dữ liệu theo `GAP-P202-01 = C`; `owner` chỉ đổi `origin` không đụng `phone` vẫn thành công `200`).
  12. `Customer360AndOrdersEndpoint_AggregateCrmDataAndReadSalesOrdersWithoutExposingCostsOrSecrets` (chèn fixture tổng hợp vào `crm.consents`, `crm.customer_interests`, `crm.customer_accounts`, `sales.orders` → `GET admin/customers/{id}` và `GET admin/customers/{id}/orders` trả đúng thứ tự/tổng số, kiểm đệ quy JSON không chứa `passwordHash`, `securityStamp`, `unitCost`, `avgCost`, `grossProfit`).
  13. `ErasedOrMergedCustomer_RejectsProfileTagAddressAndIdentityMutations` (giả lập hồ sơ `contact_status = 'erased'` và hồ sơ `merged_into_customer_id != null` → mọi lệnh ghi đều trả `409`, không thể ghi lại PII).
  14. `FailedValidationOrConflict_RollsBackSequenceSideEffectsAndLeavesZeroOrphanRows` (kiểm tra tính nguyên tử của `IUnitOfWork`).
- **`CustomerTagsAddressesAndSecurityTests.cs` (10 methods):**
  15. `TagsCrud_CreatesUpdatesListsAndRejectsDuplicateNameWith409` (`GET/POST/PATCH admin/tags[/{id}]`, trùng tên trả `409 tag_name_exists`).
  16. `PutCustomerTags_ReplacesSetIdempotentlyAdvancesCustomerRowVersionAndRejectsUnknownTag` (`PUT admin/customers/{id}/tags` thêm/bớt/rỗng/lặp lại cùng tập, kiểm `rowVersion` mới).
  17. `CustomerAddressesCrud_ValidatesActiveProvinceWardViaAdministrativePortAndSupportsWardNameFallback` (dùng bundle fixture `P009Harness`: tỉnh/phường active đúng cặp → `201`; phường thuộc tỉnh khác hoặc đã deactivate → `400 invalid_address_location`; `wardCode = null` + `wardNameText = "Phường cũ"` → `201`; DB chưa import địa giới → `422 administrative_data_not_ready`).
  18. `CustomerAddresses_SwitchingDefaultAcrossMultipleAddresses_NeverViolatesPartialUniqueIndex` (tạo địa chỉ A `isDefault=true`, tạo tiếp B `isDefault=true`, `PATCH` A lại thành `isDefault=true`, bắn đồng thời 5 request đổi default → luôn có tối đa 1 dòng `is_default = true`, không bao giờ nổ `23505 ux_customer_default_address`).
  19. `CustomerAddresses_LegacyReparentedWardIsPreservedOnNonLocationPatchAndFlaggedInactiveOnRead` (`03-database.md:90`: sau khi deactivate ward trong kỳ import mới, đọc địa chỉ thấy `isLocationActive = false`, sửa `receiverName` không làm mất mã cũ).
  20. `AC_P2_09_ContentRoleUnauthenticatedUnreadyHttpAndMissingCsrfAreRejectedAcrossAllEndpoints` (`content` gọi toàn bộ 15 endpoint `/api/v1/admin/customers*` và `/api/v1/admin/tags*` đều nhận `403 forbidden`; chưa đăng nhập → `401`; staff `MustChangePassword` → `403`; HTTP thường → `400 https_required`; thiếu/sai `X-CSRF-TOKEN` trên POST/PATCH/PUT/DELETE → `400 csrf_invalid`).
  21. `AuditLogs_RecordCustomerAddressTagAndAccountChangesWithZeroRawPiiOrSearchTextLeak` (thực hiện tạo/sửa khách, đổi địa chỉ, tạo tag, `correct-identity` với dữ liệu nhạy cảm đánh dấu riêng như `"Đặng Văn Thử"`, `"0918765432"`, `"dang.thu@example.test"`, `"123 Nguyễn Huệ"`, lý do `"Đổi từ số 0918765432"` → quét toàn bộ `platform.audit_logs.changes` xác nhận **không xuất hiện** bất kỳ chuỗi gốc hay bản bỏ dấu `"dang van thu"` / `"0918765432"`, đồng thời `reason` giải mã được bằng đúng protector `teapremium.crm.correct-identity-reason.v1`).
  22. `ProblemDetailsAndStructuredLogs_NeverEchoSubmittedPhoneEmailNameOrAddress` (gửi SĐT/email lỗi và SĐT trùng, bắt stdout JSON logger + response body, xác nhận chuỗi PII vừa gửi không xuất hiện ở bất kỳ dòng log hay thông báo lỗi nào).
  23. `ArchitectureRules_RemainGreenForCrmAndOrdersReadContracts` (xác nhận `Domain` chỉ BCL, `Application` không chạm EF/Npgsql, không gọi `DateTime.Now`/`UtcNow`, CRM không đọc trực tiếp `DbSet<Province>`/`DbSet<Order>`).
  24. `RealTpAppDatabaseRole_ExecutesAllCrmEndpointsAndCannotHardDeleteCustomer` (chạy trên kết nối `tp_app` thật: mọi thao tác `P2-02` thành công, trong khi `DELETE FROM crm.customers` vẫn bị PostgreSQL chặn `42501`).
- **`OpenApiTests.cs` (cập nhật/mở rộng 1 method):**
  25. `StoreCatalogPublishesExactlyNineReadOperations` *(cập nhật tổng số operation `95 → 110` theo Phương án A hoặc `109` theo Phương án B; kiểm tra sự hiện diện của toàn bộ đường dẫn `/api/v1/admin/customers*` & `/api/v1/admin/tags*`, enum `customerGroup`/`origin`/`acquisitionSource`/`contactStatus`, và xác nhận các schema CRM không chứa trường `cost`, `profit`, `password`, `securityStamp`)*.

### 6.3 Chuỗi lệnh kiểm chứng sẽ chạy ở G3

```powershell
./scripts/verify-linux.ps1 -Mode api -WriteBack
./scripts/verify-linux.ps1 -Filter "FullyQualifiedName~Customer|FullyQualifiedName~AuditValuePolicy|FullyQualifiedName~OpenApi"
./scripts/candidate-manifest.ps1 -Write -Base <origin/main SHA sau khi merge P2-01> -Out artifacts/p2-02/candidate.manifest
./scripts/verify-linux.ps1 -Manifest artifacts/p2-02/candidate.manifest
./scripts/verify-linux.ps1 -Mode frontend -Manifest artifacts/p2-02/candidate.manifest
git diff --check
```
*(Ghi chú: vì `packages/api-client/` thay đổi khi sinh lại OpenAPI, theo `06-infrastructure.md:221` CI sẽ chạy `full`, nên ở G3 bắt buộc chạy cả `-Mode frontend` để đảm bảo `apps/admin` và `apps/store` typecheck/build xanh với `schema.ts` mới).*

---

## 7. Rủi ro, `ASSUMPTION` và `GAP` (kèm `file:dòng`, phương án, khuyến nghị)

### 7.1 Quyết định Owner đã khóa cứng (`GAP-P202-01..03` và `ASSUMPTION-P202-01..03`)

| Mã | Phân loại & Nguồn (`file:dòng`) | Mô tả chi tiết | Quyết định Owner (KHÓA CỨNG) |
|---|---|---|---|
| `GAP-P202-01` | `MISSING_CONTRACT` — [04-business-rules.md](../spec/04-business-rules.md)`:223` (`BR-CRM-04`), `:12-20` (`BR-IAM-03/04/06`); [05-api.md](../spec/05-api.md)`:85,205`; [schema.sql](../spec/schema.sql)`:626-643` | Hành vi `correct-identity` khi khách đã có dòng `crm.customer_accounts`. | **Phương án C:** Khách đã có dòng `crm.customer_accounts` thì `correct-identity` KHÔNG được đổi/xóa `phone` → `409 account_exists`; chưa có account thì sửa bình thường (audit, reason). Ghi follow-up **`P3-05`**. |
| `GAP-P202-02` | `CONFIRMED_CONFLICT` (nhỏ) — [05-api.md](../spec/05-api.md)`:202,204` vs `:209-210` | Route `PATCH admin/tags` thiếu `[/{id}]` và quyền `GET` của `addresses`/`tags` ghi `customers.write`. | **Phương án B:** Giữ `customers.write` cho `GET/POST/PATCH/DELETE addresses` và `GET/POST/PATCH tags` đúng `05-api.md:209-210`; route `PATCH` là `/api/v1/admin/tags/{id}`. Không sửa `docs/spec/`. |
| `GAP-P202-03` | `MISSING_CONTRACT` (ranh giới pha) — [07-roadmap.md](../spec/07-roadmap.md)`:87,96,127` vs [05-api.md](../spec/05-api.md)`:206,211,213` | Phân chia endpoint giữa `P2-02`, `P2-03`, `P2-11`, `P3-05`. | **Phương án A:** `GET /api/v1/admin/customers/{id}/orders` đọc thật `sales.orders` qua port `ICustomerOrderQueries` (không mock rỗng); hoãn `POST/DELETE interests` → `P2-11`, `verify-account-phone` → `P3-05`, `import` → `P2-03`. |
| `ASSUMPTION-P202-01` | `ASSUMPTION` — [05-api.md](../spec/05-api.md)`:10,205,209,210`; [03-database.md](../spec/03-database.md)`:15` | Chính sách `rowVersion` cho `tags` và `addresses`. | **Phương án A:** Bắt buộc `rowVersion` (`xmin` Base64 mờ) trên `PATCH admin/customers/{id}` và `POST .../correct-identity`; tùy chọn `rowVersion` trên `PUT .../tags` và `POST/PATCH/DELETE .../addresses[/{aid}]` (có gửi thì kiểm `xmin` → `409`, không gửi vẫn khóa cha `FOR NO KEY UPDATE` và nâng `Customer.UpdatedAt`). |
| `ASSUMPTION-P202-02` | `ASSUMPTION` — [03-database.md](../spec/03-database.md)`:96-98` | Kiểu khớp `q` trên `customers.search_text`. | **Phương án A:** Literal substring `f_unaccent(q)` theo `03-database.md:97-98` kết hợp `OR` khớp đuôi chữ số trên `phone_e164`. |
| `ASSUMPTION-P202-03` | `ASSUMPTION` — [schema.sql](../spec/schema.sql)`:555,559`; [04-business-rules.md](../spec/04-business-rules.md)`:221`; [05-api.md](../spec/05-api.md)`:6,17` | Định dạng mã khách `KH` và tên khóa `data` khi trùng SĐT. | **Đã duyệt:** `"KH" + seq.ToString("D6")` (`KH001000`, `> 999999` không cắt); `409 customer_phone_exists` kèm `data: { "existingCustomerId": "<uuid>" }`. |
| `R-P201-04` | Từ `p2-01-implementation.md` — `BR-CRM-01` vs `BR-CRM-11` | Ranh giới regex DB `^\+84[0-9]{9}$` với đầu số di động VN. | **Đã duyệt:** Siết đầu số di động VN `{3,5,7,8,9}` ở Domain/Application, giữ `AnonymizedOrderPhone = "+84000000000"` tương thích `BR-CRM-11` mà không nới lỏng đầu vào. |

### 7.2 Rủi ro kỹ thuật đã nhận diện và biện pháp giảm thiểu trong thiết kế

1. **Lộ PII gián tiếp qua `customers.search_text` hoặc `reason` trong `platform.audit_logs` (`P0` nếu bỏ sót):**
   - *Nguyên nhân:* `search_text` không chứa từ khóa nhạy cảm trong tên cột nên không bị `AuditValuePolicy.IsSensitive` bắt theo tên tự động, nhưng bên trong chứa họ tên, Zalo, SĐT `0xxxxxxxxx` và email không dấu; `reason` của `correct-identity` là văn bản tự do nhân viên nhập (thường chứa SĐT cũ/mới).
   - *Giảm thiểu:* Nhánh `entityType == "crm.customers"` trong `AuditValuePolicy` fail-closed trả `"[redacted]"` cho `search_text`; `CustomerIdentityAudit` mã hóa `reason` bằng `IDataProtectionProvider`; có bài test tích hợp quét chuỗi PII (cả có dấu và không dấu) trên toàn bảng `platform.audit_logs`.
2. **Nổ `23505` trên partial unique index `ux_customer_default_address` khi đổi địa chỉ mặc định:**
   - *Nguyên nhân:* Index `UNIQUE(customer_id) WHERE is_default` là non-deferrable, thứ tự câu lệnh batch của EF Core trong một lần `SaveChangesAsync` không đảm bảo chạy `UPDATE false` trước `INSERT/UPDATE true`.
   - *Giảm thiểu:* Khóa dòng `crm.customers` cha bằng `FOR NO KEY UPDATE`, hạ `is_default = false` và flush `SaveChangesAsync` bước 1 trước khi bật `is_default = true` ở bước 2 trong cùng transaction `IUnitOfWork`.
3. **Đi vòng qua `AuditSaveChangesInterceptor` hoặc làm tăng `xmin` hai lần khi dựng `search_text`:**
   - *Nguyên nhân:* Dùng `UPDATE crm.customers SET search_text = ...` sau `SaveChangesAsync` trên bảng `[Audited]` vi phạm `02-architecture.md:127-128` và làm `xmin` nhảy thêm 1 nhịp.
   - *Giảm thiểu:* Tính trước `SELECT public.f_unaccent({source})` trong transaction và gán vào `Customer.SearchText` trước một lệnh `SaveChangesAsync` duy nhất.
4. **Xung đột file nóng khi tích hợp `P1-13` và `P2-01`:**
   - *Nguyên nhân:* `Program.cs`, `AppDbContextModelSnapshot.cs`, `openapi.json`, `schema.ts` là các file dùng chung giữa các nhánh.
   - *Giảm thiểu:* Chỉ bắt đầu G3 của `P2-02` sau khi `P2-01` đã merge vào `origin/main` và chạy `base-integration` (`scripts/task-integrate.ps1`) trên `D:\tra-hien-p2-02`; tái sinh OpenAPI/client bằng `verify-linux.ps1 -Mode api -WriteBack`, không bao giờ sửa hay merge tay `openapi.json` / `schema.ts`.
