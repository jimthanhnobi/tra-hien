# 04 — Luật nghiệp vụ (Business Rules)

> Mỗi luật `BR-*` phải có ít nhất 1 test mang mã luật trong tên. Khi code và luật mâu thuẫn, sửa code.
> Thời gian "now" lấy từ `IClock`. Tiền là VND nguyên. Làm tròn: **half-up đến đồng**, chỉ làm tròn ở bước cuối mỗi công thức.

---

## 1. IAM — Định danh

- **BR-IAM-01** Nhân viên: mật khẩu ≥ 12 ký tự gồm hoa, thường, số, ký tự đặc biệt; sai 5 lần → khóa 15 phút; phiên sliding 8 giờ, tối đa 7 ngày.
- **BR-IAM-02** Ở Production, tài khoản có vai trò `owner` phải bật TOTP. Đăng nhập lần đầu bị chuyển tới màn cài TOTP; chưa cài thì không gọi được API nào khác ngoài `auth/*`.
- **BR-IAM-03** Đăng ký tài khoản khách (SĐT + mật khẩu ≥ 8 ký tự, hoặc Google):
  1. Chuẩn hóa SĐT (BR-CRM-01). SĐT đã có tài khoản → lỗi `account_exists`.
  2. Có `crm.customers` cùng SĐT (chưa gộp, chưa xóa) → tài khoản **liên kết** hồ sơ đó. Không có → tạo khách `origin = web`.
  3. Đăng nhập Google lần đầu: sau khi Google xác thực, bắt buộc nhập SĐT + họ tên → làm như bước 1–2; email Google ghi `email_verified_at`.
- **BR-IAM-04** **Mở khóa lịch sử:** tài khoản mới chưa xác minh SĐT chỉ thấy đơn đặt **bằng chính tài khoản đó** và địa chỉ tạo **sau** thời điểm tạo tài khoản. Đơn cũ của hồ sơ (đơn khách vãng lai, đơn Admin nhập, đơn trước khi có tài khoản) chỉ hiện khi `history_unlocked_at` có giá trị. Giá trị này được đặt khi:
  (a) xác minh SĐT bằng OTP (giai đoạn 5), hoặc (b) nhân viên bấm "Xác minh" sau khi gọi/nhắn đúng số đó (ghi audit).
  Lý do: chưa có OTP thì ai cũng có thể đăng ký bằng SĐT của người khác.
- **BR-IAM-05** Tra cứu đơn khách vãng lai: cần `code` + SĐT trùng `contact_phone` hoặc `receiver_phone`. Kết quả che bớt: tên người nhận viết tắt, địa chỉ chỉ hiện phường/tỉnh, SĐT hiện 3 số cuối. Rate limit 20/phút/IP, sai 10 lần/giờ/IP → chặn 1 giờ.
- **BR-IAM-06** Đổi mật khẩu/quên mật khẩu làm mới `security_stamp` → mọi phiên cũ hết hiệu lực.

## 2. CAT — Catalog

- **BR-CAT-01** Slug tự sinh từ tên: bỏ dấu (`đ→d`), thường, thay ký tự không phải `[a-z0-9]` bằng `-`, gộp `-`, cắt ≤ 80 ký tự; trùng thì thêm `-2`, `-3`. Sửa tay được.
- **BR-CAT-02** SKU viết hoa, duy nhất; **không sửa được** sau khi variant có biến động kho hoặc dòng đơn.
- **BR-CAT-03** Đổi `product_type` chỉ được khi product `draft`; khi đổi, xóa giá trị thuộc tính không thuộc type mới (cảnh báo trước).
- **BR-CAT-04** Điều kiện chuyển product sang `active` (publish), trả về **toàn bộ** lỗi một lần:
  1. `name`, `short_description`, `description_html` không rỗng.
  2. `primary_category_id` trỏ tới danh mục `visible`, và có trong `product_categories`.
  3. Có ≥ 1 variant `active`, giá > 0.
  4. Có ảnh bìa với ALT hợp lệ.
  5. Mọi thuộc tính `is_required_for_publish` đều có giá trị (thuộc tính `scope=variant` phải có ở **mọi** variant active).
  6. Product có > 1 variant active thì mỗi variant phải có giá trị cho mọi thuộc tính trục (`is_variant_axis`) và tổ hợp không trùng nhau.
  7. `seo_title` ≤ 70 ký tự và `seo_description` 50–160 ký tự (Admin có nút tự điền từ tên/mô tả ngắn).
  Publish lần đầu đặt `published_at = now`.
- **BR-CAT-05** Hiển thị storefront:
  - `active` → hiện. Hết hàng: variant thường hiện "Hết hàng", nút "Báo khi có hàng" mở Zalo/liên hệ; hàng độc bản đã bán hiện "Đã bán" kèm sản phẩm tương tự (cùng material/artisan/type). Trang vẫn index được.
  - `draft` → 404. `archived` → 404, trừ khi có redirect. Khi lưu trữ, Admin gợi ý tạo redirect về danh mục chính.
  - Variant `inactive` không hiện, không bán.
- **BR-CAT-06** Không xóa product/variant/category đã từng publish hoặc có giao dịch. Chỉ đổi trạng thái.
- **BR-CAT-07** Mỗi product tối đa 1 ảnh bìa; đổi bìa trong 1 transaction. Ảnh gắn variant thì hiện khi khách chọn variant đó.
- **BR-CAT-08** Đổi slug của nội dung **đã từng công khai** (product, category, collection, material, artisan, article, page):
  1. Tạo redirect `/<prefix>/<slug-cũ>` → `/<prefix>/<slug-mới>`.
  2. Sửa mọi redirect đang trỏ tới đường cũ thành trỏ tới đường mới (không tạo chuỗi).
  3. Nếu đường mới đang là `source_path` của một redirect → xóa redirect đó (đường được dùng lại).
  4. Tất cả trong cùng transaction với việc đổi slug.
  Prefix: `/san-pham`, `/danh-muc`, `/bo-suu-tap`, `/chat-lieu`, `/nghe-nhan`, `/kien-thuc`, `/trang`.
