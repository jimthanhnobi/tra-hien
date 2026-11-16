# 07 — Lộ trình, task và nghiệm thu

Nguyên tắc: **làm xong trọn một giai đoạn (có test, chạy được) rồi mới sang giai đoạn sau.** Mỗi task ≈ 1 PR, giao cho AI theo `08-chatgpt-playbook.md`.
Kích thước: S ≤ 0,5 ngày · M ≤ 2 ngày · L ≤ 4 ngày (người + AI). Ưu tiên theo yêu cầu chủ shop: **API + Admin + Kho trước, storefront bản base.**

```
P0 Nền móng ─▶ P1 Catalog + Kho ─▶ P2 CRM + Đơn nội bộ (dùng thật cho đơn Zalo) ─▶ P3 Website bán hàng (go-live công khai)
                                                                                    ─▶ P4 Lãi lỗ ─▶ P5 Khai thác khách cũ ─▶ P6 Tích hợp + UX
```

---

## P0 — Nền móng

| ID | Task | Size | Phụ thuộc |
|---|---|---|---|
| P0-01 | Tạo repo theo 02 §2: solution .NET 10 (4 project + 3 test project), `apps/store`, `apps/admin`, `packages/api-client`, pnpm workspace, `global.json`, `Directory.Packages.props` (central package management), `.editorconfig`, analyzers bật cảnh báo = lỗi | M | — |
| P0-02 | `infra/compose.dev.yml` (postgres 17, api, store, admin), `.env.example`, README chạy local | S | P0-01 |
| P0-03 | Cross-cutting backend: `IClock`, `BusinessCalendar`, `Money`, `Result<T>`/`Error`, `ICommandHandler/IQueryHandler` + decorator validation/transaction, ProblemDetails mapping, correlation id middleware, log JSON che PII | M | P0-01 |
| P0-04 | `AppDbContext` nhiều schema, quy ước đặt tên snake_case + tên constraint tường minh, `xmin` concurrency, migration đầu tạo schema `platform` (settings, provinces, wards, audit_logs, outbox, idempotency, inbound_webhooks, notification_logs, data_protection_keys) + extension + `f_unaccent` | M | P0-03 |
| P0-05 | Audit interceptor (02 §5.4) + attribute `[Audited]` + danh sách field PII | M | P0-04 |
| P0-06 | Outbox + dispatcher + khung `PeriodicJob` có advisory lock | M | P0-04 |
| P0-07 | Idempotency middleware/filter (02 §5.3) | S | P0-04 |
| P0-08 | IAM nhân viên: Identity schema `iam`, cookie `AdminCookie`, CSRF, login/logout/me, đổi mật khẩu, TOTP, khóa tài khoản, rate limit, role+permission seed (02 §7), quản lý nhân viên, lệnh CLI `bootstrap-owner` | L | P0-04 |
| P0-09 | Settings module (`platform.settings` + API `admin/settings/{group}`), lệnh CLI `import-provinces <csv>` | S | P0-04 |
| P0-10 | ArchitectureTests (02 §3) + Testcontainers fixture + test migration từ rỗng | S | P0-04 |
| P0-11 | CI `ci.yml` (06 §7 bước 1–4) | S | P0-10 |
| P0-12 | Admin app khung: layout, đăng nhập (+TOTP), menu theo permission, trang 403/404, TanStack Query + client sinh từ OpenAPI, form chuẩn (RHF + Zod), bảng chuẩn (TanStack Table, phân trang server) | M | P0-08 |

**DoD P0:** `docker compose -f infra/compose.dev.yml up` chạy được; owner đăng nhập admin bằng TOTP; CI xanh.

**Nghiệm thu**
- AC-P0-01: Sai mật khẩu 5 lần → tài khoản bị khóa 15 phút (BR-IAM-01).
- AC-P0-02: Owner chưa cài TOTP ở Production chỉ gọi được `auth/*` (BR-IAM-02).
- AC-P0-03: Request POST không có CSRF → 400/403.
- AC-P0-04: Sửa 1 setting → có dòng audit với giá trị trước/sau.
- AC-P0-05: Test kiến trúc fail nếu Application tham chiếu EF Core hoặc gọi `DateTime.Now`.

---

