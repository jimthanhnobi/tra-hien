# P2-12: Consent (admin), merge, erase, export (BR-CRM-05, 09, 11, 13)

- **Tier:** `A` — thao tác trực tiếp quyền riêng tư dữ liệu cá nhân (gộp hồ sơ khách hàng, xóa/ẩn danh vĩnh viễn PII khách hàng & đơn hàng, xuất toàn bộ dữ liệu cá nhân, ghi nhận/thu hồi consent), quyền nhạy cảm chỉ dành cho `owner` (`customers.merge`, `customers.erase`, `customers.export`), khóa hàng chống deadlock trên PostgreSQL 17 dưới role `tp_app`, bảo toàn `BR-IAM-04`/`BR-IAM-06` trên `crm.customer_accounts`, tuân thủ `O3b = Phương án 3` (`BR-ORD-07.2`, `BR-RET-04`), và bảo toàn tuyệt đối doanh thu kế toán (`AC-P2-10`).
- **Branch / Spec baseline:** nhánh `feature/p2-12-privacy-merge-erase`, xây dựng và tích hợp nền trên `P2-01`..`P2-08`, `P2-13`, `P2-14` (đã merge vào `origin/main` tính tới mốc `135ff41079a0ba060162759299fa507cf65fc340`, hậu `P2-06` PR `#55` và `P2-14` PR `#56`).
- **Readiness & Owner decisions (`OWNER_APPROVED 2026-11-27`):** `GO` — toàn bộ gap và giả định đã được Owner phê duyệt tường minh:
  - Baseline G1 đầy đủ tại `cadce575597738bf40ad7c45689482de5a5fbc3d` ([spec-inventory.md](../analysis/spec-inventory.md), [spec-gaps.md](../analysis/spec-gaps.md), [traceability.md](../analysis/traceability.md)); G2 nền tại [conformance-review.md](../architecture/conformance-review.md).
  - **`O6b = B` (`GAP-P212-01`, `OWNER_APPROVED 2026-11-27`):** Giữ `P2-12` đúng 4 nhóm endpoint (`PUT consents`, `POST merge`, `POST erase`, `GET export`); không triển khai `DataRetentionJob` và không kích hoạt xóa dữ liệu thật trong `P2-12`. Sửa chính xác 3 dòng task trong [07-roadmap.md](../spec/07-roadmap.md) (`P3-01` dòng `123`, `P3-16` dòng `135`, `P5-01` dòng `187`) để ghi rõ quyền sở hữu `DataRetentionJob` (`BR-CRM-12`) và điều kiện không kích hoạt xóa dữ liệu thật trước khi chính sách lưu giữ nghiệp vụ, pháp lý và vận hành được phê duyệt.
  - **`ASSUMPTION-P212-01 = A` (`OWNER_APPROVED 2026-11-27`):** `rowVersion` (và `mergeRowVersion` trên `merge`) là tùy chọn; khi có gửi thì kiểm Base64 `xmin` (`400 validation_failed` nếu sai định dạng, `409 concurrency_conflict` nếu cũ); khi bỏ qua vẫn khóa hàng `FOR NO KEY UPDATE`, kiểm trạng thái `erased`/`merged` và nâng `Customer.UpdatedAt`/`xmin`.
  - **`ASSUMPTION-P212-02 = A` (`OWNER_APPROVED 2026-11-27`):** `GET admin/customers/{id}/export` trả `application/json` với `Cache-Control: no-store`, loại trừ tuyệt đối bí mật/giá vốn/ghi chú nội bộ (`passwordHash`, `securityStamp`, `providerKey`, `unitCostVnd`, `avgCostVnd`, `grossProfitVnd`, `priceOverrideReason`, `internalNote`), ghi đồng bộ `platform.audit_logs(action = 'access')` trong cùng transaction và không tự sinh dòng `crm.data_requests`.
  - **`ASSUMPTION-P212-03 = A` + Multi-Level Merged Stubs (`OWNER_APPROVED 2026-11-27`):** `POST admin/customers/{A}/erase` ẩn danh nguyên tử cả hồ sơ đích `A` lẫn toàn bộ chuỗi stub đã gộp vào `A` ở mọi độ sâu (`D -> C -> B -> A`, giải quyết đệ quy lặp dưới `pg_advisory_xact_lock` + khóa `FOR NO KEY UPDATE` theo UUID tăng dần), không để sót PII trên bất kỳ stub nào.
  - **Bảo toàn các quyết định nền đã duyệt:** `G17a`, `G17b+G28`, `R-P201-05`, `R-P204-01` (`crm.interactions` đặt `content = '[Đã xóa]'`, `follow_up_result = NULL`, chỉ đóng `follow_up_done_at` khi `follow_up_at IS NOT NULL AND follow_up_done_at IS NULL`), `O3b = Phương án 3` (`BR-ORD-07.2` & `BR-RET-04`), và ranh giới IL `CrmModuleBoundaryRule`.
- **Traceability:** xem Mục 1.2 và ánh xạ code/test ở Mục 4–5.
- **Migration/recovery:** `none` tại `P2-12` — không sửa hay thêm migration EF Core; toàn bộ cấu trúc bảng, CHECK, partial/expression unique index và quyền `tp_app`/`tp_readonly` đã hoàn tất ở `P2-01` (`202611230421_P2_01_CrmSalesFinanceFoundation`) và `P2-07` (`202611260800_P2_07_RefundClassificationAndShipping`). `P2-12` chỉ gắn `[Audited]` runtime cho `Consent`, `DataRequest`, `CustomerInterest`, siết khóa đọc chia sẻ `FOR SHARE` trên `EfCustomerOrderResolver.ResolveExistingAsync` (cùng thứ tự khóa `crm.customers -> sales.orders` trên `ReorderAdminOrderHandler`) và triển khai use cases/ports/endpoints. Rollback bằng cách revert commit code + OpenAPI/client của nhánh.
- **Lưu ý hồ sơ bất biến:** trạng thái kiểm chứng động, manifest SHA-256, verdict G4, PR URL, kết quả CI và trạng thái merge được theo dõi ở báo cáo bàn giao và PR body, không ghi tự tham chiếu vào file được theo dõi này.

---

## 1. Định danh liên kết, quyết định khóa cứng và ranh giới phạm vi

### 1.1 Quyết định Owner đã phê duyệt 22/11/2026 (KHÓA CỨNG, nguyên văn)

1. `G17a` (đã có trong schema P2-01): `ck_customer_contact` cho phép `merged_into_customer_id IS NOT NULL` với `phone_e164 = NULL` và `zalo_name = NULL`.
2. `G17b` & `G28` & `R-P201-05` (`merge B -> A`, quyền `customers.merge` — chỉ `owner`):
   - Chặn tự gộp (`merged_into_customer_id == id` — `R-P201-05`) và chặn gộp nếu A hoặc B đã `erased` hoặc A đã `merged`; nếu có khách C trước đó đã trỏ `merged_into_customer_id = B` thì làm phẳng chuỗi sang `merged_into_customer_id = A`.
   - Nếu CẢ A và B đều có `crm.customer_accounts` → từ chối `409` (`BR-CRM-05`). Nếu chỉ B có `customer_accounts` → chuyển `account.customer_id = A` kèm bảo toàn `BR-IAM-04`: nếu SĐT lệch hoặc A có lịch sử đơn/địa chỉ cũ chưa xác minh thì reset `history_unlocked_at = NULL` + xoay `security_stamp`, và kẹp mốc `created_at` địa chỉ chuyển sang để không lộ trước khi unlock.
   - Xử lý va chạm deterministic không nổ `23505`: `customer_tags` (union theo `(customer_id, tag_id)`), `customer_addresses` (nếu cả A và B đều có `is_default = true`, giữ default của A và hạ default của B trước khi chuyển `customer_id`), `customer_interests` (gộp/cộng `score` khi trùng tổ hợp `ux_customer_interest`), `consents` (mỗi cặp `(channel, purpose)` giữ bản có `captured_at` mới nhất theo `BR-CRM-05`).
   - Chuyển `sales.orders`, `crm.interactions`, `crm.data_requests`, `pricing.vouchers (bound_customer_id)`, `pricing.voucher_redemptions (customer_id)` từ B sang A (bảng `marketing.*` chưa tồn tại ở P2 nên hoãn phần `campaign_recipients`/`referral_codes` sang P5 — ghi rõ port/follow-up cho P5).
   - Đặt B: `merged_into_customer_id = A`, `phone_e164 = NULL`, cập nhật lại `search_text` của A và B, tính lại chỉ số `delivered_orders_count` / `lifetime_value_vnd` / `last_delivered_at` / `last_contacted_at` của A từ dữ liệu thật trong DB, ghi audit.
3. `G28` & `BR-CRM-11` & `AC-P2-10` (`erase`, quyền `customers.erase` — chỉ `owner`):
   - Chặn erase trên hồ sơ đã `merged` (phải thao tác trên hồ sơ đích hoặc theo quy tắc rõ ràng) và chặn nếu còn đơn `pending`/`confirmed`/`shipping`, hoặc `return_requests` đang mở (`requested`/`approved`), hoặc `payments` có `status = 'awaiting_settlement'` (COD chưa đối soát).
   - Thực hiện trong 1 transaction: `contact_status = 'erased'`, `erased_at = now`, `full_name = '[Đã xóa]'`, `phone_e164 = NULL`, `email = NULL`, `zalo_name = NULL`, `note_summary = NULL`, `search_text = ''`; xóa `customer_addresses`, `customer_tags`, `customer_interests`, `consents`; trên `interactions` đặt `content = '[Đã xóa]'` VÀ `follow_up_result = NULL` (kèm đóng hẹn mở nếu có — kiểm CHECK `follow_up_done_at IS NULL OR follow_up_at IS NOT NULL`); trên `data_requests` cũ đặt `note = NULL` rồi ghi dòng `data_requests` mới (`request_type = 'erase'`, `status = 'completed'`); trên `customer_accounts` đặt `status = 'disabled'`, `login_phone = NULL`, `email = NULL`, `password_hash = NULL`, xoay `security_stamp`, xóa `customer_logins`, xóa `sales.carts` của account; trên `sales.orders` đặt `contact_name = '[Đã xóa]'`, `receiver_name = '[Đã xóa]'`, `contact_phone = '+84000000000'`, `receiver_phone = '+84000000000'`, `contact_email = NULL`, `address_line = '[Đã xóa]'`, `customer_note = NULL` — giữ nguyên `total_vnd`, `subtotal_vnd`, `discount_vnd`, `shipping_fee_vnd`, `status` để báo cáo doanh thu không đổi (`AC-P2-10`).
4. Consent Admin (`PUT /api/v1/admin/customers/{id}/consents`, `customers.write` + CSRF, `BR-CRM-09`) và Export (`GET /api/v1/admin/customers/{id}/export`, `customers.export` — chỉ `owner`, `BR-CRM-13`, ghi `platform.audit_logs` với `action = 'access'`). Lưu ý pháp lý: ghi chú các điểm Nghị định 13/2023/NĐ-CP cần chủ shop xác nhận pháp lý chuyên môn trước go-live, không tự kết luận pháp lý.

### 1.2 Nguồn đặc tả và mã định danh (`ID`, `FR`, `BR`, `NFR`, `AC`, `DEC`)

