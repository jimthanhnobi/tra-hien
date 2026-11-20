# 03 — Cơ sở dữ liệu

DDL đích: `schema.sql`. A01 owner-approved 18/11/2026: **79 bảng, 11 schema, 2 sequence khai báo tường minh** (crm.customer_code_seq và catalog.sku_number_seq; không tính sequence ngầm của serial). Thống kê tĩnh; kết quả DDL PostgreSQL 17 của amendment nằm trong docs/reviews/a01-amendment-evidence.md, không thay bằng chứng EF migration/runtime.

## 1. Quy ước

| Hạng mục | Quy ước |
|---|---|
| Schema | Một schema mỗi module: `platform, media, catalog, inventory, pricing, crm, sales, marketing, content, finance, iam`. |
| Khóa chính | Mặc định `uuid` v7 do app sinh (`Guid.CreateVersion7()`), có thứ tự theo thời gian. Ngoại lệ đã có trong DDL: `platform.settings.key`, `platform.provinces.code`, `platform.wards.code` là `text`; `platform.idempotency_keys(scope, key)` là khóa ghép; `platform.data_protection_keys.id` là `serial`. Các bảng khác có khóa ghép/kiểu khác giữ đúng `schema.sql`, không tự thêm UUID thay thế. |
| Mã cho người đọc | `orders.code` `TP` + `yyMMdd` (giờ VN) + 4 ký tự từ bảng `23456789ABCDEFGHJKMNPQRSTUVWXYZ` (bỏ 0/O/1/I/L), tạo lại khi trùng. Khách: `KH` + sequence. Phiếu nhập `PN-yyMMdd-NNN`, đổi trả `DT-yyMMdd-NNN`, kiểm kê `KK-yyMMdd-NNN`. |
| Tiền | `bigint` VND, hậu tố `_vnd`. |
| Thời gian | `timestamptz`, hậu tố `_at`. Ngày thuần (`date`) hậu tố `_on`. |
| Enum | `text` + `CHECK`. Đổi tập giá trị = migration sửa CHECK. Domain có `static class` hằng số tương ứng. |
| Concurrency | API `rowVersion` là chuỗi mờ; client chỉ gửi lại, IAM dùng ConcurrencyStamp riêng, không SecurityStamp. `xmin` (Npgsql: `.IsRowVersion()` trên property `uint Version`), áp cho `products, variants, orders, customers, campaigns, receipts, stocktakes, return_requests, vouchers`. |
| Xóa | Không xóa cứng dữ liệu nghiệp vụ. Dùng trạng thái (`archived`, `inactive`, `cancelled`, `erased`). Ngoại lệ: giỏ hàng, link click cũ, idempotency key, upload hết hạn (job dọn). |
| FK chéo schema | Được phép (một database). Ranh giới module giữ ở tầng code (02 §3). |
| Tên constraint/index | `pk_`, `fk_`, `ux_` (unique), `ix_`, `ck_`, `ex_` (exclusion). EF Core phải đặt tên tường minh trùng với DDL. Cả 19 PK/FK/UNIQUE/CHECK của chín bảng `platform` thuộc P0-04 có tên tường minh trong `schema.sql`, gồm `ck_audit_entity_identity`; migration và EF mapping phải dùng đúng tên, cột và biểu thức đó. Các constraint ở bảng khác có thể còn tên PostgreSQL tự sinh; báo chênh lệch khi review migration ở task tương ứng, không đổi DDL ngoài chín bảng này theo F-01. Giữ các index tường minh hiện có; PK/UNIQUE chỉ dùng index ngầm của PostgreSQL, không tạo index trùng. |

## 2. Bản đồ dữ liệu

```
catalog.product_types ─< attribute_definitions ─< attribute_options
          │
catalog.products ─< variants ─1 inventory.stock_levels
   │  │  │  │        │ └─< inventory.stock_movements (sổ kho)
   │  │  │  │        └─< pricing.sale_prices
   │  │  │  └─ material (catalog.materials)   artisan (catalog.artisans)
   │  │  └─< product_attribute_values          variants ─< variant_attribute_values
   │  └─< product_media >─ media.assets
   └─< product_categories >─ categories (cây)          collections ─< collection_products

crm.customers ─1 customer_accounts ─< customer_logins
     │ ├─< customer_addresses  ├─< interactions (+ hẹn chăm sóc)  ├─< consents
     │ ├─< customer_tags >─ tags  ├─< customer_interests          └─ referral_codes
     └─< sales.orders ─< order_lines ─< inventory.reservations
            │  ├─< payments >─ cod_settlements     ├─< refunds
            │  ├─< shipments ─< shipment_events    ├─< return_requests ─< return_lines
            │  └─< order_status_history            └─ voucher_redemptions >─ pricing.vouchers
            └─ tracking_link / campaign (marketing)

marketing.campaigns ─< campaign_recipients (1 link/khách) ─ tracking_links ─< link_clicks
marketing.referrals (referrer → referee, first_order)
finance.expenses (marketing / cố định / đóng gói / ...)
content.articles ─< article_links → product/material/artisan/category     content.pages, content.redirects
platform.settings, audit_logs, outbox_messages, idempotency_keys, inbound_webhooks, notification_logs, provinces, wards, administrative_data_imports
```