## P1 — Catalog, Media, Kho

| ID | Task | Size | Phụ thuộc |
|---|---|---|---|
| P1-01 | Migration schema `media`, `catalog`, `inventory`, `pricing.sale_prices` theo `schema.sql` | M | P0 |
| P1-02 | Media: R2 presigned upload, complete, thư viện ảnh; bucket private cho evidence | M | P1-01 |
| P1-03 | Product types, thuộc tính, options (CRUD + validate kiểu) + lệnh `seed-catalog-reference` (03 §6) | M | P1-01 |
| P1-04 | Categories (cây ≤ 3 cấp), collections, materials, artisans (CRUD, publish, slug + redirect BR-CAT-08) | M | P1-01 |
| P1-05 | Products + variants: CRUD, giá trị thuộc tính, ảnh, SEO, quan hệ; publish BR-CAT-04; archive; `search_text` BR-CAT-10 | L | P1-03, P1-04 |
| P1-06 | Giá KM (sale_prices) + `PricingCalculator` BR-PRC-01/02 | S | P1-05 |
| P1-07 | Import sản phẩm Excel/CSV dry-run (BR-CAT-13) | M | P1-05 |
| P1-08 | Kho: `stock_levels`, movements, giá vốn bình quân, tồn đầu kỳ import (BR-INV-01/05/06/09) | L | P1-05 |
| P1-09 | Phiếu nhập kho, điều chỉnh, kiểm kê (BR-INV-07/08/10) | M | P1-08 |
| P1-10 | Cảnh báo tồn thấp + job đối soát kho đêm (BR-INV-11/13) | S | P1-08 |
| P1-11 | Admin UI: product types, danh mục, materials, artisans, collections | M | P1-03, P1-04 |
| P1-12 | Admin UI: danh sách + form sản phẩm (tab Thông tin / Thuộc tính / Biến thể & giá / Ảnh / SEO), checklist publish hiển thị lỗi BR-CAT-04 | L | P1-05 |
| P1-13 | Admin UI kho: tồn kho, sổ kho, phiếu nhập, điều chỉnh, kiểm kê (dùng tốt trên điện thoại) | M | P1-09 |
| P1-14 | Store API catalog (`store/products`, facets, chi tiết, navigation, materials, artisans) + test không lộ giá vốn/tồn chính xác | M | P1-05, P1-08 |

**DoD P1:** Nhập được toàn bộ catalog thật (ảnh thật, thông số, nghệ nhân, chất đất), tồn đầu kỳ và giá vốn từ bảng giá nhập; tồn khớp kiểm kê.

**Nghiệm thu**
- AC-P1-01: Publish sản phẩm thiếu ALT ảnh bìa và thiếu dung tích (thuộc tính bắt buộc) → 422 liệt kê **cả hai** lỗi.
- AC-P1-02: Ấm 2 biến thể 180ml/245ml cùng giá trị trục → không publish được (BR-CAT-04.6).
- AC-P1-03: Đổi slug sản phẩm đã publish 2 lần A→B→C → redirect A→C và B→C, không có A→B.
- AC-P1-04: Nhập kho 2 lần (10 cái × 100.000, 10 cái × 130.000) → `avg_cost = 115.000`; sổ kho có 2 dòng, Σ = on_hand.
- AC-P1-05: Variant độc bản đang có 1 → phiếu nhập thêm 1 bị từ chối `unique_piece_violation`.
- AC-P1-06: Tài khoản `content` gọi API sản phẩm/kho không nhận được field `avgCostVnd` (kiểm JSON, không chỉ UI).
- AC-P1-07: Tìm "am tu sa chu ban" ra ấm tử sa của nghệ nhân Chu Bân.
- AC-P1-08: Kiểm kê: đếm 5 khi hệ thống 6; trong lúc chờ duyệt bán 1 (on_hand 5) → duyệt xong on_hand = 4.

---

## P2 — CRM + Đơn hàng nội bộ (dùng ngay cho đơn Zalo/điện thoại)