- **BR-CAT-09** Danh mục: tối đa 3 cấp, không vòng; danh mục `hidden` → trang 404 và không hiện menu (sản phẩm vẫn truy cập được qua URL riêng).
- **BR-CAT-10** `products.search_text = f_unaccent(name ‖ subtitle ‖ mọi SKU ‖ tên variant ‖ tên artisan ‖ tên material ‖ tên product_type)`. Cập nhật khi lưu product/variant; khi đổi tên artisan/material/type → outbox `RebuildProductSearchText(ids)`.
- **BR-CAT-11** Giá hiển thị trên thẻ sản phẩm: thấp nhất trong các giá hiệu lực (BR-PRC-01) của variant active còn hàng; nếu tất cả hết hàng thì lấy thấp nhất của mọi variant active. Có KM thì hiện giá gốc gạch ngang.
- **BR-CAT-12** JSON-LD `Product` → `Offer`/`AggregateOffer` với `priceCurrency=VND`, giá hiệu lực, `availability` = `InStock` (available > 0) / `OutOfStock` / `SoldOut` (hàng độc bản đã bán). Không có `aggregateRating`/`review` khi chưa có đánh giá thật.
- **BR-CAT-13** Import sản phẩm (FR-CAT-09): mỗi dòng là 1 variant, nhóm theo `product_slug`. Dry-run trả kết quả từng dòng; chạy thật tạo product ở `draft`, variant, giá, **không** tạo tồn kho (tồn nhập ở BR-INV-06).

## 3. INV — Kho

- **BR-INV-01** Mọi thay đổi `stock_levels.on_hand` ghi đúng 1 dòng `stock_movements` trong cùng transaction, với `on_hand_after` = giá trị mới. Không có đường nào khác thay đổi `on_hand`.
- **BR-INV-02** `available = on_hand − reserved` không bao giờ âm; DB cũng chặn (`ck_stock_reserved`).
- **BR-INV-03** **Giữ hàng** khi tạo đơn (web và admin, trừ đơn backfill): với từng dòng, theo thứ tự `variant_id` tăng dần, chạy UPDATE có điều kiện (02 §5.2) rồi tạo `reservations(active)`. Bất kỳ dòng nào thiếu → rollback cả đơn, trả `out_of_stock` kèm danh sách `{variant_id, sku, available}`.
- **BR-INV-04** Vòng đời reservation:
  - `active → consumed` khi đơn chuyển `shipping`: `on_hand -= q`, `reserved -= q`, ghi movement `sale` (−q, `unit_cost = avg_cost_vnd` hiện tại), chốt `order_lines.unit_cost_vnd`.
  - `active → released` khi: đơn hủy/hết hạn, dòng bị xóa, số lượng giảm (giảm một phần: release reservation cũ, tạo reservation mới với số lượng mới), `reserved -= q`.
  - Tăng số lượng dòng: giữ thêm phần chênh lệch theo BR-INV-03.
- **BR-INV-05** **Giá vốn bình quân** khi nhập kho `q` với giá `c`:
  - Nếu `on_hand = 0` hoặc `avg_cost_vnd IS NULL` → `avg = c`.
  - Ngược lại `avg = round_half_up((on_hand × avg + q × c) / (on_hand + q))`.
  Áp dụng cho `receipt`, `opening`, `return_restock` (với `c = order_lines.unit_cost_vnd` của dòng gốc; null thì không đổi avg).
- **BR-INV-06** Tồn đầu kỳ (FR-INV-03): chỉ cho variant **chưa có** movement nào; tạo movement `opening` (+q, cost). File: `sku, quantity, unit_cost_vnd`. Dry-run bắt buộc trước khi chạy thật.
- **BR-INV-07** Phiếu nhập: `draft` sửa tự do; `posted` (cần `inventory.write`) → tạo movement `receipt` từng dòng, cập nhật avg; `posted` không sửa/hủy được. Nhập sai thì làm điều chỉnh.
- **BR-INV-08** Điều chỉnh tay: lý do ≥ 3 ký tự; giảm không được làm `on_hand < reserved`; **tăng tồn cần `inventory.approve`**. Loại `damage` (vỡ/hỏng trong kho) dùng movement `damage` với cost = avg.
- **BR-INV-09** Hàng độc bản: `on_hand ≤ 1` luôn đúng. Phiếu nhập/điều chỉnh/kiểm kê làm `on_hand > 1` → lỗi `unique_piece_violation`. Giỏ và đơn web: số lượng tối đa 1.
- **BR-INV-10** Kiểm kê: khi nhập số đếm của một variant, lưu `system_qty_at_count = on_hand` lúc đó. Duyệt (`inventory.approve`) → với dòng có `delta = counted − system_qty_at_count ≠ 0`, áp delta vào **on_hand hiện tại** (movement `stocktake`, cost = avg). Nếu sau khi áp `on_hand < reserved` hoặc âm → chặn duyệt và liệt kê dòng lỗi.
- **BR-INV-11** Cảnh báo tồn thấp: `low_stock_threshold > 0` và `available ≤ threshold`. Hiện trên dashboard; gom vào tin Telegram tổng hợp 08:00 hằng ngày.
- **BR-INV-12** Khi `available` của variant đổi giữa 0 và > 0 → outbox `StorefrontInvalidate([product:{slug}, catalog])`.
- **BR-INV-13** Job đêm đối soát: `Σ stock_movements.quantity = on_hand` và `Σ reservations(active).quantity = reserved` cho mọi variant; lệch → Telegram cảnh báo, **không** tự sửa.