- **Task ID:** `P2-12` ([07-roadmap.md](../spec/07-roadmap.md) dòng `97`, phụ thuộc `P2-02` dòng `87`).
- **Tài liệu đặc tả đối chiếu:**
  - [00-README.md](../spec/00-README.md) dòng `40-46` (quy ước VND `bigint`, UTC `timestamptz`, `Guid.CreateVersion7()`), dòng `77-78` (`Q6`, `Q7`), dòng `85-115` (`DEC-*`).
  - [01-requirements.md](../spec/01-requirements.md) dòng `39-41` (vai trò `owner`/`sales`/`content`), dòng `145` (`FR-CRM-08` consent), dòng `147-149` (`FR-CRM-10..12` merge/erase/export), dòng `190-191` (`FR-PLT-01..02`), dòng `201`, `204`, `206`, `209` (`NFR-04`, `NFR-07`, `NFR-09`, `NFR-12`). *(Lưu ý: `FR-CRM-09` tại dòng `146` là `correct-identity`, đã hoàn thành ở `P2-02`).*
  - [02-architecture.md](../spec/02-architecture.md) dòng `68-77` (§3 ranh giới module qua `Application/<B>/Contracts/`), dòng `105-109` (§5.2 `IUnitOfWork` & `xmin`), dòng `122-129` (§5.4 `[Audited]`, `AuditValuePolicy`, cấm `ExecuteUpdate/ExecuteDelete` trên bảng có audit), dòng `136-148` (§5.5 bảng `PeriodicJob` gồm `DataRetention`), dòng `189-236` (§6–§7 xác thực, CSRF, ma trận quyền `customers.write`, `customers.merge`, `customers.erase`, `customers.export`, lọc giá vốn).
  - [03-database.md](../spec/03-database.md) dòng `15-18` (`xmin`, không xóa cứng nghiệp vụ, FK chéo schema), dòng `76-80` (§3.5 khách hàng & `erased`), dòng `104-107` (§3.9 `platform.audit_logs`), dòng `136-160` (§4 quyền `tp_app` thu hồi `DELETE, TRUNCATE` trên `crm.customers` và `sales.orders`), dòng `193` & `225-237` (§5 & §7 `retention.*` và vòng đời dữ liệu).
  - [schema.sql](../spec/schema.sql) dòng `62-77` (`platform.audit_logs`, `action IN ('create','update','delete','state_change','access')`), dòng `531-714` (`pricing.vouchers`, `crm.*`), dòng `719-784` (`marketing.*` hoãn P5), dòng `789-1054` (`sales.*`, `pricing.voucher_redemptions`).
  - [04-business-rules.md](../spec/04-business-rules.md) dòng `16-20` (`BR-IAM-04`, `BR-IAM-06`), dòng `169-176` (`BR-ORD-07`, `BR-ORD-09`), dòng `216` (`BR-RET-04`), dòng `224` (`BR-CRM-05`), dòng `226` (`BR-CRM-07`), dòng `228-237` (`BR-CRM-09..14`), dòng `268-275` (`BR-FIN-01..02`).
  - [05-api.md](../spec/05-api.md) dòng `5-29` (§1 quy ước chung, `rowVersion`, CSRF, `ProblemDetails`), dòng `212` (`PUT consents`), dòng `215-217` (`POST merge`, `POST erase`, `GET export`).
  - [06-infrastructure.md](../spec/06-infrastructure.md) dòng `205-210` (backup `age` & chu kỳ lưu), dòng `216-230` (CI scope full khi đổi `openapi.json`/`schema.ts`), dòng `266-269` (checklist pháp lý trước go-live).
- **Định danh áp dụng trực tiếp cho `P2-12`:**
  - `DEC`: `DEC-01`, `DEC-07`, `DEC-13`, `DEC-14`, `DEC-15`, `DEC-16`, `DEC-17`, `DEC-18`, `DEC-19`, `DEC-20`, `DEC-30`.
  - `FR`: `FR-CRM-08` (consent Admin), `FR-CRM-10` (gộp khách), `FR-CRM-11` (ẩn danh xóa dữ liệu khách), `FR-CRM-12` (xuất dữ liệu khách), `FR-PLT-01` (audit thay đổi nhạy cảm & truy cập dữ liệu khách).
  - `BR`: **`BR-CRM-05`** (merge), **`BR-CRM-09`** (phần ghi/rút consent Admin `source = 'staff'`), **`BR-CRM-11`** (erase & ẩn danh), **`BR-CRM-13`** (export + audit `access`), phối hợp **`BR-CRM-07`** (tính lại `last_contacted_at`), **`BR-CRM-14`** (tính lại `search_text`), **`BR-IAM-04`** & **`BR-IAM-06`** (bảo toàn mở khóa lịch sử và xoay `security_stamp` tài khoản khách), **`BR-ORD-07`** / **`BR-ORD-09`** / **`BR-RET-04`** (tính lại chỉ số đơn giao thực tế), **`BR-FIN-01..02`** (bảo toàn doanh thu kế toán). *(Về `BR-CRM-10` và `BR-CRM-12` xem Mục 1.3 bên dưới).*
  - `NFR`: `NFR-04` (không rò PII ra log/ProblemDetails/cache), `NFR-07` (HTTPS, cookie `AdminCookie`, CSRF, phân quyền), `NFR-08` (không tự migrate), `NFR-09` (test mọi `BR-*` trên PG17 thật), **`NFR-12`** (consent, quyền xem/xóa dữ liệu cá nhân, audit truy cập dữ liệu nhạy cảm).
  - `AC`: trực tiếp nghiệm thu **`AC-P2-10`** ([07-roadmap.md](../spec/07-roadmap.md) dòng `115`: xóa dữ liệu khách có đơn đã giao → hồ sơ và đơn bị ẩn danh, báo cáo doanh thu tổng không đổi); giữ xanh `AC-P2-09` (`content` bị `403`), `AC-P0-04` (audit che PII) và `AC-P0-05` (kiến trúc tầng/module & cấm gọi giờ trực tiếp).

### 1.3 Trong phạm vi / Ngoài phạm vi & Rà soát toàn bộ `07-roadmap.md` cho `BR-CRM-12` (`DataRetention`)

- **Trong phạm vi (`P2-12`):**
  1. `PUT /api/v1/admin/customers/{id}/consents` (`customers.write` + CSRF): ghi mới hoặc cập nhật trạng thái `granted`/`revoked` theo cặp `(channel, purpose)` với `source = 'staff'`, `evidence`, `captured_at`, `captured_by` (`BR-CRM-09`).
  2. `POST /api/v1/admin/customers/{id}/merge` (`customers.merge` — chỉ `owner` + CSRF): gộp `B -> A` nguyên tử chống deadlock, xử lý va chạm deterministic không nổ `23505`, làm phẳng chuỗi `C -> B -> A`, gỡ tự tham chiếu `referred_by_customer_id`, bảo toàn `BR-IAM-04` khi chuyển/giữ `crm.customer_accounts`, chuyển `sales.orders` / `crm.interactions` / `crm.data_requests` / `pricing.vouchers` / `pricing.voucher_redemptions` qua port liên module, tính lại `search_text` và các chỉ số khách từ DB thật (`BR-CRM-05`, `G17a`, `G17b`, `G28`, `R-P201-05`).
  3. `POST /api/v1/admin/customers/{id}/erase` (`customers.erase` — chỉ `owner` + CSRF): kiểm 3 nhóm điều kiện chặn nghiệp vụ/tài chính (`orders`, `return_requests`, `payments`), ẩn danh `crm.customers` (gồm cả các hồ sơ stub từng gộp vào `id` để không sót PII), xóa `addresses`/`tags`/`interests`/`consents`/`customer_logins`/`sales.carts`, scrub `interactions` (đóng hẹn mở an toàn với `ck_interactions_row`), scrub `data_requests.note` cũ + ghi dòng `erase` `completed`, vô hiệu hóa `customer_accounts` + xoay `security_stamp`, ẩn danh liên hệ trên `sales.orders` mà giữ nguyên mọi trường doanh thu (`BR-CRM-11`, `AC-P2-10`, `G28`).
  4. `GET /api/v1/admin/customers/{id}/export` (`customers.export` — chỉ `owner`, `Cache-Control: no-store`): xuất toàn bộ dữ liệu cá nhân của khách dưới dạng JSON (loại trừ bí mật xác thực, giá vốn và `internal_note` nội bộ), ghi đồng bộ `platform.audit_logs` với `action = 'access'` trong cùng transaction (`BR-CRM-13`, `NFR-12`).
  5. Gắn `[Audited]` và cấu hình `AuditValuePolicy` cho `crm.consents`, `crm.data_requests`, `crm.customer_interests`; ghi `CustomerPrivacyAudit` chuyên biệt; tái sinh `openapi.json` và `schema.ts`.
- **Kiểm tra toàn bộ `07-roadmap.md` (81 task `P0-01`..`P6-08`) đối với `BR-CRM-10` và `BR-CRM-12` (`DataRetention`):**
  - **`BR-CRM-10`** (`04-business-rules.md:233` — loại `do_not_contact`, `erased`, hồ sơ đã gộp khỏi danh sách chiến dịch): đã được thực thi gián tiếp bởi `BR-MKT-01` tại **`P5-02`** (`07-roadmap.md:188`, `AC-P5-01` dòng `196`). Ở `P2-12`, dữ liệu nền cho `BR-CRM-10` được đảm bảo đúng (`contact_status = 'erased'`, `merged_into_customer_id IS NOT NULL`, xóa sạch `consents`), còn bộ lọc chọn người nhận chiến dịch thuộc **`P5-02`**.
  - **`BR-CRM-12` (`Job DataRetention xóa theo retention.*`, `04-business-rules.md:235`, `02-architecture.md:146`, `03-database.md:193,226-233`):**
    - Đã kiểm tra từng dòng từ `07-roadmap.md:17` đến `07-roadmap.md:214`: **không có bất kỳ dòng task nào trong `07-roadmap.md` ghi tên `BR-CRM-12` hay `DataRetention`** (`P2-12` ở dòng `97` chỉ ghi `BR-CRM-05, 09, 11, 13`; `FR-PLT-02` ở `01-requirements.md:191` nhắc chung các job nền; `docs/analysis/traceability.md:142` gán tạm `BR-CRM-12 -> P2-12`).
    - Đối chiếu 6 khóa `retention.*` với hiện trạng code/DB tại base `97735e7`:
      1. `retention.idempotency_hours` (`48`): bảng `platform.idempotency_keys` đã có job `PurgeIdempotencyKeys` (`P0-07`, đang hardcode `48h`).
      2. `retention.abandoned_upload_hours` (`24`): bảng `media.upload_sessions` + R2 đã có `UploadCleanup` / `ExpireUploadSessions` (`P1-02`, đang hardcode `24h`; xóa R2 ngoài DB nên không thể chạy bên trong `PeriodicJob` DB-only).
      3. `retention.carts_days` (`30`): bảng `sales.carts` đã tạo ở `P2-01` (nghiệp vụ giỏ hàng ở `P3-01`), chưa có job xóa.
      4. `retention.notification_logs_days` (`180`): bảng `platform.notification_logs` đã tạo ở `P0-04` (nghiệp vụ gửi thông báo ở `P2-13`), chưa có job xóa.
      5. `retention.web_vitals_days` (`30`): bảng `platform.web_vitals_samples` **chưa tồn tại** (hoãn tới `P3-16`).
      6. `retention.link_clicks_days` (`400`): bảng `marketing.link_clicks` **chưa tồn tại** (hoãn tới `P5-01`).
    - Để tuyệt đối không bỏ sót orphan rule mà cũng không tự ý phình scope khi chưa có lệnh Owner, điểm này được lập thành **`GAP-P212-01`** tại Mục 6.1 với 2 phương án rõ ràng (Phương án A: triển khai `DataRetentionJob` theo pha ngay trong `P2-12` cho các bảng đã tồn tại + bàn giao phần `web_vitals_samples`/`link_clicks` cho `P3-16`/`P5-01`; Phương án B: giữ `P2-12` đúng 4 API `07-roadmap.md:97` và gắn chính thức `BR-CRM-12` vào `P3-01` + `P3-16` + `P5-01`).
- **Ngoài phạm vi (thuộc các task khác trong `07-roadmap.md`):**
  - Import khách cũ CSV/XLSX (`BR-CRM-03`) → `P2-03`; CRUD interactions & follow-ups (`BR-CRM-06/07`) → `P2-04`; thêm/xóa `customer_interests` thủ công & job đêm `RecomputeCustomerStats` (`BR-CRM-08`) → `P2-11`.
  - Bảng `marketing.*` (`campaign_recipients`, `referral_codes`, `referrals`, `tracking_links`, `link_clicks`) và consent gate chiến dịch → `P5-01`, `P5-02`, `P5-05`.
  - Ô tích consent tại checkout web và màn quản lý consent của khách đăng nhập (`PUT /api/v1/account/consents`) → `P3-02`, `P5-06`.
  - Bảng `catalog.product_reviews` / ẩn danh tên người đánh giá khi erase (`G20`) → `P5-07`.
  - Nhân viên bấm xác minh SĐT mở khóa lịch sử (`POST /api/v1/admin/customers/{id}/verify-account-phone`, `BR-IAM-04 b`) → `P3-05`.
  - Giao diện Admin UI khách hàng (`apps/admin`) → `P2-14`.

