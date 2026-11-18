# 05 — Hợp đồng API

## 1. Quy ước chung

- Base path: `/api/v1`. Ba nhóm: `store/*` (công khai), `account/*` (khách đăng nhập), `admin/*` (nhân viên).
- JSON `camelCase`; tiền là số nguyên `...Vnd`; thời gian ISO 8601 UTC (`2026-11-16T07:00:00Z`); ngày thuần `yyyy-MM-dd` (giờ VN).
- OpenAPI sinh tự động tại `/openapi/v1.json` (chỉ bật ở Development và CI); TS client sinh vào `packages/api-client`.
- Phân trang: `?page=1&pageSize=20` (tối đa 100) → `{ items, page, pageSize, totalItems }`.
- Sắp xếp: `?sort=placedAt:desc`. Lọc: query string theo tên field.
- Concurrency: response trả `rowVersion` chuỗi mờ (nguồn xmin uint hoặc ConcurrencyStamp IAM, không SecurityStamp); client chỉ gửi lại, không phân tích; malformed → 400, stale → 409; PATCH/POST thay đổi aggregate gửi lại `rowVersion`; lệch → 409.
- Idempotency: header `Idempotency-Key: <uuid>` ở các endpoint ghi chú **[idem]**.
- CSRF: mọi method không phải GET/HEAD từ trình duyệt gửi header `X-CSRF-TOKEN` (lấy từ `GET .../auth/csrf`).
- Lỗi: `application/problem+json`
  ```json
  { "type": "https://teapremium/errors/out_of_stock", "title": "Sản phẩm không đủ hàng",
    "status": 409, "code": "out_of_stock", "detail": "...", "traceId": "...",
    "errors": { "items[0].quantity": ["..."] },  "data": { ... } }
  ```

| HTTP | `code` tiêu biểu |
|---|---|
| 400 | `validation_failed` |
| 401 | `unauthenticated` |
| 403 | `forbidden`, `totp_required` |
| 404 | `not_found` |
| 409 | `concurrency_conflict`, `idempotency_key_reused`, `invalid_status_transition`, `out_of_stock`, `price_changed`, `customer_phone_exists`, `account_exists`, `unique_piece_violation`, `cart_has_unavailable_items` |
| 422 | `publish_requirements_not_met`, `voucher_*`, `referral_*`, `discount_requires_owner`, `price_below_allowed`, `too_many_pending_orders`, `return_window_expired` |
| 429 | `rate_limited` |

## 2. Store API (không cần đăng nhập)

