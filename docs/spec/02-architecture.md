# 02 — Kiến trúc

## 1. Tổng quan

```
                Internet
                   │
        Cloudflare (DNS, CDN, WAF, cache ảnh)
                   │
           VPS Việt Nam ── Caddy (TLS, reverse proxy)
           ├── shop.<domain>   → web-store  (Next.js, SSR/ISR)     /api/* → api
           ├── admin.<domain>  → web-admin  (Next.js, client-heavy) /api/* → api
           └── api (ASP.NET Core .NET 10, 1 process: HTTP + background workers)
                   │
              PostgreSQL 17 (cùng VPS, volume riêng)

  media.<domain> → Cloudflare R2 (ảnh sản phẩm/bài viết, public read)
  Backup        → object storage tại VN (mã hóa age)
  Email         → SMTP provider · Telegram Bot API (thông báo shop)
```

- **Một API, một database, một process** chạy cả HTTP và job nền (`BackgroundService`).
- Hai app Next.js tách biệt (DEC-15): storefront tối ưu SEO và cache; admin không cần SEO, luôn sau đăng nhập.
- Trình duyệt luôn gọi API **cùng origin** (`https://shop.<domain>/api/...`, `https://admin.<domain>/api/...`); Caddy chuyển tiếp (DEC-16). Không có BFF.
- Storefront render server-side gọi API qua mạng nội bộ Docker (`http://api:8080`).

## 2. Cấu trúc repository

```
teapremium/
├─ backend/
│  ├─ src/
│  │  ├─ TeaPremium.Domain/            # entity, value object, enum, domain rule. Không phụ thuộc package nào.
│  │  ├─ TeaPremium.Application/       # use case (command/query + handler), interface port, DTO, validation.
│  │  ├─ TeaPremium.Infrastructure/    # EF Core, Npgsql, R2, SMTP, Telegram, providers, job, outbox.
│  │  └─ TeaPremium.Api/               # endpoint, auth, middleware, Program.cs, CLI commands.
│  └─ tests/
│     ├─ TeaPremium.UnitTests/         # domain + application (fake ports)
│     ├─ TeaPremium.IntegrationTests/  # Testcontainers PostgreSQL + WebApplicationFactory
│     └─ TeaPremium.ArchitectureTests/ # NetArchTest: luật phụ thuộc
├─ apps/
│  ├─ store/                           # Next.js storefront
│  └─ admin/                           # Next.js admin
├─ packages/
│  └─ api-client/                      # TS types sinh từ OpenAPI (openapi-typescript), dùng chung 2 app
├─ infra/
│  ├─ compose.dev.yml
│  ├─ compose.prod.yml
│  ├─ caddy/Caddyfile
│  ├─ postgres/init-roles.sql
│  └─ backup/ (backup.sh, restore-drill.sh)
├─ docs/spec/                          # bộ spec này
└─ .github/workflows/ (ci.yml, deploy.yml)
```

Trong mỗi project backend, **tổ chức theo module trước, theo kỹ thuật sau**:

```
TeaPremium.Domain/Catalog/Product.cs
TeaPremium.Domain/Inventory/StockLevel.cs
TeaPremium.Application/Orders/PlaceWebOrder/PlaceWebOrderCommand.cs
TeaPremium.Application/Orders/PlaceWebOrder/PlaceWebOrderHandler.cs
TeaPremium.Application/Orders/PlaceWebOrder/PlaceWebOrderValidator.cs
TeaPremium.Infrastructure/Persistence/Configurations/Orders/OrderConfiguration.cs
TeaPremium.Api/Endpoints/Admin/OrdersEndpoints.cs
```

## 3. Luật phụ thuộc (kiểm bằng ArchitectureTests)