## 3. Giải thích các quyết định chính

### 3.1 Thuộc tính sản phẩm theo loại (EAV có kiểu)
- Mỗi `product_type` (ấm, chén, tống, khay, hũ trà, bình bông, xông trầm, bộ ly rượu...) có danh sách `attribute_definitions`.
- Giá trị lưu ở `product_attribute_values` (chung cho sản phẩm) hoặc `variant_attribute_values` (khác nhau theo biến thể, ví dụ dung tích).
- Mỗi dòng giá trị có **đúng một** cột giá trị (`ck_pav_one_value`) đúng với `data_type` (app kiểm).
- Lọc storefront dựa trên `is_filterable` + index `(attribute_id, option_id)` / `(attribute_id, value_number)`.
- Chất liệu và nghệ nhân **không** là thuộc tính EAV mà là cột FK của `products`, vì có trang SEO riêng và dùng cho CRM.

### 3.2 Variant và hàng độc bản
- Mọi product có ≥ 1 variant; giá, SKU, tồn nằm ở variant. sku_base cấp một lần từ catalog.sku_number_seq, prefix/type/material theo BR-CAT-14; mã đã cấp không tự đổi, UNIQUE + retry có giới hạn và khóa theo product bảo vệ concurrent requests. Prefix/code có thể NULL khi cấu hình chưa hoàn tất; cấp mã phải validate.
- Draft được thiếu giá; inactive được price_vnd NULL kể cả khi product active, không bán. Publish đòi giá mọi variant active, kích hoạt phải validate, active đã publish không sửa giá về NULL (BR-CAT-15). CHECK cột không thay luật liên bảng. description_word_count là số từ dẫn xuất; robots_noindex là cờ thủ công, không bị logic thin-description ghi đè.
- `is_unique_piece = true` ⇒ app không cho `on_hand > 1` (BR-INV-09). Không đặt bằng trigger để lỗi trả về rõ ràng cho người dùng.

### 3.3 Tồn kho = bảng số dư + sổ biến động
- `stock_levels.reserved` có từ P1-01; bảng reservations + FK order_line chỉ tạo P2-05. P1 đối soát movements/on_hand; đối soát reservation chỉ khi có bảng, không tuyên bố đã kiểm trước P2-05.
- `stock_levels` giữ số dư để đọc nhanh và giữ hàng nguyên tử (02 §5.2).
- `stock_movements` là sổ append-only; `on_hand_after` giúp đối soát. Job kiểm tra hằng đêm: `Σ quantity = on_hand` cho mọi variant; lệch → InventoryAlert nội bộ P1 an toàn/idempotent, retry có giới hạn; Telegram từ P2-13 theo trạng thái hiện tại, không replay cảnh báo cũ.
- `reservations` gắn từng `order_line`; tổng `quantity` của reservation `active` theo variant = `stock_levels.reserved` (kiểm hằng đêm).
- Giá vốn bình quân `avg_cost_vnd` cập nhật khi nhập kho (BR-INV-05). `order_lines.unit_cost_vnd` chốt lúc xuất kho → báo cáo lãi không đổi khi giá nhập sau này thay đổi.

### 3.4 Đơn hàng
- Snapshot đầy đủ người đặt, người nhận, địa chỉ, tên/SKU/giá → sửa sản phẩm/khách sau này không làm sai đơn cũ.
- `payment_status` là giá trị **dẫn xuất** từ `payments` và `refunds`, lưu lại để lọc nhanh; chỉ được cập nhật bởi `OrderPaymentStatusCalculator` (BR-PAY-02).
- `is_first_purchase` chốt lúc đặt: khách chưa từng có đơn `delivered` và `had_legacy_purchase = false`.
- `acquisition_source` copy từ khách lúc đặt; `tracking_link_id/campaign_id/utm` lấy từ cookie phân bổ (BR-MKT-09).

