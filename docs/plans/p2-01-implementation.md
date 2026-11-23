# P2-01: Migration `crm` (trừ accounts/logins chưa dùng vẫn tạo), `sales`, `pricing.vouchers/redemptions`, `finance.expenses`

- **Gate/status:** `READY_FOR_LOCAL_G4` (G3 hoàn tất trên worktree `D:\tra-hien-p2-01`; áp dụng Mục 1.1 khóa cứng + 3 điều kiện bắt buộc C1–C3 của Orchestrator; chưa commit/push).
- **Tier:** `A` — migration/DDL mở rộng 3 schema mới + 1 schema hiện hữu (26 bảng + 1 sequence), tiền `bigint`, PII khách hàng/đơn hàng, `xmin` concurrency và phân quyền runtime/backup (`tp_app`/`tp_readonly`) + `preflight.sql`.
- **Worktree/base:** `D:\tra-hien-p2-01`, nhánh `feature/p2-01-crm-sales-finance-db`, base `origin/main` `e63e90b99b596ba4e1d218d2a920733cbd79edbb`. (`D:\tra-hien-p1-13` đang chạy song song UI kho `P1-13` và sẽ merge trước; khi `main` tiến sẽ chạy `base-integration` + focused G4 trước khi publish).
- **Readiness (scoped G1/G2):** `GO`.
  - Baseline G1 đầy đủ tại `cadce575597738bf40ad7c45689482de5a5fbc3d` ([spec-inventory.md](../analysis/spec-inventory.md), [spec-gaps.md](../analysis/spec-gaps.md)); G2 nền tại [conformance-review.md](../architecture/conformance-review.md) và [task-readiness.md](task-readiness.md) (mốc B01 `dadc4435e8f115faaeb9e8511e56e09d5d517a8a`); G1/G2 kho P1 tại `99c6810b54d151d6a30334c9384524e3ba2b3552` ([p1-inventory-architecture.md](p1-inventory-architecture.md)) và `P1-10` tại `e63e90b99b596ba4e1d218d2a920733cbd79edbb` ([p1-10-implementation.md](p1-10-implementation.md)).
  - Diff `docs/spec/` từ `99c6810..e63e90b` chỉ gồm làm rõ quyền độc bản `P1-08`, hậu tố mã chứng từ kho `P1-09`, hợp đồng đọc Store Catalog `P1-14` và dòng API cảnh báo kho `P1-10`; `schema.sql` và `03-database.md` cho khối `crm`/`sales`/`pricing`/`finance` không đổi ngoài các điểm được owner chốt ngày `22/11/2026` bên dưới.
  - Dependency của `P2-01` (`07-roadmap.md:86` — nền DB P0 + P1: `P0-04`, Closure #1 PR #22, `P1-01`..`P1-12`, `P1-14`, `P1-10`) đã merge vào `origin/main` (`6` migrations hiện hành). `P1-13` chỉ là Admin UI kho (`apps/admin`), không chạm schema/EF migration.
  - Hai gap chuyển pha của `P2-01` trong `spec-gaps.md` (`G03` marketing FK tại dòng 32 và `G21` reservations FK tại dòng 56) cùng hai xung đột `ck_customer_contact` (`BR-CRM-05`) và `REVOKE DELETE` trên `sales.order_lines` (`BR-ORD-08`/`BR-INV-04`) đã được owner khóa cứng ngày `22/11/2026`. Không còn GAP chặn G3.
- **Traceability:**
  - Nguồn đặc tả: [07-roadmap.md](../spec/07-roadmap.md) dòng `86` (`P2-01`), dòng `90` (`P2-05`), dòng `187-188` (`P5-01`/`P5-02`); [schema.sql](../spec/schema.sql) dòng `499-511`, `530-713`, `788-1052`, `1217-1232`; [03-database.md](../spec/03-database.md) §1, §2, §3.3–3.7, §4, §7, §8; [02-architecture.md](../spec/02-architecture.md) §2, §3, §5.1, §5.2, §5.4, §6; [04-business-rules.md](../spec/04-business-rules.md) §1, §3–§11, §13; [05-api.md](../spec/05-api.md) §1, §4.4–§4.8; [06-infrastructure.md](../spec/06-infrastructure.md) §5, §7.
  - Định danh liên kết:
    - `DEC-01`, `DEC-02`, `DEC-03`, `DEC-05`, `DEC-07`, `DEC-08`, `DEC-09`, `DEC-10`, `DEC-12`, `DEC-13`, `DEC-14`, `DEC-17`, `DEC-18`, `DEC-20`, `DEC-30`.
    - `FR-CRM-01..12`, `FR-IAM-04..06`, `FR-PRC-03..04`, `FR-CRT-01..05`, `FR-CRT-07`, `FR-ORD-01..06`, `FR-PAY-01..05`, `FR-SHP-02..04`, `FR-RET-01..03`, `FR-FIN-01..08`, `FR-MKT-07`, `FR-PLT-01..02`.
    - `BR-CRM-01..03`, `BR-CRM-05`, `BR-CRM-06`, `BR-CRM-08`, `BR-CRM-09`, `BR-CRM-11`, `BR-CRM-12`, `BR-CRM-14`, `BR-IAM-03`, `BR-IAM-04`, `BR-IAM-06`, `BR-PRC-04..07`, `BR-CRT-01`, `BR-CRT-05`, `BR-CRT-06`, `BR-ORD-01..05`, `BR-ORD-07..10`, `BR-PAY-01..02`, `BR-PAY-05..07`, `BR-SHP-02..05`, `BR-RET-01..03`, `BR-INV-04`, `BR-FIN-01..05`.
    - `NFR-02`, `NFR-03`, `NFR-04`, `NFR-05`, `NFR-07`, `NFR-08`, `NFR-09`, `NFR-12`.
    - Nền dữ liệu cho `AC-P2-01..10` (trực tiếp chứng minh cơ chế DB cho `AC-P2-05` `xmin`, `AC-P2-07` `cod_settlements`, `AC-P2-08` `ux_customers_phone`, `AC-P2-10` bảo vệ bảng đơn/khách và ẩn danh; giữ nguyên `AC-P0-05` kiểm thử kiến trúc). `P2-01` không tự nghiệm thu use case/HTTP của các `AC-P2-*`.
- **Scope / out of scope:** xem Mục 1 bên dưới.
- **Cases:** xem Mục 7 bên dưới (`T01`–`T17`, `18` test methods = `151` ca thực thi kiểm thử mới trên PostgreSQL 17, `0` skipped).
- **Migration/recovery:** 1 migration forward `202611230421_P2_01_CrmSalesFinanceFoundation` + `Down` có pre-drop guard trên DB dùng một lần, không sửa 6 migration cũ; môi trường có dữ liệu thật chỉ backup + roll forward (Mục 3 & Mục 4).
- **Verification:** vòng tập trung `CrmSalesFinance` đã PASS `151/151` (`0` skipped) tại `artifacts/verify-linux/20261123-114353-941-backend/verify.log` (kèm `27/27` CI scope classifier, `dotnet format whitespace`, `build -warnaserror`, idempotent `migrations.sql` `160307` bytes và `OpenAPI/client are current`). Full suite cuối cùng chạy ngay sau khi khóa `artifacts/p2-01/candidate.manifest` (SHA-256 và log đường dẫn đầy đủ ghi trong khối báo cáo G3 gửi Orchestrator để không sửa file sau khi ghi manifest).
- **Risks/follow-ups:** xem Mục 8 bên dưới.
- **Review/publish:** chờ G3 hoàn tất → ghi `artifacts/p2-01/candidate.manifest` → chạy full `./scripts/verify-linux.ps1 -Manifest artifacts/p2-01/candidate.manifest` → G4 độc lập khác hãng → xin phép owner trước khi `publish-pr`.

---

## 1. Quyết định owner đã duyệt (22/11/2026) và ranh giới phạm vi

### 1.1 Quyết định owner đã duyệt 22/11/2026 (KHÓA CỨNG, nguyên văn)

1. Ranh giới: 26 bảng + 3 schema mới `crm`, `sales`, `finance` + sequence `crm.customer_code_seq`.
   - `crm` (10): `customers`, `tags`, `customer_tags`, `customer_addresses`, `customer_accounts`, `customer_logins`, `interactions` (`fk_interactions_order` → `sales.orders`), `customer_interests`, `consents`, `data_requests`.
   - `pricing` (2, schema đã có): `vouchers` (`fk_vouchers_customer`), `voucher_redemptions` (`fk_redemptions_order`, `fk_redemptions_customer`).
   - `sales` (13): `carts`, `cart_items`, `orders`, `order_lines`, `order_status_history`, `cod_settlements`, `payments`, `return_requests`, `return_lines`, `return_evidence`, `refunds`, `shipments`, `shipment_events`.
   - `finance` (1): `expenses`.
   - HOÃN: `inventory.reservations` + `fk_reservations_line` → `P2-05`; toàn bộ `marketing.*` và 4 FK (`crm.interactions.campaign_id`, `sales.orders.tracking_link_id`, `sales.orders.campaign_id`, `finance.expenses.campaign_id`) → `P5-01`/`P5-02`; giữ 4 cột UUID nullable không FK + `ix_orders_campaign` ngay bây giờ.
2. Spec amendment được phép (G3 sẽ sửa, PLAN chỉ ghi chính xác file:dòng):
   - `schema.sql:585` và chỗ trích dẫn trong `03-database.md` (nếu có): `ck_customer_contact` → `CHECK (contact_status = 'erased' OR merged_into_customer_id IS NOT NULL OR phone_e164 IS NOT NULL OR zalo_name IS NOT NULL)`.
   - `03-database.md:156`: bỏ `sales.order_lines` khỏi `REVOKE DELETE`; giữ `REVOKE DELETE, TRUNCATE` trên `sales.orders`, `sales.payments`, `sales.refunds`, `sales.order_status_history`, `crm.customers`; `REVOKE TRUNCATE` mọi bảng. Ghi chú `P2-05` sẽ cho `inventory.reservations.order_line_id` nullable khi `status='released'` + `CHECK (status = 'released' OR order_line_id IS NOT NULL)` + `fk_reservations_line` `ON DELETE SET NULL`; cập nhật `schema.sql:499-509`, `908-909` cho khớp.
3. EF Core/PG17: `xmin` concurrency trên `crm.customers`, `sales.orders`, `sales.return_requests`, `pricing.vouchers`; tiền `bigint` (`long`), `numeric` đúng precision, `jsonb`, mọi PK/FK/UNIQUE/partial unique/CHECK/GIN (`f_unaccent`, `gin_trgm_ops`) và tên constraint đúng `schema.sql`. Không sửa migration cũ; 1 migration forward + `Down` sạch; mở rộng `GrantsSql` và `preflight`: `tp_readonly` chỉ `SELECT`; `tp_app` DML tường minh, `REVOKE` như mục 2, `USAGE` trên `crm.customer_code_seq`, không grant option.
4. Không API, seed, use case, UI. Không đụng `apps/` hay `packages/`.

### 1.1b Điều kiện bắt buộc của Orchestrator (23/11/2026 — ghi trước khi code)

- **C1 (Preflight tương thích `P1Head` + bản đóng băng literal trong migration):** `infra/postgres/preflight.sql` và bản preflight đóng băng trong migration mới phải chạy PASS trên DB đang ở `P1Head` (`202611191437_P1_04_TaxonomyPublicationHistory`) trước khi `P2-01` `Up` (`scripts/dev-migrate.ps1:13-20` chạy preflight trước migrate). Mọi kiểm tra mới trên đối tượng P2 (`sales.orders`, `crm.customers`, `crm.customer_code_seq`…) phải bỏ qua khi đối tượng chưa tồn tại (`to_regclass`/`to_regnamespace` hoặc join catalog `pg_class`/`pg_namespace`), không được lỗi `"does not exist"`. Bắt buộc có test: chạy `preflight.sql` trên DB `P1Head` có dữ liệu giả → PASS, rồi `Up` `P2-01` → PASS, rồi chạy `preflight.sql` lại → PASS. Bản preflight trong migration mới là literal đóng băng như `202611190201_P1_01_CatalogInventoryFoundation.cs`, tuyệt đối không gọi `GrantsSql` lúc runtime.
- **C2 (Phạm vi file được phép):** Chỉ sửa/tạo trong `backend/`, `infra/postgres/preflight.sql`, `docs/spec/schema.sql` + `docs/spec/03-database.md` (chỉ đúng 4 đoạn Mục 5), `docs/plans/p2-01-implementation.md`. Không đụng `apps/`, `packages/`, `scripts/`, 6 migration cũ hay `D:\tra-hien-p1-13`.
- **C3 (Parity T01/T02 theo đúng chuẩn P1-01):** `T01`/`T02` so sánh tên constraint implicit theo đúng cách `CatalogInventoryParityTests` của `P1-01` đang làm (không tự chế cơ chế mới); sửa số ca `T02` khớp số hạng mục thực tế (`9` ca). Nếu phát hiện mâu thuẫn mới giữa `schema.sql` và `03-database.md`/business rules thì STOP và báo GAP, không tự chọn.

### 1.2 Trong phạm vi / Ngoài phạm vi

- **Trong phạm vi (`P2-01`):**
  - Cập nhật đúng 2 file đặc tả được duyệt ở Mục 5 (`docs/spec/schema.sql`, `docs/spec/03-database.md`) khi vào G3.
  - Khai báo persistence POCO entities và cấu hình EF Core tường minh cho 26 bảng + `crm.customer_code_seq` trong `TeaPremium.Infrastructure.Persistence`.
  - Tạo đúng 1 migration forward mới (`<yyyyMMddHHmm>_P2_01_CrmSalesFinanceFoundation`) + `Down` an toàn trên DB dùng một lần, không đụng 6 migration lịch sử.
  - Mở rộng `GrantsSql.cs` và `infra/postgres/preflight.sql` (nguồn nhúng `TeaPremium.DatabasePreflight.sql` của `GrantsSql.Preflight`).
  - Bộ integration test PostgreSQL 17 (`0` skipped) cho parity, pending-model-changes, idempotent script, `Down`/`Up`, quyền thật `tp_app`/`tp_readonly`/preflight, probe CHECK/partial unique/expression unique/GIN và xung đột `xmin` trên 4 entity.
- **Ngoài phạm vi (thuộc các task sau):**
  - `inventory.reservations` + indexes + `fk_reservations_line` (`P2-05`).
  - Toàn bộ schema `marketing` (`campaigns`, `campaign_products`, `tracking_links`, `link_clicks`, `campaign_recipients`, `referral_codes`, `referrals`) và 4 FK tới `marketing.*` (`P5-01`/`P5-02`/`P5-05`).
  - Schema `content` còn lại (`article_categories`, `authors`, `articles`, `article_links`, `pages`, `landing_pages`, `not_found_logs` → `P3-07`/`P3-14`/`P3-17`), `catalog.product_reviews`/`review_media` (`P5-07`), `platform.web_vitals_samples` (`P3-16`).
  - Đánh dấu `[Audited]` và phân loại `AuditValuePolicy` cho các entity CRM/Sales/Pricing/Finance (thực hiện tại từng task nghiệp vụ `P2-02`, `P2-05`, `P2-07`, `P2-12`, `P4-01`, `P5-05` theo `02-architecture.md:123-126`).
  - Mọi Domain logic, Application use case/port/validator, API endpoint, OpenAPI/TS client, CLI/seed, job nền và UI (`apps/`, `packages/`).

---

## 2. Checklist chi tiết 26 bảng + sequence `crm.customer_code_seq`

**Quy ước chung cho toàn bộ 26 bảng:**
- Tất cả thuộc cùng một `AppDbContext` (`TeaPremium.Infrastructure.Persistence.AppDbContext`, `DEC-01`); cột "Module / Cấu hình" ghi rõ module nghiệp vụ sở hữu theo `02-architecture.md` §2–§3 để các task `P2-02+` chỉ truy cập qua repository/port của đúng module đó.
- Không có cột generated lưu trữ hay `numeric`/`decimal` mới trong 26 bảng (`numeric(12,2)` hiện chỉ có ở 2 bảng EAV của P1; mọi cột tiền kể cả `vouchers.discount_value` đều là `bigint` → `long`/`long?`). Chỉ có 1 cột `jsonb` (`sales.orders.utm`) và 1 cột `date` (`finance.expenses.incurred_on` → `DateOnly`).
- 4 bảng dùng cột hệ thống `xmin` (`uint Version`, `.HasColumnName("xmin").HasColumnType("xid").IsRowVersion()`, tuyệt đối không sinh cột thường `xmin` hay `version` trong DDL): `crm.customers`, `sales.orders`, `sales.return_requests`, `pricing.vouchers`.
- Tên constraint explicit trong `schema.sql` và trong quyết định owner (`fk_vouchers_customer`, `fk_interactions_order`, `fk_redemptions_order`, `fk_redemptions_customer`, `ux_orders_code`, `ck_customer_contact`, `ck_customer_not_self_ref`, `ck_account_login_phone`, `ck_order_*`, `ck_line_*`) được giữ chính xác 100%; các constraint implicit trong `schema.sql` đặt tên tường minh theo tiền tố `pk_`, `fk_`, `ux_`, `ck_` giống `P1-01` (`03-database.md:18`).
- **Sequence:** `crm.customer_code_seq` (`schema.sql:555`), module `CRM` / `AppDbContext`: `bigint`, `START WITH 1000`, `INCREMENT BY 1`, `MINVALUE 1`, `MAXVALUE 9223372036854775807`, `NO CYCLE`, `CACHE 1`.

| # | Bảng (`schema.table`) | Dòng `schema.sql` | Module (`AppDbContext`) | Cột đặc biệt (`xmin`, `jsonb`, money `bigint`, `date`) | Index / UNIQUE / Partial Unique / Expression / GIN | Ràng buộc `CHECK` | Khóa ngoại `FK` (kèm hành vi `ON DELETE` & FK hoãn) |
|---:|---|---|---|---|---|---|---|
| 1 | `crm.customers` | `557-593` | `CRM` (`Customer`) | `xmin` (`uint Version`); money: `lifetime_value_vnd bigint = 0`; không `jsonb`/generated | `ux_customers_code` (`UNIQUE(code)`); partial unique `ux_customers_phone` (`phone_e164 WHERE phone_e164 IS NOT NULL AND merged_into_customer_id IS NULL`); GIN `ix_customers_search` (`search_text gin_trgm_ops`); `ix_customers_last_delivered` (`last_delivered_at`); `ix_customers_group` (`customer_group`) | Inline: `length(trim(full_name)) >= 1`, `phone_e164 ~ '^\+84[0-9]{9}$'`, `email IS NULL OR email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'`, `customer_group IN ('retail','wholesale','reseller')`, `origin IN ('legacy_import','web','admin')`, `acquisition_source IN ('unknown','legacy','google_organic','google_ads','facebook','zalo_personal','zalo_oa','referral','direct','offline','other')`, `contact_status IN ('active','do_not_contact','erased')`, `delivered_orders_count >= 0`, `lifetime_value_vnd >= 0`. Explicit: **`ck_customer_contact`** (bản sửa Mục 5: `contact_status = 'erased' OR merged_into_customer_id IS NOT NULL OR phone_e164 IS NOT NULL OR zalo_name IS NOT NULL`), **`ck_customer_not_self_ref`** (`referred_by_customer_id IS NULL OR referred_by_customer_id <> id`) | Self-FK `referred_by_customer_id → crm.customers(id)` (NO ACTION); self-FK `merged_into_customer_id → crm.customers(id)` (NO ACTION) |
| 2 | `crm.tags` | `597-601` | `CRM` (`Tag`) | Không | `ux_tags_name` (`UNIQUE(name)`) | Không | Không |
| 3 | `crm.customer_tags` | `602-607` | `CRM` (`CustomerTag`) | PK ghép `(customer_id, tag_id)` | `ix_customer_tags_tag` (`tag_id`) | Không | `customer_id → crm.customers(id) ON DELETE CASCADE`; `tag_id → crm.tags(id) ON DELETE CASCADE` |
| 4 | `crm.customer_addresses` | `609-624` | `CRM` (`CustomerAddress`) | Không | Partial unique `ux_customer_default_address` (`customer_id WHERE is_default`) | Inline: `receiver_phone ~ '^\+84[0-9]{9}$'`. Table: `ward_code IS NOT NULL OR ward_name_text IS NOT NULL` | `customer_id → crm.customers(id) ON DELETE CASCADE`; chéo schema: `province_code → platform.provinces(code)` (NO ACTION), `ward_code → platform.wards(code)` (NO ACTION) |
| 5 | `crm.customer_accounts` | `626-643` | `CRM` (`CustomerAccount`) | Không | `ux_customer_accounts_customer_id` (`UNIQUE(customer_id)`); nullable unique constraint `ux_customer_accounts_login_phone` (`UNIQUE(login_phone)` qua SQL `ALTER TABLE`); expression partial unique `ux_account_email` (`lower(email) WHERE email IS NOT NULL` qua SQL) | Inline: `login_phone ~ '^\+84[0-9]{9}$'`, `status IN ('active','locked','disabled')`. Explicit: **`ck_account_login_phone`** (`status = 'disabled' OR login_phone IS NOT NULL`) | `customer_id → crm.customers(id) ON DELETE RESTRICT` |
| 6 | `crm.customer_logins` | `645-652` | `CRM` (`CustomerLogin`) | PK ghép `(provider, provider_key)` (`text, text`) | Chỉ PK | Inline: `provider IN ('google','zalo')` | `account_id → crm.customer_accounts(id) ON DELETE CASCADE` |
| 7 | `crm.interactions` | `654-672`, `879-881` | `CRM` (`Interaction`) | Cột `campaign_id uuid` nullable giữ sẵn | `ix_interactions_customer` (`customer_id, occurred_at DESC`); partial `ix_interactions_follow_up` (`follow_up_at WHERE follow_up_at IS NOT NULL AND follow_up_done_at IS NULL`) | Inline: `channel IN ('zalo_personal','zalo_oa','phone','facebook','in_person','email','web','other')`, `kind IN ('inquiry','consultation','quote','follow_up','complaint','note','campaign_message')`, `length(content) BETWEEN 1 AND 4000`. Table: `follow_up_done_at IS NULL OR follow_up_at IS NOT NULL` | `customer_id → crm.customers(id) ON DELETE CASCADE`; chéo module **`fk_interactions_order`**: `order_id → sales.orders(id) ON DELETE SET NULL`. **HOÃN:** `fk_interactions_campaign` (`campaign_id → marketing.campaigns`) sang `P5-02` |
| 8 | `crm.customer_interests` | `674-688` | `CRM` (`CustomerInterest`) | Không | Expression unique `ux_customer_interest` (`(customer_id, interest_type, coalesce(ref_id, '00000000-0000-0000-0000-000000000000'::uuid), coalesce(price_band, ''), source)` qua SQL); `ix_interest_lookup` (`interest_type, ref_id`) | Inline: `interest_type IN ('material','artisan','product_type','category','price_band')`, `price_band IN ('under_1m','1m_3m','3m_10m','over_10m')`, `source IN ('order','manual')`, `score > 0`. Table (2 CHECK): `(interest_type = 'price_band') = (price_band IS NOT NULL)` và `(interest_type = 'price_band') = (ref_id IS NULL)` | `customer_id → crm.customers(id) ON DELETE CASCADE` |
| 9 | `crm.consents` | `690-701` | `CRM` (`Consent`) | Không | `ux_consents_customer_id_channel_purpose` (`UNIQUE(customer_id, channel, purpose)`) | Inline: `channel IN ('zalo_personal','zalo_oa','sms','email','phone')`, `purpose IN ('marketing_new_products','marketing_promotions','customer_care')`, `status IN ('granted','revoked')`, `source IN ('checkout','account','staff','import','link')`, `length(trim(evidence)) >= 3` | `customer_id → crm.customers(id) ON DELETE CASCADE` |
| 10 | `crm.data_requests` | `703-713` | `CRM` (`DataRequest`) | Không | Chỉ PK | Inline: `request_type IN ('export','erase','correct','withdraw_consent')`, `status IN ('open','completed','rejected')` | `customer_id → crm.customers(id)` (NO ACTION) |
| 11 | `pricing.vouchers` | `530-550`, `594-595` | `Pricing` (`Voucher`) | `xmin` (`uint Version`); money/value: `discount_value bigint`, `max_discount_vnd bigint?`, `min_order_vnd bigint = 0` | `ux_vouchers_code` (`UNIQUE(code)`) | Inline: `code ~ '^[A-Z0-9]{4,20}$'`, `kind IN ('referral_reward','manual')`, `discount_type IN ('fixed','percent')`, `discount_value > 0`, `max_discount_vnd IS NULL OR max_discount_vnd > 0`, `min_order_vnd >= 0`, `usage_limit IS NULL OR usage_limit > 0`, `per_customer_limit > 0`, `used_count >= 0`, `status IN ('active','disabled')`. Table (3 CHECK): `ends_at > starts_at`, `discount_type <> 'percent' OR discount_value BETWEEN 1 AND 50`, `usage_limit IS NULL OR used_count <= usage_limit` | Chéo module **`fk_vouchers_customer`**: `bound_customer_id → crm.customers(id)` (NO ACTION) |
| 12 | `pricing.voucher_redemptions` | `1043-1052` | `Pricing` (`VoucherRedemption`) | Money: `amount_vnd bigint` | `ux_voucher_redemptions_order_id` (`UNIQUE(order_id)`); partial `ix_redemptions_voucher_customer` (`voucher_id, customer_id WHERE status = 'applied'`) | Inline: `amount_vnd > 0`, `status IN ('applied','reverted')` | `voucher_id → pricing.vouchers(id)` (NO ACTION); chéo module **`fk_redemptions_order`**: `order_id → sales.orders(id)` (NO ACTION); chéo module **`fk_redemptions_customer`**: `customer_id → crm.customers(id)` (NO ACTION) |
| 13 | `sales.carts` | `788-797` | `Sales` (`Cart`) | Không | `ux_carts_token_hash` (`UNIQUE(token_hash)`); partial unique `ux_cart_account` (`customer_account_id WHERE customer_account_id IS NOT NULL`); `ix_cart_expires` (`expires_at`) | Không | Chéo module: `customer_account_id → crm.customer_accounts(id) ON DELETE CASCADE` |
| 14 | `sales.cart_items` | `799-805` | `Sales` (`CartItem`) | PK ghép `(cart_id, variant_id)` | Chỉ PK | Inline: `quantity BETWEEN 1 AND 99` | `cart_id → sales.carts(id) ON DELETE CASCADE`; chéo schema: `variant_id → catalog.variants(id) ON DELETE CASCADE` |
| 15 | `sales.orders` | `807-877` | `Sales` (`Order`) | `xmin` (`uint Version`); `utm jsonb` nullable; money: `subtotal_vnd bigint`, `discount_vnd bigint = 0`, `shipping_fee_vnd bigint = 0`, `total_vnd bigint`; giữ `tracking_link_id uuid?`, `campaign_id uuid?` | **`ux_orders_code`** (`UNIQUE(code)`); `ix_orders_status_placed` (`status, placed_at DESC`); `ix_orders_customer` (`customer_id, placed_at DESC`); partial `ix_orders_delivered` (`delivered_at WHERE delivered_at IS NOT NULL`); `ix_orders_payment` (`payment_status, status`); partial `ix_orders_due` (`payment_due_at WHERE status = 'pending' AND payment_method = 'bank_transfer' AND payment_status = 'unpaid'`); partial `ix_orders_campaign` (`campaign_id WHERE campaign_id IS NOT NULL`); `ix_orders_contact_phone` (`contact_phone`) | Inline: `code ~ '^TP[0-9]{6}[A-Z0-9]{4}$'`, `origin IN ('web','admin')`, `sales_channel IN ('web','zalo_personal','zalo_oa','facebook','phone','in_person','other')`, `status IN ('pending','confirmed','shipping','delivered','delivery_failed','cancelled')`, `payment_method IN ('cod','bank_transfer')`, `payment_status IN ('unpaid','partially_paid','paid','partially_refunded','refunded')`, `contact_phone ~ '^\+84[0-9]{9}$'`, `receiver_phone ~ '^\+84[0-9]{9}$'`, `subtotal_vnd >= 0`, `discount_vnd >= 0`, `shipping_fee_vnd >= 0`, `total_vnd >= 0`. Explicit (6 CHECK): **`ck_order_total`** (`total_vnd = subtotal_vnd - discount_vnd + shipping_fee_vnd`), **`ck_order_discount`** (`discount_vnd <= subtotal_vnd`), **`ck_order_delivered`** (`(status = 'delivered') = (delivered_at IS NOT NULL)`), **`ck_order_cancelled`** (`(status = 'cancelled') = (cancelled_at IS NOT NULL)`), **`ck_order_web_channel`** (`origin <> 'web' OR sales_channel = 'web'`), **`ck_order_due`** (`payment_method <> 'bank_transfer' OR origin <> 'web' OR payment_due_at IS NOT NULL`) | Chéo module: `customer_id → crm.customers(id)` (NO ACTION), `customer_account_id → crm.customer_accounts(id)` (NO ACTION), `voucher_id → pricing.vouchers(id)` (NO ACTION). **HOÃN:** FK trên `tracking_link_id → marketing.tracking_links` (`P5-01`) và `campaign_id → marketing.campaigns` (`P5-02`) |
| 16 | `sales.order_lines` | `883-906` | `Sales` (`OrderLine`) | Money: `list_price_vnd bigint`, `unit_price_vnd bigint`, `line_total_vnd bigint`, `unit_cost_vnd bigint?` | `ux_order_lines_order_id_variant_id` (`UNIQUE(order_id, variant_id)`); `ix_order_lines_variant` (`variant_id`); `ix_order_lines_product` (`product_id`) | Inline: `quantity BETWEEN 1 AND 1000`, `list_price_vnd >= 0`, `unit_price_vnd >= 0`, `price_source IN ('list','sale','override')`, `unit_cost_vnd IS NULL OR unit_cost_vnd >= 0`. Explicit (3 CHECK): **`ck_line_total`** (`line_total_vnd = quantity * unit_price_vnd`), **`ck_line_override`** (`price_source <> 'override' OR length(trim(coalesce(price_override_reason,''))) >= 3`), **`ck_line_returned`** (`returned_quantity BETWEEN 0 AND quantity`) | `order_id → sales.orders(id) ON DELETE CASCADE`; chéo schema: `variant_id → catalog.variants(id)` (NO ACTION), `product_id → catalog.products(id)` (NO ACTION). **HOÃN:** FK ngược `inventory.reservations.fk_reservations_line` sang `P2-05` |
| 17 | `sales.order_status_history` | `911-921` | `Sales` (`OrderStatusHistory`) | Không | `ix_order_history` (`order_id, occurred_at`) | Inline: `actor_type IN ('staff','customer','system')` | `order_id → sales.orders(id) ON DELETE CASCADE` |
| 18 | `sales.cod_settlements` | `923-935` | `Sales` (`CodSettlement`) | Money: `gross_cod_vnd bigint`, `fee_vnd bigint = 0`, `net_received_vnd bigint` | Chỉ PK | Inline: `gross_cod_vnd >= 0`, `fee_vnd >= 0`. Table: `net_received_vnd = gross_cod_vnd - fee_vnd` | Không |
| 19 | `sales.payments` | `937-960` | `Sales` (`Payment`) | Money: `amount_vnd bigint` | `ix_payments_order` (`order_id`); partial `ix_payments_awaiting` (`created_at WHERE status = 'awaiting_settlement'`); partial unique `ux_payments_bank_ref` (`provider, bank_reference WHERE bank_reference IS NOT NULL AND status <> 'voided'`) | Inline: `method IN ('bank_transfer','cod','cash')`, `provider IN ('manual','sepay')`, `amount_vnd > 0`, `status IN ('awaiting_settlement','confirmed','voided')`. Table (3 CHECK): `(status = 'confirmed') = (confirmed_at IS NOT NULL)`, `status <> 'awaiting_settlement' OR method = 'cod'`, `status <> 'voided' OR void_reason IS NOT NULL` | `order_id → sales.orders(id) ON DELETE RESTRICT`; `cod_settlement_id → sales.cod_settlements(id)` (NO ACTION) |
| 20 | `sales.return_requests` | `962-977` | `Sales` (`ReturnRequest`) | `xmin` (`uint Version`) | `ux_return_requests_code` (`UNIQUE(code)`) | Inline: `reason IN ('damaged_in_transit','defective','wrong_item')`, `status IN ('requested','approved','rejected','resolved')`, `resolution IN ('refund','replacement')`. Table: `status NOT IN ('approved','resolved') OR resolution IS NOT NULL` | `order_id → sales.orders(id)` (NO ACTION) |
| 21 | `sales.return_lines` | `978-985` | `Sales` (`ReturnLine`) | Không | `ux_return_lines_return_request_id_order_line_id` (`UNIQUE(return_request_id, order_line_id)`) | Inline: `quantity > 0` | `return_request_id → sales.return_requests(id) ON DELETE CASCADE`; `order_line_id → sales.order_lines(id)` (NO ACTION) |
| 22 | `sales.return_evidence` | `986-990` | `Sales` (`ReturnEvidence`) | PK ghép `(return_request_id, asset_id)` | Chỉ PK | Không | `return_request_id → sales.return_requests(id) ON DELETE CASCADE`; chéo schema: `asset_id → media.assets(id)` (NO ACTION) |
| 23 | `sales.refunds` | `992-1004` | `Sales` (`Refund`) | Money: `amount_vnd bigint` | `ix_refunds_order` (`order_id`) | Inline: `amount_vnd > 0`, `method IN ('bank_transfer','cash')` | `order_id → sales.orders(id)` (NO ACTION); `return_request_id → sales.return_requests(id)` (NO ACTION) |
| 24 | `sales.shipments` | `1006-1031` | `Sales` (`Shipment`) | Money: `cod_amount_vnd bigint = 0`, `shipping_cost_vnd bigint?`, `return_cost_vnd bigint?` | Partial unique `ux_shipment_active_order` (`order_id WHERE purpose = 'order' AND status NOT IN ('cancelled','failed','returned')`); partial unique `ux_shipment_tracking` (`carrier_name, tracking_code WHERE tracking_code IS NOT NULL`) | Inline: `purpose IN ('order','replacement')`, `provider IN ('manual','ghn','ghtk')`, `status IN ('created','picked_up','in_transit','delivered','failed','returned','cancelled')`, `cod_amount_vnd >= 0`, `shipping_cost_vnd IS NULL OR shipping_cost_vnd >= 0`, `return_cost_vnd IS NULL OR return_cost_vnd >= 0`. Table: `purpose <> 'replacement' OR return_request_id IS NOT NULL` | `order_id → sales.orders(id)` (NO ACTION); `return_request_id → sales.return_requests(id)` (NO ACTION) |
| 25 | `sales.shipment_events` | `1033-1041` | `Sales` (`ShipmentEvent`) | Không | Chỉ PK | Inline: `source IN ('manual','webhook')` | `shipment_id → sales.shipments(id) ON DELETE CASCADE` |
| 26 | `finance.expenses` | `1217-1232` | `Finance` (`Expense`) | Money: `amount_vnd bigint`; `incurred_on date` (`DateOnly`); giữ `campaign_id uuid?` | `ix_expenses_date` (`incurred_on, category`) | Inline: `category IN ('marketing','fixed','packaging','shipping_other','payment_fee','damage_other','other')`, `amount_vnd > 0`. Table (2 CHECK): `category <> 'marketing' OR channel IS NOT NULL`, `category <> 'fixed' OR extract(day FROM incurred_on) = 1` | **HOÃN:** FK `campaign_id → marketing.campaigns(id)` (`schema.sql:1225`) sang `P5-02` |

---

## 3. Thứ tự tạo trong migration, nội dung `Down` và cách xử lý FK chéo module

### 3.1 Cách xử lý FK chéo module `crm` ↔ `sales` ↔ `pricing` không phá kiểm thử kiến trúc

1. **Giữ một `AppDbContext` duy nhất trong `TeaPremium.Infrastructure.Persistence`:**
   - Theo `DEC-01`, `02-architecture.md` §3 và `03-database.md:17` ("FK chéo schema: Được phép (một database). Ranh giới module giữ ở tầng code (02 §3)"), hệ thống dùng duy nhất `AppDbContext` cho mọi schema. Không tách nhiều `DbContext` (tránh gãy `public."__EFMigrationsHistory"`, snapshot và transaction `IUnitOfWork` đơn DB).
2. **Persistence POCO không có navigation chéo module và không rò sang `Domain`/`Application`:**
   - Toàn bộ 26 lớp dữ liệu (`Customer`, `Tag`, `CustomerTag`, ..., `Voucher`, `VoucherRedemption`, `Cart`, ..., `Expense`) đặt trong `TeaPremium.Infrastructure.Persistence` (`CrmSalesFinanceEntities.cs`), chỉ giữ thuộc tính khóa ngoại kiểu vô hướng (`Guid`, `Guid?`, `string`, `string?`) — **không khai báo navigation property chéo module** (như `Order.Customer`, `Order.Voucher`, `Interaction.Order`, `VoucherRedemption.Order`, `OrderLine.Variant`).
   - Cấu hình FK chéo module trong `CrmSalesFinanceModel.cs` dùng generic không navigation: `b.HasOne<Customer>().WithMany().HasForeignKey(x => x.BoundCustomerId).HasConstraintName("fk_vouchers_customer").OnDelete(DeleteBehavior.NoAction)` (giống hệt cách `CatalogInventoryModel.cs` đã nối `catalog` ↔ `media` ↔ `inventory` ↔ `pricing`).
   - `TeaPremium.Domain` và `TeaPremium.Application` hoàn toàn không bị chỉnh sửa ở `P2-01` → `ProjectGraphTests` và `CompiledArchitectureTests` (`AC-P0-05`: Domain chỉ BCL, Application không tham chiếu EF Core/Npgsql/Infrastructure, không gọi `DateTime.Now`) giữ xanh tuyệt đối; sang `P2-02+`, repository của module A không thể vô tình nạp/ghi đồ thị entity của module B.
3. **Khử chu trình tạo bảng giữa `crm` ↔ `pricing` ↔ `sales`:**
   - Trên đồ thị bảng thực tế, `crm.customers` không phụ thuộc `pricing` hay `sales`; `pricing.vouchers` chỉ phụ thuộc `crm.customers`; `sales.orders` phụ thuộc `(crm.customers, crm.customer_accounts, pricing.vouchers)`; `crm.interactions` và `pricing.voucher_redemptions` phụ thuộc `(crm.customers, sales.orders, pricing.vouchers)`. Tuy nhiên, để tường minh hóa đúng cấu trúc `schema → sequence → bảng → FK chéo module` (giống `schema.sql:594,879`), migration tách bước `AddForeignKey` cho các FK chéo module sau khi toàn bộ 26 bảng đã được tạo.

### 3.2 Thứ tự thực thi trong `Up(MigrationBuilder migrationBuilder)`

1. **Preflight đầu migration (`migrationBuilder.Sql`):** chạy bản đóng băng `GrantsSql.Preflight` (đã mở rộng kiểm 11 schema P0–P2). Nếu thiếu role `tp_owner`/`tp_app`/`tp_readonly`, chạy sai user (không phải `tp_owner`), sai owner DB/schema/object, hoặc có drift ACL/grant-option trên bất kỳ schema hiện hữu nào → dừng ngay trước khi tạo bất kỳ schema/bảng mới nào.
2. **Tạo schema (`EnsureSchema`):** tạo 3 schema mới `crm`, `sales`, `finance` (schema `pricing` đã có từ `P1-01`; `EnsureSchema` sinh `CREATE SCHEMA IF NOT EXISTS` an toàn khi chạy idempotent).
3. **Tạo sequence (`CreateSequence`):** `crm.customer_code_seq` (`START WITH 1000`, kiểu `bigint`, `INCREMENT BY 1`, không cycle).
4. **Tạo 26 bảng (`CreateTable`) kèm PK, UNIQUE nội bảng (NOT NULL), CHECK và FK nội module / FK sang P0–P1 (`platform`, `catalog`, `media`) theo bậc phụ thuộc:**
   - *Nhóm nền không phụ thuộc bảng P2 khác:* `crm.customers` (gồm self-FK `referred_by_customer_id`, `merged_into_customer_id`), `crm.tags`, `pricing.vouchers` (chưa gắn `fk_vouchers_customer`), `sales.cod_settlements`, `finance.expenses`.
   - *Nhóm con nội module `crm`:* `crm.customer_tags`, `crm.customer_addresses` (FK `platform.provinces`/`wards`), `crm.customer_accounts`, `crm.customer_logins`, `crm.interactions` (FK `crm.customers`; chưa gắn `fk_interactions_order`), `crm.customer_interests`, `crm.consents`, `crm.data_requests`.
   - *Nhóm `sales` và `pricing.voucher_redemptions`:* `sales.carts`, `sales.cart_items` (FK `catalog.variants`), `sales.orders` (`ux_orders_code`, `ck_order_*`), `sales.order_lines` (FK `catalog.variants`, `catalog.products`), `sales.order_status_history`, `sales.payments`, `sales.return_requests`, `sales.return_lines`, `sales.return_evidence` (FK `media.assets`), `sales.refunds`, `sales.shipments`, `sales.shipment_events`, `pricing.voucher_redemptions`.
5. **Ràng buộc UNIQUE nullable và 31 chỉ mục tường minh (`CreateIndex` + `Sql`):**
   - `ALTER TABLE crm.customer_accounts ADD CONSTRAINT ux_customer_accounts_login_phone UNIQUE (login_phone);` (tránh EF alternate key ép `NOT NULL`).
   - 2 expression unique indexes qua SQL cố định: `ux_account_email` (`lower(email) WHERE email IS NOT NULL`) và `ux_customer_interest` (`coalesce(ref_id, ...), coalesce(price_band, '')`).
   - GIN index `ix_customers_search` (`USING gin (search_text gin_trgm_ops)`), 6 partial unique indexes (`ux_customers_phone`, `ux_customer_default_address`, `ux_cart_account`, `ux_payments_bank_ref`, `ux_shipment_active_order`, `ux_shipment_tracking`) và 22 B-tree/partial indexes còn lại (gồm `ix_orders_campaign`).
6. **Gắn các FK chéo module `crm` ↔ `sales` ↔ `pricing` (`AddForeignKey`):**
   - `pricing.vouchers`: `fk_vouchers_customer` (`bound_customer_id → crm.customers(id)`, `NO ACTION`).
   - `sales.carts`: `fk_carts_customer_account_id` (`customer_account_id → crm.customer_accounts(id)`, `ON DELETE CASCADE`).
   - `sales.orders`: `fk_orders_customer_id` (`customer_id → crm.customers(id)`, `NO ACTION`), `fk_orders_customer_account_id` (`customer_account_id → crm.customer_accounts(id)`, `NO ACTION`), `fk_orders_voucher_id` (`voucher_id → pricing.vouchers(id)`, `NO ACTION`).
   - `crm.interactions`: `fk_interactions_order` (`order_id → sales.orders(id)`, `ON DELETE SET NULL`).
   - `pricing.voucher_redemptions`: `fk_redemptions_order` (`order_id → sales.orders(id)`, `NO ACTION`), `fk_redemptions_customer` (`customer_id → crm.customers(id)`, `NO ACTION`).
7. **Áp quyền cố định cuối migration (`migrationBuilder.Sql`):** chạy bản đóng băng của `GrantsSql.CrmSalesFinance()` (bao gồm `PlatformAndIamGrants` + `CatalogAndInventoryGrants` + `CrmSalesFinanceGrants` + hậu kiểm `Preflight` để tự xác nhận mọi ACL vừa cấp đều hợp lệ).

### 3.3 Nội dung `Down(MigrationBuilder migrationBuilder)` (sạch về `P1Head`, không sót schema/grant)

1. **Pre-drop guard (`DO $p201_down$ ... END $p201_down$;`) chạy trước mọi lệnh xóa:**
   - Kiểm tra `current_user = 'tp_owner'`.
   - Khóa `LOCK TABLE ... IN ACCESS EXCLUSIVE MODE` toàn bộ 26 bảng thuộc `P2-01`.
   - Kiểm dữ liệu từng bảng trong 26 bảng: hễ có ≥ 1 dòng ở bất kỳ bảng nào → `RAISE EXCEPTION 'P2-01 Down refused: data in %; roll forward or restore an approved backup'`.
   - Kiểm catalog PostgreSQL (`pg_depend`, `pg_class`, `pg_proc`, `pg_type`, `pg_attribute`, `pg_constraint`, `pg_trigger`, `pg_default_acl`) tương tự `P1-01`: từ chối ngay nếu trong `crm`, `sales`, `finance` có bất kỳ object/collation/operator family/function/type/table lạ nào, hoặc trong `pricing` có object lạ ngoài tập `P1-01` (`pricing.sale_prices`) + `P2-01` (`vouchers`, `voucher_redemptions`), hoặc có cột/constraint/trigger/FK bên ngoài tham chiếu tới 26 bảng hay `crm.customer_code_seq`.
2. **Gỡ các FK chéo module (`DropForeignKey`):** gỡ `fk_interactions_order`, `fk_redemptions_order`, `fk_redemptions_customer`, `fk_orders_voucher_id`, `fk_orders_customer_account_id`, `fk_orders_customer_id`, `fk_carts_customer_account_id`, `fk_vouchers_customer`.
3. **Xóa 26 bảng (`DropTable`) theo đúng thứ tự ngược phụ thuộc:**
   - `sales.shipment_events` → `sales.shipments`, `sales.refunds`, `sales.return_evidence`, `sales.return_lines` → `pricing.voucher_redemptions`, `sales.return_requests`, `sales.payments`, `sales.order_status_history`, `sales.order_lines`, `sales.cart_items`, `crm.interactions` → `sales.orders`, `sales.carts`, `crm.customer_logins` → `crm.data_requests`, `crm.consents`, `crm.customer_interests`, `crm.customer_accounts`, `crm.customer_addresses`, `crm.customer_tags`, `pricing.vouchers` → `finance.expenses`, `sales.cod_settlements`, `crm.tags`, `crm.customers`. (`pricing.sale_prices` của `P1-01` được giữ nguyên).
4. **Xóa sequence (`DropSequence`):** `DROP SEQUENCE crm.customer_code_seq`.
5. **Thu hồi default privileges / grants và xóa 3 schema mới (tuyệt đối KHÔNG `CASCADE`):**
   ```sql
   ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA crm,sales,finance REVOKE SELECT ON TABLES FROM tp_readonly;
   ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA crm,sales,finance REVOKE USAGE ON SEQUENCES FROM tp_app;
   ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA crm,sales,finance REVOKE SELECT ON SEQUENCES FROM tp_readonly;
   REVOKE ALL ON SCHEMA crm,sales,finance FROM PUBLIC,tp_app,tp_readonly;
   DROP SCHEMA crm;
   DROP SCHEMA sales;
   DROP SCHEMA finance;
   ```
   *(Lưu ý: schema `pricing` cùng default privileges của `pricing` và ACL của `pricing.sale_prices` thuộc `P1-01` nên được giữ nguyên để trạng thái sau `Down` khớp 100% với `202611191437_P1_04_TaxonomyPublicationHistory`).*

---

## 4. Ma trận grants cho `tp_app` và `tp_readonly` (26 bảng + sequence + schema)

**Nguyên tắc chung (`DEC-30`, `03-database.md` §4, Quyết định owner #2 & #3):**
- `PUBLIC`: `REVOKE ALL` trên mọi schema (`crm`, `sales`, `finance`, `pricing`), mọi bảng và `crm.customer_code_seq`.
- Không role nào (`tp_app`, `tp_readonly`) có `WITH GRANT OPTION` (`is_grantable = false` ở mọi ACL), không có `TRUNCATE`, `REFERENCES`, `TRIGGER`, `MAINTAIN`, không có quyền tạo bảng (`CREATE` trên schema) và không có default DML cho `tp_app` trên bảng mới.
- **Schemas (`crm`, `sales`, `finance`, cùng `pricing` đã có):** `tp_app` = `USAGE`; `tp_readonly` = `USAGE`.
- **Default privileges (`FOR ROLE tp_owner IN SCHEMA crm, sales, finance`):** `TABLES` → `GRANT SELECT TO tp_readonly` (không cấp default DML cho `tp_app`); `SEQUENCES` → `GRANT USAGE TO tp_app`, `GRANT SELECT TO tp_readonly`.

| # | Đối tượng DB | Loại | Quyền của `tp_app` (runtime API) | Quyền bị thu hồi / cấm với `tp_app` (`42501`) | Quyền của `tp_readonly` (chỉ backup) | Ghi chú nghiệp vụ / bảo vệ |
|---:|---|---|---|---|---|---|
| S1 | `crm.customer_code_seq` | Sequence | `USAGE` (`nextval`, `currval`) | `SELECT`, `UPDATE` (`setval`), grant option | `SELECT` (phục vụ `pg_dump`; cấm `nextval`/`setval`) | Cấp mã `KH` bắt đầu từ `1000` |
| 1 | `crm.customers` | Table | `SELECT, INSERT, UPDATE` | **`DELETE`, `TRUNCATE`**, `MAINTAIN`, DDL, grant option | `SELECT` | **Bảng bảo vệ #1/5** (chỉ ẩn danh `erased` / gộp `merged_into_customer_id`, cấm xóa cứng) |
| 2 | `crm.tags` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Quản trị nhãn khách |
| 3 | `crm.customer_tags` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Gắn/gỡ nhãn khách & xóa khi erase (`BR-CRM-11`) |
| 4 | `crm.customer_addresses` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Sổ địa chỉ khách & xóa khi erase (`BR-CRM-11`) |
| 5 | `crm.customer_accounts` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Tài khoản khách (`disabled` khi erase) |
| 6 | `crm.customer_logins` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Liên kết OAuth; xóa khi erase (`BR-CRM-11`) |
| 7 | `crm.interactions` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Tương tác & hẹn chăm sóc (`content='[Đã xóa]'` khi erase) |
| 8 | `crm.customer_interests` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Sở thích tự suy/thêm tay & xóa khi erase |
| 9 | `crm.consents` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Consent theo kênh/mục đích & xóa khi erase |
| 10 | `crm.data_requests` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Yêu cầu quyền dữ liệu cá nhân (`NFR-12`) |
| 11 | `pricing.vouchers` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Voucher giảm giá / thưởng giới thiệu |
| 12 | `pricing.voucher_redemptions` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Lượt áp/hoàn voucher theo đơn |
| 13 | `sales.carts` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Giỏ hàng (xóa sau đặt đơn / hết hạn 30 ngày) |
| 14 | `sales.cart_items` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Dòng giỏ hàng |
| 15 | `sales.orders` | Table | `SELECT, INSERT, UPDATE` | **`DELETE`, `TRUNCATE`**, `MAINTAIN`, DDL, grant option | `SELECT` | **Bảng bảo vệ #2/5** (chứng từ kế toán vĩnh viễn) |
| 16 | `sales.order_lines` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | **Cho phép `DELETE`** theo quyết định owner #2 để sửa dòng đơn `pending`/`confirmed` (`BR-ORD-08`) |
| 17 | `sales.order_status_history` | Table | `SELECT, INSERT, UPDATE` | **`DELETE`, `TRUNCATE`**, `MAINTAIN`, DDL, grant option | `SELECT` | **Bảng bảo vệ #3/5** (lịch sử chuyển trạng thái đơn) |
| 18 | `sales.cod_settlements` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Phiên đối soát COD hãng vận chuyển |
| 19 | `sales.payments` | Table | `SELECT, INSERT, UPDATE` | **`DELETE`, `TRUNCATE`**, `MAINTAIN`, DDL, grant option | `SELECT` | **Bảng bảo vệ #4/5** (sổ thu tiền; hủy dùng `status = 'voided'`) |
| 20 | `sales.return_requests` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Yêu cầu đổi trả |
| 21 | `sales.return_lines` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Dòng hàng đổi trả |
| 22 | `sales.return_evidence` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Liên kết ảnh/video bằng chứng đổi trả |
| 23 | `sales.refunds` | Table | `SELECT, INSERT, UPDATE` | **`DELETE`, `TRUNCATE`**, `MAINTAIN`, DDL, grant option | `SELECT` | **Bảng bảo vệ #5/5** (chứng từ hoàn tiền vĩnh viễn) |
| 24 | `sales.shipments` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Vận đơn giao/đổi hàng |
| 25 | `sales.shipment_events` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Sự kiện hành trình vận đơn |
| 26 | `finance.expenses` | Table | `SELECT, INSERT, UPDATE, DELETE` | `TRUNCATE`, `MAINTAIN`, DDL, grant option | `SELECT` | Chi phí (`DELETE` trong 30 ngày có audit theo `05-api.md:250`) |

---

## 5. Đặc tả được phép sửa trong G3 (Spec Amendment — `file:dòng` chính xác)

*(Phiên PLAN này tuyệt đối không sửa `docs/spec/`. G3 chỉ áp đúng 4 đoạn thay đổi dưới đây).*

### 5.1 [schema.sql](../spec/schema.sql) dòng `499-509` — `inventory.reservations` cho `P2-05`

- **Trước (`docs/spec/schema.sql:499-509`):**
```sql
CREATE TABLE inventory.reservations (
  id             uuid PRIMARY KEY,
  variant_id     uuid NOT NULL REFERENCES catalog.variants(id),
  order_line_id  uuid NOT NULL,                 -- FK thêm sau khi tạo sales.order_lines
  quantity       int NOT NULL CHECK (quantity > 0),
  status         text NOT NULL DEFAULT 'active' CHECK (status IN ('active','consumed','released')),
  created_at     timestamptz NOT NULL DEFAULT now(),
  closed_at      timestamptz,
  close_reason   text,
  CHECK ((status = 'active') = (closed_at IS NULL))
);
```
- **Sau (`docs/spec/schema.sql:499-510`):**
```sql
CREATE TABLE inventory.reservations (
  id             uuid PRIMARY KEY,
  variant_id     uuid NOT NULL REFERENCES catalog.variants(id),
  order_line_id  uuid,                          -- FK thêm sau khi tạo sales.order_lines; NULL khi released và dòng đơn bị xóa
  quantity       int NOT NULL CHECK (quantity > 0),
  status         text NOT NULL DEFAULT 'active' CHECK (status IN ('active','consumed','released')),
  created_at     timestamptz NOT NULL DEFAULT now(),
  closed_at      timestamptz,
  close_reason   text,
  CHECK ((status = 'active') = (closed_at IS NULL)),
  CHECK (status = 'released' OR order_line_id IS NOT NULL)
);
```

### 5.2 [schema.sql](../spec/schema.sql) dòng `585` — `ck_customer_contact` trên `crm.customers`

*(Đã kiểm tra toàn bộ `docs/spec/03-database.md`: không có chỗ nào trích dẫn trực tiếp tên/biểu thức `ck_customer_contact`; câu giải thích ở `03-database.md:77` đã ghi "với hồ sơ chưa gộp" nên không mâu thuẫn).*

- **Trước (`docs/spec/schema.sql:585`):**
```sql
  CONSTRAINT ck_customer_contact CHECK (contact_status = 'erased' OR phone_e164 IS NOT NULL OR zalo_name IS NOT NULL),
```
- **Sau (`docs/spec/schema.sql:585`):**
```sql
  CONSTRAINT ck_customer_contact CHECK (contact_status = 'erased' OR merged_into_customer_id IS NOT NULL OR phone_e164 IS NOT NULL OR zalo_name IS NOT NULL),
```

### 5.3 [schema.sql](../spec/schema.sql) dòng `908-909` — `fk_reservations_line` cho `P2-05`

- **Trước (`docs/spec/schema.sql:908-909`):**
```sql
ALTER TABLE inventory.reservations
  ADD CONSTRAINT fk_reservations_line FOREIGN KEY (order_line_id) REFERENCES sales.order_lines(id) ON DELETE RESTRICT;
```
- **Sau (`docs/spec/schema.sql:908-909`):**
```sql
ALTER TABLE inventory.reservations
  ADD CONSTRAINT fk_reservations_line FOREIGN KEY (order_line_id) REFERENCES sales.order_lines(id) ON DELETE SET NULL;
```

### 5.4 [03-database.md](../spec/03-database.md) dòng `153-158` — `REVOKE` của `tp_app` và ghi chú `P2-05`

- **Trước (`docs/spec/03-database.md:153-158`):**
```markdown
Ngoại lệ thu hồi quyền của `tp_app`:
```sql
REVOKE UPDATE, DELETE, TRUNCATE ON platform.audit_logs, inventory.stock_movements FROM tp_app;
REVOKE DELETE, TRUNCATE ON sales.orders, sales.order_lines, sales.payments, sales.refunds,
                         sales.order_status_history, crm.customers FROM tp_app;
```
```
- **Sau (`docs/spec/03-database.md:153-160`):**
```markdown
Ngoại lệ thu hồi quyền của `tp_app` (mọi bảng đều `REVOKE TRUNCATE` khỏi `tp_app`):
```sql
REVOKE UPDATE, DELETE, TRUNCATE ON platform.audit_logs, inventory.stock_movements FROM tp_app;
REVOKE DELETE, TRUNCATE ON sales.orders, sales.payments, sales.refunds,
                         sales.order_status_history, crm.customers FROM tp_app;
```

`sales.order_lines` không thu hồi `DELETE` để cho phép xóa dòng khi sửa đơn `pending`/`confirmed` (`BR-ORD-08`, `BR-INV-04`); ở `P2-05`, `inventory.reservations.order_line_id` sẽ cho phép `NULL` khi `status = 'released'` kèm `CHECK (status = 'released' OR order_line_id IS NOT NULL)` và `fk_reservations_line` `ON DELETE SET NULL`.
```

---

## 6. Kế hoạch file chính xác trong G3

Tổng cộng **19 file code/spec** (`17` file trong `backend/` + `2` file trong `docs/spec/`), cộng `1` file SQL preflight dùng chung nhúng vào `TeaPremium.Infrastructure` (`infra/postgres/preflight.sql`, theo `TeaPremium.Infrastructure.csproj:29`) và cập nhật kết quả thực thi vào chính hồ sơ `docs/plans/p2-01-implementation.md` này (**tổng 21 file**, `0` file trong `apps/` hay `packages/`):

### 6.1 `docs/spec/` (2 file sửa)
1. `docs/spec/schema.sql` — sửa đúng 3 vị trí ở Mục 5.1, 5.2, 5.3.
2. `docs/spec/03-database.md` — sửa đúng vị trí ở Mục 5.4.

### 6.2 `backend/src/` (7 file: 4 tạo mới, 3 sửa) + embedded SQL preflight (1 file sửa)
3. `infra/postgres/preflight.sql` *(sửa — embedded resource `TeaPremium.DatabasePreflight.sql` đọc bởi `GrantsSql.Preflight` và `scripts/dev-migrate.ps1`)* — mở rộng phạm vi kiểm ACL/PUBLIC/grant-option/column-grant từ `('platform','iam','public')` sang đầy đủ `('platform','iam','public','media','catalog','inventory','pricing','content','crm','sales','finance')`, đồng thời chặn `tp_app` có `DELETE` trên 5 bảng bảo vệ (`sales.orders`, `sales.payments`, `sales.refunds`, `sales.order_status_history`, `crm.customers`) hoặc `UPDATE`/`DELETE` trên 3 sổ append-only (`platform.audit_logs`, `platform.administrative_data_imports`, `inventory.stock_movements`).
4. `backend/src/TeaPremium.Infrastructure/Persistence/CrmSalesFinanceEntities.cs` *(tạo mới)* — 26 persistence POCO entities cho `crm`, `pricing` (`Voucher`, `VoucherRedemption`), `sales`, `finance` (`Expense`).
5. `backend/src/TeaPremium.Infrastructure/Persistence/CrmSalesFinanceModel.cs` *(tạo mới)* — cấu hình EF Core tường minh cho `crm.customer_code_seq` và 26 bảng (tên bảng/schema, kiểu cột, default SQL, PK, FK, UNIQUE, partial unique, GIN, CHECK, annotations cho nullable unique và expression indexes).
6. `backend/src/TeaPremium.Infrastructure/Persistence/AppDbContext.cs` *(sửa)* — gọi `CrmSalesFinanceModel.Configure(builder)` trong `OnModelCreating` và bổ sung ánh xạ `DateOnly`/`DateOnly? => "date"` trong vòng kiểm kiểu cuối.
7. `backend/src/TeaPremium.Infrastructure/Persistence/GrantsSql.cs` *(sửa)* — thêm `CrmSalesFinance()` và hằng `CrmSalesFinanceGrants` theo đúng ma trận Mục 4.
8. `backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611230421_P2_01_CrmSalesFinanceFoundation.cs` *(tạo mới)* — migration forward + `Down` theo Mục 3 (preflight và grants là literal đóng băng, không gọi `GrantsSql` lúc runtime).
9. `backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611230421_P2_01_CrmSalesFinanceFoundation.Designer.cs` *(tạo mới)* — metadata EF Designer của migration mới.
10. `backend/src/TeaPremium.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs` *(sửa)* — snapshot model sau khi thêm 26 entity và `crm.customer_code_seq`.

### 6.3 `backend/tests/` (10 file: 4 tạo mới, 6 sửa giữ tương thích regression P0/P1)
11. `backend/tests/TeaPremium.IntegrationTests/CrmSalesFinanceFixture.cs` *(tạo mới)* — fixture PG17 dùng `SharedPostgresServer`, hằng `P1Head = "202611191437_P1_04_TaxonomyPublicationHistory"` và bộ seed dữ liệu giả tổng hợp (không PII thật).
12. `backend/tests/TeaPremium.IntegrationTests/CrmSalesFinanceParityTests.cs` *(tạo mới)* — kiểm parity catalog PG17 đầy đủ cho 26 bảng + `crm.customer_code_seq` và detector bắt lệch ngữ nghĩa (`T01`, `T02`).
13. `backend/tests/TeaPremium.IntegrationTests/CrmSalesFinanceDatabaseTests.cs` *(tạo mới)* — kiểm pending model changes, idempotent script chạy 2 lần, quyền thật `tp_app`/`tp_readonly`, preflight drift, probe CHECK/partial unique/expression unique/GIN và xung đột `xmin` trên 4 entity (`T03`–`T05`, `T09`–`T17`).
14. `backend/tests/TeaPremium.IntegrationTests/CrmSalesFinanceRollbackTests.cs` *(tạo mới)* — kiểm `Down` về `P1Head` sạch schema/grant, `Up` lại thành công, và chặn `Down` khi bất kỳ bảng nào trong 26 bảng có dữ liệu hoặc có object ngoài phạm vi (`T06`–`T08`).
15. `backend/tests/TeaPremium.IntegrationTests/CatalogInventoryParityTests.cs` *(sửa)* — ghim DB kiểm chứng P1 tại `202611191437_P1_04_TaxonomyPublicationHistory` để phạm vi `pricing` của P1 giữ đúng 25 bảng (2 bảng `pricing` mới được kiểm ở `CrmSalesFinanceParityTests`).
16. `backend/tests/TeaPremium.IntegrationTests/CatalogInventoryDatabaseTests.cs` *(sửa)* — cập nhật số lượng migration tổng ở head mới nhất (`6` → `7`), phân biệt bảng P1/P2 trong `pricing` và giữ kiểm tra đóng băng lịch sử của `P1-01` khi `GrantsSql.Preflight` mở rộng ở `P2-01`.
17. `backend/tests/TeaPremium.IntegrationTests/CatalogInventoryRollbackTests.cs` *(sửa)* — ghim mốc migrate-up của bài test rollback P1 hoặc đếm đúng 25 bảng thuộc P1 sau khi `pricing` có thêm 2 bảng ở `P2-01`.
18. `backend/tests/TeaPremium.IntegrationTests/TaxonomyMigrationTests.cs` *(sửa)* — ghim `target: "202611191437_P1_04_TaxonomyPublicationHistory"` trong `DownRefusesToDiscardKnownPublicationHistory` để test trực tiếp guard `P1-04`.
19. `backend/tests/TeaPremium.IntegrationTests/DatabaseRolesTests.cs` *(sửa)* — cập nhật số dòng `public."__EFMigrationsHistory"` tại head mới nhất (`6L` → `7L`).
20. `backend/tests/TeaPremium.IntegrationTests/P009ImportTests.cs` *(sửa)* — thay 2 khối mock `CREATE SCHEMA crm/sales` tạm thời từ P0-09 sang chèn dữ liệu giả trực tiếp vào bảng thật `crm.customers`/`crm.customer_addresses`/`sales.orders` do `P2-01` vừa tạo.

### 6.4 `docs/plans/` (1 file cập nhật bằng chứng G3)
21. `docs/plans/p2-01-implementation.md` *(cập nhật khi chạy G3)* — điền ID migration thực tế, số liệu PASS, đường dẫn log `artifacts/verify-linux/` và quy chiếu `artifacts/p2-01/candidate.manifest`.

---

## 7. Kế hoạch kiểm thử PostgreSQL 17 (`0` skipped)

Mọi integration test chạy trên container PostgreSQL 17 thật qua `SharedPostgresServer` / `Testcontainers.PostgreSql`, **tuyệt đối `0` skipped** (`verify-linux.ps1` chặn skip). Thực tế triển khai **18 test methods (`[Fact]`/`[Theory]`) = `151` ca thực thi kiểm thử mới** trong 3 test class mới, cộng toàn bộ suite regression hiện hữu (`1.211` ca):

| Mã | Test method dự kiến | Loại / Số ca | Nội dung kiểm chứng & kết quả bắt buộc |
|---|---|---:|---|
| `T01` | `CrmSalesFinanceParityTests.EveryColumnConstraintIndexForeignKeyAndSequenceMatchesApprovedDdl` | `[Fact]` (1) | Đối chiếu toàn bộ `pg_attribute`, `pg_attrdef`, `pg_constraint`, `pg_indexes`, `pg_sequences` của 4 schema `('crm','sales','finance','pricing')` giữa DB chạy DDL tham chiếu trích từ `docs/spec/schema.sql` (chỉ lược 4 FK `marketing.*` đã hoãn) và DB chạy EF migration: khớp 100% cả 26 bảng mới (+ `pricing.sale_prices` = 27), `crm.customer_code_seq` (`start_value = 1000`), mọi tên explicit/owner-locked, kiểu, nullability, default, biểu thức CHECK, FK `ON DELETE` và index predicate; xác nhận `0` bảng `marketing.*` và `0` bảng `inventory.reservations`. |
| `T02` | `CrmSalesFinanceParityTests.ParityDetectsSameNamedSemanticDrift` | `[Theory]` (9) | Chọc sai từng hạng mục trên DB tham chiếu (kiểu cột, nullability, default, biểu thức `ck_customer_contact`, predicate `ux_customers_phone`, biểu thức `ux_account_email`, biểu thức `ux_customer_interest`, `ON DELETE` của `fk_interactions_order`, `INCREMENT` của `crm.customer_code_seq`) → bộ so sánh parity phải phát hiện lệch ngay. |
| `T03` | `CrmSalesFinanceDatabaseTests.ModelHasNoPendingChangesAndMigrationMatchesAuthoringContract` | `[Fact]` (1) | `HasPendingModelChanges() == false` (kèm CLI `dotnet ef migrations has-pending-model-changes` trong `verify-linux.ps1`), đúng 26 `CreateTableOperation` + 1 `CreateSequenceOperation` (`crm.customer_code_seq`), không có cột người dùng `xmin`/`version`, SQL đầu/cuối khớp `GrantsSql.Preflight` và `GrantsSql.CrmSalesFinance()`. |
| `T04` | `CrmSalesFinanceDatabaseTests.EmptyAndExistingP1UpgradePreserveDataSequencesAndHistory` | `[Fact]` (1) | Nâng cấp từ DB rỗng và từ DB tại `P1Head` đã có dữ liệu giả (`platform`, `iam`, `media`, `catalog`, `inventory`, `pricing.sale_prices`, `content.redirects`, `catalog.sku_number_seq`) lên `P2-01`: chạy `infra/postgres/preflight.sql` + `GrantsSql.Preflight` trên `P1Head` (C1) → PASS, chạy `Up` `P2-01` → PASS, chạy lại `preflight.sql` + `GrantsSql.Preflight` → PASS; fingerprint dữ liệu P0/P1, giá trị sequence SKU và 6 dòng lịch sử cũ giữ nguyên 100% (tổng `7` migrations). |
| `T05` | `CrmSalesFinanceDatabaseTests.IdempotentScriptExecutesTwiceAndPreservesState` | `[Fact]` (1) | Sinh `IMigrator.GenerateScript(..., Idempotent)` (tương đương `dotnet ef migrations script --idempotent`), chạy lần 1, chèn dữ liệu giả P2 + gọi `nextval('crm.customer_code_seq')` (`1000`), chạy lại toàn bộ script lần 2 → không lỗi, `7` dòng history không trùng, dữ liệu P0–P2 giữ nguyên, `nextval` kế tiếp trả `1001`. |
| `T06` | `CrmSalesFinanceRollbackTests.EmptyDownToP1LeavesZeroLeftoverSchemasOrGrantsAndUpAgainWorks` | `[Fact]` (1) | Chạy `Down` từ `P2-01` về `P1Head` trên DB rỗng P2 → xác nhận xóa sạch 26 bảng, `crm.customer_code_seq`, `to_regnamespace('crm') IS NULL`, `to_regnamespace('sales') IS NULL`, `to_regnamespace('finance') IS NULL`, `pg_default_acl` và `nspacl`/`relacl` khớp tuyệt đối snapshot tại `P1Head` (`pricing` và `pricing.sale_prices` còn nguyên); sau đó `MigrateAsync()` lên lại `P2-01` thành công. |
| `T07` | `CrmSalesFinanceRollbackTests.DownWithDataInAnyOwnedTableRefusesBeforeAnyDrop` | `[Theory]` (26) | Mỗi `InlineData` chèn dữ liệu tối thiểu vào **1 trong 26 bảng** → `Down` về `P1Head` ném `PostgresException` (`P0001`, `"P2-01 Down refused: data in ..."`), toàn bộ 26 bảng/sequence/schema/OID giữ nguyên không mất bất kỳ object nào. |
| `T08` | `CrmSalesFinanceRollbackTests.DownWithOutsideObjectOrDependencyRefusesAndRecovers` | `[Theory]` (9) | Tạo bảng lạ trong `crm`/`sales`/`finance`/`pricing`, view/function/FK ngoài phạm vi tham chiếu `crm.customers`/`sales.orders`/`crm.customer_code_seq`, collation trong `crm` → `Down` từ chối trước mọi lệnh xóa; dọn object lạ xong `Down` + `Up` thành công. |
| `T09` | `CrmSalesFinanceDatabaseTests.RealTpAppLoginAllowsExpectedDmlOrderLinesDeleteAndSequenceNextval` | `[Fact]` (1) | Đăng nhập thật bằng `tp_app` (không `SET ROLE`): `SELECT/INSERT/UPDATE` hợp lệ, **`DELETE FROM sales.order_lines` THÀNH CÔNG**, `DELETE` trên các bảng được phép xóa (`sales.cart_items`, `sales.carts`, `crm.customer_addresses`, `finance.expenses`, ...) thành công, **`SELECT nextval('crm.customer_code_seq')` THÀNH CÔNG** (`1000`, `1001`, cộng 20 truy vấn song song không trùng), `public.f_unaccent` thành công. |
| `T10` | `CrmSalesFinanceDatabaseTests.RealTpAppLoginRejectsProtectedDeletesTruncatesDdlAndGrantOptions` | `[Theory]` (40) | Đăng nhập thật bằng `tp_app`: **`DELETE` trên 5 bảng bảo vệ (`sales.orders`, `sales.payments`, `sales.refunds`, `sales.order_status_history`, `crm.customers`) bắt buộc lỗi `42501`** (5 ca); **`TRUNCATE` trên toàn bộ 26 bảng bắt buộc lỗi `42501`** (26 ca); `CREATE TABLE` trong `crm`/`sales`/`finance`/`pricing`, `ALTER TABLE`, `DROP TABLE`, `setval('crm.customer_code_seq', 2000)`, `GRANT` sang `PUBLIC` và kiểm `WITH GRANT OPTION = false` (9 ca). |
| `T11` | `CrmSalesFinanceDatabaseTests.RealTpReadonlyLoginAllowsSelectOnlyAndRejectsEveryWrite` | `[Theory]` (12) | Đăng nhập thật bằng `tp_readonly`: đọc được cả 26 bảng và `crm.customer_code_seq.last_value`; mọi lệnh `INSERT`, `UPDATE`, `DELETE`, `TRUNCATE` trên `crm`, `sales`, `finance`, `pricing`, `SELECT nextval('crm.customer_code_seq')` và `CREATE TABLE` đều lỗi **`42501`**; `is_grantable = false`. |
| `T12` | `CrmSalesFinanceDatabaseTests.PreflightRejectsPrivilegeAndGrantOptionDriftInPhase2Schemas` | `[Theory]` (12) | Giả lập drift bằng owner (`GRANT SELECT ... WITH GRANT OPTION` cho `tp_readonly`, `GRANT USAGE ON SEQUENCE crm.customer_code_seq TO tp_app WITH GRANT OPTION`, `GRANT USAGE ON SCHEMA crm TO tp_app WITH GRANT OPTION`, `GRANT DELETE ON sales.orders/payments/refunds/order_status_history/crm.customers TO tp_app`, `GRANT TRUNCATE`, `GRANT INSERT TO tp_readonly`, column grant trên `crm.customers`, `GRANT SELECT ... TO PUBLIC`) → cả `GrantsSql.Preflight` lẫn bản preflight đóng băng của `P2-01` đều chặn ngay mà không sửa che giấu ACL. |
| `T13` | `CrmSalesFinanceDatabaseTests.CustomerContactNotSelfRefAndAccountPhoneChecksEnforceAllCases` | `[Theory]` (10) | Probe trực tiếp trên PG17: (1) khách có `phone_e164`, `zalo_name` NULL → **OK**; (2) khách chỉ có `zalo_name`, `phone_e164` NULL → **OK**; (3) khách `contact_status = 'erased'`, cả hai NULL → **OK**; (4) **khách đã gộp (`merged_into_customer_id IS NOT NULL`, `contact_status = 'active'`), `phone_e164` NULL, `zalo_name` NULL → THÀNH CÔNG**; (5–6) khách `active`/`do_not_contact` chưa gộp mà thiếu cả `phone_e164` lẫn `zalo_name` → **`23514` (`ck_customer_contact`)**; (7–8) `referred_by_customer_id = id` → **`23514` (`ck_customer_not_self_ref`)**, khác `id` → **OK**; (9–10) `customer_accounts` `status = 'active'` thiếu `login_phone` → **`23514` (`ck_account_login_phone`)**, `status = 'disabled'` với nhiều dòng `login_phone IS NULL` → **THÀNH CÔNG**. |
| `T14a` | `CrmSalesFinanceDatabaseTests.CrmPartialAndExpressionUniqueIndexesEnforceExactRules` | `[Fact]` (1) | Probe `ux_customers_phone` (2 khách chưa gộp trùng SĐT → **`23505`**, khách đã gộp `merged_into_customer_id IS NOT NULL` hoặc `phone_e164 IS NULL` → **OK**), `ux_customer_default_address` (2 địa chỉ `is_default = true` cùng khách → **`23505`**, nhiều `is_default = false` hoặc khách khác → **OK**), `ux_account_email` (`'Tea@Example.test'` vs `'tea@example.test'` → **`23505`**), `ux_customer_interest` (trùng tổ hợp `coalesce(ref_id, ...)` / `coalesce(price_band, '')` → **`23505`**). |
| `T14b` | `CrmSalesFinanceDatabaseTests.SalesPartialUniqueAndOrderCodeConstraintsEnforceExactRules` | `[Fact]` (1) | Probe **`ux_orders_code`** (trùng mã đơn → **`23505`**) + `ck_orders_code` (sai mẫu `^TP[0-9]{6}[A-Z0-9]{4}$` → **`23514`**), `ux_cart_account`, `ux_payments_bank_ref` (trùng khi chưa `voided` → **`23505`**, ghi lại sau khi bản trước `status = 'voided'` → **OK**), `ux_shipment_active_order` (2 vận đơn `order` đang hoạt động → **`23505`**, sau `cancelled`/`failed`/`returned` → **OK**), `ux_shipment_tracking`. |
| `T15` | `CrmSalesFinanceDatabaseTests.SalesPricingAndFinanceChecksRejectInvalidRowsWith23514` | `[Theory]` (20) | Probe đầy đủ 20 biểu thức CHECK: `ck_order_total`, `ck_order_discount`, `ck_order_delivered`, `ck_order_cancelled`, `ck_order_web_channel`, `ck_order_due`, `ck_line_total`, `ck_line_override`, `ck_line_returned`, `vouchers` (`ends_at <= starts_at`, `percent > 50`, `used_count > usage_limit`), `cod_settlements` (`net <> gross - fee`), `payments` (`confirmed` thiếu `confirmed_at`, `awaiting_settlement` khác `cod`, `voided` thiếu `void_reason`), `return_requests` (`approved` thiếu `resolution`), `shipments` (`replacement` thiếu `return_request_id`), `expenses` (`marketing` thiếu `channel`, `fixed` có ngày `<> 1`) → đều trả đúng **`23514`**. |
| `T16` | `CrmSalesFinanceDatabaseTests.GinCustomerSearchIndexSupportsUnaccentVietnameseLookup` | `[Fact]` (1) | Kiểm `ix_customers_search` có `USING gin (search_text gin_trgm_ops)`, ghi `search_text = public.f_unaccent(...)` cho tên/Zalo có dấu tiếng Việt (`Đặng Trần Hiên`) và tìm qua `public.f_unaccent('dang tran')` trả đúng dòng. |
| `T17` | `CrmSalesFinanceDatabaseTests.XminOptimisticConcurrencyThrowsOnStaleWriteForAllFourEntities` | `[Theory]` (4) | Lần lượt trên **4 entity (`Customer`, `Order`, `ReturnRequest`, `Voucher`)**: 2 `AppDbContext` đăng nhập bằng `tp_app` cùng tải 1 bản ghi, context 1 sửa & `SaveChangesAsync()` (xác nhận `Version` đổi), context 2 sửa & `SaveChangesAsync()` → ném **`DbUpdateConcurrencyException`**, giá trị DB không bị ghi đè; đồng thời xác nhận `information_schema.columns` không có cột thường `xmin`/`version` trong `crm`, `sales`, `pricing`, `finance`. |

**Chuỗi lệnh kiểm chứng bắt buộc khi chạy G3:**
```powershell
./scripts/verify-linux.ps1 -Filter "FullyQualifiedName~CrmSalesFinance"
./scripts/candidate-manifest.ps1 -Write -Base e63e90b99b596ba4e1d218d2a920733cbd79edbb -Out artifacts/p2-01/candidate.manifest
./scripts/verify-linux.ps1 -Manifest artifacts/p2-01/candidate.manifest
git diff --check
```

---

## 8. Rủi ro, `ASSUMPTION` và `GAP` đối chiếu `schema.sql` ↔ `03-database.md` ↔ `04-business-rules.md`

### 8.1 Trạng thái GAP chặn `P2-01`
- **Không còn GAP nào chặn `P2-01` (`0` blocking GAP).** Bốn điểm vướng trực tiếp của migration Phase 2 (`G03` marketing FKs tại `spec-gaps.md:32`, `G21` reservations FK tại `spec-gaps.md:56`, `ck_customer_contact` với khách đã gộp theo `BR-CRM-05` tại `schema.sql:585` vs `04-business-rules.md:224`, và `REVOKE DELETE` trên `sales.order_lines` theo `BR-ORD-08` tại `03-database.md:156` vs `04-business-rules.md:175`) đều đã được owner khóa cứng trong quyết định ngày `22/11/2026`.

### 8.2 Các điểm chênh lệch / ràng buộc liên tài liệu phát hiện khi rà soát (không chặn `P2-01`, bàn giao rõ cho task nghiệp vụ tương ứng)

| Mã | Nguồn đối chiếu (`file:dòng`) | Phân tích kỹ thuật | Cách xử lý tại `P2-01` và mốc sau |
|---|---|---|---|
| `R-P201-01` *(Rủi ro nghiệp vụ cho `P2-07`)* | `docs/spec/schema.sql:953` (`CHECK ((status = 'confirmed') = (confirmed_at IS NOT NULL))`) ↔ `docs/spec/04-business-rules.md:198` (`BR-PAY-06` hủy/void payment đã thu) | Khi một khoản thu `confirmed` (`confirmed_at IS NOT NULL`) bị owner chuyển sang `status = 'voided'`, biểu thức `(status = 'confirmed') = (confirmed_at IS NOT NULL)` bắt buộc `confirmed_at` phải đồng thời đặt về `NULL` (nếu giữ `confirmed_at` cũ sẽ lỗi `23514`). | `P2-01` giữ nguyên CHECK của `schema.sql:953` theo quyết định khóa cứng #3. Tại `P2-07`, handler `VoidPayment` hoặc xóa `confirmed_at = null` (lịch sử `confirmed_at` cũ vẫn lưu trong `platform.audit_logs`), hoặc owner chốt nới CHECK thành `(status <> 'confirmed' OR confirmed_at IS NOT NULL)` bằng migration nhỏ nếu muốn giữ cột `confirmed_at` trên dòng `voided`. |
| `R-P201-02` *(Liên kết `G12` cho `P2-06`/`P5-05`)* | `docs/spec/schema.sql:1046` (`order_id uuid NOT NULL UNIQUE` trên `pricing.voucher_redemptions`) ↔ `docs/spec/04-business-rules.md:105,175` (`BR-PRC-05`, `BR-ORD-08`) & `docs/analysis/spec-gaps.md:37` (`G12`) | `order_id` là `UNIQUE` tuyệt đối (không lọc `WHERE status = 'applied'`). Nếu đơn sửa giảm dưới `min_order` làm redemption chuyển `reverted`, sau đó đơn sửa tăng lại và áp lại voucher, việc `INSERT` dòng thứ 2 cùng `order_id` sẽ đụng `23505`. | `P2-01` giữ đúng `UNIQUE(order_id)` và cấp `UPDATE, DELETE` cho `tp_app` trên `pricing.voucher_redemptions`. Tại `P2-06`/`P5-05`, logic gỡ/áp lại mã trên cùng một đơn cập nhật tại chỗ dòng `voucher_redemptions` hiện có (hoặc xóa dòng `reverted`) trong cùng transaction với `used_count`. |
| `R-P201-03` *(Rủi ro thiết kế cho `P5-05`)* | `docs/spec/schema.sql:657` (`crm.interactions.staff_id uuid NOT NULL`) ↔ `docs/spec/04-business-rules.md:247` (`BR-MKT-07` job nền `QualifyReferrals` tạo hẹn chăm sóc) | Job nền `QualifyReferrals` chạy với actor `system`, không có nhân viên đăng nhập, trong khi `crm.interactions.staff_id` là `NOT NULL` (không có FK sang `iam.staff_users`). | Không ảnh hưởng `P2-01` và `P2-04` (mọi interaction ở P2 đều do nhân viên Admin tạo). Trước `P5-05`, chốt gán `staff_id` về owner active đầu tiên hoặc thêm migration additive cho phép `staff_id` nullable khi tạo bởi hệ thống. |
| `R-P201-04` *(Ranh giới CHECK vs App rule ở `P2-05`/`P2-06`/`P2-07`)* | `docs/spec/schema.sql:811,815,867` ↔ `docs/spec/04-business-rules.md:166` (`BR-ORD-04`) & `docs/spec/schema.sql:940` (`payments.method`) | (a) `ck_order_web_channel` chỉ chặn chiều `origin = 'web' => sales_channel = 'web'`; chiều ngược `origin = 'admin' => sales_channel <> 'web'` (`BR-ORD-04`) chưa chặn ở DB. (b) `sales.orders.payment_method` chỉ cho `('cod','bank_transfer')`, trong khi `sales.payments.method` cho thêm `'cash'` (khách mua trực tiếp `in_person` trả tiền mặt). (c) Regex SĐT DB `^\+84[0-9]{9}$` rộng hơn `BR-CRM-01` (số thứ 2 `∈ {3,5,7,8,9}`) để cho phép giá trị ẩn danh `'+84000000000'` của `BR-CRM-11`. | `P2-01` giữ đúng biểu thức `schema.sql` (không tự ý siết regex SĐT kẻo làm gãy `BR-CRM-11` ở `P2-12`). `P2-02`/`P2-05`/`P2-06` kiểm chặt tập đầu số di động VN và `sales_channel <> 'web'` ở tầng Domain/Application. |
| `R-P201-05` *(Bất biến tự tham chiếu khi gộp khách cho `P2-12`)* | `docs/spec/schema.sql:580,586` ↔ `docs/spec/04-business-rules.md:224` (`BR-CRM-05`) | `schema.sql:586` có `ck_customer_not_self_ref` cho `referred_by_customer_id <> id`, nhưng không khai báo CHECK tương tự cho `merged_into_customer_id <> id`. | Không tự thêm constraint ngoài `schema.sql` ở `P2-01`; `P2-12` bắt buộc kiểm `merged_into_customer_id != id` và chống chuỗi gộp `A → B → C` trong transaction gộp khách. |
| `R-P201-06` *(Quyền `UPDATE` trên bảng bảo vệ xóa & đồng bộ nhánh `P1-13`)* | `docs/spec/03-database.md:156`, Quyết định owner #2 & `D:\tra-hien-p1-13` | (a) 5 bảng bảo vệ (`orders`, `payments`, `refunds`, `order_status_history`, `customers`) thu hồi `DELETE, TRUNCATE` nhưng vẫn giữ `UPDATE` (cần cho `orders`, `payments`, `customers`; riêng `refunds` và `order_status_history` chỉ insert theo nghiệp vụ). (b) `P1-13` đang làm song song và sẽ merge trước `P2-01`. | (a) Giữ đúng danh sách `REVOKE` đã khóa cứng; tại `P2-05`/`P2-07` repository của `order_status_history` và `refunds` chỉ phơi bày phương thức thêm mới. (b) Sau khi `P1-13` merge vào `main`, dùng `base-integration` (`scripts/task-integrate.ps1`) + focused G4 trước khi publish `P2-01`. |
| `ASSUMPTION` | Không có | Mọi bảng, cột, kiểu, default, index, CHECK, FK và quyền trong kế hoạch này đều bám trực tiếp `docs/spec/schema.sql`, `03-database.md` và quyết định khóa cứng ngày `22/11/2026` của owner; **0 `ASSUMPTION` tự suy diễn**. | — |