## 4. PRC — Giá và khuyến mãi

- **BR-PRC-01** Giá hiệu lực của variant tại thời điểm t: nếu có `sale_prices` với `starts_at ≤ t < ends_at` → `sale_price_vnd`, ngược lại `price_vnd`.
- **BR-PRC-02** `sale_price_vnd < price_vnd` tại thời điểm tạo; nếu sau đó giá niêm yết giảm xuống ≤ giá KM thì giá hiệu lực = `min(price, sale_price)`.
- **BR-PRC-03** Sửa giá niêm yết/giá KM cần `pricing.write`, có audit, phát `StorefrontInvalidate`. Đơn đã đặt không bị ảnh hưởng (snapshot).
- **BR-PRC-04** Mỗi đơn dùng **tối đa 1 mã** (voucher **hoặc** mã giới thiệu). Kiểm voucher theo thứ tự, trả lỗi đầu tiên gặp:
  `voucher_not_found` → `voucher_disabled` → `voucher_not_started`/`voucher_expired` → `voucher_usage_exhausted` → `voucher_not_for_customer` (khi `bound_customer_id` khác khách) → `voucher_customer_limit` → `voucher_min_order`.
  `merchandise = Σ line_total` (sau giá KM). Giảm = `fixed`: `min(value, merchandise)`; `percent`: `min(floor(merchandise × value / 100), max_discount_vnd ?? ∞)`.
- **BR-PRC-05** Áp voucher khi đặt đơn: `UPDATE vouchers SET used_count = used_count + 1 WHERE id = @id AND (usage_limit IS NULL OR used_count < usage_limit)`; 0 dòng → `voucher_usage_exhausted`. Tạo `voucher_redemptions(applied)`. Đơn hủy/hết hạn → `reverted`, `used_count − 1`.
- **BR-PRC-06** Đơn Admin: giảm giá toàn đơn bằng tay cần `discount_note`. Người không có `costs.read` chỉ giảm tối đa `orders.max_staff_discount_percent` (mặc định 10%) của merchandise; vượt → `discount_requires_owner`.
- **BR-PRC-07** Sửa giá dòng (override) cần `orders.override_price` + lý do. Nếu `unit_price < avg_cost` và người thao tác không có `costs.read` → lỗi `price_below_allowed` (thông báo không lộ giá vốn).

## 5. CRT — Giỏ hàng và checkout

- **BR-CRT-01** Token giỏ: 32 byte ngẫu nhiên (base64url) trong cookie `tp_cart` (HttpOnly, Secure, SameSite=Lax, 30 ngày); DB chỉ lưu SHA-256.
- **BR-CRT-02** Thêm vào giỏ: product `active`, variant `active`, `quantity ≥ 1`, `quantity ≤ min(available, orders.max_quantity_per_line_web)`; hàng độc bản tối đa 1. Vượt → set bằng mức tối đa và trả cảnh báo.
- **BR-CRT-03** Xem giỏ luôn tính lại: giá hiệu lực, tồn, trạng thái; dòng không bán được đánh dấu `unavailable` và không tính vào tổng.
- **BR-CRT-04** Đăng nhập khi đang có giỏ khách vãng lai: gộp vào giỏ tài khoản (cộng số lượng, cắt theo BR-CRT-02), xóa giỏ khách vãng lai.
- **BR-CRT-05** Báo giá checkout (`POST checkout/quote`) và đặt đơn dùng **cùng một** `CheckoutCalculator`:
  ```
  merchandise   = Σ quantity × effective_price          (bỏ dòng unavailable)
  discount      = BR-PRC-04 hoặc BR-MKT-05               (≤ merchandise)
  shipping_fee  = BR-SHP-01(merchandise − discount)
  total         = merchandise − discount + shipping_fee
  ```
- **BR-CRT-06** Đặt đơn (`POST checkout/orders`, header `Idempotency-Key`), client gửi `expected_total_vnd`. Thứ tự trong **một transaction**:
  1. Kiểm idempotency (02 §5.3).
  2. Validate: họ tên 2–100 ký tự; SĐT di động VN hợp lệ; email hợp lệ nếu có; tỉnh + phường thuộc nhau; địa chỉ 5–255 ký tự; ghi chú ≤ 500; đồng ý điều khoản = true; phương thức thanh toán đang bật; giỏ có ≥ 1 dòng bán được; **không còn dòng unavailable** (có thì trả `cart_has_unavailable_items`).
  3. Tính lại theo BR-CRT-05; khác `expected_total_vnd` → 409 `price_changed` kèm báo giá mới, không tạo đơn.
  4. Chống lạm dụng (BR-CRT-09).
  5. Xác định khách (BR-CRT-07).
  6. Tạo `orders(pending)` + `order_lines` (snapshot) + `order_status_history`; chuyển khoản thì `payment_due_at = now + orders.bank_transfer_hold_hours`.
  7. Giữ hàng (BR-INV-03). Áp mã (BR-PRC-05 / BR-MKT-05).
  8. Phân bổ nguồn (BR-MKT-09). Consent nếu khách tích (BR-CRM-09).
  9. Outbox: `OrderPlaced` → email khách (nếu có email), Telegram + email shop; `StorefrontInvalidate` nếu có variant về 0.
  10. Xóa giỏ. Trả `{order_code, total_vnd, payment_method, payment_instructions?}`.