| Method | Path | Mô tả |
|---|---|---|
| GET | `store/settings` | Thông tin website công khai, phương thức thanh toán bật, phí ship, ngưỡng freeship |
| GET | `store/navigation` | Cây danh mục visible, collections visible |
| GET | `store/products` | Danh sách. Query: `category`, `collection`, `type`, `material`, `artisan`, `priceMin`, `priceMax`, `attr.<code>=<optionCode\|min-max>`, `inStock=true`, `q`, `sort=newest\|price_asc\|price_desc`, `page`. Trả thêm `facets` (đếm theo filter) |
| GET | `store/products/{slug}` | Chi tiết: thông tin, thuộc tính hiển thị, variants (giá hiệu lực, giá gốc, `availability`: `in_stock`/`out_of_stock`/`sold`, **không** trả số tồn chính xác nếu > 5 → trả `"lowStock": true` khi ≤ 3), ảnh, SEO, sản phẩm liên quan |
| GET | `store/categories/{slug}`, `store/collections/{slug}` | Thông tin + SEO (sản phẩm lấy qua `store/products`) |
| GET | `store/materials`, `store/materials/{slug}` | Chất liệu |
| GET | `store/artisans`, `store/artisans/{slug}` | Nghệ nhân |
| GET | `store/articles`, `store/articles/{slug}` | Bài viết; `?category=`, `?relatedTo=product:{id}` |
| GET | `store/pages/{slug}` | Trang tĩnh |
| GET | `store/redirects?path=` | Tra redirect (storefront gọi khi 404) |
| GET | `store/provinces`, `store/provinces/{code}/wards` | Địa giới |
| GET | `store/search?q=` | Gợi ý nhanh: ≤ 8 sản phẩm, ≤ 3 bài viết |
| GET | `store/sitemap-data?type=` | Dữ liệu cho sitemap (slug, lastmod, ảnh) theo loại (BR-SEO-77) |
| GET | `store/landing-pages/{slug}` | Trang chủ đề + cờ `indexable` (BR-SEO-41); sản phẩm lấy qua `store/products?landing={slug}` |
| GET | `store/authors/{slug}` | Tác giả + bài viết |
| GET | `store/products/{slug}/reviews?page=` | Đánh giá đã duyệt + `aggregate {count, average}` |
| GET | `store/reviews/request/{token}` · POST `store/reviews` `{token, rating, content, assetIds[]}` | Form đánh giá qua link có token (BR-SEO-90) |
| GET | `store/feeds/google-merchant` | Dữ liệu feed (app store render thành `/feeds/google-merchant.xml`) |
| POST | `store/not-found` | Ghi log 404 (chỉ từ server store, header secret nội bộ) |
| POST | `store/vitals` | Ghi Web Vitals (rate limit, không PII) |
| GET | `/r/{code}` | (Route của app store, gọi `store/links/{code}/click`) BR-MKT-04 |
| POST | `store/links/{code}/click` | Body `{visitorId, userAgent, referrerHost}` → `{targetPath, setAttribution}`; chỉ gọi từ server store (header secret nội bộ) |

### Giỏ hàng và checkout (cookie `tp_cart`, CSRF)
| Method | Path | Mô tả |
|---|---|---|
| GET | `store/cart` | Giỏ đã tính lại (BR-CRT-03) |
| PUT | `store/cart/items/{variantId}` | Body `{quantity}`; 0 = xóa |
| DELETE | `store/cart` | Xóa giỏ |
| POST | `store/checkout/quote` | Body `{discountCode?, provinceCode}` → `{lines, merchandiseVnd, discountVnd, discountLabel, shippingFeeVnd, totalVnd, warnings[]}` |
| POST | `store/checkout/orders` **[idem]** | Body: `{contact:{name, phone, email?}, shipping:{receiverName, receiverPhone, provinceCode, wardCode, addressLine}, saveAddress?, customerNote?, paymentMethod, discountCode?, acceptTerms, marketingConsent, expectedTotalVnd, website /*honeypot, phải rỗng*/}` → 201 `{orderCode, status, totalVnd, paymentMethod, paymentInstructions?: {bankName, accountNumber, accountName, amountVnd, transferContent, vietQrPayload, dueAt}}` |
| POST | `store/orders/lookup` | Body `{code, phone}` → đơn đã che (BR-IAM-05) |
| POST | `store/orders/lookup/cancel` | Body `{code, phone, reason?}` — chỉ khi `pending` |

## 3. Account API (cookie `CustomerCookie`)

| Method | Path | Mô tả |
|---|---|---|
| GET | `account/auth/csrf` | Token CSRF |
| POST | `account/auth/register` | `{phone, fullName, password, email?}` (BR-IAM-03) |
| POST | `account/auth/login` | `{phone, password}` |
| GET | `account/auth/google/start` → callback | OIDC; lần đầu chuyển tới `account/auth/complete-profile` |
| POST | `account/auth/complete-profile` | `{phone, fullName}` |
| POST | `account/auth/logout` | |
| POST | `account/auth/forgot-password` / `reset-password` | Qua email đã xác minh |
| GET/PATCH | `account/profile` | Tên, email (đổi email → xác minh lại); không đổi SĐT ở giai đoạn 2 |
| GET/POST/PATCH/DELETE | `account/addresses[/{id}]` | Sổ địa chỉ (BR-IAM-04) |
| GET | `account/orders`, `account/orders/{code}` | Lịch sử (BR-IAM-04) |
| POST | `account/orders/{code}/cancel` | Khi `pending` |
| GET | `account/referral` | Mã giới thiệu, số người đã giới thiệu, voucher đang có |
| GET/PUT | `account/consents` | Xem/bật/tắt nhận tin theo kênh |