### 3.5 Khách hàng
- Định danh: `phone_e164` duy nhất (với hồ sơ chưa gộp). Khách cũ chỉ có Zalo vẫn tạo được (`zalo_name` bắt buộc khi thiếu SĐT).
- Chỉ số (`delivered_orders_count`, `lifetime_value_vnd`, `first/last_delivered_at`) cập nhật bằng sự kiện `OrderDelivered`/`OrderReturned` và đối soát lại hằng đêm.
- `had_legacy_purchase`: khách import từ danh sách cũ mà chủ shop xác nhận đã từng mua → coi là khách quay lại khi mua lần đầu trên hệ thống.
- Ẩn danh hóa (`erased`): xóa tên, SĐT, email, Zalo, địa chỉ, ghi chú; giữ id và đơn hàng (đơn hàng cũng ẩn danh phần liên hệ) để báo cáo không lệch.

### 3.6 Địa chỉ Việt Nam
- Từ 01/07/2025 địa giới còn 2 cấp: **tỉnh/thành → phường/xã/đặc khu**. P0-04 chỉ tạo cấu trúc `platform.provinces`/`platform.wards`, không nhúng danh mục. P0-09 sở hữu `import-provinces <csv>` và migration bổ sung `platform.administrative_data_imports` đúng DDL F-02 đã được owner duyệt sau independent G4 PASS. Bản ghi quyết định, source checksums và acceptance matrix tại [F-02 readiness](../plans/p0-09-f02-readiness.md); amendment này căn chỉnh schema đích, không thực thi migration/import hoặc cấp quyền triển khai P0-09.
- **Nguồn và mapping đã ghim:** community repository `thanglequoc/vietnamese-provinces-database`, tag `v5.2.0`, commit `62b1abdfe39625289c37a58ef6678e9db3d37f84`, `json/full_json_generated_data_vn_units.json`, MIT. Fixture/pin/source hashes đã VERIFIED: **34 provinces / 3321 wards chỉ cho release này**, không hardcode mọi release. Community dataset không phải nguồn pháp lý. Full official-catalog reconciliation và source→official mapping vẫn NOT VERIFIED, bắt buộc trước **production APPLY**. Mỗi bundle lưu cơ quan/URL chính thức riêng community source, version/effective date, thời điểm tải/xác minh, SHA-256 đúng bytes nguồn/CSV/manifest và mapping; tên file/URL hoặc counts không chứng minh provenance/completeness.
- **CSV `vn-admin-csv/1`:** header chính xác `province_code,province_name,ward_code,ward_name`; xuất UTF-8 không BOM, comma/CSV quoting, LF và LF cuối, nhập có thể chấp nhận một BOM đầu/CRLF nhưng hash trên bytes thực nhập. Đọc tên logic fullName từ field nguồn **`FullName`**, không `Name`; NFC, giữ dấu/tiền tố. Mã TEXT giữ số 0 đầu, không parse/pad/trim thành số; profile này kiểm ASCII hai chữ số tỉnh/năm chữ số ward. Sắp code ordinal; mỗi tỉnh có đúng một declaration `code,FullName,,` rồi ward rows, kể cả tỉnh không ward; mỗi ward phải có declaration parent và đúng `ProvinceCode`. Cấm duplicate/conflicting codes/names, orphan, half-empty ward, encoding lỗi và cấp thứ ba. CSV không đổi sort_order hiện có; tỉnh mới mặc định 0.
- **Manifest/lịch sử:** sidecar UTF-8 strict `provinces.manifest.json`, schema `vn-admin-manifest/1`, không duplicate/unknown fields; hash raw bytes, không self-hash. Lưu identity/source/version/effective date/mode/normalization, counts/payload hashes, base_import_id, change/deactivation evidence và recovery-path metadata. Bundle bất biến lưu raw source/CSV/manifest/plan/approval/receipt ngoài DB; ledger lưu manifest JSONB cùng hashes/receipt, không dùng Settings API hoặc bảng idempotency 48h làm lịch sử. UUID v7 và applied_at do app/IClock cấp; JSONB không thay raw bytes đã băm. Ledger append-only: chỉ SELECT/INSERT cho runtime, predecessor FK + `UNIQUE NULLS NOT DISTINCT` bảo vệ một genesis/một successor; CHECK genesis/no-self/operation và recovery/re_advance FKs giữ đúng schema.sql. Importer dưới lock kiểm cross-row semantics và append một event, không sửa/xóa event cũ.
- **Full snapshot/delta:** full cần bằng chứng phạm vi đầy đủ của input, production thêm official reconciliation; vắng mã chỉ là candidate deactivate `is_active=false` sau impact analysis/operator approval, không DELETE/cascade ẩn. Delta giữ nguyên mã vắng mặt, chỉ deactivate danh sách tường minh trong manifest; declaration tỉnh không ngầm xóa ward còn lại. Upsert là active; reactivation/reparent phải hiện trong plan. Không cho cùng mã vừa upsert vừa deactivate hoặc active ward dưới inactive/missing province.
- **Version/recovery/re-advance:** current active release lấy tại tail; historical high-water là max ordinal/effective_on của normal imports đã commit, không giảm bởi recovery/re_advance. Normal release mới phải tăng ordinal và không lùi effective date so high-water; chặn ngày hiệu lực tương lai; không so version string. Identity `(dataset_id, repository, dataset_version)` có tuple bất biến `(commit, source hashes, CSV hash/bytes, normalization, mode, deactivate list, release_order, effective_on)`; khác tuple cùng identity thì reject, correction cần identity/ordinal mới đã duyệt. Recovery có incident/target/approved ordered re_advance_path; re_advance chỉ được bước kế tiếp của path, trỏ normal import gốc qua re_advance_of, tăng so current active và đúng tiền đề delta. Intervening recovery/normal import vô hiệu path cũ. Mỗi bước cần current-base dry-run/approval mới, append event; `A1 → B2 → recovery A1 → re_advance B2` được với path đã duyệt, high-water `1/2/2/2`. Không coi historical acceptance là NO-OP hoặc quyền downgrade/replay.
- **Dry-run/apply:** dry-run không ghi data/history, báo tổng/thêm/sửa/unchanged/missing/reparent/reactivate/deactivate và errors; plan fingerprint băm exact plan bytes, bind target/profile/migrations, base tail/current/high-water/path, input hashes và catalog/reference-impact digests. Apply dùng cùng connection/UoW READ COMMITTED; lấy `pg_try_advisory_xact_lock(8300,9)` (hai int32, namespace platform/key administrative imports) trước đọc mutable state. Busy thì rollback/import_busy, không tự retry. Sau advisory lock: khóa provinces rồi wards SHARE ROW EXCLUSIVE (quyền UPDATE theo §4), reference tables nếu cần theo thứ tự tên; **không table/row-lock ledger**. Đọc lại tail/fingerprint, reject stale/conflicting plans; data mutations và ledger append cùng transaction giữ khóa đến commit/rollback. SQL/validation/cancel/history/unique-predecessor failure rollback toàn bộ. Exact current-tail manifest/CSV/plan/receipt retry với catalog digest đúng là NO-OP; plan khác base cũ stale_plan. Mất COMMIT acknowledgement phải đọc lại ledger xác định kết quả, không retry mù; concurrent import/re_advance tối đa một append.
- Khi ward chuyển province, phải báo diff và phân tích tác động trước apply. `crm.customer_addresses` giữ `province_code` và `ward_code` bằng hai FK độc lập (`schema.sql`), nên có thể còn cặp mã không nhất quán sau chuyển parent: **không âm thầm sửa địa chỉ đã lưu**; kiểm lại tính nhất quán trước khi tái dùng địa chỉ cho fulfillment. Cơ chế tái xác nhận địa chỉ thuộc task CRM/đặt đơn liên quan, không thuộc P0-04. Mã hành chính cũ vẫn phải lưu để FK/lịch sử còn đọc được. Đơn lưu snapshot mã và tên tại thời điểm đặt, không bị import sửa lại. Có `ward_name_text` dự phòng khi dữ liệu mã chưa khớp hãng ship.
- Migration hoàn tất nghĩa là cấu trúc DB sẵn sàng; bảng tham chiếu có thể vẫn rỗng đến P0-09. P0-09 kiểm dữ liệu hành chính trước khi mở các luồng chọn/tái dùng địa chỉ; không tự đổi `/health/ready` thành kiểm bảng tỉnh/phường (xem §8). Nếu cần đổi global health contract, phải có quyết định đặc tả riêng.
- **Hai gate độc lập:** CODING cần F-02 contract/DDL owner-approved, amendment spec qua G4 và giao G3 riêng, fixture/pin/checksum verified; khi đủ cho phép local/test development, không đòi production evidence. LocalTest chỉ Development/Test với DB dùng một lần allowlist `teapremium_dev*`/`teapremium_test*` local/Testcontainers, khai official evidence chưa verified đúng thực tế. PRODUCTION APPLY còn cần full official reconciliation/source mapping, production runtime-role permission tests, encrypted backup/restore drill, current-base diff/fresh approval và policy version/recovery hợp lệ. Plan bind target/profile; Production không thể chọn LocalTest bằng cờ/manifest. Production lỗi backup hoặc thiếu evidence thì HOLD. Controlled recovery có incident, backup/approval mới, giữ mã/FK và snapshot đơn; không force/destructive downgrade. Down chỉ tp_owner trên DB dùng một lần và ledger rỗng; ledger có row thì roll forward/controlled recovery, không xóa history.