- **BR-CRT-07** Xác định khách khi đặt: tìm `customers` theo `contact_phone` (chưa gộp, chưa xóa).
  - Có → gắn đơn vào hồ sơ đó. **Không sửa** tên/email/nhóm của hồ sơ; nếu hồ sơ chưa có email và khách nhập email → điền email.
  - Không có → tạo khách `origin=web`, `acquisition_source` theo BR-MKT-09.
  - Đang đăng nhập → luôn dùng hồ sơ của tài khoản; `contact_phone` khác SĐT tài khoản vẫn được (mua hộ).
  - Đăng nhập và tích "Lưu địa chỉ" → lưu vào `customer_addresses`.
- **BR-CRT-08** Khách `do_not_contact` vẫn đặt được (chỉ chặn marketing).
- **BR-CRT-09** Chống đặt ảo (đơn web): mỗi SĐT tối đa 3 đơn chuyển khoản `pending` chưa thanh toán và 5 đơn/ngày; vượt → `too_many_pending_orders`. Honeypot field + rate limit (02 §6).

## 6. ORD — Đơn hàng

### 6.1 State machine

```
              ┌──────────── cancel (staff/khách/hệ thống) ──────────────┐
              │                                                        ▼
  [pending] ──confirm──▶ [confirmed] ──ship──▶ [shipping] ──deliver──▶ [delivered]
              │               │                     │
              │               └─cancel─▶ [cancelled]  └─fail──▶ [delivery_failed]
              └──────────────────────────────────────────────▶ [cancelled]
```

| Chuyển | Ai | Điều kiện | Tác động |
|---|---|---|---|
| pending → confirmed | staff (`orders.write`); hệ thống (BR-PAY-03) | `bank_transfer` (đơn web): `payment_status = paid`. COD: nhân viên đã liên hệ xác nhận (checkbox ghi chú bắt buộc nếu `orders.cod_requires_call=true`) | `confirmed_at` |
| pending → cancelled | staff (lý do bắt buộc); khách (qua tra đơn/tài khoản, lý do tùy chọn); hệ thống (BR-ORD-05) | — | Nhả hàng, hoàn voucher, hủy referral, email khách |
| confirmed → cancelled | staff (lý do bắt buộc) | Không có shipment đang hoạt động | Như trên. Nếu đã thu tiền → đơn hiện trong "Cần hoàn tiền" |
| confirmed → shipping | staff | Tạo shipment (BR-SHP-02); `payment_status` ∈ {paid} **hoặc** `payment_method = cod` | Tiêu thụ reservation (BR-INV-04), chốt giá vốn, `shipped_at`, email kèm mã vận đơn |
| shipping → delivered | staff; webhook (giai đoạn 5) | `delivered_at` ≤ now và ≥ `shipped_at` | BR-ORD-07 |
| shipping → delivery_failed | staff; webhook | Lý do bắt buộc | BR-SHP-03 |

- **BR-ORD-01** Mọi chuyển trạng thái phải qua method của aggregate `Order` (`Confirm`, `Cancel`, `MarkShipping`, `MarkDelivered`, `MarkDeliveryFailed`); không set `status` trực tiếp. Chuyển không hợp lệ → `invalid_status_transition`.
- **BR-ORD-02** Mỗi lần chuyển: 1 dòng `order_status_history`, audit, sự kiện outbox tương ứng (`OrderConfirmed`, `OrderCancelled`, `OrderShipped`, `OrderDelivered`, `OrderDeliveryFailed`).
- **BR-ORD-03** `delivered` và `cancelled`, `delivery_failed` là trạng thái cuối. Giao lại sau khi thất bại = nút "Tạo lại đơn" sao chép thành đơn mới (`pending`, giữ hàng lại).
- **BR-ORD-04** Đơn Admin (`origin=admin`): chọn `sales_channel` ≠ `web`; tạo ở `pending` hoặc tích "Đã xác nhận" để tạo thẳng `confirmed`. Không có `payment_due_at`, không tự hủy.
- **BR-ORD-05** **Tự hủy đơn chuyển khoản quá hạn** (job 5 phút): `status = pending AND payment_method = bank_transfer AND payment_status = unpaid AND payment_due_at < now` → `cancelled`, `status_reason = 'payment_expired'`, actor `system`. Đơn `partially_paid` **không** tự hủy, chỉ đưa vào danh sách cần xử lý.
- **BR-ORD-06** Gia hạn thanh toán: staff, mỗi lần tối đa +72 giờ, lý do bắt buộc. Hủy sớm: staff hủy với lý do (sau khi liên hệ khách).
- **BR-ORD-07** Khi `delivered`:
  1. COD: tạo `payments(cod, awaiting_settlement, amount = shipment.cod_amount_vnd)` nếu > 0.
  2. Cập nhật chỉ số khách (`delivered_orders_count`, `lifetime_value_vnd += total − refunds`, `first/last_delivered_at`).
  3. Cập nhật sở thích khách (BR-CRM-08).
  4. Cấp mã giới thiệu nếu khách chưa có (BR-MKT-06).
  5. Bắt đầu đếm thời gian xét giới thiệu (BR-MKT-07).