## 4. Admin API (cookie `AdminCookie`, CSRF, permission)

Cột **Quyền** là permission tối thiểu. Mọi response có giá vốn/lãi chỉ trả các field đó khi có `costs.read`.

### 4.1 Auth và nhân viên
| Method | Path | Quyền |
|---|---|---|
| GET | `admin/auth/csrf` | — |
| POST | `admin/auth/login` `{email, password, totpCode?}` | — |
| POST | `admin/auth/logout` · GET `admin/auth/me` (user, roles, permissions; không có ownerMfaCoverageOk trong P0) | đăng nhập |
| POST | `admin/auth/totp/setup` · `admin/auth/totp/verify` `{code}` | đăng nhập |
| POST | `admin/auth/change-password` `{currentPassword,newPassword}` | đăng nhập |
| GET/POST/PATCH | `admin/staff[/{id}]` (tạo, khóa, đổi vai trò, reset mật khẩu); POST `{email,displayName,initialPassword,roles}` | `staff.manage` |
| GET | `admin/audit?entityType=&entityId=&actorId=&from=&to=` | `audit.read` |

Tạo thêm owner (không bắt buộc) dùng POST `admin/staff` với `roles=["owner"]` bởi owner đã hoàn tất onboarding, cookie `AdminCookie` + CSRF; owner mới có phiên riêng đổi mật khẩu và verify TOTP. Không giả định có Staff UI. Không có endpoint HTTP reset MFA; CLI `reset-staff-mfa` và cờ coverage **không nằm trong P0** theo B01/BR-IAM-07/08. API hiện hữu về staff, login, password, TOTP và phân quyền giữ nguyên.

### 4.2 Catalog
| Method | Path | Quyền |
|---|---|---|
| GET/POST/PATCH | `admin/product-types[/{id}]`, `.../{id}/attributes[/{attrId}]`, `.../attributes/{attrId}/options[/{optId}]` | read: `catalog.read`; ghi: `catalog.write` |
| GET/POST/PATCH | `admin/categories[/{id}]`, `admin/collections[/{id}]`, `.../{id}/products` (PUT danh sách + thứ tự) | như trên |
| GET/POST/PATCH | `admin/materials[/{id}]`, `admin/artisans[/{id}]`; POST `.../{id}/publish` | `catalog.write`, publish: `catalog.publish` |
| GET | `admin/products?q=&status=&type=&category=&material=&artisan=&stock=low\|out\|in` | `catalog.read` |
| POST | `admin/products` `{productTypeId, name, ...}` → tạo draft + 1 variant mặc định | `catalog.write` |
| GET/PATCH | `admin/products/{id}` (thông tin, thuộc tính, danh mục, SEO, quan hệ) | `catalog.read`/`write` |
| POST | `admin/products/{id}/publish` → 200 kèm warnings[] (thin_description_noindex) hoặc 422 publish_requirements_not_met kèm **toàn bộ** lỗi BR-CAT-04; metadata không nới | `catalog.publish` |
| POST | `admin/products/{id}/archive` `{redirectTo?}` · `.../restore-draft` | `catalog.publish` |
| GET/POST/PATCH | `admin/products/{id}/variants[/{variantId}]` (giá chỉ sửa được khi có `pricing.write`) | `catalog.write` |
| POST/PATCH/DELETE | `admin/products/{id}/media[/{mediaId}]`, POST `.../media/{mediaId}/cover`, PUT `.../media/order` | `catalog.write` |
| POST | `admin/products/import?dryRun=true\|false` (multipart xlsx/csv) | `catalog.write` |
| GET/POST/DELETE | `admin/variants/{id}/sale-prices[/{id}]` | `pricing.write` |
| POST | `admin/media/upload-intents` `{fileName, mimeType, byteSize, purpose, slugHint?}` → `{uploadId, putUrl, headers, expiresAt}` | `catalog.write` hoặc `content.write` hoặc `returns.write` (purpose=evidence) |
| POST | `admin/media/upload-intents/{id}/complete` → asset | như trên |
| GET | `admin/media?q=&purpose=` | `catalog.read` |