---

## 2. Hợp đồng chi tiết 4 nhóm endpoint, phân quyền, CSRF và Concurrency

Mọi endpoint đặt dưới `/api/v1/admin/customers/{id:guid}/...`, bắt buộc HTTPS (`400 https_required`), đúng `AdminHost` nếu cấu hình (`403 forbidden`), cookie `AdminCookie` thỏa `AdminSecurity.Ready` (`401 unauthenticated` / `403 forbidden` / `403 totp_required`), header response `Cache-Control: no-store`, và đọc JSON nghiêm ngặt (`AllowDuplicateProperties = false`, `UnmappedMemberHandling = Disallow`, `PropertyNameCaseInsensitive = false`, `NumberHandling = Strict`, giới hạn body `128 KiB`).

### 2.1 Ma trận hợp đồng 4 endpoint (`05-api.md` §4.5 dòng `212`, `215–217`)

| # | Method & Path (`/api/v1/...`) | Dòng `05-api.md` | Quyền (`owner` / `sales` / `content`) | CSRF (`X-CSRF-TOKEN`) | Request Body | Response thành công | Mã lỗi (`ProblemDetails`) |
|---:|---|---|---|---|---|---|---|
| 1 | `PUT admin/customers/{id}/consents` | `212` | `customers.write` (`owner`: ✓, `sales`: ✓, `content`: `403`) | **Bắt buộc** (`400 csrf_invalid`) | `{ channel, purpose, status, evidence, rowVersion? }` | `200` `CustomerConsentsResult` (`{ customerId, rowVersion, upserted, consents[] }`) | `400 validation_failed`, `401`, `403`, `404 not_found`, `409 concurrency_conflict` / `customer_erased` / `customer_merged`, `415 unsupported_media_type` |
| 2 | `POST admin/customers/{id}/merge` | `215` | `customers.merge` (`owner`: ✓, `sales`: `403`, `content`: `403`) | **Bắt buộc** (`400 csrf_invalid`) | `{ mergeCustomerId, rowVersion?, mergeRowVersion? }` (`id` = A giữ lại, `mergeCustomerId` = B gộp vào A) | `200` `CustomerDetailView` (hồ sơ 360° mới của A kèm `rowVersion` mới và stats đã tính lại) | `400 validation_failed` (`self_merge_not_allowed`), `401`, `403`, `404 not_found`, `409 concurrency_conflict` / `customer_erased` / `customer_merged` / `account_exists`, `415 unsupported_media_type` |
| 3 | `POST admin/customers/{id}/erase` | `216` | `customers.erase` (`owner`: ✓, `sales`: `403`, `content`: `403`) | **Bắt buộc** (`400 csrf_invalid`) | `{ reason, requestChannel, rowVersion? }` | `200` `CustomerEraseResult` (`{ customerId, code, contactStatus: "erased", erasedAt, rowVersion, dataRequestId, anonymizedOrdersCount, scrubbedMergedStubsCount }`) | `400 validation_failed`, `401`, `403`, `404 not_found`, `409 concurrency_conflict` / `customer_merged` (`data: { mergedIntoCustomerId }`) / `customer_erased` / `customer_erase_blocked` (`data: { blockingReasons[] }`), `415 unsupported_media_type` |
| 4 | `GET admin/customers/{id}/export` | `217` | `customers.export` (`owner`: ✓, `sales`: `403`, `content`: `403`) | Không (`GET` an toàn, chỉ cookie `AdminCookie`) | Không có body / query | `200` `CustomerDataExportView` + `Cache-Control: no-store` | `400 validation_failed` (`id == Guid.Empty`), `401`, `403`, `404 not_found` |

### 2.2 Quy tắc Validation đầu vào chi tiết

1. **`PutCustomerConsentRequest`:**
   - `channel` ∈ `{"zalo_personal", "zalo_oa", "sms", "email", "phone"}` (khớp `ck_consents_channel`).
   - `purpose` ∈ `{"marketing_new_products", "marketing_promotions", "customer_care"}` (khớp `ck_consents_purpose`).
   - `status` ∈ `{"granted", "revoked"}` (khớp `ck_consents_status`); `source` luôn do server gán cứng `"staff"` (khớp `ck_consents_source` và `BR-CRM-09`, tuyệt đối không nhận từ client).
   - `evidence`: bắt buộc, không chứa `\0`, sau `Trim()` dài `3..1000` ký tự (khớp `ck_consents_evidence`: `length(trim(evidence)) >= 3`).
   - `rowVersion`: tùy chọn (`ASSUMPTION-P212-01`), nếu có phải giải mã được qua `CatalogVersions.TryDecode` (sai định dạng → `400 validation_failed`).
2. **`MergeCustomersRequest`:**
   - `mergeCustomerId`: `Guid` hợp lệ `!= Guid.Empty` và **`!= id`** (`R-P201-05`; trùng `id` → `400 validation_failed` trên trường `mergeCustomerId` với mã `"self_merge_not_allowed"` ngay trước khi chạm DB, đồng thời kiểm lặp lại ở handler/store).
   - `rowVersion` (của A) và `mergeRowVersion` (của B): tùy chọn; nếu gửi phải hợp lệ Base64 `xmin`.
3. **`EraseCustomerRequest`:**
   - `reason`: bắt buộc, không chứa `\0`, sau `Trim()` dài `3..500` ký tự.
   - `requestChannel`: bắt buộc, không chứa `\0`, thuộc allowlist `CustomerRules.DataRequestChannels = {"zalo_personal", "zalo_oa", "phone", "facebook", "in_person", "email", "web", "account", "other"}` để dữ liệu ghi vào `crm.data_requests.channel` luôn chuẩn hóa và an toàn với `AuditValuePolicy`.
   - `rowVersion`: tùy chọn; nếu gửi phải hợp lệ Base64 `xmin`.

### 2.3 Chiến lược Concurrency, `xmin` và Thứ tự khóa chống Deadlock (`40P01`)

1. **Thứ tự phân cấp khóa toàn cục (Lock Hierarchy DAG):**
   Mọi giao dịch ghi của `P2-12` chạy bên trong `EfUnitOfWork.ExecuteAsync` (`READ COMMITTED`) và tuân thủ nghiêm ngặt thứ tự khóa một chiều:
   `pg_advisory_xact_lock (cho merge/erase đa dòng) → crm.customers (sắp theo UUID tăng dần) → crm.customer_accounts → sales.orders (sắp theo id ASC) → pricing.vouchers (sắp theo id ASC) → các bảng con nội bộ`.
2. **Chống Deadlock tuyệt đối khi đua đồng thời `Merge(A, B)` ↔ `Merge(B, A)` hoặc chuỗi `Merge(A, B)` ↔ `Merge(A, C)`:**
   - Bước 1: Gọi `SELECT pg_advisory_xact_lock(@crmPrivacyKey)` (hằng `long` cố định tính từ `SHA256("TeaPremium.CrmPrivacy:MergeErase")` — `tp_app` đã được cấp `GRANT EXECUTE ON FUNCTION pg_catalog.pg_advisory_xact_lock(bigint)` tại `GrantsSql.cs:69`). Khóa giao dịch nhẹ này tuần tự hóa mọi lệnh `merge`/`erase` trước khi giữ bất kỳ row lock nào (loại bỏ hoàn toàn khả năng khóa chéo với tập khách `C` làm phẳng chuỗi `merged_into_customer_id = B` hoặc tập khách `D` có `referred_by_customer_id = B`).
   - Bước 2: So sánh định danh `string.CompareOrdinal(idA.ToString("D"), idB.ToString("D"))` để xác định `(firstId, secondId)` theo đúng thứ tự byte chuẩn của `uuid`, rồi thực hiện tuần tự 2 câu lệnh `SELECT *, xmin FROM crm.customers WHERE id = {firstId} FOR NO KEY UPDATE` → `SELECT *, xmin FROM crm.customers WHERE id = {secondId} FOR NO KEY UPDATE`. Vì `Merge`/`Erase` không bao giờ `DELETE` `crm.customers` hay sửa khóa chính `id`, mức khóa `FOR NO KEY UPDATE` vẫn chặn chặt `FOR SHARE` (`EfCustomerOrderResolver`), `FOR NO KEY UPDATE` (`EfCustomerStore`/`EfInteractionStore`/`PutCustomerConsent`) và `FOR UPDATE`, đồng thời **tương thích với khóa FK `FOR KEY SHARE`** trên `fk_customers_referred_by_customer_id` khi `PATCH customer` đồng thời cập nhật `referredByCustomerId`, loại bỏ hoàn toàn chu trình deadlock `40P01`.
   - Bước 3: Khóa tiếp mọi dòng `crm.customers` phụ liên quan (`WHERE id = ANY({auxIds}) ORDER BY id ASC FOR NO KEY UPDATE`) và **tái xác minh `refRow.ReferredByCustomerId == sourceCustomerId` sau khi đã giữ khóa hàng** để tránh ghi đè cập nhật đồng thời từ `PATCH` đổi `referredByCustomerId` sang giá trị khác.
   - Kết quả khi 2 request `Merge(A -> B)` và `Merge(B -> A)` bắn đồng thời (`Task.WhenAll`): request vào trước thắng `200 OK` và đặt `merged_into_customer_id` cho hồ sơ bị gộp; request thứ hai mở khóa thấy một trong hai hồ sơ đã có `merged_into_customer_id != null` (hoặc lệch `xmin` nếu gửi `rowVersion`), lập tức rollback sạch và trả **`409 customer_merged`** (hoặc `409 concurrency_conflict`), **0 deadlock `40P01`, 0 lỗi `500`**.
3. **Kiểm tra `xmin` (`rowVersion`):**
   - Với mọi thao tác ghi có gửi `rowVersion` (hoặc `mergeRowVersion`), kiểm `entity.Version == expectedVersion` ngay sau khi `FOR NO KEY UPDATE` và gán `entry.Property(x => x.Version).OriginalValue = expectedVersion` trước `SaveChangesAsync`; bắt `DbUpdateConcurrencyException` qua savepoint → trả `409 concurrency_conflict`.
4. **Siết tuần tự hóa tuyệt đối với `POST /api/v1/admin/orders` và `POST /api/v1/admin/orders/{id}/reorder` (`P2-06`):**
   - `EfCustomerOrderResolver.ResolveExistingAsync` dùng `SELECT *, xmin FROM crm.customers WHERE id = {customerId} FOR SHARE` (`.AsNoTracking()`) khi đang chạy trong transaction, nhờ đó các lệnh `POST admin/orders` song song trên cùng khách hàng không chặn lẫn nhau nhưng serialize tuyệt đối với khóa `FOR NO KEY UPDATE` của `Erase`/`Merge` (loại bỏ hoàn toàn race tạo đơn active song song khi đang xóa hoặc gộp khách).
   - `ReorderAdminOrderHandler` lấy trước `sourceCustomerId` (`IOrderRepository.GetCustomerIdAsync`) và khóa `crm.customers FOR SHARE` qua `ResolveExistingAsync` **trước** khi gọi `orders.LoadForUpdateAsync(command.OrderId)` (`sales.orders FOR UPDATE`), giữ 100% Lock Hierarchy DAG một chiều `crm.customers -> sales.orders` với `Erase`/`Merge` (0 chu trình AB-BA, 0 deadlock `40P01`).

---

## 3. Thiết kế chi tiết Transaction từng bảng, Biên Module và Audit

### 3.1 Biên Module sạch theo `02-architecture.md` §3