1. `Domain` không tham chiếu project/package nào (trừ BCL).
2. `Application` chỉ tham chiếu `Domain` và các package trừu tượng (`Microsoft.Extensions.*.Abstractions`, `FluentValidation`). **Không** tham chiếu EF Core.
3. `Infrastructure` tham chiếu `Application`, `Domain`.
4. `Api` tham chiếu `Application`, `Infrastructure` (chỉ để đăng ký DI).
5. **Ranh giới module:** handler của module A không đọc/ghi trực tiếp bảng của module B. Muốn dùng thì gọi interface do module B công bố trong `Application/<B>/Contracts/` (ví dụ `IInventoryReservations`, `ICustomerResolver`, `IPricingCalculator`).
   Ngoại lệ được phép: query báo cáo (`Application/Reports`) được đọc nhiều schema, **chỉ đọc**.
6. Endpoint không chứa nghiệp vụ: chỉ bind input → gọi handler → map kết quả.

## 4. Mẫu use case

Không dùng MediatR. Mỗi use case:

```csharp
public sealed record ConfirmOrderCommand(Guid OrderId, uint RowVersion) : ICommand<OrderDto>;

public sealed class ConfirmOrderHandler(IOrderRepository orders, IUnitOfWork uow, IClock clock, ICurrentActor actor)
    : ICommandHandler<ConfirmOrderCommand, OrderDto>
{
    public async Task<Result<OrderDto>> Handle(ConfirmOrderCommand cmd, CancellationToken ct) { ... }
}
```

- `ICommandHandler<TCommand, TResult>` / `IQueryHandler<TQuery, TResult>` tự định nghĩa, đăng ký DI bằng scan assembly.
- Trả về `Result<T>` với lỗi kiểu `Error(code, message, type)`; `type ∈ {validation, not_found, conflict, forbidden, business_rule}`. Không ném exception cho lỗi nghiệp vụ dự kiến.
- Validation input bằng FluentValidation trước khi vào handler (pipeline decorator).
- Domain invariant kiểm trong entity (method của entity ném `DomainException` → map thành `business_rule`).
- Application **không** thấy `DbContext`/`DbSet`. Application chỉ thấy repository/port theo module (ví dụ `IOrderRepository`, `IStockRepository`) và `IUnitOfWork`; Infrastructure hiện thực bằng EF Core. Repository chỉ load/save aggregate, **không chứa luật nghiệp vụ**. Query đọc phức tạp dùng `I<Module>Queries` hiện thực ở Infrastructure, trả DTO trực tiếp.

## 5. Cross-cutting

### 5.1 Tiền và thời gian
- `Money` trong Domain là `readonly record struct Money(long Vnd)`; cấm số âm trừ khi field cho phép (điều chỉnh).
- `IClock` (bọc `TimeProvider`) — **không** gọi `DateTime.Now`/`DateTimeOffset.UtcNow` trực tiếp (ArchitectureTests chặn).
- `BusinessCalendar` chuyển UTC ↔ ngày giờ Việt Nam (`Asia/Ho_Chi_Minh`, UTC+7, không DST). Kỳ báo cáo là khoảng nửa mở `[from, to)` theo ngày Việt Nam.

### 5.2 Giao dịch và đồng thời
- Mỗi command chạy trong **một transaction** (`IUnitOfWork.ExecuteAsync`).
- Khóa lạc quan: các aggregate sửa được từ Admin (`products`, `variants`, `orders`, `customers`, `campaigns`) dùng `xmin` làm concurrency token; client gửi `rowVersion`; lệch → HTTP 409 `concurrency_conflict`.
- Tồn kho dùng **UPDATE có điều kiện nguyên tử**, không đọc-rồi-ghi:
  ```sql
  UPDATE inventory.stock_levels
     SET reserved = reserved + @qty, updated_at = now()
   WHERE variant_id = @id AND on_hand - reserved >= @qty;
  -- 0 dòng bị ảnh hưởng → hết hàng
  ```
  Khi giữ nhiều variant trong 1 đơn, xử lý theo thứ tự `variant_id` tăng dần để tránh deadlock.

