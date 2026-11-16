# 03 — Cơ sở dữ liệu

DDL đích: `schema.sql` (đã chạy thử trên PostgreSQL 16/17: 72 bảng, 11 schema). File này giải thích **vì sao** và các quy ước mà DDL không tự nói được.

## 1. Quy ước

| Hạng mục | Quy ước |
|---|---|
| Schema | Một schema mỗi module: `platform, media, catalog, inventory, pricing, crm, sales, marketing, content, finance, iam`. |
| Khóa chính | `uuid` v7 do app sinh (`Guid.CreateVersion7()`), có thứ tự theo thời gian nên index không bị phân mảnh. |
| Mã cho người đọc | `orders.code` `TP` + `yyMMdd` (giờ VN) + 4 ký tự từ bảng `23456789ABCDEFGHJKMNPQRSTUVWXYZ` (bỏ 0/O/1/I/L), tạo lại khi trùng. Khách: `KH` + sequence. Phiếu nhập `PN-yyMMdd-NNN`, đổi trả `DT-yyMMdd-NNN`, kiểm kê `KK-yyMMdd-NNN`. |
| Tiền | `bigint` VND, hậu tố `_vnd`. |
| Thời gian | `timestamptz`, hậu tố `_at`. Ngày thuần (`date`) hậu tố `_on`. |
| Enum | `text` + `CHECK`. Đổi tập giá trị = migration sửa CHECK. Domain có `static class` hằng số tương ứng. |
| Concurrency | `xmin` (Npgsql: `.IsRowVersion()` trên property `uint Version`), áp cho `products, variants, orders, customers, campaigns, receipts, stocktakes, return_requests, vouchers`. |
| Xóa | Không xóa cứng dữ liệu nghiệp vụ. Dùng trạng thái (`archived`, `inactive`, `cancelled`, `erased`). Ngoại lệ: giỏ hàng, link click cũ, idempotency key, upload hết hạn (job dọn). |
| FK chéo schema | Được phép (một database). Ranh giới module giữ ở tầng code (02 §3). |
| Tên constraint/index | `pk_`, `fk_`, `ux_` (unique), `ix_`, `ck_`, `ex_` (exclusion). EF Core phải đặt tên tường minh trùng với DDL. |

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
platform.settings, audit_logs, outbox_messages, idempotency_keys, inbound_webhooks, notification_logs, provinces, wards
```

## 3. Giải thích các quyết định chính

### 3.1 Thuộc tính sản phẩm theo loại (EAV có kiểu)
- Mỗi `product_type` (ấm, chén, tống, khay, hũ trà, bình bông, xông trầm, bộ ly rượu...) có danh sách `attribute_definitions`.
- Giá trị lưu ở `product_attribute_values` (chung cho sản phẩm) hoặc `variant_attribute_values` (khác nhau theo biến thể, ví dụ dung tích).
- Mỗi dòng giá trị có **đúng một** cột giá trị (`ck_pav_one_value`) đúng với `data_type` (app kiểm).
- Lọc storefront dựa trên `is_filterable` + index `(attribute_id, option_id)` / `(attribute_id, value_number)`.
- Chất liệu và nghệ nhân **không** là thuộc tính EAV mà là cột FK của `products`, vì có trang SEO riêng và dùng cho CRM.

### 3.2 Variant và hàng độc bản
- Mọi product có ≥ 1 variant; giá, SKU, tồn nằm ở variant.
- `is_unique_piece = true` ⇒ app không cho `on_hand > 1` (BR-INV-09). Không đặt bằng trigger để lỗi trả về rõ ràng cho người dùng.

### 3.3 Tồn kho = bảng số dư + sổ biến động
- `stock_levels` giữ số dư để đọc nhanh và giữ hàng nguyên tử (02 §5.2).
- `stock_movements` là sổ append-only; `on_hand_after` giúp đối soát. Job kiểm tra hằng đêm: `Σ quantity = on_hand` cho mọi variant; lệch → cảnh báo Telegram.
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
- Từ 01/07/2025 địa giới còn 2 cấp: **tỉnh/thành → phường/xã/đặc khu**. Bảng `platform.provinces`, `platform.wards` seed từ danh mục chính thức (nhập bằng lệnh CLI từ file CSV, cập nhật được).
- Đơn lưu cả mã và tên tại thời điểm đặt. Có `ward_name_text` dự phòng khi dữ liệu mã chưa khớp hãng ship.

### 3.7 Tìm kiếm không dấu
- `search_text` = `f_unaccent(...)` các trường liên quan, **app tự cập nhật** khi lưu (BR-CAT-10, BR-CRM-14).
- Query: `WHERE search_text LIKE '%' || f_unaccent(@q) || '%'` (escape `%`, `_`, `\` trong @q) hoặc `search_text % f_unaccent(@q)` cho tìm gần đúng; index GIN trigram.
- Tìm SĐT: chuẩn hóa input về chữ số rồi so khớp đuôi (`phone_e164 LIKE '%' || @digits`).

### 3.8 Giá khuyến mãi
- Ràng buộc exclusion `ex_sale_prices_no_overlap` cấm 2 khoảng KM chồng nhau trên cùng variant.
- Giá hiệu lực = `sale_price` nếu `now ∈ [starts_at, ends_at)` và nhỏ hơn giá niêm yết, ngược lại giá niêm yết (BR-PRC-01).

## 4. Vai trò database (tạo bởi `infra/postgres/init-roles.sql`)

| Role | Quyền | Dùng bởi |
|---|---|---|
| `tp_owner` | Sở hữu mọi schema/bảng, chạy migration | Migration bundle khi deploy |
| `tp_app` | `SELECT, INSERT, UPDATE, DELETE` trên bảng nghiệp vụ; `USAGE` sequence | API |
| `tp_readonly` | `SELECT` | Backup (`pg_dump`), truy vấn tay khi điều tra |

Ngoại lệ thu hồi quyền của `tp_app`:
```sql
REVOKE UPDATE, DELETE, TRUNCATE ON platform.audit_logs, inventory.stock_movements FROM tp_app;
REVOKE DELETE, TRUNCATE ON sales.orders, sales.order_lines, sales.payments, sales.refunds,
                         sales.order_status_history, crm.customers FROM tp_app;
```

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
| `retention.*` | `{carts_days:30, link_clicks_days:400, idempotency_hours:48, notification_logs_days:180, abandoned_upload_hours:24}` | | BR-CRM-12 |

Giá trị tiền mặc định chỉ là gợi ý để chạy dev; chủ shop chốt trước go-live.

## 6. Dữ liệu seed

**Seed bằng migration** (cần cho hệ thống chạy): role + permission, `platform.settings` mặc định, tỉnh/phường.

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
2. Trước go-live: được sửa migration tùy ý (DB dev tạo lại từ đầu). **Sau go-live: chỉ thêm (additive)**. Đổi tên/xóa cột theo 3 bước: thêm cột mới → backfill + code đọc cả hai → bỏ cột cũ ở bản sau.
3. CI sinh `dotnet ef migrations script --idempotent` làm artifact để review SQL.
4. Deploy chạy migration bundle bằng `tp_owner` **sau backup**, trước khi khởi động API mới. API không tự migrate.
5. `/health/ready` báo lỗi nếu còn migration chưa áp.
6. Integration test luôn tạo DB từ rỗng bằng migration (không `EnsureCreated`).