| ID | Task | Size | Phụ thuộc |
|---|---|---|---|
| P2-01 | Migration `crm` (trừ accounts/logins chưa dùng vẫn tạo), `sales`, `pricing.vouchers/redemptions`, `finance.expenses` | M | P1 |
| P2-02 | Customers: CRUD, chuẩn hóa SĐT, tìm không dấu, tags, địa chỉ, correct-identity, 360° (BR-CRM-01..04, 14) | L | P2-01 |
| P2-03 | Import khách cũ dry-run (BR-CRM-03) | M | P2-02 |
| P2-04 | Interactions + hẹn chăm sóc + "Cần liên hệ hôm nay" (BR-CRM-06/07) | M | P2-02 |
| P2-05 | Order aggregate + state machine + `CheckoutCalculator` dùng chung (BR-ORD, BR-CRT-05) + giữ/nhả/tiêu thụ hàng (BR-INV-03/04) + test đồng thời NFR-02 | L | P2-01, P1-08 |
| P2-06 | Tạo/sửa/hủy đơn Admin (BR-ORD-04/08, BR-PRC-06/07), đơn backfill (BR-ORD-09) | M | P2-05 |
| P2-07 | Payments: ghi nhận chuyển khoản/tiền mặt, `OrderPaymentStatusCalculator`, void, refund (BR-PAY-01..03, 06, 07) | M | P2-05 |
| P2-08 | Shipments thủ công + sự kiện + delivered/failed + restock (BR-SHP-02..05, BR-ORD-07) | M | P2-05 |
| P2-09 | Đối soát COD (BR-PAY-05) | S | P2-07, P2-08 |
| P2-10 | Đổi trả (BR-RET-01..04) | M | P2-07, P2-08 |
| P2-11 | Sở thích khách từ đơn (BR-CRM-08), chỉ số khách + job đối soát | S | P2-08 |
| P2-12 | Consent (admin), merge, erase, export (BR-CRM-05, 09, 11, 13) | M | P2-02 |
| P2-13 | Notifications: Telegram + email shop, template, digest 08:00 (BR-PLT-03) | S | P0-06 |
| P2-14 | Admin UI khách hàng: danh sách lọc, 360°, import, hẹn chăm sóc | L | P2-02..04 |
| P2-15 | Admin UI đơn hàng: danh sách lọc nhanh theo trạng thái, **tạo đơn nhanh trên điện thoại** (tìm khách theo SĐT/tên, tạo nhanh, thêm sản phẩm bằng tìm kiếm), chi tiết, thanh toán, vận đơn, in phiếu, đổi trả, đối soát COD | L | P2-05..10 |
| P2-16 | Dashboard (FR-FIN-08) | S | P2-15 |

**DoD P2:** Nhân viên xử lý **mọi đơn Zalo/điện thoại hằng ngày trong hệ thống**; tồn kho trừ đúng; khách cũ đã import.

**Nghiệm thu**
- AC-P2-01: 20 request đồng thời tạo đơn cho chiếc ấm độc bản cuối → đúng 1 đơn thành công, 19 lỗi `out_of_stock`, `reserved = 1`.
- AC-P2-02: Hủy đơn `confirmed` → `reserved` giảm, sổ kho không có dòng mới (chưa xuất kho).
- AC-P2-03: Chuyển `shipping` → `on_hand` giảm, movement `sale` có `unit_cost`, `order_lines.unit_cost_vnd` được chốt.
- AC-P2-04: Ghi 2 lần thanh toán cùng `Idempotency-Key` → 1 payment. Cùng key khác số tiền → 409.
- AC-P2-05: Hai nhân viên sửa cùng đơn với cùng `rowVersion` → người thứ hai nhận 409.
- AC-P2-06: Nhân viên `sales` giảm giá 15% → 422 `discount_requires_owner`; owner thì được.
- AC-P2-07: Đơn COD giao thành công → payment `awaiting_settlement`; đối soát 3 đơn với phí 15.000 → `net = gross − 15.000`, 3 payment `confirmed`, đơn `paid`.
- AC-P2-08: Import file 500 dòng có 3 SĐT trùng nhau trong file, 2 SĐT đã có → kết quả dòng đúng lý do; hồ sơ có sẵn không bị đổi.
- AC-P2-09: Tài khoản `content` gọi `admin/customers` → 403.
- AC-P2-10: Xóa dữ liệu khách có đơn đã giao → hồ sơ và đơn bị ẩn danh, báo cáo doanh thu tổng không đổi.