- `TeaPremium.Application.Crm` **tuyệt đối không** truy cập trực tiếp bảng hay entity của `sales.*` hoặc `pricing.*`.
- **`Orders` / `Sales` công bố port `IOrderCustomerPrivacyPort`** tại `TeaPremium.Application/Orders/Contracts/IOrderCustomerPrivacyPort.cs` (hiện thực bởi `EfOrderCustomerPrivacyPort` tại `TeaPremium.Infrastructure/Orders/EfOrderCustomerPrivacyPort.cs`):
  - `Task<bool> HasOrdersAsync(Guid customerId, CancellationToken ct)` — phục vụ kiểm tra lịch sử đơn cũ của `A` cho `BR-IAM-04`.
  - `Task<int> ReassignCustomerOrdersAsync(Guid sourceCustomerId, Guid targetCustomerId, DateTime nowUtc, CancellationToken ct)` — khóa `FOR UPDATE` theo `id ASC` và chuyển `sales.orders.customer_id` từ `B -> A` bằng tracked entities.
  - `Task<CustomerDeliveredOrderStats> ComputeDeliveredOrderStatsAsync(Guid customerId, CancellationToken ct)` — tính lại `FirstDeliveredAt`, `LastDeliveredAt`, `DeliveredOrdersCount`, `LifetimeValueVnd` từ dữ liệu thật trong `sales.orders`, `sales.order_lines`, `sales.refunds`.
  - `Task<IReadOnlyList<string>> CheckEraseBlockersAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken ct)` — khóa `sales.orders` của khách gốc và toàn bộ chuỗi stub gộp đa tầng (`WHERE customer_id = ANY(...) ORDER BY id ASC FOR UPDATE`), kiểm tra đồng thời 3 điều kiện chặn erase trên `sales.orders`, `sales.return_requests`, `sales.payments`.
  - `Task DeleteAccountCartsAsync(IReadOnlyCollection<Guid> customerAccountIds, CancellationToken ct)` — xóa `sales.carts` (cascade sang `sales.cart_items`) của các tài khoản bị vô hiệu hóa.
  - `Task<int> AnonymizeCustomerOrdersAsync(IReadOnlyCollection<Guid> customerIds, string anonymizedText, string anonymizedPhone, DateTime nowUtc, CancellationToken ct)` — ẩn danh 7 cột PII trên `sales.orders` thuộc khách gốc và mọi stub gộp đa tầng, giữ nguyên mọi cột tiền và trạng thái (`AC-P2-10`).
  - `Task<IReadOnlyList<CustomerExportOrderView>> ListOrdersForExportAsync(Guid customerId, CancellationToken ct)` — đọc danh sách đơn + dòng đơn để xuất cho khách (không có `unitCostVnd`, `priceOverrideReason`, `internalNote`).
- **`Pricing` công bố port `IPricingCustomerMergePort`** tại `TeaPremium.Application/Pricing/Contracts/IPricingCustomerMergePort.cs` (hiện thực bởi `EfPricingCustomerMergePort` tại `TeaPremium.Infrastructure/Pricing/EfPricingCustomerMergePort.cs`):
  - `Task<(int MovedVouchers, int MovedRedemptions)> ReassignCustomerPricingAsync(Guid sourceCustomerId, Guid targetCustomerId, CancellationToken ct)` — khóa `pricing.vouchers` (`FOR UPDATE` theo `id ASC`) có `bound_customer_id == B` chuyển sang `A`, và chuyển `pricing.voucher_redemptions` có `customer_id == B` sang `A` bằng tracked entities.
- **Port hoãn cho `Marketing` (`P5-01`/`P5-02`/`P5-05`) và `Reviews` (`P5-07`):**
  - Bảng `marketing.*` và `catalog.product_reviews` chưa tồn tại ở P2 ([p2-01-implementation.md](p2-01-implementation.md) dòng `60-61`). Khi triển khai `P5-01`/`P5-02`/`P5-05`, module `Marketing` sẽ công bố `IMarketingCustomerPrivacyPort` để tiêm vào `MergeCustomersHandler` & `EraseCustomerHandler`, xử lý: (a) `marketing.tracking_links.customer_id` (`B -> A`), (b) `marketing.campaign_recipients` (`G17` `UNIQUE(campaign_id, customer_id)`: nếu trùng chiến dịch giữa A và B thì giữ dòng đã `'sent'`, hoặc dòng mới hơn, và cập nhật/xóa bản còn lại trước khi đổi `customer_id`), (c) `marketing.referral_codes` (`PK(customer_id)`, `BR-CRM-05`: nếu A đã có mã thì giữ mã của A và đặt `is_active = false` hoặc xóa mã chưa dùng của B; nếu A chưa có mã thì chuyển mã của B sang A), (d) `marketing.referrals` (`referrer_customer_id`, `referee_customer_id` `UNIQUE`). Tại `P5-07` (`G20`), bổ sung ẩn danh `catalog.product_reviews.display_name` khi erase.

### 3.2 Thiết kế từng bảng cho Transaction `Merge (B -> A)` — Đối chiếu CHECK, UNIQUE, FK và `tp_app`

*(Mọi thao tác dùng tracked EF Core entities + `SaveChangesAsync` theo 2 pha trong cùng 1 transaction `EfUnitOfWork` để vừa tuân thủ cấm `ExecuteUpdate/ExecuteDelete` trên bảng `[Audited]`, vừa loại bỏ hoàn toàn xung đột thứ tự batch của EF Core trên các unique index non-deferrable).*

**Tiền điều kiện sau khi khóa `A` và `B` (`FOR NO KEY UPDATE`):**
- `A` hoặc `B` không tồn tại → `404 not_found`.
- `A.ContactStatus == "erased"` hoặc `B.ContactStatus == "erased"` → `409 customer_erased`.
- `A.MergedIntoCustomerId != null` hoặc `B.MergedIntoCustomerId != null` → `409 customer_merged` (chặn gộp vào hồ sơ đã gộp và chặn gộp lại một stub đã gộp trước đó).
- Tải `accountA` và `accountB` (`crm.customer_accounts`, khóa `FOR UPDATE` nếu tồn tại): nếu `accountA != null && accountB != null` → từ chối ngay **`409 account_exists`** (`BR-CRM-05`), rollback sạch.

| Bước | Bảng (`schema.table`) | Quyền `tp_app` (`P2-01`) | Thao tác chi tiết trong Transaction | Đối chiếu CHECK / UNIQUE / FK (`0` lỗi `23503`/`23505`/`23514`/`42501`) |
|---:|---|---|---|---|
| M1 (Pha 1 flush) | `crm.customer_tags` | `SELECT, INSERT, UPDATE, DELETE` | Tải `tagsA`, `tagsB`. Xóa toàn bộ dòng `tagsB` (`db.RemoveRange(tagsB)`), tính tập `missingOnA = tagsB.TagIds \ tagsA.TagIds`. | PK ghép `(customer_id, tag_id)` không cho sửa trực tiếp cột khóa trong EF; xóa `tagsB` ở Pha 1 rồi chèn `missingOnA` cho `A` ở Pha 2 → **0 lỗi `23505` `pk_customer_tags`**. |
| M2 (Pha 1 flush) | `crm.customer_addresses` | `SELECT, INSERT, UPDATE, DELETE` | Tải `addrsA`, `addrsB`. Nếu cả `addrsA` và `addrsB` đều có dòng `IsDefault == true`: giữ `IsDefault = true` của `A`, đặt `addrB.IsDefault = false`, `addrB.UpdatedAt = nowUtc` ở Pha 1 **trước khi** đổi `CustomerId = A` ở Pha 2. Đồng thời kẹp `CreatedAt` theo `BR-IAM-04` (xem chi tiết bên dưới). | `ux_customer_default_address` (`UNIQUE(customer_id) WHERE is_default`) là non-deferrable: hạ `IsDefault = false` ở Pha 1 `SaveChangesAsync` trước khi gán `CustomerId = A` ở Pha 2 đảm bảo **0 lỗi `23505`**. Các cột `receiver_phone`, `province_code`, `ward_code`/`ward_name_text` giữ nguyên thỏa `ck_customer_addresses_*`. |
| M3 (Pha 1 flush) | `crm.customer_interests` | `SELECT, INSERT, UPDATE, DELETE` | Tải `intA`, `intB`. Nhóm theo khóa biểu thức `(InterestType, RefId ?? Guid.Empty, PriceBand ?? "", Source)`: nếu trùng với dòng đã có trên `A`, cộng dồn `rowA.Score = (int)Math.Min((long)rowA.Score + rowB.Score, int.MaxValue)`, `rowA.UpdatedAt = nowUtc` và `db.Remove(rowB)` ở Pha 1; nếu không trùng thì đổi `rowB.CustomerId = A`, `rowB.UpdatedAt = nowUtc` ở Pha 2. | Xóa `rowB` trùng ở Pha 1 trước khi chuyển các `rowB` còn lại sang `A` → **0 lỗi `23505` `ux_customer_interest`**; `Score` luôn `>= 1` thỏa `ck_customer_interests_score` (`score > 0`). |
| M4 (Pha 1 flush) | `crm.consents` | `SELECT, INSERT, UPDATE, DELETE` | Tải `conA`, `conB`. Với mỗi cặp `(Channel, Purpose)`: nếu chỉ B có → chuyển `conB.CustomerId = A` ở Pha 2; nếu cả A và B cùng có → chọn bản có `CapturedAt` mới nhất (`conB.CapturedAt > conA.CapturedAt` chọn B; `<` chọn A; nếu bằng nhau đến microsecond thì ưu tiên `"revoked"` để an toàn quyền riêng tư, nếu cùng status giữ A). Xóa bản thua ở Pha 1 `SaveChangesAsync`, sau đó nếu bản thắng là `conB` thì chuyển `conB.CustomerId = A` ở Pha 2. | Xóa bản thua ở Pha 1 `SaveChangesAsync` trước khi cập nhật `conB.CustomerId = A` ở Pha 2 → **0 lỗi `23505` `ux_consents_customer_id_channel_purpose`**. `evidence` giữ nguyên thỏa `ck_consents_evidence`. |
| M5 (Pha 2) | `crm.customer_accounts` (+ bảo toàn `BR-IAM-04`) | `SELECT, INSERT, UPDATE, DELETE` | **(a) Nếu chỉ B có `accountB`:** kiểm `hasOrdersOnA = await ordersPort.HasOrdersAsync(A.Id)` và `hasAddressesOnA = addrsA.Length > 0` (trước khi gộp), `phoneMismatch = !string.Equals(accountB.LoginPhone, A.PhoneE164, Ordinal)`. Chuyển `accountB.CustomerId = A.Id`. Nếu `phoneMismatch \|\| hasOrdersOnA \|\| hasAddressesOnA`: đặt `accountB.HistoryUnlockedAt = null` và xoay `accountB.SecurityStamp = Guid.CreateVersion7().ToString("N")` (`BR-IAM-06`/`BR-IAM-04`); nếu `phoneMismatch` cũng đặt `accountB.PhoneVerifiedAt = null`. Kẹp mốc địa chỉ: mọi địa chỉ cũ của `A` (và địa chỉ của `B` nếu `phoneMismatch`) có `CreatedAt >= accountB.CreatedAt` được kẹp về `accountB.CreatedAt.AddMicroseconds(-1)`. **(b) Nếu chỉ A có `accountA` và `accountA.HistoryUnlockedAt == null`:** mọi địa chỉ chuyển từ `B` sang `A` có `CreatedAt >= accountA.CreatedAt` được kẹp về `accountA.CreatedAt.AddMicroseconds(-1)` để không lộ địa chỉ chưa xác minh của B cho `accountA` trước khi unlock. *(Dùng `.AddMicroseconds(-1)` thay vì `.AddTicks(-1)` vì PostgreSQL `timestamptz` có độ phân giải microsecond).* | `A` chưa có account nên `ux_customer_accounts_customer_id` không đụng `23505`; `login_phone`, `email`, `status` không đổi nên `ux_customer_accounts_login_phone`, `ux_account_email` và `ck_account_login_phone` thỏa 100%. |
| M6 (Pha 2) | `sales.orders`, `crm.interactions`, `crm.data_requests`, `pricing.vouchers`, `pricing.voucher_redemptions` | `UPDATE` có trên tất cả | Chuyển toàn bộ dòng của `B` sang `A`: `ordersPort.ReassignCustomerOrdersAsync(B.Id, A.Id, nowUtc)` (`sales.orders.customer_id = A`; giữ nguyên `customer_account_id`), `interactions` (`customer_id = A`), `data_requests` (`customer_id = A`), `pricingPort.ReassignCustomerPricingAsync(B.Id, A.Id)` (`vouchers.bound_customer_id = A`, `voucher_redemptions.customer_id = A`). Flush `SaveChangesAsync` để các câu truy vấn tính lại chỉ số đọc thấy dữ liệu mới ngay trong transaction. | `pricing.voucher_redemptions` chỉ có `UNIQUE(order_id)` (cột `ix_redemptions_voucher_customer` là non-unique index) nên A và B cùng từng dùng 1 mã công khai trên 2 đơn khác nhau vẫn chuyển sạch **0 lỗi `23505`**. Không gọi `DELETE` trên `sales.orders` → **0 lỗi `42501`**. |
| M7 (Pha 2) | `crm.customers` (`C`, `D`, `B`, `A`) | `SELECT, INSERT, UPDATE` (cấm `DELETE`) | 1) Làm phẳng chuỗi gộp đa tầng: mọi khách `C` trỏ chuyển tiếp về `B` (`D -> C -> B`) → đặt `C.MergedIntoCustomerId = A.Id`, `C.UpdatedAt = nowUtc`. 2) Gỡ tự tham chiếu `referred_by_customer_id`: nếu `A.ReferredByCustomerId == B.Id` → đặt `A.ReferredByCustomerId = (B.ReferredByCustomerId != A.Id && B.ReferredByCustomerId != B.Id ? B.ReferredByCustomerId : null)`; nếu `B.ReferredByCustomerId == A.Id` → đặt `B.ReferredByCustomerId = null`; mọi khách thứ ba `D` (`D.Id != A.Id && D.Id != B.Id`) có `D.ReferredByCustomerId == B.Id` → chuyển sang `A.Id`. 3) Cập nhật `B`: `B.MergedIntoCustomerId = A.Id`, `B.PhoneE164 = null`, `B.FirstDeliveredAt = null`, `B.LastDeliveredAt = null`, `B.DeliveredOrdersCount = 0`, `B.LifetimeValueVnd = 0`, `B.LastContactedAt = null`, dựng lại `B.SearchText` qua `public.f_unaccent(...)` (đã loại SĐT cũ), `B.UpdatedAt = nowUtc`. 4) Tính lại chỉ số thật của `A`: gọi `ordersPort.ComputeDeliveredOrderStatsAsync(A.Id)` theo **`O3b = Phương án 3`** (`CustomerOrderStatsCalculator`: chỉ tính refund thương mại `kind IN ('return','adjustment')`, tuyệt đối không trừ `overpayment`; chỉ loại đơn theo `BR-RET-04` khi 100% dòng đơn đã trả qua các phiếu `return_requests(status='resolved', resolution='refund')` — không tính `replacement` và không dựa đơn thuần vào `order_lines.returned_quantity` — VÀ tổng hoàn tiền hàng thương mại `Σ(amount_vnd - shipping_amount_vnd)` `>= subtotal_vnd - discount_vnd`; cộng `lifetime_value_vnd = Σ max(0, total_vnd - commercial_refunded_vnd)` bao gồm `is_backfilled = true` với số học `checked`), và tính `A.LastContactedAt = MAX(interactions.occurred_at WHERE customer_id IN (A, B) AND kind <> 'note')` theo `BR-CRM-07`; dựng lại `A.SearchText`, `A.UpdatedAt = nowUtc`. | - `B` có `merged_into_customer_id = A.Id IS NOT NULL` nên dù `B.PhoneE164 = null` và `B.ZaloName = null` vẫn thỏa **`ck_customer_contact`** (`G17a`, **0 lỗi `23514`**).<br>- `ux_customers_phone` bỏ qua `B` ngay lập tức.<br>- Mọi dòng `A`, `B`, `D` đều thỏa **`ck_customer_not_self_ref`** (`referred_by_customer_id <> id`, **0 lỗi `23514`**).<br>- Chỉ `UPDATE`, không `DELETE` trên `crm.customers` → **0 lỗi `42501`**. |