Upload/complete tuân 02 §5.10/schema: ảnh ≤15 MiB, evidence mp4/quicktime ≤50 MiB, kiểm object thực, private/presigned GET có kiểm quyền. Product/variant write/publish/kích hoạt tuân BR-CAT-14/15; inactive/giá NULL không đưa storefront/cart/order/feed/JSON-LD, không tự đổi SKU khi sửa type/material/thêm variant.

### 4.3 Kho
| Method | Path | Quyền |
|---|---|---|
| GET | `admin/inventory?q=&status=low\|out\|in&type=` (DTO theo quyền bên dưới) | `inventory.read` |
| GET | `admin/inventory/{variantId}/movements?type=&from=&to=` | `inventory.read` + `inventory.write`; giá vốn cần `costs.read` |
| POST | `admin/inventory/opening-import?dryRun=` (file `sku, quantity, unit_cost_vnd`) | `inventory.approve` |
| GET/POST/PATCH | `admin/receipts[/{id}]`; POST `.../{id}/post` **[idem]**; POST `.../{id}/cancel` (chỉ draft) | `inventory.write` |
| POST | `admin/inventory/adjustments` `{variantId, quantityDelta, type: adjustment\|damage, reason}` | `inventory.write` (tăng: `inventory.approve`) |
| GET/POST | `admin/stocktakes[/{id}]`; PUT `.../{id}/lines/{variantId}` `{countedQty}`; POST `.../{id}/submit`, `.../{id}/approve`, `.../{id}/cancel` | `inventory.write`; approve: `inventory.approve` |

Người đọc hạn chế (inventory.read không inventory.write) chỉ thấy availability in_stock/out_of_stock, không low/onHand/reserved/available/giá vốn/movements. Filter/response lồng nhau/export không được tiết lộ mức low hoặc số lượng gián tiếp. Inventory.write được số lượng/movements, giá vốn vẫn cần costs.read. Kiểm JSON/403 theo permission, không chỉ ẩn UI/không hardcode role.

### 4.4 Đơn hàng, thanh toán, giao hàng, đổi trả
| Method | Path | Quyền |
|---|---|---|
| GET | `admin/orders?status=&paymentStatus=&channel=&origin=&from=&to=&q=&customerId=&flag=needs_refund\|overpaid\|expiring` | `orders.read` |
| GET | `admin/orders/{id}` (dòng, tiền, payments, refunds, shipments, returns, history) | `orders.read` |
| POST | `admin/orders` **[idem]** `{customerId \| newCustomer{fullName, phone}, salesChannel, paymentMethod, lines[{variantId, quantity, unitPriceVnd?, priceOverrideReason?}], discountVnd?, discountNote?, shippingFeeVnd?, shipping{...}, internalNote?, confirmNow, isBackfilled?, deliveredAt?}` | `orders.write` (+ `orders.override_price` nếu có giá tay; backfill: owner) |
| PATCH | `admin/orders/{id}` (BR-ORD-08) `{rowVersion, lines?, shipping?, discountVnd?, shippingFeeVnd?, internalNote?}` | `orders.write` |
| POST | `admin/orders/{id}/confirm` `{rowVersion, note?}` | `orders.write` |
| POST | `admin/orders/{id}/cancel` `{rowVersion, reason}` | `orders.write` |
| POST | `admin/orders/{id}/extend-payment` `{rowVersion, hours ≤ 72, reason}` | `orders.write` |
| POST | `admin/orders/{id}/shipments` `{carrierName, trackingCode?, shippingCostVnd?, note?}` → đơn `shipping` | `orders.write` |
| PATCH | `admin/shipments/{id}` `{trackingCode?, shippingCostVnd?, returnCostVnd?}` | `orders.write` |
| POST | `admin/shipments/{id}/events` `{status: picked_up\|in_transit\|delivered\|failed\|returned, occurredAt, note?}` | `orders.write` |
| POST | `admin/orders/{id}/reorder` (BR-ORD-03) | `orders.write` |
| GET | `admin/orders/{id}/packing-slip` (HTML) | `orders.read` |
| POST | `admin/orders/{id}/payments` **[idem]** `{method: bank_transfer\|cash, amountVnd, receivedAt, bankReference?, note?}` | `payments.write` |
| POST | `admin/payments/{id}/void` `{reason}` | owner |
| GET | `admin/payments/awaiting?type=bank_transfer\|cod&carrier=` | `payments.write` |
| POST | `admin/cod-settlements` **[idem]** `{carrierName, reference?, settledAt, paymentIds[], feeVnd}` | `payments.write` |
| POST | `admin/orders/{id}/refunds` **[idem]** `{amountVnd, method, bankReference?, reason, returnRequestId?, refundedAt}` | `refunds.write` |
| GET/POST | `admin/returns[/{id}]` `{orderId, reason, description, lines[{orderLineId, quantity}], evidenceAssetIds[]}` | `returns.write` |
| POST | `admin/returns/{id}/approve` `{resolution, lines[{orderLineId, restockable}], note?}` · `.../reject` `{note}` · `.../receive-goods` · `.../resolve` | `returns.write` |