- **BR-ORD-08** Sửa đơn (dòng, số lượng, địa chỉ, phí ship, giảm giá) chỉ khi `pending`/`confirmed`, cần `rowVersion`. Giữ/nhả hàng theo BR-INV-04; tính lại tổng; tính lại `payment_status`. Sửa đơn web có voucher thì kiểm lại `min_order`; không đạt → bỏ voucher và cảnh báo.
- **BR-ORD-09** Đơn backfill (`is_backfilled`, chỉ owner): nhập lịch sử mua cũ; tạo thẳng `delivered` với `delivered_at` trong quá khứ; **không** giữ hàng, **không** tạo movement, **không** tạo payment; tính vào chỉ số khách và sở thích; **mặc định loại khỏi báo cáo tài chính** (BR-FIN-01).
- **BR-ORD-10** Mã đơn theo 03 §1; nội dung chuyển khoản = mã đơn (không dấu, không khoảng trắng).
- **BR-ORD-11** Khách chỉ thấy nhãn trạng thái tiếng Việt: Chờ xác nhận / Chờ thanh toán (pending + bank_transfer + chưa đủ tiền) / Đã xác nhận / Đang giao / Đã giao / Giao không thành công / Đã hủy.
- **BR-ORD-12** In phiếu giao hàng: mã đơn, mã vạch mã đơn, người nhận, SĐT, địa chỉ, dòng hàng (không giá nếu tích "ẩn giá"), số tiền COD.

## 7. PAY — Thanh toán

- **BR-PAY-01** Ghi nhận chuyển khoản (staff `payments.write`, `Idempotency-Key`): `amount > 0`, `received_at ≤ now`, `bank_reference` không trùng (trừ bản ghi đã void). Đơn `cancelled` vẫn ghi được (tiền đã vào) nhưng đơn bị gắn cờ "cần hoàn tiền".
- **BR-PAY-02** Trạng thái thanh toán (chỉ `OrderPaymentStatusCalculator` được tính):
  ```
  paid      = Σ payments(confirmed).amount
  refunded  = Σ refunds.amount
  net       = paid − refunded
  if refunded > 0: status = (net == 0) ? refunded : partially_refunded
  elif paid == 0:  status = unpaid
  elif paid < total: status = partially_paid
  else: status = paid            (paid > total → cờ "thu thừa", cần hoàn)
  ```
  Tính lại sau mọi thay đổi payment/refund/tổng đơn.
- **BR-PAY-03** Đơn web `pending` + `bank_transfer` vừa đạt `paid` và `orders.auto_confirm_on_full_payment = true` (mặc định true) → tự chuyển `confirmed` (actor system), email "Đã nhận thanh toán".
- **BR-PAY-04** Hướng dẫn chuyển khoản: số tiền = `total − net`; nội dung = mã đơn; VietQR payload theo 02 §5.7; kèm hạn `payment_due_at` (giờ VN).
- **BR-PAY-05** Đối soát COD: chọn các payment `awaiting_settlement` của cùng hãng → tạo `cod_settlements(gross = Σ amount, fee, net)`; các payment → `confirmed`, `received_at = settled_at`, gắn `cod_settlement_id`. `gross` phải đúng Σ đã chọn.
- **BR-PAY-06** Hủy (void) payment: chỉ owner, lý do bắt buộc; payment đã thuộc phiên đối soát COD không void được.
- **BR-PAY-07** Hoàn tiền (`refunds.write`, `Idempotency-Key`): `amount ≤ net` hiện tại; lý do bắt buộc; liên kết `return_request` nếu có.

## 8. SHP — Giao hàng

- **BR-SHP-01** Phí ship đơn web: `base = merchandise − discount`; nếu `shipping.free_threshold_vnd` khác null và `base ≥ threshold` → 0; ngược lại `shipping.flat_fee_vnd`. Đơn Admin: mặc định theo công thức, nhân viên sửa được.
- **BR-SHP-02** Tạo shipment (`ShippingProvider = manual`): đơn `confirmed`; nhập hãng, mã vận đơn (có thể để trống rồi bổ sung), `shipping_cost_vnd` (có thể bổ sung sau); `cod_amount = payment_method = cod ? total − net : 0`. Tạo shipment chuyển đơn sang `shipping` (BR-ORD bảng).
- **BR-SHP-03** Shipment `failed` → đơn `delivery_failed`. Khi hàng về kho (shipment `returned`, nhân viên xác nhận) → movement `return_restock` cho mọi dòng với cost = `unit_cost_vnd` của dòng; ghi `return_cost_vnd` nếu có.
- **BR-SHP-04** Một đơn chỉ có 1 shipment `purpose=order` đang hoạt động (index `ux_shipment_active_order`).
- **BR-SHP-05** Báo cáo liệt kê shipment chưa có `shipping_cost_vnd` → báo cáo lãi đánh dấu "chưa đủ dữ liệu".

## 9. RET — Đổi trả

- **BR-RET-01** Tạo yêu cầu: đơn `delivered`, trong `returns.window_days` (mặc định 7) ngày từ `delivered_at` (owner được bỏ qua với lý do); lý do thuộc `damaged_in_transit | defective | wrong_item`; `damaged_in_transit` cần ≥ 1 ảnh/video bằng chứng; số lượng mỗi dòng ≤ `quantity − returned_quantity`.
- **BR-RET-02** Duyệt → chọn `refund` hoặc `replacement`:
  - `refund`: tạo refund (BR-PAY-07), số tiền ≤ giá trị dòng trả (theo `unit_price` sau phân bổ giảm giá theo tỷ lệ); `returned_quantity += q`. Hàng nhận về và còn bán được (`restockable`) → movement `return_restock`. Hàng vỡ không nhận về → không có movement; giá vốn của nó tự thành lỗ trong báo cáo (BR-FIN-02).
  - `replacement`: tạo shipment `purpose=replacement` và movement `replacement_out` (−q, cost = avg). Không có doanh thu; chi phí = giá vốn + ship.
- **BR-RET-03** `resolved` khi refund đã ghi hoặc shipment thay thế `delivered`. Từ chối cần `decision_note`.
- **BR-RET-04** Đơn bị trả toàn bộ và hoàn đủ tiền → không còn tính là khách đã mua (chỉ số khách tính lại), referral liên quan bị `rejected`.