---

## P3 — Website bán hàng (go-live công khai)

| ID | Task | Size | Phụ thuộc |
|---|---|---|---|
| P3-01 | Giỏ hàng (BR-CRT-01..04) | M | P2 |
| P3-02 | Checkout quote + đặt đơn web (BR-CRT-05..09), VietQR (02 §5.7), mã giới thiệu để sẵn (validate trả `referral_not_available` đến P5) | L | P3-01 |
| P3-03 | Job tự hủy đơn quá hạn + gia hạn (BR-ORD-05/06), tự xác nhận khi đủ tiền (BR-PAY-03) | S | P3-02 |
| P3-04 | Tra cứu đơn + khách hủy đơn pending (BR-IAM-05) | S | P3-02 |
| P3-05 | Tài khoản khách: đăng ký/đăng nhập SĐT, Google, quên mật khẩu qua email, sổ địa chỉ, lịch sử đơn, mở khóa lịch sử (BR-IAM-03..06) | L | P3-02 |
| P3-06 | Email khách (đặt đơn, đã nhận tiền, đang giao, hủy) | S | P2-13 |
| P3-07 | Content: articles, pages, redirects, sanitizer (BR-CNT-01..03, 06) + Admin UI editor Tiptap | L | P1 |
| P3-08 | Store app base: layout, trang chủ (bộ sưu tập, hàng mới), danh mục + bộ lọc, chi tiết sản phẩm (gallery, chọn biến thể, thông số, nghệ nhân/chất liệu, liên quan), tìm kiếm, giỏ, checkout, trang xác nhận + QR, tra đơn, tài khoản, bài viết, trang nghệ nhân/chất liệu, chính sách | L | P3-01..07 |
| P3-09 | SEO: metadata, canonical, JSON-LD, sitemap, robots gate, cache theo tag + revalidate (02 §5.8, BR-CNT-04/05, BR-CAT-12) | M | P3-08 |
| P3-10 | Hạ tầng production: compose.prod, Caddy, roles DB, backup + restore drill, deploy workflow, uptime monitor (06) | M | song song |
| P3-11 | E2E Playwright: đặt đơn COD; đặt đơn QR → admin ghi tiền → tự xác nhận; đơn QR quá hạn tự hủy | M | P3-08 |
| P3-12 | Checklist go-live (06 §10) | S | tất cả |

**DoD P3:** Khách thật đặt được đơn trên web bằng điện thoại; nhân viên xử lý trong Admin; backup/restore đã diễn tập; checklist go-live hoàn tất.

**Nghiệm thu**
- AC-P3-01: Đổi giá sản phẩm khi khách đang ở checkout → đặt đơn nhận 409 `price_changed` với báo giá mới; không tạo đơn.
- AC-P3-02: Đơn QR không thanh toán → sau 24h tự `cancelled` (`payment_expired`), hàng trở lại bán được, email gửi khách.
- AC-P3-03: Admin ghi đủ tiền cho đơn QR → đơn tự `confirmed`, khách nhận email.
- AC-P3-04: Đăng ký tài khoản bằng SĐT của khách cũ → **không** thấy đơn cũ cho tới khi nhân viên xác minh (BR-IAM-04).
- AC-P3-05: Tra đơn đúng mã sai SĐT → 404; 11 lần sai trong 1 giờ → bị chặn.
- AC-P3-06: Trang sản phẩm có JSON-LD Offer đúng giá hiệu lực và `availability`; hàng độc bản đã bán → `SoldOut`.
- AC-P3-07: Sửa giá KM trong Admin → trang sản phẩm cập nhật trong ≤ 1 phút (revalidate tag).
- AC-P3-08: Lighthouse mobile trang sản phẩm: Performance ≥ 80, SEO ≥ 95, Accessibility ≥ 90.
- AC-P3-09: `SITE_INDEXABLE=false` → robots chặn toàn bộ, sitemap rỗng.

---

## P4 — Báo cáo lãi lỗ