### 3.3 Thiết kế từng bảng cho Transaction `Erase` — Đối chiếu CHECK, UNIQUE, FK, `tp_app` và `AC-P2-10`

**Tiền điều kiện sau khi lấy `pg_advisory_xact_lock` + khóa `Customer` (`FOR NO KEY UPDATE`):**
1. Không tồn tại → `404 not_found`.
2. `customer.MergedIntoCustomerId is { } targetId` → từ chối **`409 customer_merged`** kèm `data = { mergedIntoCustomerId = targetId }` (bắt buộc owner thao tác trên hồ sơ đích `targetId`).
3. `customer.ContactStatus == "erased"` → từ chối **`409 customer_erased`**.
4. Khóa `sales.orders` của khách gốc và toàn bộ chuỗi stub gộp đa tầng (`WHERE customer_id = ANY(...) ORDER BY id ASC FOR UPDATE`) qua `ordersPort.CheckEraseBlockersAsync(locked.AllCustomerIds, ct)` và kiểm 3 điều kiện chặn:
   - Còn đơn `status IN ('pending', 'confirmed', 'shipping')` → thêm lý do `"active_orders"`.
   - Còn `sales.return_requests` thuộc đơn của khách có `status IN ('requested', 'approved')` → thêm lý do `"open_return_requests"`.
   - Còn `sales.payments` thuộc đơn của khách có `status == 'awaiting_settlement'` → thêm lý do `"awaiting_cod_settlement"`.
   - Nếu danh sách lý do khác rỗng → từ chối **`409 customer_erase_blocked`** kèm `data = { blockingReasons }` (chỉ mã tĩnh, không PII), rollback sạch.

| Bước | Bảng (`schema.table`) | Quyền `tp_app` (`P2-01`) | Thao tác chi tiết trong Transaction | Đối chiếu CHECK / UNIQUE / FK (`0` lỗi `23503`/`23505`/`23514`/`42501`) |
|---:|---|---|---|---|
| E1 | `crm.customer_addresses`, `crm.customer_tags`, `crm.customer_interests`, `crm.consents` | `SELECT, INSERT, UPDATE, DELETE` | Tải tracked rows theo `customer_id == id` và `db.RemoveRange(...)` cho cả 4 bảng. | Cả 4 bảng đều cấp `DELETE` cho `tp_app` và không có bảng con nào khác tham chiếu tới chúng → **0 lỗi `23503` / `42501`**. |
| E2 | `crm.interactions` | `SELECT, INSERT, UPDATE, DELETE` | Tải mọi dòng `customer_id == id`. Trên từng dòng: đặt `Content = "[Đã xóa]"`, `FollowUpResult = null`; **chỉ khi** `FollowUpAt != null && FollowUpDoneAt == null` (hẹn chăm sóc đang mở) thì đặt `FollowUpDoneAt = nowUtc` để đóng hẹn mở (tuyệt đối giữ `FollowUpDoneAt = null` nếu `FollowUpAt == null`). | - `length('[Đã xóa]') = 8` ∈ `[1, 4000]` thỏa **`ck_interactions_content`**.<br>- Khi `FollowUpAt == null`, `FollowUpDoneAt` giữ `null`; khi đóng hẹn mở thì `FollowUpAt != null` nên luôn thỏa **`ck_interactions_row` (`follow_up_done_at IS NULL OR follow_up_at IS NOT NULL`)** → **0 lỗi `23514`**. |
| E3 | `crm.data_requests` | `SELECT, INSERT, UPDATE, DELETE` | 1) Trên mọi dòng `crm.data_requests` cũ của khách: đặt `Note = null`; nếu có dòng đang `Status == "open"` thì chuyển `Status = "completed"`, `CompletedAt = nowUtc`, `HandledBy = actor.Id`. 2) Thêm 1 dòng `DataRequest` mới: `Id = Guid.CreateVersion7()`, `CustomerId = id`, `RequestType = "erase"`, `Status = "completed"`, `Channel = requestChannel`, `Note = null`, `RequestedAt = nowUtc`, `CompletedAt = nowUtc`, `HandledBy = actor.Id`. | `request_type = 'erase'` và `status = 'completed'` khớp `ck_data_requests_request_type` và `ck_data_requests_status`; `Note = null` không lưu lại lý do thô của khách. |
| E4 | `sales.carts`, `crm.customer_logins`, `crm.customer_accounts` | `DELETE` trên `carts`, `cart_items`, `customer_logins`; `UPDATE` trên `customer_accounts` | Nếu khách có `account` trong `crm.customer_accounts`: 1) Gọi `ordersPort.DeleteAccountCartsAsync(account.Id)` xóa mọi `sales.carts` của account (`sales.cart_items` tự xóa theo `ON DELETE CASCADE`). 2) Xóa mọi dòng `crm.customer_logins` (`account_id == account.Id`). 3) Cập nhật `account`: `Status = "disabled"`, `LoginPhone = null`, `Email = null`, `EmailVerifiedAt = null`, `PhoneVerifiedAt = null`, `HistoryUnlockedAt = null`, `PasswordHash = null`, `FailedLoginCount = 0`, `LockoutEnd = null`, `SecurityStamp = Guid.CreateVersion7().ToString("N")`. | - Không xóa dòng `customer_accounts` nên không phạm `fk_orders_customer_account_id` (`NO ACTION`, **0 lỗi `23503`**).<br>- `status = 'disabled'` với `login_phone = NULL` thỏa **`ck_account_login_phone` (`status = 'disabled' OR login_phone IS NOT NULL`)**, thỏa `ck_customer_accounts_login_phone` (`NULL ~ regex` là `UNKNOWN` → PASS), và `ux_customer_accounts_login_phone` cho phép nhiều dòng `NULL` (**0 lỗi `23505` / `23514`**). |
| E5 | `sales.orders` (`AC-P2-10`) | `SELECT, INSERT, UPDATE` (cấm `DELETE`) | Qua `ordersPort.AnonymizeCustomerOrdersAsync`: trên mọi đơn `customer_id == id` (lúc này chỉ còn `delivered`, `delivery_failed`, `cancelled`), đặt `ContactName = "[Đã xóa]"`, `ReceiverName = "[Đã xóa]"`, `ContactPhone = "+84000000000"`, `ReceiverPhone = "+84000000000"`, `ContactEmail = null`, `AddressLine = "[Đã xóa]"`, `CustomerNote = null`, `UpdatedAt = nowUtc`. **Giữ nguyên tuyệt đối:** `Code`, `Status`, `PaymentMethod`, `PaymentStatus`, `ProvinceCode`, `ProvinceName`, `WardCode`, `WardName`, `SubtotalVnd`, `DiscountVnd`, `ShippingFeeVnd`, `TotalVnd`, `DeliveredAt`, `IsBackfilled`, `order_lines`, `payments`, `refunds`, `cod_settlements`, `shipments`. | - `'+84000000000'` khớp `ck_orders_contact_phone` & `ck_orders_receiver_phone` (`^\+84[0-9]{9}$`).<br>- Mọi ràng buộc tiền `ck_order_total`, `ck_order_discount`, `ck_order_delivered`, `ck_order_cancelled` giữ nguyên (**0 lỗi `23514`**).<br>- Chỉ `UPDATE`, không `DELETE` → **0 lỗi `42501`**.<br>- Tổng doanh thu hàng `R1`, phí ship `S`, hoàn tiền `RF`, doanh thu thuần `NR` (`BR-FIN-02`) giữ nguyên 100% (**`AC-P2-10`**). |
| E6 | `crm.customers` (`id` + toàn bộ chuỗi stub đã gộp vào `id` ở mọi độ sâu) | `SELECT, INSERT, UPDATE` (cấm `DELETE`) | 1) Trên hồ sơ đích `customer`: đặt `ContactStatus = "erased"`, `ErasedAt = nowUtc`, `FullName = "[Đã xóa]"`, `PhoneE164 = null`, `Email = null`, `ZaloName = null`, `NoteSummary = null`, `SearchText = ""` (đánh dấu tường minh `entry.Property(x => x.SearchText).IsModified = true`), `LastContactedAt = null`, `UpdatedAt = nowUtc`. 2) **Dọn sạch chuỗi stub đa tầng (`ASSUMPTION-P212-03 = A`):** giải quyết bao đóng chuyển tiếp lặp (`D -> C -> B -> A` ở mọi độ sâu) dưới `pg_advisory_xact_lock`, khóa `FOR NO KEY UPDATE` theo thứ tự UUID tăng dần, kiểm điều kiện chặn & xóa/ẩn danh đồng bộ mọi stub `S` (`ContactStatus = "erased"`, `ErasedAt = nowUtc`, `FullName = "[Đã xóa]"`, `PhoneE164 = null`, `Email = null`, `ZaloName = null`, `NoteSummary = null`, `SearchText = ""`, `LastContactedAt = null`, `UpdatedAt = nowUtc`, giữ nguyên `S.MergedIntoCustomerId`) để bảo đảm 0 PII tồn dư trên bất kỳ stub nào. | - `length(trim('[Đã xóa]')) = 8 >= 1` thỏa **`ck_customers_full_name`**.<br>- `contact_status = 'erased'` thỏa **`ck_customer_contact`** khi cả `phone_e164` và `zalo_name` đều `NULL` (**0 lỗi `23514`**).<br>- Chỉ `UPDATE`, không `DELETE` → **0 lỗi `42501`**. |