## 10. CRM — Khách hàng

- **BR-CRM-01** Chuẩn hóa SĐT: bỏ khoảng trắng, `.`, `-`, `(`, `)`; chấp nhận `0xxxxxxxxx` (10 số, số thứ 2 ∈ {3,5,7,8,9}), `84xxxxxxxxx`, `+84xxxxxxxxx` → `+84xxxxxxxxx`. Khác → `invalid_phone`. SĐT bàn ghi ở ghi chú.
- **BR-CRM-02** SĐT duy nhất trong các hồ sơ chưa gộp. Tạo trùng → 409 `customer_phone_exists` kèm `existing_customer_id`.
- **BR-CRM-03** Import khách cũ: cột `full_name*, phone, zalo_name, customer_group, tags, note, had_purchase` (mặc định `true` theo xác nhận chủ shop), tối đa 2.000 dòng/lần. `origin=legacy_import`, `acquisition_source=legacy`. Trùng SĐT trong file → giữ dòng đầu; trùng hồ sơ có sẵn → bỏ qua (không ghi đè). Dòng thiếu cả phone lẫn zalo_name → lỗi. `note` → 1 interaction `kind=note`. Dry-run trả kết quả từng dòng; chạy thật validate lại.
- **BR-CRM-04** Sửa hồ sơ thường không đổi được `phone_e164`, `origin`, `acquisition_source`, `had_legacy_purchase`. Đổi các trường này qua `POST customers/{id}/correct-identity` (lý do bắt buộc; SĐT cần `customers.write`, các trường còn lại chỉ owner).
- **BR-CRM-05** Gộp hồ sơ (owner): giữ A, gộp B vào A. Chuyển đơn, interaction, địa chỉ, tags, interests, consents (mỗi cặp kênh+mục đích giữ bản `captured_at` mới nhất), recipients; mã giới thiệu giữ của A. Cả hai đều có tài khoản → từ chối. B: `merged_into_customer_id = A`, `phone_e164 = NULL`. Tính lại chỉ số A. Audit.
- **BR-CRM-06** Interaction: `occurred_at ≤ now`; `follow_up_at > occurred_at`; hoàn thành hẹn cần `follow_up_result`. Danh sách "Cần liên hệ hôm nay" = hẹn chưa xong có `follow_up_at` < cuối ngày hôm nay (giờ VN), sắp xếp quá hạn trước.
- **BR-CRM-07** `last_contacted_at` cập nhật khi thêm interaction (trừ `kind=note`) hoặc recipient chiến dịch được đánh dấu "Đã gửi".
- **BR-CRM-08** Sở thích từ đơn giao thành công: mỗi dòng cộng `score +1` cho `material`, `artisan`, `product_type`, `category` (danh mục chính) của sản phẩm, và `price_band` theo `unit_price` (`under_1m` < 1tr ≤ `1m_3m` < 3tr ≤ `3m_10m` < 10tr ≤ `over_10m`). Nhân viên thêm/xóa sở thích `manual`.
- **BR-CRM-09** **Consent marketing** (cần xác nhận lại với tư vấn pháp lý trước khi chạy chiến dịch):
  - Chiến dịch `new_arrival`, `promotion`, `reactivation` chỉ chọn được khách có consent `granted` đúng **kênh** và **mục đích** (`marketing_new_products` hoặc `marketing_promotions`).
  - Chiến dịch `care` (hỏi thăm, xin phép gửi thông tin hàng mới) được gửi cho khách `active` chưa có consent, **tối đa 1 lần/180 ngày/khách**. Khách đồng ý → nhân viên ghi consent (`source=staff`, `evidence` mô tả, ví dụ "Khách trả lời đồng ý qua Zalo 16/11/2026").
  - Checkout có ô tích (mặc định **không** tích) "Nhận thông tin sản phẩm mới qua Zalo/SMS" → consent `source=checkout`, evidence = mã đơn.
  - Khách từ chối/yêu cầu ngừng → consent `revoked` (hoặc `contact_status = do_not_contact` nếu ngừng mọi kênh).
- **BR-CRM-10** `do_not_contact`, `erased`, hồ sơ đã gộp → không bao giờ vào danh sách chiến dịch.
- **BR-CRM-11** Xóa dữ liệu (owner, FR-CRM-11): chặn nếu còn đơn `pending/confirmed/shipping`, đổi trả mở hoặc COD chưa đối soát. Thực hiện: `contact_status=erased`, `erased_at`; null `full_name→'[Đã xóa]'`, `phone_e164`, `email`, `zalo_name`, `note_summary`, `search_text=''`; xóa địa chỉ, tags, interests, consents, nội dung interaction (`content='[Đã xóa]'`); tài khoản `disabled`, `login_phone=NULL`, `email=NULL`, xóa `customer_logins`, `password_hash=NULL`, xóa giỏ hàng; đơn hàng: `contact_name/receiver_name='[Đã xóa]'`, `contact_phone/receiver_phone='+84000000000'`, `contact_email=NULL`, `address_line='[Đã xóa]'`, `customer_note=NULL`. Ghi `crm.data_requests` + audit.
- **BR-CRM-12** Job `DataRetention` xóa theo `retention.*` (03 §7).
- **BR-CRM-13** Xuất dữ liệu khách (owner): JSON gồm hồ sơ, địa chỉ, đơn, consent, interaction; audit `action=access`.
- **BR-CRM-14** `customers.search_text = f_unaccent(full_name ‖ zalo_name ‖ code ‖ chữ số SĐT dạng 0xxxxxxxxx ‖ email)`.