### 4.5 Khách hàng
| Method | Path | Quyền |
|---|---|---|
| GET | `admin/customers?q=&group=&tag=&interest=<type>:<id>&lastDeliveredBefore=&lastDeliveredAfter=&minLtv=&contactStatus=&hasConsent=<channel>:<purpose>` | `customers.read` |
| POST | `admin/customers` | `customers.write` |
| GET/PATCH | `admin/customers/{id}` (360°: thống kê, sở thích, consent, tags, địa chỉ) | `customers.read`/`write` |
| POST | `admin/customers/{id}/correct-identity` `{phone?, origin?, acquisitionSource?, hadLegacyPurchase?, reason}` | BR-CRM-04 |
| GET | `admin/customers/{id}/orders`, `.../interactions`, `.../campaigns` | `customers.read` |
| POST/PATCH | `admin/customers/{id}/interactions[/{iid}]`; POST `.../interactions/{iid}/complete-follow-up` `{result}` | `customers.write` |
| GET | `admin/follow-ups?due=today\|overdue\|week` | `customers.read` |
| GET/POST/PATCH/DELETE | `admin/customers/{id}/addresses[/{aid}]` | `customers.write` |
| PUT | `admin/customers/{id}/tags` `{tagIds[]}` · GET/POST/PATCH `admin/tags` | `customers.write` |
| POST/DELETE | `admin/customers/{id}/interests[/{iid}]` (manual) | `customers.write` |
| PUT | `admin/customers/{id}/consents` `{channel, purpose, status, evidence}` | `customers.write` |
| POST | `admin/customers/{id}/verify-account-phone` (BR-IAM-04 b) | `customers.write` |
| POST | `admin/customers/import?dryRun=` | `customers.write` |
| POST | `admin/customers/{id}/merge` `{mergeCustomerId}` | `customers.merge` |
| POST | `admin/customers/{id}/erase` `{reason, requestChannel}` | `customers.erase` |
| GET | `admin/customers/{id}/export` | `customers.export` |