### 3.4 Chính sách `[Audited]`, `AuditValuePolicy` và `CustomerPrivacyAudit` (`action = 'access'` / `'state_change'`)

1. **Đánh dấu `[Audited]` trong `CrmSalesFinanceEntities.cs` và cập nhật `AuditSaveChangesInterceptor.cs`:**
   - Gắn `[Audited]` cho 3 entity khóa đơn UUID thuộc nghiệp vụ `P2-12`: `Consent` (`crm.consents`), `DataRequest` (`crm.data_requests`), `CustomerInterest` (`crm.customer_interests`). *(Các bảng `Customer`, `CustomerAddress`, `Tag`, `CustomerAccount` đã gắn `[Audited]` từ `P2-02`; `CustomerTag` và `CustomerLogin` là khóa ghép nên không gắn `[Audited]` theo `02-architecture.md:125`)*.
   - Mở rộng điều kiện `ValueGenerated.OnAdd` tại `AuditSaveChangesInterceptor.cs:66` (`entry.Entity is StockLevel or Customer or CustomerAddress or Consent or DataRequest or CustomerInterest`) để khi thêm mới `DataRequest` (`status = 'completed'`) hoặc `CustomerInterest` (`score`, `updated_at`), giá trị gán tường minh luôn được ghi nhận vào `AuditLog.Changes`.
2. **Phân loại fail-closed trong `AuditValuePolicy.Filter`:**
   - `crm.consents`:
     - `Safe`: `customer_id` (`Guid`), `channel` (`CustomerRules.ConsentChannels`), `purpose` (`CustomerRules.ConsentPurposes`), `status` (`"granted"` \| `"revoked"`), `source` (`CustomerRules.ConsentSources`), `captured_at` (`DateTime`), `captured_by` (`Guid`).
     - `Pii` (`"[redacted]"`): **`evidence`** (văn bản bằng chứng nhân viên nhập thường chứa tên/SĐT/Zalo khách).
   - `crm.data_requests`:
     - `Safe`: `customer_id` (`Guid`), `request_type` (`CustomerRules.DataRequestTypes`), `status` (`CustomerRules.DataRequestStatuses`), `channel` (`CustomerRules.DataRequestChannels`), `requested_at` / `completed_at` (`DateTime`), `handled_by` (`Guid`).
     - `Pii` (`"[redacted]"`): **`note`** (văn bản tự do).
   - `crm.customer_interests`:
     - `Safe`: `customer_id` (`Guid`), `interest_type` (`CustomerRules.InterestTypes`), `ref_id` (`Guid`), `price_band` (`CustomerRules.PriceBands`), `source` (`"order"` \| `"manual"`), `score` (`int`), `updated_at` (`DateTime`).
   - *Bảo chứng không lộ PII khi `erase`:* trong `AuditSaveChangesInterceptor`, cả `before` lẫn `after` đều đi qua `AuditValuePolicy.Filter(...)` trước khi serialize JSON, nên dòng audit sinh ra lúc `erase`/`delete` chỉ ghi `"[redacted]"` cho tên/SĐT/email/địa chỉ/evidence cũ, không bao giờ chép ngược PII bị xóa sang `platform.audit_logs`.
3. **Bản ghi `CustomerPrivacyAudit` chuyên biệt trong cùng transaction:**
   - **`Merge`:** ghi `AuditLog` (`entity_type = "crm.customers"`, `entity_id = targetCustomerId`, `action = "state_change"`, `changes` chỉ chứa UUID & bộ đếm: `operation = "merge"`, `merged_customer_id`, `flattened_chain_count`, `account_transferred`, `history_unlock_reset`, `moved_orders_count`, `moved_interactions_count`, `moved_vouchers_count`, `moved_redemptions_count`).
   - **`Erase`:** ghi `AuditLog` (`entity_type = "crm.customers"`, `entity_id = customerId`, `action = "state_change"`, `changes` chứa `operation = "erase"`, `data_request_id`, `request_channel`, `anonymized_orders_count`, `scrubbed_interactions_count`, `scrubbed_merged_stubs_count`, `account_disabled`, `reason = DataProtection.Protect(reason)` với purpose `"teapremium.crm.erase-reason.v1"`, `reason_format = "data_protection_v1"`).
   - **`Export` (`BR-CRM-13`):** `ExportCustomerData` triển khai `ICommand<CustomerDataExportView>` để chạy trong `EfUnitOfWork.ExecuteAsync`; sau khi nạp dữ liệu khách tồn tại, `CustomerPrivacyAudit.RecordExportAccess` ghi `AuditLog` (`entity_type = "crm.customers"`, `entity_id = customerId`, **`action = "access"`**, `actor_type = "staff"`, `actor_id = actor.Id`, `changes = {"operation":"export_customer_data"}`). Nếu ghi `AuditLog` lỗi, transaction tự rollback và không trả dữ liệu PII ra ngoài.

---

## 4. Danh mục file thực hiện (tách biên module sạch theo `CrmModuleBoundaryRule`)

Toàn bộ hợp đồng, validator, handler, store và endpoint của `P2-12` được tách vào cụm file `CustomerPrivacy*` độc lập, và các port liên module (`Orders`/`Pricing`) được đăng ký hoàn toàn bên trong `OrdersRegistration.AddOrders()` (tuyệt đối không tham chiếu từ `TeaPremium.Infrastructure.Crm` nhằm tuân thủ kiểm thử IL `CrmModuleBoundaryRule`).

### 4.1 Sửa đặc tả có ủy quyền (`O6b = B` — chỉ 3 dòng roadmap)
1. `docs/spec/07-roadmap.md` *(sửa đúng 3 dòng `P3-01` dòng `123`, `P3-16` dòng `135`, `P5-01` dòng `187`)* — xác định chính thức quyền sở hữu và rào chắn bật `DataRetentionJob` (`BR-CRM-12`).

### 4.2 `TeaPremium.Domain` (2 file: 1 mới, 1 sửa — chỉ BCL)
2. `backend/src/TeaPremium.Domain/Crm/CustomerRules.cs` *(sửa)* — bổ sung hằng `AnonymizedText = "[Đã xóa]"`, giới hạn độ dài evidence/reason, các tập hằng `ConsentStatuses`, `ConsentSources`, `DataRequestTypes`, `DataRequestStatuses`, `DataRequestChannels`, cùng các hàm thuần `FirstConsentWins` (`BR-CRM-05`), `ShouldResetMergedAccountUnlock` & `ClampAddressCreatedAtBeforeAccount` (`BR-IAM-04`), `ComputeInteractionScrubState` (`R-P204-01`), và `ResolveMergedStubClosure` (`ASSUMPTION-P212-03`).
3. `backend/src/TeaPremium.Domain/Orders/CustomerOrderStatsCalculator.cs` *(mới)* — bộ tính chỉ số đơn đã giao thuần miền theo `O3b = Phương án 3` (`BR-ORD-07.2`, `BR-ORD-09`, `BR-RET-04`).

### 4.3 `TeaPremium.Application` (7 file: 5 mới, 2 sửa — không tham chiếu EF Core/Npgsql)
4. `backend/src/TeaPremium.Application/Crm/CustomerPrivacyContracts.cs` *(mới)* — commands (`PutCustomerConsent`, `MergeCustomers`, `EraseCustomer`, `ExportCustomerData`), DTO kết quả (`CustomerConsentItemView`, `CustomerConsentsResult`, `CustomerEraseResult`, `CustomerDataExportView` và các sub-view không lộ bí mật/giá vốn/`internalNote`), audit records, `ICustomerPrivacyStore`, và `CustomerPrivacyJson`.
5. `backend/src/TeaPremium.Application/Crm/CustomerPrivacyValidators.cs` *(mới)* — `PutCustomerConsentValidator`, `MergeCustomersValidator` (chặn `TargetCustomerId == MergeCustomerId`), `EraseCustomerValidator`, `ExportCustomerDataValidator`.
6. `backend/src/TeaPremium.Application/Crm/CustomerPrivacyHandlers.cs` *(mới)* — 4 command handlers điều phối khóa, tiền điều kiện chặn, `ICustomerPrivacyStore`, `IOrderCustomerPrivacyPort`, `IPricingCustomerMergePort` và `IClock`.
7. `backend/src/TeaPremium.Application/Orders/Contracts/IOrderCustomerPrivacyPort.cs` *(mới)* — port liên module của `Orders`/`Sales`.
8. `backend/src/TeaPremium.Application/Pricing/Contracts/IPricingCustomerMergePort.cs` *(mới)* — port liên module của `Pricing`.
9. `backend/src/TeaPremium.Application/Orders/OrderContracts.cs` *(sửa)* — bổ sung `GetCustomerIdAsync(Guid orderId, CancellationToken ct)` trên `IOrderRepository`.
10. `backend/src/TeaPremium.Application/Orders/AdminOrderUseCases.cs` *(sửa)* — cập nhật `ReorderAdminOrderHandler` khóa `crm.customers FOR SHARE` trước `sales.orders FOR UPDATE` để đồng nhất Lock Hierarchy DAG với `Erase`/`Merge`.