## 11. MKT — Marketing và giới thiệu

- **BR-MKT-01** Chọn người nhận chiến dịch: áp bộ lọc (FR-MKT-02) rồi loại: BR-CRM-10; không có kênh liên lạc phù hợp (Zalo cá nhân cần SĐT hoặc `zalo_name`); không thỏa consent (BR-CRM-09); đã nhận tin từ chiến dịch khác trong `marketing.min_days_between_messages` ngày (mặc định 14). Trả số bị loại theo từng lý do.
- **BR-MKT-02** Mỗi người nhận có 1 `tracking_link` (code 8 ký tự base62 ngẫu nhiên), `target_path = landing_path` của chiến dịch. Tin nhắn render từ template với biến `{ten}`, `{link}`, `{san_pham}` (tên các sản phẩm của chiến dịch). Link đầy đủ: `https://shop.<domain>/r/{code}`.
- **BR-MKT-03** "Đã gửi": `status=sent`, `sent_at`, `sent_by`; tạo interaction `kind=campaign_message`; BR-CRM-07. Bỏ qua: `skipped` + lý do.
- **BR-MKT-04** `GET /r/{code}`: link không tồn tại/hết hạn → 302 về `/`. Ngược lại: nếu User-Agent là bot (danh sách) → `is_bot=true`, không đặt cookie; nếu không: `click_count + 1`, ghi `link_clicks(visitor_id)`, đặt cookie `tp_attr = {link_code, clicked_at}` 30 ngày (ghi đè — last click), 302 tới `target_path`. Không ghi IP.
- **BR-MKT-05** Mã giới thiệu ở checkout: hợp lệ khi mã `is_active`; SĐT người mua **không** thuộc hồ sơ đã có đơn giao thành công, `had_legacy_purchase=false`, `origin≠legacy_import`; không trùng SĐT người giới thiệu; `merchandise ≥ referral.welcome_min_order_vnd`. Giảm `referral.welcome_discount_vnd` (≤ merchandise). Tạo `marketing.referrals(pending)`; khách mới tạo → `referred_by_customer_id`, `acquisition_source=referral`.
- **BR-MKT-06** Cấp mã giới thiệu (6 ký tự, `23456789ABCDEFGHJKMNPQRSTUVWXYZ`) khi khách có đơn giao thành công đầu tiên; nhân viên cấp tay được.
- **BR-MKT-07** Job `QualifyReferrals`: referral `pending` có đơn đầu `delivered` và `now ≥ delivered_at + referral.qualify_after_days`, không có refund/đổi trả mở → `qualified` → tạo voucher `referral_reward` gắn người giới thiệu theo `referral.reward_voucher` → `rewarded`; gửi email (nếu có) và tạo hẹn chăm sóc cho nhân viên "Báo khách nhận quà giới thiệu". Đơn đầu bị hủy/giao thất bại/hoàn toàn bộ → `rejected`.
- **BR-MKT-08** Mỗi người giới thiệu tối đa `referral.max_rewards_per_year` (mặc định 20) phần thưởng/năm.
- **BR-MKT-09** Phân bổ nguồn cho đơn web:
  - `tracking_link_id`/`campaign_id` từ cookie `tp_attr` nếu click ≤ 30 ngày.
  - `utm` từ cookie `tp_utm` (lưu `utm_source/medium/campaign` và `referrer_host` của lần vào đầu có tham số, 30 ngày).
  - `acquisition_source` của **khách mới** (thứ tự ưu tiên): có mã giới thiệu → `referral`; link theo dõi có kênh → kênh đó (`zalo_personal`, `zalo_oa`, `facebook`); `utm_medium ∈ {cpc, paid}` và `utm_source=google` → `google_ads`; `utm_source=facebook` → `facebook`; `referrer_host` là Google → `google_organic`; ngược lại `direct`. Khách cũ giữ nguyên `acquisition_source`.
- **BR-MKT-10** Chỉ số chiến dịch: số người nhận, đã gửi, đã click (người nhận có ≥ 1 click không phải bot), đơn gắn chiến dịch (đặt trong `[starts_at, ends_at + 14 ngày)`), đơn đã giao, doanh thu thuần, lãi gộp (chỉ `costs.read`), chi phí (expenses có `campaign_id`).

## 12. CNT — Nội dung và SEO

- **BR-CNT-01** Bài viết `in_review → published` cần `content.publish`; điều kiện: tiêu đề, excerpt 50–300 ký tự, nội dung ≥ 300 từ, ảnh bìa có ALT, `seo_title`, `seo_description`. `content.write` không có `publish` chỉ gửi duyệt được.
- **BR-CNT-02** HTML sinh ở server từ Tiptap JSON qua sanitizer whitelist (thẻ: p, h2–h4, ul, ol, li, strong, em, a[href rel], img[src alt width height] chỉ từ domain media, blockquote, table cơ bản, figure, figcaption). Storefront chỉ render `content_html`. Link ngoài thêm `rel="noopener nofollow"`.
- **BR-CNT-03** Trang chính sách (`policy_return`, `policy_shipping`, `policy_payment`, `policy_privacy`, `terms`) và `about` phải `published` trước khi `SITE_INDEXABLE=true` và trước go-live.
- **BR-CNT-04** sitemap: product `active` không noindex, category `visible`, collection `visible`, material/artisan/article/page `published`; `lastmod = updated_at`. `SITE_INDEXABLE=false` → robots `Disallow: /` và sitemap rỗng.
- **BR-CNT-05** Canonical mặc định = URL chính của trang; trang lọc/tìm kiếm/phân trang > 1 có `noindex,follow`.
- **BR-CNT-06** Redirect: middleware storefront tra `content.redirects` (cache 5 phút) **chỉ khi** trang trả 404; tăng `hit_count` bất đồng bộ.