### 5.3 Idempotency
- Header `Idempotency-Key` (UUID) bắt buộc cho: đặt đơn web, tạo đơn admin, ghi thanh toán, hoàn tiền, xác nhận phiếu nhập.
- Bảng `platform.idempotency_keys(key, scope, request_hash, response_status, response_body, created_at)`; cùng key + cùng hash → trả lại response cũ; cùng key khác hash → 409 `idempotency_key_reused`. Giữ 48 giờ.

### 5.4 Audit
- `SaveChanges` interceptor ghi `platform.audit_logs` cho mọi entity đánh dấu `[Audited]`: entity, id, action, actor (staff/customer/system), thay đổi trước/sau theo field, correlation id.
- Field PII (`phone`, `email`, `address_*`, `full_name`, `receiver_*`, `notes`) ghi `"[redacted]"`; field tiền, trạng thái, giá, giá vốn ghi giá trị thật.
- Cấm `ExecuteUpdate/ExecuteDelete` trên bảng có audit, **trừ** `stock_levels` (đã có sổ `stock_movements`) và các job dọn dẹp ghi rõ trong spec.
- DB: role ứng dụng không có quyền `UPDATE/DELETE/TRUNCATE` trên `platform.audit_logs` và `inventory.stock_movements`.

### 5.5 Outbox, sự kiện và job nền
- Thay đổi cần tác động bên ngoài (email, Telegram, sau này webhook/SePay/GHN) ghi `platform.outbox_messages` **trong cùng transaction**.
- `OutboxDispatcher` (BackgroundService) lấy batch bằng `FOR UPDATE SKIP LOCKED`, gửi, đánh dấu `processed_at`; lỗi thì tăng `attempts`, backoff lũy thừa, tối đa 10 lần rồi `dead`.
- Sự kiện nội bộ trong cùng process (ví dụ `OrderDelivered` → cập nhật chỉ số khách, xét giới thiệu) cũng đi qua outbox để chạy lại được.
- Job định kỳ (`PeriodicJob`), mỗi job giữ `pg_try_advisory_lock` riêng để an toàn khi chạy nhiều instance:

| Job | Chu kỳ | Việc |
|---|---|---|
| `ExpireUnpaidOrders` | 5 phút | BR-ORD-07 |
| `ExpireCarts` | 1 giờ | xóa giỏ hết hạn |
| `ExpireUploadSessions` | 1 giờ | dọn upload dở |
| `RecomputeCustomerStats` | 02:00 hằng ngày | đối chiếu chỉ số khách (sự kiện cập nhật realtime, job này sửa lệch) |
| `ActivateScheduledPrices` | 5 phút | revalidate cache khi giá KM bắt đầu/kết thúc |
| `QualifyReferrals` | 1 giờ | BR-MKT-07 |
| `DataRetention` | 03:00 hằng ngày | BR-CRM-12 |
| `PurgeIdempotencyKeys` | 1 giờ | xóa key > 48h |

### 5.6 Provider mở rộng
```csharp
public interface IPaymentProvider {
    string Code { get; }                                   // "manual_bank_transfer", "cod", sau này "sepay"
    PaymentInstructions BuildInstructions(OrderPaymentContext ctx);   // QR payload, nội dung CK
    Task<WebhookResult> HandleWebhookAsync(HttpRequest req, CancellationToken ct); // manual: NotSupported
}
public interface IShippingProvider {
    string Code { get; }                                   // "manual", sau này "ghn", "ghtk"
    Task<ShippingQuote?> QuoteAsync(ShippingQuoteRequest r, CancellationToken ct); // manual: null → dùng phí đồng giá
    Task<CreateShipmentResult> CreateAsync(CreateShipmentRequest r, CancellationToken ct); // manual: dùng mã do người nhập
    Task<WebhookResult> HandleWebhookAsync(HttpRequest req, CancellationToken ct);
}
public interface INotificationChannel { string Code { get; } Task SendAsync(Notification n, CancellationToken ct); } // email, telegram
public interface IOtpSender { ... }   // giai đoạn 5
```
Webhook đầu vào (giai đoạn 5) lưu thô vào `platform.inbound_webhooks` (unique theo `provider + external_id`) rồi mới xử lý → idempotent.