### 4.6 Marketing
| Method | Path | Quyền |
|---|---|---|
| GET/POST/PATCH | `admin/campaigns[/{id}]`, PUT `.../{id}/products` | `marketing.write` |
| POST | `admin/campaigns/{id}/recipients/preview` `{filters}` → `{eligibleCount, excluded:{reason: count}, sample[]}` | `marketing.write` |
| POST | `admin/campaigns/{id}/recipients` `{filters}` → tạo recipients + link (BR-MKT-01/02) | `marketing.write` |
| GET | `admin/campaigns/{id}/recipients?status=` (kèm `renderedMessage`, `link`) | `marketing.write` |
| POST | `admin/campaigns/{id}/recipients/{rid}/mark-sent` · `.../skip` `{reason}` | `marketing.write` |
| GET | `admin/campaigns/{id}/metrics` (BR-MKT-10) | `marketing.write` (+ `costs.read` cho lãi) |
| GET/POST | `admin/tracking-links` (link chung cho bài đăng/QR in) | `marketing.write` |
| GET | `admin/referrals?status=` · POST `admin/customers/{id}/referral-code` | `marketing.write` |
| GET/POST/PATCH | `admin/vouchers[/{id}]` | owner (`pricing.write`) |

### 4.7 Nội dung và cấu hình
| Method | Path | Quyền |
|---|---|---|
| GET/POST/PATCH | `admin/articles[/{id}]`; POST `.../{id}/submit-review`, `.../publish`, `.../archive`; GET `.../preview` | `content.write` / `content.publish` |
| GET/POST/PATCH | `admin/article-categories[/{id}]`, `admin/pages[/{id}]` (+ publish) | `content.write` / `content.publish` |
| GET/POST/DELETE | `admin/redirects[/{sourcePath}]` | `content.write` |
| GET/POST/PATCH | `admin/landing-pages[/{id}]` + POST `.../publish`, GET `.../{id}/preview-products` | `content.write` / `content.publish` |
| GET/POST/PATCH | `admin/authors[/{id}]` + publish | `content.write` / `content.publish` |
| GET | `admin/reviews?status=` · POST `admin/reviews/{id}/approve` · `.../reject {reason}` | `content.write` |
| POST | `admin/order-lines/{id}/review-link` → link đánh giá để gửi tay qua Zalo | `orders.write` |
| GET | `admin/seo/audit?level=error\|warning&type=` · POST `admin/seo/audit/run` | `catalog.read` hoặc `content.write` |
| GET | `admin/seo/page-check?type=&id=` (Điểm SEO một trang, BR-SEO-61) | như trên |
| GET | `admin/seo/not-found?resolved=false` · POST `.../{path}/resolve` | `content.write` |
| GET/PUT | `admin/settings/{group}` (`site`, `shipping`, `payment`, `orders`, `referral`, `notify`, `retention`, `marketing`) | `settings.write` (GET `site`: `content.write`) |

### 4.8 Tài chính, báo cáo, hệ thống
| Method | Path | Quyền |
|---|---|---|
| GET/POST/PATCH/DELETE | `admin/expenses[/{id}]?from=&to=&category=` (xóa chỉ trong 30 ngày, có audit) | `finance.write` |
| GET | `admin/reports/pnl?from=&to=&includeBackfilled=` | `finance.read` |
| GET | `admin/reports/channels?from=&to=&by=sales_channel\|acquisition_source` | `finance.read` |
| GET | `admin/reports/cashflow?from=&to=` | `finance.read` |
| GET | `admin/reports/products?from=&to=&groupBy=product\|type\|material\|artisan` · `admin/reports/slow-stock` | `finance.read` |
| GET | `admin/reports/customers?from=&to=` | `finance.read` |
| GET | `admin/reports/revenue-export?year=&month=` (xlsx) | `finance.read` |
| GET | `admin/dashboard` (FR-FIN-08; field tiền chỉ trả khi `finance.read`) | đăng nhập |
| GET | `admin/system/outbox?status=dead` · POST `.../{id}/retry` | owner |

## 5. Nội bộ

| Method | Path | Mô tả |
|---|---|---|
| GET | `/health/live`, `/health/ready` | Health |
| POST | `store:3000/api/revalidate` | Gọi từ API (header `X-Revalidate-Secret`), body `{tags[]}` |
| POST | `api/v1/webhooks/{provider}` | Giai đoạn 5 (SePay, GHN, GHTK, Zalo) — lưu `inbound_webhooks` trước, xử lý sau |