## 13. FIN — Báo cáo tài chính

Kỳ báo cáo `[from, to)` theo ngày giờ Việt Nam. Chỉ người có `finance.read`.

- **BR-FIN-01** **Đơn ghi nhận** của kỳ: `delivered_at ∈ kỳ` và `is_backfilled = false` (trừ khi tích "gồm đơn lịch sử").
- **BR-FIN-02** Công thức lãi lỗ:
  ```
  R1  Doanh thu hàng           = Σ (subtotal − discount)           của đơn ghi nhận
  S   Phí ship thu khách       = Σ shipping_fee                    của đơn ghi nhận
  RF  Hoàn tiền                = Σ refunds.amount có refunded_at ∈ kỳ, của đơn đã từng delivered
  NR  Doanh thu thuần          = R1 + S − RF
  C1  Giá vốn hàng bán         = Σ quantity × unit_cost_vnd        dòng của đơn ghi nhận
  C2  Giá vốn hàng trả về kho  = Σ |quantity| × unit_cost_vnd      movement return_restock ∈ kỳ (ref_type='return')
  COGS                         = C1 − C2
  GP  Lãi gộp                  = NR − COGS
  F   Chi phí vận hành bán hàng = Σ shipments.shipping_cost + return_cost (shipment tạo ∈ kỳ)
                                + Σ cod_settlements.fee (settled_at ∈ kỳ)
                                + Σ cost movement replacement_out, damage, adjustment/stocktake âm − dương (∈ kỳ)
                                + expenses(packaging, shipping_other, payment_fee, damage_other) ∈ kỳ
  CM1 Lãi sau vận hành         = GP − F
  M   Marketing                = expenses(marketing) ∈ kỳ
  CM2 Lãi sau marketing        = CM1 − M
  X   Chi phí cố định & khác   = expenses(fixed, other) ∈ kỳ
  NP  Lãi ròng                 = CM2 − X
  ```
  Chiết khấu voucher/giới thiệu đã nằm trong `discount` nên không tính lại ở M.
- **BR-FIN-03** Mỗi báo cáo kèm **độ đầy đủ dữ liệu**: số dòng thiếu `unit_cost_vnd`, số shipment thiếu `shipping_cost_vnd`, số COD chưa đối soát, số đơn `shipping` quá 10 ngày. Có thiếu → hiện cảnh báo "Lãi chưa chính xác".
- **BR-FIN-04** Dòng tiền: vào = payments `confirmed` theo `received_at` (chuyển khoản, tiền mặt) + `cod_settlements.net_received` theo `settled_at`; ra = refunds theo `refunded_at`; COD đang treo = Σ payments `awaiting_settlement`.
- **BR-FIN-05** Theo kênh (`sales_channel`) và theo nguồn (`acquisition_source` trên đơn): số đơn, NR, COGS, GP; M theo `expenses.channel`; CM2 theo kênh. **Không** phân bổ chi phí cố định theo kênh. Hai bảng kênh và nguồn **không được cộng với nhau**.
- **BR-FIN-06** Khách: khách mới trong kỳ = khách có đơn ghi nhận đầu tiên với `is_first_purchase=true`; khách quay lại = có đơn ghi nhận và (đã có đơn delivered trước kỳ hoặc `had_legacy_purchase`); tỷ lệ mua lại = khách có ≥ 2 đơn delivered (tính `had_legacy_purchase` là 1) / khách có ≥ 1; AOV = R1 / số đơn ghi nhận.
- **BR-FIN-07** Sản phẩm: số lượng bán, NR theo dòng (phân bổ giảm giá đơn theo tỷ lệ giá trị dòng), GP; hàng tồn lâu = variant có `on_hand > 0` không có movement `sale` trong 90 ngày.
- **BR-FIN-08** Xuất doanh thu theo tháng/năm (Excel): mỗi đơn ghi nhận một dòng (mã đơn, ngày giao, tổng tiền, phí ship, giảm giá, hoàn tiền, phương thức thanh toán, số hóa đơn nếu có) + tổng tháng. Ghi chú trên file: "Căn cứ ghi nhận do kế toán xác nhận".

## 14. PLT — Nền tảng

- **BR-PLT-01** Audit theo 02 §5.4. Xem audit cần `audit.read`.
- **BR-PLT-02** Outbox: tối đa 10 lần thử, backoff `min(2^attempts phút, 6 giờ)`; `dead` → hiện ở Admin "Sự cố hệ thống" + Telegram.
- **BR-PLT-03** Thông báo (template tiếng Việt, lưu trong code):

| Template | Kênh | Khi | Nội dung không được chứa |
|---|---|---|---|
| `order_placed_customer` | email | đặt đơn | — |
| `order_placed_shop` | Telegram + email shop | đặt đơn | SĐT, địa chỉ (chỉ mã đơn, tổng tiền, phương thức, số món, link Admin) |
| `payment_received_customer` | email | đơn đủ tiền | — |
| `order_shipped_customer` | email | shipping | — |
| `order_cancelled_customer` | email | hủy (kể cả hết hạn) | — |
| `referral_reward_customer` | email | BR-MKT-07 | — |
| `daily_digest_shop` | Telegram | 08:00 | PII. Gồm: đơn chờ xác nhận, chờ thanh toán sắp hết hạn, COD chưa đối soát, hẹn chăm sóc hôm nay, tồn thấp, outbox dead |

- **BR-PLT-04** Email gửi qua SMTP provider; thất bại không làm hỏng giao dịch gốc (đi qua outbox).