### 4.4 `TeaPremium.Infrastructure` (11 file: 4 mới, 7 sửa)
11. `backend/src/TeaPremium.Infrastructure/Persistence/CrmSalesFinanceEntities.cs` *(sửa)* — gắn `[Audited]` lên `CustomerInterest`, `Consent`, `DataRequest` (giữ nguyên mọi `[Audited]` hiện có).
12. `backend/src/TeaPremium.Infrastructure/Persistence/AuditSaveChangesInterceptor.cs` *(sửa)* — giữ đồng thời `Order` (`P2-06`) và thêm `CustomerInterest`, `Consent`, `DataRequest` vào nhánh giữ trường `ValueGenerated.OnAdd` tường minh khi `EntityState.Added`.
13. `backend/src/TeaPremium.Infrastructure/Persistence/AuditValuePolicy.cs` *(sửa)* — phân loại allowlist fail-closed cho `crm.consents`, `crm.data_requests`, `crm.customer_interests` (`evidence` và `note` luôn `[redacted]`).
14. `backend/src/TeaPremium.Infrastructure/Crm/CustomerPrivacyAudit.cs` *(mới)* — ghi `AuditLog` cho `merge` (`state_change`), `erase` (`state_change` + `IDataProtectionProvider` bảo vệ `reason` bằng purpose `"teapremium.crm.erase-reason.v1"`) và `export` (`access`).
15. `backend/src/TeaPremium.Infrastructure/Crm/EfCustomerPrivacyStore.cs` *(mới)* — hiện thực `ICustomerPrivacyStore` trên `AppDbContext` với khóa `pg_advisory_xact_lock`, khóa `crm.customers` bằng `FOR NO KEY UPDATE` theo UUID tăng dần (tương thích FK `FOR KEY SHARE`), tái kiểm tra `referred_by_customer_id` dưới khóa hàng, bao đóng đa tầng cho stub đã gộp và flush 2 pha (chỉ chạm schema `crm` + `AuditLog`).
16. `backend/src/TeaPremium.Infrastructure/Crm/EfCustomerOrderResolver.cs` *(sửa)* — khóa `crm.customers FOR SHARE` trong `ResolveExistingAsync` khi đang có transaction để serialize tuyệt đối với `Erase`/`Merge` mà không chặn lẫn nhau giữa các lệnh tạo đơn song song.
17. `backend/src/TeaPremium.Infrastructure/Crm/CrmRegistration.cs` *(sửa)* — giữ `ICustomerOrderResolver` (`P2-06`) và chỉ đăng ký thêm `CustomerPrivacyAudit`, `ICustomerPrivacyStore -> EfCustomerPrivacyStore` (tuân thủ nghiêm `CrmModuleBoundaryRule`).
18. `backend/src/TeaPremium.Infrastructure/Orders/EfOrderCustomerPrivacyPort.cs` *(mới)* — hiện thực `IOrderCustomerPrivacyPort` bằng tracked entities trên `sales.*` và `CustomerOrderStatsCalculator` (`O3b = Phương án 3`).
19. `backend/src/TeaPremium.Infrastructure/Orders/EfOrderRepository.cs` *(sửa)* — hiện thực `GetCustomerIdAsync`.
20. `backend/src/TeaPremium.Infrastructure/Pricing/EfPricingCustomerMergePort.cs` *(mới)* — hiện thực `IPricingCustomerMergePort` bằng tracked entities trên `pricing.vouchers` và `pricing.voucher_redemptions`.
21. `backend/src/TeaPremium.Infrastructure/Orders/OrdersRegistration.cs` *(sửa)* — giữ `IAdminOrderQueries` (`P2-06`) và đăng ký `IOrderCustomerPrivacyPort -> EfOrderCustomerPrivacyPort`, `IPricingCustomerMergePort -> EfPricingCustomerMergePort`.

### 4.5 `TeaPremium.Api` (3 file: 1 mới, 2 sửa)
22. `backend/src/TeaPremium.Api/Crm/CustomerPrivacyEndpoints.cs` *(mới)* — ánh xạ `PUT /api/v1/admin/customers/{id:guid}/consents`, `POST .../merge`, `POST .../erase`, `GET .../export`.
23. `backend/src/TeaPremium.Api/Crm/CustomerOpenApi.cs` *(sửa)* — đăng ký schema strict (`AdditionalPropertiesAllowed = false`, `required`, enum, chuẩn hóa kiểu `integer` thuần cho các bộ đếm/VND) cho request/response mới của `P2-12`.
24. `backend/src/TeaPremium.Api/Program.cs` *(sửa)* — giữ đủ đăng ký `P2-06` (`AdminOrdersOpenApi`, `MapStoreAdministrativeData`, `MapAdminOrders`) và gọi `app.MapCustomerPrivacy()`.

### 4.6 Kiểm thử, OpenAPI/TS client, ADR & hồ sơ task
25. `backend/tests/TeaPremium.UnitTests/CustomerPrivacyRulesAndValidatorsTests.cs` *(mới — 14 test methods)*
26. `backend/tests/TeaPremium.IntegrationTests/CustomerMergeAndConsentsTests.cs` *(mới — 13 test methods PG17)*
27. `backend/tests/TeaPremium.IntegrationTests/CustomerEraseAndExportTests.cs` *(mới — 13 test methods PG17)*
28. `backend/tests/TeaPremium.IntegrationTests/AuditValuePolicyTests.cs` *(sửa — bổ sung 2 `[Theory]` methods)*
29. `backend/tests/TeaPremium.IntegrationTests/OpenApiTests.cs` *(sửa — cập nhật `133 -> 137` operations, xác minh 4 route `P2-12`, schema strict, enum, kiểu `integer`, và 0 bí mật/giá vốn/`internalNote` trong toàn bộ cây `CustomerDataExportView`)*
30. `packages/api-client/openapi.json` & `packages/api-client/src/generated/schema.ts` *(tái sinh tự động qua `./scripts/verify-linux.ps1 -Mode api -WriteBack`, tuyệt đối không sửa tay)*
31. `packages/api-client/src/customer-privacy-contract.test-d.ts` *(mới — kiểm thử tĩnh kiểu TS cho cả 4 endpoint, tùy chọn `rowVersion`/`mergeRowVersion`, kiểu số thuần và cấm trường bí mật/giá vốn/`internalNote`)*
32. `docs/decisions/ADR-P2-01-phase2-contracts.md` *(sửa — ghi nhận trạng thái merge `P2-06` PR `#55` `e586a44`, `P2-14` PR `#56` `135ff41` và các quyết định `P2-12` đã `OWNER_APPROVED 2026-11-27`: `O6b = B` / `GAP-P212-01`, `ASSUMPTION-P212-01/02/03 = A`; giữ nguyên 100% trạng thái chưa duyệt của `O5`, `G27` diff, `G24`, `Q1`–`Q8`)*
33. `docs/plans/p2-12-implementation.md` *(hồ sơ bất biến này)*.

---

## 5. Kế hoạch và danh mục kiểm thử chi tiết (`0` skipped)

Mọi test gắn mã `BR-CRM-*` / `BR-IAM-*` / `BR-ORD-07.2` / `BR-RET-04` / `AC-P2-10` theo [00-README.md](../spec/00-README.md) dòng `35-36`: **14 unit test methods** + **26 integration test methods PG17 mới** + **2 `[Theory]` methods trong `AuditValuePolicyTests.cs`** + kiểm thử OpenAPI `137` operations & `customer-privacy-contract.test-d.ts`.

### 5.1 Unit tests (`TeaPremium.UnitTests/CustomerPrivacyRulesAndValidatorsTests.cs` — 14 methods)

1. `BrCrm09PutCustomerConsentValidatorEnforcesChannelPurposeStatusTrimmedEvidenceAndRowVersion` (`[Theory]`)
2. `BrCrm05Rp20105MergeCustomersValidatorRejectsSelfMergeEmptyIdsAndMalformedRowVersion` (`[Fact]`)
3. `BrCrm11EraseCustomerValidatorEnforcesReasonLengthRequestChannelAllowlistAndNulRejection` (`[Theory]`)
4. `BrCrm13ExportCustomerDataValidatorRejectsEmptyCustomerId` (`[Fact]`)
5. `BrCrm05ConsentWinnerSelectionKeepsLatestCapturedAtAndResolvesTimestampTieSafely` (`[Theory]`)
6. `BrIam04AccountMergePolicyResetsUnlockAndRotatesStampWhenPhoneDiffersOrTargetHasUnverifiedHistory` (`[Theory]`)
7. `BrIam04ClampAddressCreatedAtBeforeAccountSubtractsOneMicrosecondForPostgresTimestamptz` (`[Fact]`)
8. `AssumptionP21203ResolveMergedStubClosureFindsMultiLevelChainsAndHandlesCyclesSafely` (`[Fact]`)
9. `BrOrd072BrRet04O3bDeliveredOrderStatsCalculationUsesCommercialRefundsOnlyNeverOverpaymentAndRequires100PctRefundReturn` (`[Fact]`)
10. `BrCrm11Rp20401InteractionScrubRuleClearsFollowUpResultAndClosesOnlyOpenFollowUps` (`[Theory]`)
11. `BrCrm05MergeCustomersHandlerRejectsSelfErasedMergedStaleOrDualAccountBeforeMutatingPorts` (`[Theory]` với fake ports)
12. `BrCrm11EraseCustomerHandlerRejectsMergedStubErasedStaleAndPropagatesSalesBlockersWithoutMutating` (`[Theory]` với fake ports)
13. `BrCrm13ExportCustomerDataHandlerReturns404WithoutAuditWhenMissingAndRecordsAuditOnSuccess` (`[Fact]` với fake ports)
14. `PrivacyRequestsStrictJsonDeserializationRejectsDuplicateKeysUnknownFieldsAndNonObjects` (`[Fact]`)

### 5.2 Integration tests trên PostgreSQL 17 dưới `tp_app` thật (`TeaPremium.IntegrationTests`)

- **`CustomerMergeAndConsentsTests.cs` (13 methods):**
  1. `BrCrm09PutCustomerConsentUpsertsGrantAndRevokeWithStaffSourceAdvancesRowVersionAndFeedsHasConsentFilter`
  2. `BrCrm09PutCustomerConsentRejectsStaleRowVersionErasedOrMergedCustomerWith409`
  3. `BrCrm05O3bMergeCustomersMovesOrdersInteractionsDataRequestsVouchersAndRedemptionsRecomputesRealStatsAndSearchText` (gồm cả `overpayment` vs `adjustment`/`return`, `BR-RET-04`, `replacement`, `is_backfilled`)
  4. `BrCrm05G17aMergeCustomersPhoneOnlySourceWithNullZaloClearsPhoneAndSatisfiesCkCustomerContactUnderTpApp`
  5. `BrCrm05MergeCustomersResolvesCollisionsOnTagsDefaultAddressInterestsAndConsentsWithout23505`
  6. `G17bRp20105MergeCustomersFlattensChainCIntoBIntoAHandlesReferrerSelfRefAndRejectsSelfErasedOrAlreadyMerged`
  7. `G28BrCrm05MergeCustomersWhenBothHaveAccountsReturns409AccountExistsAndLeavesDatabaseUntouched`
  8. `G28BrIam04MergeCustomersTransfersSourceAccountResetsHistoryUnlockRotatesSecurityStampAndClampsPreExistingAddresses`
  9. `G28BrIam04MergeCustomersWhenOnlyTargetHasUnverifiedAccountClampsTransferredAddressesFromSourceAndRotatesStamp`
  10. `BrCrm05ConcurrentBidirectionalMergeAbAndBaNeverDeadlocksExactlyOneSucceedsAndOtherReturns409`
  11. `ConcurrentMergeAgainstCreateAdminOrderAndCreateInteractionOnSourceNeverDeadlocksOrOrphansChildRows`
  12. `F01MergeForNoKeyUpdateConcurrentlyWithPatchReferredByCustomerIdNeverDeadlocks40P01`
  13. `F02MergeRevalidatesReferredByCustomerIdAfterLockingNeverOverwritesConcurrentReassignmentOrClear`
- **`CustomerEraseAndExportTests.cs` (13 methods):**
  1. `AcP210BrCrm11EraseCustomerWithDeliveredOrdersAnonymizesAllPiiDisablesAccountDeletesCartsAndPreservesExactRevenueTotals`
  2. `AssumptionP21203BrCrm11EraseCustomerAnonymizesMultiLevelMergedStubsChainDIntoCIntoBIntoALeavingZeroResidualPii` (kiểm chứng cả chuỗi 3 tầng `D -> C -> B -> A` lẫn việc không tồn tại `DataRetentionJob` theo `O6b = B`)
  3. `G28BrCrm11EraseCustomerBlocksWhenPendingConfirmedOrShippingOrderExists`
  4. `G28BrCrm11EraseCustomerBlocksWhenOpenReturnRequestOrAwaitingSettlementPaymentExists`
  5. `G28BrCrm11EraseCustomerBlocksOnMergedStubWithTargetCustomerIdDataAndRejectsSecondEraseWith409`
  6. `BrCrm11EraseCustomerHandlesAllInteractionFollowUpStatesAndMultipleDisabledAccountsWithoutConstraintViolation`
  7. `BrCrm13ExportCustomerDataReturnsFullCustomerJsonWithNoStoreHeaderExcludesSecretsCostsInternalNotesAndWritesAccessAudit`
  8. `Nfr07PermissionsAndCsrfSalesCanPutConsentsButGets403OnMergeEraseExportContentGets403OnAll`
  9. `Nfr04Nfr12AuditLogsAndProblemDetailsNeverLeakRawPiiConsentEvidenceOrEraseReason`
  10. `RealTpAppDatabaseRoleExecutesConsentMergeEraseExportWithout42501WhileDeleteOnCustomersAndOrdersRemainsBlocked`
  11. `ConcurrentEraseCustomerAndCreateAdminOrderNeverDeadlocksAndNeverLeavesBothErasedAndActiveOrder`
  12. `ConcurrentEraseCustomerAndReorderDeliveredOrderNeverDeadlocksAndMaintainsStrictSerializability`
  13. `ConcurrentEraseCustomerAgainstCreateInteractionAndPatchCustomerNeverDeadlocksAndLeavesZeroUnscrubbedPii`