### 5.7 VietQR
Sinh payload EMVCo theo chuẩn VietQR (NAPAS) **tại server** từ cấu hình `bank_bin`, `account_number`, `account_name`, số tiền, nội dung = mã đơn. Không gọi dịch vụ ngoài. Trả về chuỗi payload; frontend render QR bằng thư viện (ví dụ `qrcode`). Có unit test CRC16.

### 5.8 Cache storefront
- Storefront dùng fetch có tag (`product:{slug}`, `category:{slug}`, `catalog`, `settings`, `article:{slug}`) và `revalidate` mặc định 300s.
- API phát sự kiện outbox `StorefrontInvalidate(tags[])` khi publish/sửa sản phẩm, giá, KM, tồn chuyển 0↔>0, bài viết, settings. Dispatcher gọi `POST http://store:3000/api/revalidate` (secret nội bộ) → `revalidateTag`.
- Trang giỏ, checkout, tài khoản, tra đơn: **không cache**.
- Tồn kho và giá trên trang sản phẩm có thể trễ ≤ 5 phút; **checkout luôn kiểm lại** giá và tồn tại API.

### 5.9 Lỗi và logging
- Lỗi trả `application/problem+json` (RFC 9457) có `code` máy đọc được (xem `05-api.md §1`).
- Log JSON ra stdout, có `correlation_id`; không log body request; middleware che PII.
- Health: `/health/live` (process), `/health/ready` (DB + migration đã áp đủ).

### 5.10 Upload ảnh
- `POST /admin/media/upload-intents` → kiểm MIME (jpeg, png, webp, avif), kích thước ≤ 15MB → trả presigned PUT R2 (hết hạn 10 phút) + `upload_id`.
- Trình duyệt PUT thẳng lên R2. `POST /admin/media/upload-intents/{id}/complete` → server HEAD object, đọc kích thước ảnh, tạo `media_assets`.
- Biến thể ảnh (thumbnail, kích thước responsive): dùng Cloudflare Image Resizing **hoặc** Next.js Image loader trỏ R2. Chọn cấu hình ở `06-infrastructure.md`. DB chỉ lưu bản gốc.

## 6. Xác thực

| | Nhân viên | Khách hàng |
|---|---|---|
| Lưu trữ | ASP.NET Core Identity, schema `iam` | Bảng riêng `crm.customer_accounts`, `crm.customer_logins`; băm mật khẩu bằng `PasswordHasher<CustomerAccount>` |
| Cookie | `__Host-tp_admin`, chỉ trên `admin.<domain>`, sliding 8 giờ, tuyệt đối 7 ngày | `__Host-tp_customer`, chỉ trên `shop.<domain>`, sliding 30 ngày |
| Scheme | `AdminCookie` | `CustomerCookie` |
| CSRF | Antiforgery: cookie + header `X-CSRF-TOKEN` cho mọi method không an toàn | Như bên trái, cho các endpoint `account/*`, `cart/*`, `checkout/*` |
| 2 lớp | TOTP (bắt buộc với owner) | Không |
| Google | Không | OpenID Connect (giai đoạn 2) |

- Endpoint `admin/*` chỉ chấp nhận scheme `AdminCookie`; `account/*` chỉ `CustomerCookie`. Không có token trong localStorage.
- Rate limit (theo IP thật qua header `CF-Connecting-IP` chỉ khi request đến từ dải IP Cloudflare đã cấu hình ở Caddy):
  đăng nhập 10/phút/IP và 5 lần sai/15 phút/tài khoản; đặt đơn 10/phút/IP; tra đơn 20/phút/IP; đăng ký 5/giờ/IP.

## 7. Phân quyền

Permission là claim; policy theo permission. Role → permission seed bằng migration.