### 3.7 Tìm kiếm không dấu
- `search_text` = `f_unaccent(...)` các trường liên quan, **app tự cập nhật** khi lưu (BR-CAT-10, BR-CRM-14).
- Query: `WHERE search_text LIKE '%' || f_unaccent(@q) || '%'` (escape `%`, `_`, `\` trong @q) hoặc `search_text % f_unaccent(@q)` cho tìm gần đúng; index GIN trigram.
- Tìm SĐT: chuẩn hóa input về chữ số rồi so khớp đuôi (`phone_e164 LIKE '%' || @digits`).

### 3.8 Giá khuyến mãi
- Ràng buộc exclusion `ex_sale_prices_no_overlap` cấm 2 khoảng KM chồng nhau trên cùng variant.
- Giá hiệu lực = `sale_price` nếu `now ∈ [starts_at, ends_at)` và nhỏ hơn giá niêm yết, ngược lại giá niêm yết (BR-PRC-01).

### 3.9 Định danh và lịch sử audit
- `platform.audit_logs` lưu định danh theo cặp `entity_type` và **đúng một** trong `entity_id uuid` (khóa UUID) hoặc `entity_key text` (khóa TEXT/khóa ghép khi có hợp đồng riêng). `entity_key` không rỗng; UUID không tự chuyển sang TEXT. `entity_type` là tên bảng DB có schema ổn định; truy vấn lịch sử dùng `(entity_type, entity_id)` hoặc `(entity_type, entity_key)` với chỉ mục từng phần tương ứng, thêm lọc actor/thời gian bằng `ix_audit_actor`/`ix_audit_time`.
- Audit là lịch sử append-only, không dùng FK đa hình tới mọi entity nguồn. Role `tp_app` không được `UPDATE/DELETE/TRUNCATE` audit (§4); migration sau này không được xóa hoặc viết lại audit history khi đổi tên entity/khóa mà không có kế hoạch tương thích truy vấn và phục hồi đã duyệt. `changes` phải che PII/secret, kể cả JSON lồng nhau, theo 02 §5.4; P0-05 kiểm thử.

### 3.10 Lịch sử public taxonomy — P1-04

`catalog.categories`, `catalog.collections`, `catalog.materials`, `catalog.artisans`
có `first_published_at timestamptz NULL`. BR-CAT-08: app/IClock đặt đúng một lần
trong transaction đầu tiên đưa entity ra public; không overwrite/clear khi hide,
archive, republish hoặc rename. Không thay IDs, FKs, slug/status/metadata hiện có.

Migration EF additive mới phải đi cùng entity/mapping/snapshot; không sửa migration
P1-01 hoặc migration đã commit. Trước apply, assessment bốn bảng trên đúng target:
bảng rỗng không cần backfill; dữ liệu cũ có row phải phân biệt lịch sử có bằng
chứng (đã public với thời điểm xác minh được, hoặc chưa từng public) và chưa xác
định. Không suy ra lịch sử chỉ từ status hiện tại; không dùng created_at/updated_at,
thời điểm migration hoặc thời điểm tùy ý làm timestamp publish lịch sử. Không coi
NULL trên row cũ chưa xác minh là bằng chứng chưa từng public. Nếu còn lịch sử chưa
xác định, **HOLD migration/backfill** cho target đó đến khi có phương án/evidence
được owner duyệt; không âm thầm chọn đã/chưa public. Bằng chứng target thật không
được suy từ fixture/test DB. Chi tiết executable backfill/recovery phải được review
cùng migration, không phải đã được chứng minh bằng amendment này.

Rollback source không xóa mốc public. Với dữ liệu thật, backup trước migration và
roll forward/recovery có kiểm soát theo §8; Down chỉ DB dùng một lần, không xóa
history/giá trị có ý nghĩa hoặc dependency ngoài scope. Seed material của P1-03
độc lập: không sửa bốn bảng trong migration P1-03, không overwrite metadata hay
tái sinh SKU đã cấp. P1-04 phải kiểm compatibility với reference records trước khi
nhận đủ evidence triển khai; amendment không chạy seed hay cấp quyền production.

## 4. Vai trò database (tạo bởi `infra/postgres/init-roles.sql`)

| Role | Quyền | Dùng bởi |
|---|---|---|
| `tp_owner` | Sở hữu mọi schema/bảng, chạy migration | Migration bundle khi deploy |
| `tp_app` | `SELECT, INSERT, UPDATE, DELETE` trên bảng nghiệp vụ; `USAGE` sequence | API |
| `tp_readonly` | SELECT cho backup đầy đủ và SELECT sequence cần cho pg_dump; không DML/DDL/nextval | Chỉ tài khoản backup có kiểm soát, không dùng truy vấn thủ công tùy ý |


A01/D11: `infra/postgres/init-roles.sql` là nguồn role chung, idempotent cho volume mới **và DB hiện hữu**, không tự reset password/role/DB/data. Wrapper `infra/postgres/init-roles.sh` truyền biến bí mật đúng cách (mount SQL có biến psql không tự nhận env). Với DB hiện hữu chạy cùng SQL ngoài init hook, backup/preflight ownership trước; bất thường dừng để owner duyệt remediation từng object, không xóa volume hoặc REASSIGN OWNED hàng loạt.

Quyền object do migration bổ sung cấp, qua helper `backend/src/TeaPremium.Infrastructure/Persistence/GrantsSql.cs` sinh SQL cố định lúc tạo migration; không đổi migration cũ khi helper đổi. Closure #1 sửa quyền platform/iam hiện hữu, P1-01 cấp 5 schema mới; app USAGE schema/sequence, DML theo bảng và các REVOKE dưới đây; quyền đọc history/readiness, EXECUTE f_unaccent/advisory lock theo spec. Default privileges gắn đúng tp_owner và không thay grants/revoke explicit ngoại lệ. Test dùng SQL/helper/migrations chung, đăng nhập thật app/backup, không dùng GRANT thay thế để né thiếu quyền. Runtime dev/prod dùng tp_app không owner/superuser/CREATEDB/CREATEROLE/BYPASSRLS/DDL hoặc thừa kế đặc quyền.

`scripts/dev-migrate.ps1` preflight role trước **toàn chuỗi**; helper cũng kiểm đầu migration mới. Chạy EF trực tiếp bỏ preflight vẫn có thể lỗi ở GRANT migration P0-09 cũ; không sửa migration cũ để giấu thứ tự. Re-run provisioning/idempotent SQL giữ dữ liệu/IAM/ledger/password/sequence; kiểm empty/existing/missing-role và quyền âm. Các artifact này thuộc closure #1, chưa được tạo bởi amendment tài liệu.

**Backup và điều tra tách biệt (bổ sung owner):** tp_readonly có thể đọc dữ liệu nhạy cảm cần cho backup đầy đủ, kể cả IAM credentials/Data Protection keys, nên credential chỉ được cấp cho job/container backup được ủy quyền, bảo vệ bằng secret/OS/network và audit truy cập; không cung cấp cho người truy vấn tùy ý hoặc ứng dụng. SELECT không tự phân biệt pg_dump với SQL tay; giới hạn sử dụng được thực thi qua kiểm soát credential/execution, phải kiểm chứng ở closure/P3-10.

Điều tra dùng danh tính/session riêng được owner phê duyệt theo vụ việc, quyền read tối thiểu theo bảng/cột/view đã che dữ liệu, có audit/thời hạn/thu hồi; không kế thừa tp_readonly/tp_owner hoặc quyền đọc chung IAM credentials, token/security stamp/authenticator secret và platform.data_protection_keys.xml. Không SELECT * các bảng nhạy cảm. Chỉ sự cố cần dữ liệu nhạy cảm mới có ủy quyền riêng; không ngầm cấp qua “readonly”. Test kiểm deny sensitive columns và tách credential backup/điều tra; restore drill dùng môi trường cô lập, output không chứa secret.

Ngoại lệ thu hồi quyền của `tp_app`:
```sql
REVOKE UPDATE, DELETE, TRUNCATE ON platform.audit_logs, inventory.stock_movements FROM tp_app;
REVOKE DELETE, TRUNCATE ON sales.orders, sales.order_lines, sales.payments, sales.refunds,
                         sales.order_status_history, crm.customers FROM tp_app;
```

F-02/P0-09: `platform.administrative_data_imports` là ledger append-only, `tp_app` chỉ có SELECT/INSERT, `tp_readonly` chỉ SELECT. Grants/revoke và EXECUTE cho `pg_catalog.pg_try_advisory_xact_lock(integer, integer)` đúng khối F-02 trong `schema.sql`; không cấp UPDATE/DELETE/TRUNCATE/MAINTAIN cho ledger, không dùng tp_owner làm runtime để né thiếu quyền. Role phải tồn tại trước khi áp grants; test P0-09 đăng nhập thật bằng restricted tp_app và kiểm quyền âm. Các quyền này là DDL đích, chưa phải bằng chứng môi trường đã cấu hình/chạy thành công.

## 5. Khóa cấu hình `platform.settings`

| Key | Kiểu giá trị | Mặc định | Ghi chú |
|---|---|---|---|
| `shipping.flat_fee_vnd` | number | 30000 | BR-SHP-01 |
| `shipping.free_threshold_vnd` | number \| null | 1000000 | null = không freeship |
| `orders.bank_transfer_hold_hours` | number | 24 | DEC-10 |
| `orders.max_quantity_per_line_web` | number | 10 | Chống đặt ảo |
| `orders.cod_requires_call` | boolean | true | BR-ORD bảng chuyển trạng thái |
| `orders.auto_confirm_on_full_payment` | boolean | true | BR-PAY-03 |
| `orders.max_staff_discount_percent` | number | 10 | BR-PRC-06 |
| `returns.window_days` | number | 7 | BR-RET-01 |
| `marketing.min_days_between_messages` | number | 14 | BR-MKT-01 |
| `referral.max_rewards_per_year` | number | 20 | BR-MKT-08 |
| `payment.bank` | `{bin, bank_name, account_number, account_name}` | — | VietQR |
| `payment.methods_enabled` | `["cod","bank_transfer"]` | cả hai | |
| `referral.welcome_discount_vnd` | number | 100000 | BR-MKT-05 |
| `referral.welcome_min_order_vnd` | number | 500000 | |
| `referral.reward_voucher` | `{type, value, max_discount_vnd, min_order_vnd, valid_days}` | `{fixed,100000,null,500000,90}` | BR-MKT-07 |
| `referral.qualify_after_days` | number | 7 | Sau khi giao |
| `notify.telegram_chat_id` | string | — | |
| `notify.shop_emails` | string[] | — | |
| `site.*` | thông tin website (tên, hotline, Zalo URL, Facebook URL, địa chỉ, giờ làm việc, logo, favicon, OG mặc định) | — | Public |
| `seo.brand_name` | string | — | Tên thương hiệu trong title, JSON-LD, feed |
| `seo.sold_unique_keep_days` | number | 180 | BR-SEO-21 |
| `seo.handling_days` / `seo.transit_days` | `{min,max}` | `{0,1}` / `{1,5}` | `shippingDetails` (BR-SEO-71) |
| `seo.return_policy` | `{category, days?}` | xác định khi làm P3 | `hasMerchantReturnPolicy` |
| `seo.organization` | `{logo_asset_id, same_as[], has_store, address?, geo?, opening_hours?}` | — | BR-SEO-70/97 |
| `seo.analytics` | `{ga4_measurement_id?}` | — | BR-SEO-94 |
| `retention.*` | `{carts_days:30, link_clicks_days:400, idempotency_hours:48, notification_logs_days:180, abandoned_upload_hours:24, web_vitals_days:30}` | | BR-CRM-12 |

Giá trị tiền mặc định chỉ là gợi ý để chạy dev; chủ shop chốt trước go-live.

## 6. Dữ liệu seed

**Seed bằng migration** (cần cho hệ thống chạy): role + permission theo P0-08, `platform.settings` mặc định theo P0-09. P0-04 chỉ tạo cấu trúc bảng; không chuyển seed IAM/Settings sang P0-04.

**Dữ liệu địa giới hành chính**: P0-09 nhập CSV theo §3.6 sau migration cấu trúc P0-04 và migration ledger F-02 của P0-09; ledger khởi đầu rỗng, không seed history/danh mục bằng migration. LocalTest dùng fixture đã ghim; production phải đạt production APPLY gate. Không chuyển ledger hoặc import dữ liệu vào migration P0-04.

**Seed tham chiếu catalog** (lệnh CLI `seed-catalog-reference`, chạy một lần, sửa được trong Admin):

| product_type | Thuộc tính (code: kiểu, đơn vị, cờ) |
|---|---|
| `teapot` (Ấm) | `capacity_ml`: number ml, variant, axis, filter, required · `filter_type`: option (luoi_loc, 1_lo, 7_lo, 9_lo, khac), filter · `technique`: option (thu_cong_toan_phan, ban_thu_cong, khuon), filter · `dimensions`: dimensions cm · `artisan_rank`: text · `certificate`: text (chỉ khi có bằng chứng) |
| `cup` (Chén/Ly) | `capacity_ml` number, variant, axis, filter · `set_size`: number (cái/bộ) · `dimensions` |
| `pitcher` (Tống) | `capacity_ml` · `dimensions` |
| `tea_tray` (Khay trà) | `tray_style`: option (khay_nuoc, khay_kho, khay_hut_nuoc) · `dimensions` · `wood_type`: text |
| `tea_caddy` (Hũ trà) | `capacity_g`: number g · `dimensions` |
| `vase` (Bình bông) | `dimensions` |
| `incense` (Xông trầm) | `incense_style`: option (hop, gom, khac) · `dimensions` |
| `wine_set` (Bộ ly/bình rượu) | `set_size` · `capacity_ml` · `dimensions` |
| `accessory` (Phụ kiện: ngắt nước, kẹp, lọc...) | `dimensions` |

Mã SKU seed tham chiếu: teapot→AM, cup→CH, pitcher→TO, tea_tray→KH, tea_caddy→HU, vase→BB, incense→XT, wine_set→RU, accessory→PK. Materials: tử nê TN, lục nê LN, đoạn nê DN, chu nê CN, hồng nê HN, hắc nê HE, gang GA, thủy tinh TT, gốm GM, sứ SU, đồng hợp kim DO, gỗ GO, đá DA. XX dành cho chưa chọn material. Owner sửa prefix/code trong Admin không đổi mã đã cấp.

Mỗi product type có `google_product_category` để map feed Merchant (chọn từ danh mục sản phẩm của Google khi làm P3-15, Admin sửa được).

Materials gợi ý: tử nê, lục nê, đoạn nê, chu nê, hồng nê, hắc nê, gang, thủy tinh, gốm, sứ, đồng hợp kim, gỗ, đá. Chủ shop sửa tên/mô tả trước khi publish.

**Seed demo** (lệnh CLI `seed-demo`, chỉ chạy khi `ASPNETCORE_ENVIRONMENT=Development` và DB tên `teapremium_dev*`): đi qua **use case thật** (không chèn thẳng entity), để demo không vượt qua được luật publish.

## 7. Vòng đời dữ liệu

| Dữ liệu | Giữ | Xử lý khi hết hạn |
|---|---|---|
| Giỏ hàng | 30 ngày từ lần sửa cuối | Xóa |
| Link click | 400 ngày | Xóa (đã có `click_count` tổng) |
| Idempotency key | 48 giờ | Xóa |
| Upload dở | 24 giờ | Xóa object R2 + đánh dấu `expired` |
| Notification log | 180 ngày | Xóa |
| Audit log | Vĩnh viễn (giai đoạn đầu). Khi > 5 triệu dòng: partition theo tháng | — |
| Khách `erased` | Giữ id, đơn ẩn danh | — |
| Đơn hàng, thanh toán, sổ kho | Vĩnh viễn (chứng từ kế toán) | — |

## 8. Chính sách migration

1. Migration EF Core đặt tên `yyyyMMddHHmm_<Mô_tả>`; đặt tên constraint/index tường minh.
2. Closure/P1-01 chỉ thêm migration, không sửa migration lịch sử. Chỉ DB dev/test dùng một lần, chưa có dữ liệu thật và scope được duyệt mới được tạo lại để phát triển; không xóa DB/volume để áp role/grant. **Từ lần nhập dữ liệu thật đầu tiên (có thể trước go-live): chỉ roll forward**, backup trước migration; revert source không tự rollback DB. Sau go-live chỉ additive; đổi tên/xóa cột theo 3 bước: thêm cột mới → backfill + code đọc cả hai → bỏ cột cũ ở bản sau. Down chỉ trên DB dùng một lần, từ chối trước xóa nếu có dữ liệu/dependency ngoài scope; không DROP SCHEMA CASCADE làm mất object khác.
3. CI sinh `dotnet ef migrations script --idempotent` làm artifact để review SQL.
4. Deploy chạy migration bundle bằng `tp_owner` **sau backup**, trước khi khởi động API mới. API không tự migrate.
5. `/health/ready` báo lỗi nếu còn migration chưa áp.
6. Integration test luôn tạo DB từ rỗng bằng migration (không `EnsureCreated`).