- **`AuditValuePolicyTests.cs` (bổ sung 2 methods):**
  1. `P212EntitiesAllowlistStructuredFieldsAndRedactConsentEvidenceAndDataRequestNotes`
  2. `P212EntitiesRejectSpoofedStringsOnSafeFields`

### 5.3 Lộ trình kiểm chứng 2 pha (không chạm host PG lock khi `P2-06` đang chạy)

- **Pha 1 (host-only, zero Docker/PG):** `dotnet build backend/TeaPremium.sln -warnaserror`, `dotnet format whitespace backend/TeaPremium.sln --verify-no-changes`, `dotnet test backend/tests/TeaPremium.UnitTests/TeaPremium.UnitTests.csproj --no-build`, `dotnet test backend/tests/TeaPremium.ArchitectureTests/TeaPremium.ArchitectureTests.csproj --no-build`, `git diff --check`.
- **Pha 2 (hậu `P2-06` PR `#55` & `P2-14` PR `#56`, mốc `135ff41079a0ba060162759299fa507cf65fc340`):** tích hợp nền lên `origin/main` mới nhất, cập nhật `docs/decisions/ADR-P2-01-phase2-contracts.md`, `OpenApiTests.cs` và `packages/api-client/src/customer-privacy-contract.test-d.ts`, tái sinh OpenAPI/TS client bằng `./scripts/verify-linux.ps1 -Mode api -WriteBack`, kiểm thử tập trung PG17 (bao gồm toàn bộ test tranh chấp đồng thời với `POST admin/orders`, `reorder`, `interactions`, `PATCH customer`), sau đó đóng băng `artifacts/p2-12/candidate.manifest` và chạy `./scripts/verify-linux.ps1 -Mode full -Manifest artifacts/p2-12/candidate.manifest` (`FULL_ALL`, `0` skipped trên PG17 thật + toàn bộ bộ test frontend/Playwright gồm `P2-14`) trước khi bàn giao G4.

---

## 6. Rủi ro, `ASSUMPTION`, `GAP` (kèm `file:dòng`) và Lưu ý pháp lý

### 6.1 Trạng thái chốt `GAP` và `ASSUMPTION` (`OWNER_APPROVED 2026-11-27`)

| Mã | Trạng thái | Nguồn (`file:dòng`) | Quyết định áp dụng |
|---|---|---|---|
| `GAP-P212-01` (`O6b`) | **`OWNER_APPROVED` (`B`, `2026-11-27`)** | [04-business-rules.md](../spec/04-business-rules.md)`:235` (`BR-CRM-12`), [07-roadmap.md](../spec/07-roadmap.md)`:97,123,135,187` | Giữ `P2-12` đúng 4 nhóm endpoint (`PUT consents`, `POST merge`, `POST erase`, `GET export`), không triển khai `DataRetentionJob` và không xóa dữ liệu thật trong `P2-12`. Đã cập nhật chính xác 3 dòng `P3-01` (`:123`), `P3-16` (`:135`), `P5-01` (`:187`) trong `docs/spec/07-roadmap.md` về sở hữu `DataRetentionJob` và rào chắn phê duyệt chính sách lưu giữ trước khi bật xóa dữ liệu thật. |
| `ASSUMPTION-P212-01` | **`OWNER_APPROVED` (`A`, `2026-11-27`)** | [05-api.md](../spec/05-api.md)`:10,214,217,218` | `rowVersion` (và `mergeRowVersion` trên `merge`) là tùy chọn: có gửi thì kiểm Base64 `xmin` nghiêm ngặt (`400 validation_failed` / `409 concurrency_conflict`), bỏ qua vẫn khóa hàng `FOR NO KEY UPDATE`, kiểm `erased`/`merged` và nâng `UpdatedAt`/`xmin`. |
| `ASSUMPTION-P212-02` | **`OWNER_APPROVED` (`A`, `2026-11-27`)** | [04-business-rules.md](../spec/04-business-rules.md)`:261` (`BR-CRM-13`), [05-api.md](../spec/05-api.md)`:219` | `GET admin/customers/{id}/export` chỉ trả `application/json` kèm `Cache-Control: no-store`, loại trừ tuyệt đối bí mật/giá vốn/`internalNote`, ghi đồng bộ `platform.audit_logs(action='access')` trong cùng transaction và không tự tạo dòng `crm.data_requests`. |
| `ASSUMPTION-P212-03` | **`OWNER_APPROVED` (`A` + Multi-Level Stubs, `2026-11-27`)** | [schema.sql](../spec/schema.sql)`:581` | `POST admin/customers/{A}/erase` ẩn danh nguyên tử cả `A` lẫn mọi stub đã gộp trỏ chuyển tiếp về `A` ở bất kỳ độ sâu nào (`D -> C -> B -> A`), bảo đảm 0 PII tồn dư. |

### 6.2 Rủi ro kỹ thuật / vận hành và bàn giao cho các task sau

1. **Rủi ro xung đột tích hợp với các nhánh P2 đang chạy song song (`P2-03`, `P2-04`, `P2-05`, `P2-13`):**
   - *Giảm thiểu:* Tách toàn bộ code mới của `P2-12` sang các file `CustomerPrivacy*`, `EfOrderCustomerPrivacyPort`, `EfPricingCustomerMergePort`; chỉ chạm tối thiểu `CustomerRules.cs`, `CrmRegistration.cs`, `CustomerOpenApi.cs`, `Program.cs`, `CrmSalesFinanceEntities.cs`, `AuditSaveChangesInterceptor.cs`, `AuditValuePolicy.cs`. Dùng tracked entities thay vì `ExecuteUpdate/ExecuteDelete` để tương thích tự động khi `P2-04` bật `[Audited]` cho `Interaction` và `P2-05` bật `[Audited]` cho `Order`. Nếu `origin/main` tiến trước khi publish `P2-12`, chạy `base-integration` (`scripts/task-integrate.ps1`) + tái sinh OpenAPI/client + focused G4.
2. **Rủi ro hồi sinh PII từ bản sao lưu DB cũ sau khi đã `erase` (bàn giao `P3-10` / `P3-12`):**
   - *Phân tích:* Theo `06-infrastructure.md:205-206`, các bản `pg_dump` mã hóa `age` được lưu giữ 14 ngày / 8 tuần / 12 tháng. Nếu xảy ra sự cố phải khôi phục DB từ bản backup cũ hơn thời điểm khách yêu cầu `erase`, dữ liệu PII đã xóa trong quá khứ có thể xuất hiện trở lại.
   - *Giảm thiểu:* Ghi nhận vào runbook khôi phục ở `P3-10` / checklist go-live `P3-12`: nhật ký tối thiểu các mã khách/UUID đã hoàn tất `erase` (không chứa PII) cần được đối chiếu và chạy lại ẩn danh ngay trong môi trường cô lập trước khi mở lại lưu lượng sau restore.
3. **Bàn giao mở rộng rõ ràng cho Phase 3 và Phase 5:**
   - **`P3-05`:** Endpoint `GET account/orders` và `GET account/addresses` của khách đăng nhập bắt buộc tôn trọng `history_unlocked_at` (`orders.customer_account_id == account.id` và `addresses.created_at >= account.created_at` khi `history_unlocked_at IS NULL`), khớp với cơ chế reset `history_unlocked_at` và kẹp `created_at.AddMicroseconds(-1)` của `P2-12`.
   - **`P5-01` / `P5-02` / `P5-05`:** Triển khai `IMarketingCustomerPrivacyPort` nối vào `MergeCustomersHandler` & `EraseCustomerHandler` cho `marketing.tracking_links`, `marketing.campaign_recipients` (`G17`), `marketing.referral_codes` (`BR-CRM-05`), `marketing.referrals`, và bổ sung `GREATEST` với `campaign_recipients.sent_at` khi tính `last_contacted_at`.
   - **`P5-07` (`G20`):** Khi tạo `catalog.product_reviews`, bổ sung ẩn danh `display_name` (ví dụ `'[Đã xóa]'`) trong luồng `erase`.

### 6.3 Lưu ý pháp lý bắt buộc xác nhận chuyên môn trước Go-live (`NFR-12`, `G24`, `Q7`)

> **Không tự kết luận tuân thủ pháp lý.** Trước khi nhập dữ liệu khách hàng thật hoặc bật `SITE_INDEXABLE=true` go-live ở `P3-12` ([06-infrastructure.md](../spec/06-infrastructure.md) dòng `267-268`), chủ shop cần làm việc với tư vấn pháp lý / kế toán có chuyên môn tại Việt Nam để xác nhận các điểm sau theo **Nghị định 13/2023/NĐ-CP** và **Luật Bảo vệ dữ liệu cá nhân 2025** (cùng các nghị định hướng dẫn hiện hành):
> 1. **Thời hạn & quy trình xác minh yêu cầu chủ thể dữ liệu (`export`, `erase`, rút `consent`):** Xác nhận thời hạn phản hồi/thực hiện theo luật định (ví dụ mốc 72 giờ đối với yêu cầu xóa dữ liệu hoặc cung cấp dữ liệu theo Nghị định 13/2023/NĐ-CP Điều 14, Điều 16 và quy định tương ứng của Luật BVDLCN) cùng quy trình xác minh danh tính người yêu cầu qua Zalo/điện thoại/email trước khi `owner` thực thi `export` hoặc `erase` để tránh lộ PII cho người mạo danh hoặc xóa nhầm hồ sơ.
> 2. **Ngoại lệ lưu trữ chứng từ kế toán/thuế so với nghĩa vụ xóa dữ liệu (`AC-P2-10`, `DEC-18`, `Q6`):** Xác nhận việc ẩn danh các trường định danh trên `sales.orders` (`[Đã xóa]`, `+84000000000`) trong khi giữ lại mã đơn, ngày giao, tỉnh/phường, dòng hàng, số tiền thanh toán/hoàn tiền vừa đáp ứng ngoại lệ không xóa dữ liệu phục vụ nghĩa vụ pháp lý/kế toán/thuế hộ kinh doanh, vừa đủ điều kiện đối với cơ quan thuế, cũng như chính sách lưu giữ các bản sao lưu mã hóa định kỳ.
> 3. **Tính hợp lệ của bằng chứng đồng ý (`consent`) theo từng mục đích & liên hệ khách cũ (`BR-CRM-09`, `Q7`):** Xác nhận nội dung Thông báo xử lý dữ liệu cá nhân (`policy_privacy`), tính đầy đủ pháp lý của trường `evidence` do nhân viên ghi nhận (`source = 'staff'`), nguyên tắc đồng ý tách biệt theo từng mục đích (`marketing_new_products`, `marketing_promotions`, `customer_care`), và cơ sở pháp lý khi gửi tin hỏi thăm `care` tối đa 1 lần/180 ngày tới tệp 400–500 khách cũ chưa có consent điện tử.
> 4. **Hồ sơ đánh giá tác động xử lý dữ liệu cá nhân (DPIA) & chuyển dữ liệu ra nước ngoài (`DEC-19`, `G24`):** Xác nhận nghĩa vụ lập/lưu trữ/nộp hồ sơ đánh giá tác động xử lý dữ liệu cá nhân và đánh giá tác động chuyển dữ liệu cá nhân qua biên giới (nếu sử dụng hạ tầng/dịch vụ ngoài lãnh thổ Việt Nam như Cloudflare CDN/R2, Google OIDC, nhà cung cấp SMTP).