| Permission | owner | sales | content |
|---|:-:|:-:|:-:|
| `catalog.read` | ✓ | ✓ | ✓ |
| `catalog.write` (sản phẩm, ảnh, thuộc tính, SEO) | ✓ |  | ✓ |
| `catalog.publish` | ✓ |  | ✓ |
| `pricing.write` (giá bán, giá KM) | ✓ |  |  |
| `costs.read` (giá vốn, lãi) | ✓ |  |  |
| `inventory.read` | ✓ | ✓ | ✓ (chỉ còn/hết) |
| `inventory.write` (nhập kho, điều chỉnh, kiểm kê) | ✓ | ✓ |  |
| `inventory.approve` (duyệt kiểm kê, điều chỉnh lớn) | ✓ |  |  |
| `orders.read` / `orders.write` | ✓ | ✓ |  |
| `orders.override_price` (sửa giá dòng) | ✓ | ✓ |  |
| `payments.write` (ghi nhận, đối soát) | ✓ | ✓ |  |
| `refunds.write` | ✓ |  |  |
| `returns.write` | ✓ | ✓ |  |
| `customers.read` / `customers.write` | ✓ | ✓ |  |
| `customers.export` / `customers.erase` / `customers.merge` | ✓ |  |  |
| `marketing.write` (chiến dịch, link) | ✓ | ✓ |  |
| `marketing.spend` (chi phí marketing) | ✓ |  |  |
| `content.write` / `content.publish` | ✓ |  | ✓ |
| `finance.read` / `finance.write` | ✓ |  |  |
| `settings.write` | ✓ |  |  |
| `staff.manage` | ✓ |  |  |
| `audit.read` | ✓ |  |  |

**Lọc field theo quyền:** DTO trả cho người không có `costs.read` **không chứa** `unit_cost`, `avg_cost`, `gross_profit`... (không phải chỉ ẩn ở UI). Có integration test cho mỗi endpoint trả giá vốn.

## 8. Cấu hình và secret

- Cấu hình qua biến môi trường; file `.env` chỉ ở máy chủ, quyền 600, không commit.
- Secret: chuỗi kết nối DB, khóa R2, SMTP, Telegram bot token, Google client secret, revalidate secret, khóa Data Protection.
- Data Protection: lưu khóa trong DB (`PersistKeysToDbContext`), `SetApplicationName("teapremium")`, bảo vệ khóa bằng chứng chỉ X.509 lưu trong secret (để chạy được nhiều instance sau này).
- Cấu hình nghiệp vụ (phí ship, ngưỡng freeship, giờ giữ hàng, tài khoản ngân hàng) lưu DB `platform.settings`, sửa qua Admin, có audit.

## 9. Frontend

- **Chung:** TypeScript strict, Next.js App Router, Tailwind, `packages/api-client` sinh từ OpenAPI của API (`/openapi/v1.json`), không viết tay type DTO.
- **Admin (`apps/admin`):** shadcn/ui, TanStack Query + TanStack Table, React Hook Form + Zod, Tiptap cho rich text. Ưu tiên chức năng và tốc độ thao tác, mobile dùng được.
- **Store (`apps/store`):** bản base: layout, trang chủ đơn giản, danh mục/lọc, chi tiết sản phẩm, giỏ, checkout, xác nhận + QR, tra đơn, tài khoản, bài viết, trang nghệ nhân/chất liệu, trang chính sách. UX/visual làm lại sau → tách component trình bày khỏi data-fetching để thay giao diện không đụng logic.

## 10. Chiến lược test

| Tầng | Công cụ | Bắt buộc cho |
|---|---|---|
| Unit | xUnit, FluentAssertions | Mọi BR trong Domain/Application; tính tiền; state machine; VietQR CRC; chuẩn hóa SĐT |
| Integration | Testcontainers PostgreSQL, WebApplicationFactory | Endpoint, phân quyền, lọc field giá vốn, idempotency, xmin 409, audit, migration áp từ rỗng |
| Đồng thời | Integration, `Task.WhenAll` 20 request | NFR-02 giữ hàng độc bản; idempotency song song |
| Kiến trúc | NetArchTest | §3 và cấm `DateTime.Now` |
| E2E (giai đoạn 2+) | Playwright | Đặt đơn COD, đặt đơn QR + admin xác nhận, hủy tự động |