| ID | Task | Size |
|---|---|---|
| P4-01 | Chi phí (`finance.expenses`) CRUD + nhập chi phí cố định theo tháng | S |
| P4-02 | P&L (BR-FIN-01..03) + cảnh báo độ đầy đủ dữ liệu | M |
| P4-03 | Báo cáo kênh/nguồn (BR-FIN-05), dòng tiền (BR-FIN-04) | M |
| P4-04 | Báo cáo sản phẩm, hàng tồn lâu, khách (BR-FIN-06/07) | M |
| P4-05 | Xuất doanh thu tháng/năm Excel (BR-FIN-08) | S |
| P4-06 | Admin UI báo cáo (bảng + biểu đồ đơn giản, chọn kỳ theo tháng VN) | M |

**Nghiệm thu**
- AC-P4-01: Bộ dữ liệu mẫu cố định (fixture) cho tháng 11 → mọi dòng R1, S, RF, NR, COGS, GP, F, CM1, M, CM2, X, NP khớp bảng tính tay đính kèm trong test.
- AC-P4-02: Đơn giao 31/10 23:30 giờ VN thuộc kỳ tháng 10, không thuộc tháng 11.
- AC-P4-03: Báo cáo kênh và nguồn có cùng tổng NR; không có chỗ nào cộng hai bảng.
- AC-P4-04: Có 1 shipment thiếu chi phí → báo cáo hiện cảnh báo "Lãi chưa chính xác".

---

## P5 — Khai thác khách cũ và giới thiệu

| ID | Task | Size |
|---|---|---|
| P5-01 | Tracking links + `/r/{code}` + cookie phân bổ + UTM (BR-MKT-04/09) | M |
| P5-02 | Chiến dịch: tạo, chọn sản phẩm, bộ lọc người nhận + preview loại trừ (BR-MKT-01), consent gate (BR-CRM-09) | L |
| P5-03 | Màn hình gửi tay: danh sách người nhận với tin nhắn đã render + nút Sao chép/Mở Zalo (`https://zalo.me/<sđt>`) + Đã gửi/Bỏ qua (BR-MKT-02/03) | M |
| P5-04 | Chỉ số chiến dịch (BR-MKT-10) | S |
| P5-05 | Giới thiệu bạn bè: cấp mã, áp mã ở checkout, xét thưởng, voucher (BR-MKT-05..08, BR-PRC-04/05) | L |
| P5-06 | Consent ở checkout + trang tài khoản (BR-CRM-09) | S |

**Nghiệm thu**
- AC-P5-01: Chiến dịch `new_arrival` loại hết khách chưa có consent; chiến dịch `care` chọn được nhưng không chọn lại khách đã nhận `care` trong 180 ngày.
- AC-P5-02: Khách click link chiến dịch rồi 3 ngày sau đặt đơn → đơn có `campaign_id`; chỉ số chiến dịch tăng.
- AC-P5-03: Khách cũ (legacy) dùng mã giới thiệu → 422 `referral_not_eligible`.
- AC-P5-04: Đơn đầu của người được giới thiệu giao xong 7 ngày, không đổi trả → người giới thiệu có voucher; đơn bị hoàn tiền → referral `rejected`.

---

## P6 — Tích hợp và nâng cấp (theo nhu cầu)

| ID | Việc | Điều kiện bắt đầu |
|---|---|---|
| P6-01 | SePay (hoặc tương đương): webhook tiền vào → tự ghi payment khớp mã đơn | Đơn chuyển khoản > ~10/ngày |
| P6-02 | GHN/GHTK: báo phí, tạo vận đơn, webhook trạng thái, đối soát COD | Đơn > ~10/ngày |
| P6-03 | Zalo OA đã xác thực: ZNS thông báo đơn, OTP; Zalo Login; OA follower → consent | Có OA xác thực, kiểm điều kiện API hiện hành |
| P6-04 | OTP SMS cho quên mật khẩu/mở khóa lịch sử | Khi cần |
| P6-05 | Hóa đơn điện tử | Theo kế toán/ngưỡng doanh thu |
| P6-06 | Thiết kế lại UX storefront (Tĩnh · Mộc · Sâu) | Sau nghiên cứu người dùng; chỉ đụng tầng trình bày |
| P6-07 | WAL archiving (RPO 15 phút), tách DB, nhiều instance API | Theo 02/06 khi số liệu đòi hỏi |
