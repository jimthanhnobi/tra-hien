# P2-13: Notifications: Telegram + email shop, template, digest 08:00 (BR-PLT-03); cảnh báo hiện tại/digest, không phát lại backlog P1 lỗi thời

- **Gate/status:** `READY_FOR_FOCUSED_G4` (đã hoàn tất `base-integration` lên `origin/main` `862fbf05fc8b9f9c34fc0441e559bb13e24604a3` gồm PR #45 `P2-02` và PR #46 `P2-05`; chưa commit/push).
- **Tier:** `A` — tích hợp kênh gửi ra ngoài (Telegram Bot API + SMTP), xử lý secret hạ tầng (`TELEGRAM_BOT_TOKEN`, `SMTP_*`) và kiểm soát chống rò rỉ PII khách hàng / tên nghệ nhân / giá vốn (`NFR-04`, `BR-PLT-03`, `02-architecture.md` §7–§8).
- **Worktree/base:** `D:\tra-hien-p2-13`, nhánh `feature/p2-13-notifications`, base `origin/main` `862fbf05fc8b9f9c34fc0441e559bb13e24604a3` (đã gồm PR #44 `P2-01`, PR #45 `P2-02`, PR #46 `P2-05`).
- **Readiness (scoped G1/G2):** `GO` — toàn bộ `GAP-01..03` và `ASSUMPTION-01..02` đã được Owner phê duyệt và khóa cứng ngày `23/11/2026`; `0` migration mới, không sửa `AppDbContextModelSnapshot.cs`.
  - Tái sử dụng G1 đầy đủ tại `cadce575597738bf40ad7c45689482de5a5fbc3d` ([spec-inventory.md](../analysis/spec-inventory.md), [spec-gaps.md](../analysis/spec-gaps.md) mục `G35` dòng `84`), G2 nền tại [conformance-review.md](../architecture/conformance-review.md) + [task-readiness.md](task-readiness.md) (mốc B01 `dadc4435e8f115faaeb9e8511e56e09d5d517a8a`), quyết định owner `A01 D14` ([ADR-PROPOSAL-A01](../decisions/ADR-PROPOSAL-A01-p0-closure-p1-owner-decisions.md) dòng `27, 117, 164`), hồ sơ `P1-10` ([p1-10-implementation.md](p1-10-implementation.md)) và `P2-01` ([p2-01-implementation.md](p2-01-implementation.md)).
  - Diff `docs/spec/` từ `e63e90b` tới `85218d1` chỉ chỉnh grants/ràng buộc của `inventory.reservations`, `sales.order_lines` và `crm.customers` trong `03-database.md` và `schema.sql` phục vụ `P2-01`; không ảnh hưởng `P2-13`.
  - Dependency đã merge đầy đủ vào `origin/main` (`85218d1`): `P0-06`, `P0-09`, `P1-10`, và `P2-01` (`CrmSalesFinanceEntities.cs` / `CrmSalesFinanceModel.cs`: `sales.orders`, `sales.payments`, `crm.interactions`, `crm.customers`).
- **Owner notes (khóa cứng `23/11/2026`):**
  - `GAP-01 = 1B`: truy vấn thật đủ 6 chỉ số digest trên bảng `P2-01` (`sales.orders`, `sales.payments`, `crm.interactions`, `crm.customers`, `catalog.variants`, `inventory.stock_levels`, `inventory.stock_movements`, `platform.outbox_messages`); `0` migration mới, không đụng `AppDbContextModelSnapshot.cs`.
  - `GAP-02 = 2A` (`ASSUMPTION` owner duyệt `23/11/2026`, cửa sổ `24 giờ`): *"chờ thanh toán sắp hết hạn"* = `status = 'pending' AND is_backfilled = false AND payment_method = 'bank_transfer' AND payment_status = 'unpaid' AND now < payment_due_at <= now + 24h`; *"đơn chờ xác nhận"* = `status = 'pending' AND is_backfilled = false` và loại các đơn đã thuộc nhóm chờ thanh toán (`payment_method = 'bank_transfer' AND payment_status IN ('unpaid', 'partially_paid')`).
  - `GAP-03 = 3A`: Telegram tức thời có chống lặp/watermark cho `reconciliation_mismatch` mới (`InventoryAlertHandler`) và outbox chuyển `dead` (`OutboxBatchProcessor`, loại trừ `Type = NotificationDelivery` để không lặp vô tận) + tổng hợp trong `daily_digest_shop`; `low_stock` chỉ trong digest `08:00`. Không replay backlog P1.
  - `ASSUMPTION-01 = A`: `notify.telegram_chat_id` nhận `null` hoặc `""` = tắt kênh Telegram (`SettingsCatalog.cs`), không đổi OpenAPI.
  - `ASSUMPTION-02`: BCL `.NET` sau `ISmtpTransport`, `0` package NuGet mới, không sửa lockfile.
- **Traceability:** xem Mục 1 bên dưới.
- **Scope / out of scope:** xem Mục 1 bên dưới.
- **Cases:** xem Mục 8 bên dưới (`13` unit test methods = `36` ca trong `TeaPremium.UnitTests` + `7` infra policy/STARTTLS test methods = `26` ca + `11` PostgreSQL 17 integration tests trong `TeaPremium.IntegrationTests`; tổng `73` ca thực thi kiểm thử mới, `0` skipped, `0` gọi mạng thật).
- **Migration/recovery:** `none` — không thêm bảng/cột, không sinh EF Core migration, không chạm `AppDbContextModelSnapshot.cs` (Mục 5). Khôi phục bằng cách revert commit code của nhánh.
- **Verification:** vòng tập trung sau remediation (`Notification|CompiledArchitecture`) đã PASS `36/36` IntegrationTests (gồm 3 ca STARTTLS chứng chỉ trong bộ nhớ) và `29/29` ArchitectureTests, sau đó sửa 1 dòng kiểm dấu chấm cuối miền cho `"user@example.test."`. Toàn bộ suite backend cuối cùng chạy bằng `-Manifest artifacts/p2-13/candidate.manifest` ngay sau khi khóa lại manifest mới (SHA-256 mới và đường dẫn log cuối ghi trong báo cáo gửi Orchestrator để không sửa bất kỳ file nào sau khi ghi manifest).
- **Risks/follow-ups:** xem Mục 9 bên dưới (`P3-06` tái sử dụng `INotificationChannel` + `NotificationDelivery` cho email khách; `P2-05`/`P2-06` tái sử dụng hằng số `NotificationContracts.OrderPaymentExpiringWindow = 24h` cho `flag=expiring`; `P3-10` bổ sung implicit TLS cổng `465` nếu nhà cung cấp SMTP yêu cầu vì `P2-13` từ chối tường minh cổng `465` và dùng STARTTLS `587`; `P2-16` hoàn thiện dashboard/admin dead-outbox).
- **Review/publish:** chờ focused G4 re-review trên cùng worktree với `artifacts/p2-13/candidate.manifest` mới, sau đó xin phép Owner trước khi `publish-pr`.

---

## 1. Định danh, truy vết đặc tả và phạm vi

### 1.1 Định danh & liên kết đặc tả
- **Task ID:** `P2-13` — *Notifications: Telegram + email shop, template, digest 08:00 (BR-PLT-03); cảnh báo hiện tại/digest, không phát lại backlog P1 lỗi thời* ([07-roadmap.md](../spec/07-roadmap.md) dòng `98`, cỡ `S`, phụ thuộc `P0-06`, `P1-10`).
- **FR:** `FR-PLT-02` ([01-requirements.md](../spec/01-requirements.md) dòng `191` — job nền gửi email/Telegram qua outbox), `FR-PLT-03` (dòng `192` — nhật ký thông báo đã gửi và trạng thái), `FR-CRT-06` (dòng `102` — phần thông báo đơn mới cho shop qua Telegram và email), `FR-INV-07` (dòng `85` — cảnh báo tồn thấp tổng hợp ở digest).
- **BR:**
  - `BR-PLT-02` ([04-business-rules.md](../spec/04-business-rules.md) dòng `300` — outbox tối đa 10 lần, backoff `min(2^attempts phút, 6 giờ)`, `dead` → Admin + Telegram).
  - `BR-PLT-03` (dòng `301-312` — template tiếng Việt lưu trong code; triển khai trực tiếp `order_placed_shop` dòng `306` và `daily_digest_shop` dòng `311`, kèm khung template dùng lại cho `P3-06`/`P5-05`).
  - `BR-PLT-04` (dòng `313` — gửi email qua SMTP provider đi qua outbox, thất bại không làm hỏng giao dịch gốc).
  - `BR-INV-11` (dòng `93` — digest `08:00` theo tồn thấp hiện tại, không phát lại cảnh báo P1 cũ).
  - `BR-INV-13` (dòng `95`; [03-database.md](../spec/03-database.md) dòng `66` — lệch đối soát sổ kho gửi Telegram theo trạng thái hiện tại, không replay cảnh báo lỗi thời).
  - Tham chiếu điều kiện lọc dữ liệu digest / sự kiện nguồn: `BR-CRT-06` (dòng `131`), `BR-ORD-04/05/09/11` (dòng `166-167, 176, 178`), `BR-PAY-05` (dòng `197`), `BR-CRM-06` (dòng `225`).
- **NFR:** `NFR-04` ([01-requirements.md](../spec/01-requirements.md) dòng `201` — cấm lộ PII), `NFR-08` (dòng `205` — không auto-migrate), `NFR-09` (dòng `206` — test mọi `BR-*` trên PG17 thật), `NFR-12` (dòng `209` — bảo vệ dữ liệu cá nhân, băm địa chỉ nhận).
- **AC:** Duy trì `AC-P0-04` ([07-roadmap.md](../spec/07-roadmap.md) dòng `38-39`), `AC-P0-05` (dòng `40`), `AC-P1-06` (dòng `73`); dựng hạ tầng nền cho `AC-P3-02`, `AC-P3-03` (dòng `146-147`, nghiệm thu tại `P3-03`/`P3-06`).
- **DEC:** `DEC-01` ([00-README.md](../spec/00-README.md) dòng `85`), `DEC-14` (dòng `98`), `DEC-19` (dòng `103`), `DEC-20` (dòng `104`), `DEC-30` (dòng `114`), `A01 D14` ([ADR-PROPOSAL-A01](../decisions/ADR-PROPOSAL-A01-p0-closure-p1-owner-decisions.md) dòng `27, 117, 164`).

### 1.2 Trong phạm vi / Ngoài phạm vi
- **Trong phạm vi (`P2-13`):**
  1. Cổng `INotificationChannel` ([02-architecture.md](../spec/02-architecture.md) dòng `162`) và hai adapter `TelegramNotificationChannel` (HTTP Bot API) + `SmtpNotificationChannel` (SMTP).
  2. Bộ khung template tiếng Việt lưu trong code (`BR-PLT-03`) + cơ chế escape an toàn cho `order_placed_shop`, `daily_digest_shop` (và cảnh báo kỹ thuật shop theo chốt `GAP-03`).
  3. Hợp đồng outbox `OrderPlaced` (phần thông báo shop) + message giao vận đơn kênh `NotificationDelivery` + `NotificationDeliveryHandler` ghi nhận `platform.notification_logs` (`FR-PLT-03`, `recipient_hash` SHA-256) và đảm bảo idempotency khi retry.
  4. `DailyShopDigestJob` kế thừa `PeriodicJob` dưới `JobExecutionGuard`, chạy đúng 1 lần/ngày lịch Việt Nam lúc/sau `08:00` `Asia/Ho_Chi_Minh`, đọc trạng thái hiện tại, không replay backlog P1.
- **Ngoài phạm vi (thuộc các task sau):**
  - Email gửi khách hàng (`order_placed_customer`, `payment_received_customer`, `order_shipped_customer`, `order_cancelled_customer`) → `P3-06` ([07-roadmap.md](../spec/07-roadmap.md) dòng `128`).
  - Email thưởng giới thiệu (`referral_reward_customer`, `BR-MKT-07`) → `P5-05` (dòng `191`); link đánh giá sau giao 7 ngày (`BR-SEO-90`) → `P5-07` (dòng `193`).
  - Tóm tắt SEO Audit hằng tuần qua Telegram (`BR-SEO-60`) → `P3-17` (dòng `136`).
  - Kênh SMS / ZNS / OTP → `P6-03`, `P6-04` (dòng `209-210`).
  - Endpoint quản trị `GET/POST admin/system/outbox` ([05-api.md](../spec/05-api.md) dòng `258`), Admin UI và job dọn `DataRetention` (`BR-CRM-12`).

---

## 2. Luồng sự kiện và cơ chế "không phát lại backlog P1 lỗi thời"

### 2.1 Phân loại sự kiện gửi ngay vs gộp vào Digest `08:00` `Asia/Ho_Chi_Minh`
1. **Gửi ngay qua Outbox (Immediate):**
   - **`order_placed_shop`** (`BR-PLT-03` tại `04-business-rules.md:306`; `FR-CRT-06` tại `01-requirements.md:102`; `BR-CRT-06` mục 9 tại `04-business-rules.md:131`):
     - Khi nhận message outbox `OrderPlaced` (bỏ qua đơn lịch sử `is_backfilled = true` theo `BR-ORD-09` tại `04-business-rules.md:176`), `OrderPlacedNotificationHandler` đọc `platform.settings`:
       - Nếu `notify.telegram_chat_id` đã cấu hình và hợp lệ mốc bật kênh → xếp hàng 1 `NotificationDelivery` (`channel = "telegram"`, `template = "order_placed_shop"`).
       - Với mỗi email phân biệt trong `notify.shop_emails` hợp lệ mốc bật kênh → xếp hàng 1 `NotificationDelivery` (`channel = "email"`, `template = "order_placed_shop"`).
   - **Cảnh báo kỹ thuật tức thời (chờ owner chốt `GAP-03` — khuyến nghị bật có rào chắn):**
     - `InventoryAlert` loại `reconciliation_mismatch` (`BR-INV-13` tại `04-business-rules.md:95`; `03-database.md:66`): sau khi `InventoryAlertHandler` kiểm lại DB thấy `Σ stock_movements.quantity != on_hand` **vẫn còn lệch**, nếu thuộc ngày VN hiện tại và sau mốc bật kênh Telegram thì xếp hàng 1 `NotificationDelivery` (`telegram`, `inventory_reconciliation_shop`).
     - `Outbox dead` (`BR-PLT-02` tại `04-business-rules.md:300`): khi `OutboxBatchProcessor` đánh dấu `status = 'dead'` ở lần thử thứ 10 cho một message **khác** `NotificationDelivery` (tránh vòng lặp vô tận khi chính Telegram hỏng), xếp hàng 1 `NotificationDelivery` (`telegram`, `outbox_dead_shop`).
2. **Gộp vào Digest `08:00` `Asia/Ho_Chi_Minh` (`daily_digest_shop`):**
   - **Đặc tả:** `BR-PLT-03` (`04-business-rules.md:311`), `BR-INV-11` (`04-business-rules.md:93`), `02-architecture.md:135`.
   - **Kênh & Giờ chạy:** Chỉ gửi qua **Telegram** (`notify.telegram_chat_id`) lúc/sau `08:00` giờ Việt Nam (`01:00` UTC qua `BusinessCalendar.ToUtc(vietnamDate, new TimeOnly(8, 0))`).
   - **Nội dung tổng hợp từ trạng thái DB hiện hành (không đọc event lịch sử):**
     1. *Tồn thấp (`BR-INV-11`):* số lượng + danh sách rút gọn mã `SKU` (tối đa 10 SKU đầu sắp theo `SKU`, phần dư ghi `... và N SKU khác`) thỏa `low_stock_threshold > 0 && (on_hand - reserved) <= low_stock_threshold` (kèm tổng số SKU đang lệch sổ kho `reconciliation_mismatch` nếu > 0). **`InventoryAlert(low_stock)` tuyệt đối không gửi tin Telegram lẻ khi biến động kho.**
     2. *Outbox `dead` (`BR-PLT-02`, `BR-PLT-03`):* tổng dòng `platform.outbox_messages` có `status = 'dead'`.
     3. *Đơn chờ xác nhận*, *Chờ thanh toán sắp hết hạn*, *COD chưa đối soát*, *Hẹn chăm sóc hôm nay*: truy vấn theo phương án `GAP-01`/`GAP-02` được owner phê duyệt.

### 2.2 Bốn lớp chặn "không phát lại backlog P1 lỗi thời" (`BR-INV-11/13`, `02-architecture.md:135`, `A01 D14`)
1. **Không quét lại outbox lịch sử:** Không có code startup, migration hay job nào đọc lại các dòng `platform.outbox_messages` có `status IN ('processed', 'dead')` từ P1.
2. **`low_stock` chỉ đọc trạng thái hiện tại ở Digest `08:00`:** `InventoryAlertHandler` giữ nguyên xử lý nội bộ P1 cho `kind = "low_stock"` (kiểm tồn hiện hành + structured log nội bộ rồi đánh dấu `processed`); mọi dòng `InventoryAlert(low_stock)` cũ đang `pending`/retry từ P1 hoàn tất nội bộ mà không phát sinh tin Telegram.
3. **Watermark mốc kích hoạt kênh (`platform.settings.updated_at`) + Ngày hiệu lực + Kiểm lại trạng thái sống:**
   - Nếu kênh nhận chưa được cấu hình trong `platform.settings` (hoặc môi trường chưa cấp secret kênh), bộ chuyển đổi sự kiện hoàn tất no-op an toàn (không treo retry chờ cấu hình theo đúng `A01 D14`).
   - Nếu `outboxMessage.OccurredAt < setting.UpdatedAt` (sự kiện sinh ra **trước** thời điểm chủ shop bật hoặc đổi `notify.telegram_chat_id` / `notify.shop_emails` tại `EfSettingsStore.SetValue`), sự kiện bị bỏ qua không gửi ra kênh đó — chặn triệt để việc vừa bật kênh đã xả hàng loạt cảnh báo/đơn cũ đang nằm trong hàng đợi retry.
   - Với `reconciliation_mismatch`: bắt buộc `alert.VietnamDate == BusinessCalendar.ToVietnamDate(clock.UtcNow)` **và** truy vấn lại sổ kho ngay lúc xử lý vẫn còn lệch; nếu đã khớp hoặc từ ngày cũ → bỏ qua no-op.
4. **Không gửi bù digest ngày quá khứ:** `DailyShopDigestJob` chỉ đánh giá đúng `todayVn = BusinessCalendar.ToVietnamDate(clock.UtcNow)`, không lùi ngày sinh bù digest của những ngày hệ thống tắt hoặc chưa bật kênh.

---

## 3. Cơ chế gửi, Idempotency, Retry/Backoff, Timeout, Rate Limit và `PeriodicJob`

1. **Tách `NotificationDelivery` đơn kênh / đơn người nhận (`IOutboxHandler`):**
   - `OutboxBatchProcessor` hiện gọi `handlers.SingleOrDefault(x => x.MessageType == message.Type)` (`OutboxBatchProcessor.cs:70`) và bọc handler trong savepoint `"before_delivery"` (`OutboxBatchProcessor.cs:67,95`).
   - Để lỗi SMTP không làm rollback/gửi trùng tin Telegram đã gửi thành công (và sẵn sàng cho `P3-06` thêm email khách cùng sự kiện `OrderPlaced`), mỗi lượt gửi ra ngoài được tách thành 1 dòng `OutboxMessage` độc lập có `Type = "NotificationDelivery"` xử lý bởi `NotificationDeliveryHandler`.
2. **Idempotency chống gửi trùng khi retry / crash sau gửi (`platform.notification_logs`):**
   - Vì mọi ghi nhận trên `AppDbContext` của `OutboxBatchProcessor` sẽ bị `RollbackToSavepointAsync("before_delivery")` xóa mất nếu handler ném lỗi, `NotificationDeliveryHandler` dùng `IServiceScopeFactory` mở **scope `AppDbContext` ngắn hạn riêng** cho `platform.notification_logs`:
     - **Trước khi gọi mạng:** mở transaction ngắn, lấy `pg_advisory_xact_lock(bigint)` (`GrantsSql.cs:51`) băm từ `(channel, template, ref_type, ref_id, recipient_hash)`, tra `platform.notification_logs` qua index `ix_notif_ref(ref_type, ref_id)` (`schema.sql:133`). Nếu đã có dòng `status = 'sent'` → return ngay (không gọi HTTP/SMTP lần hai kể cả khi process từng crash trước lúc kịp commit `OutboxMessage.Status = 'processed'`). Nếu chưa gửi thành công → tạo mới (`Guid.CreateVersion7()`) hoặc cập nhật dòng hiện hữu sang `status = 'queued'`, `error = null`, rồi commit scope ngắn để giải phóng kết nối DB trước khi gọi mạng.
     - **Sau khi gọi mạng:** mở scope ngắn cập nhật dòng `NotificationLog` thành `status = 'sent'`, `sent_at = clock.UtcNow.UtcDateTime` (nếu thành công), hoặc `status = 'failed'`, `error = safeErrorCode` (nếu thất bại) rồi ném `NotificationDeliveryException(safeErrorCode)` (không gắn `InnerException`) để `OutboxBatchProcessor` điều phối retry.
3. **Retry / Backoff & Dead-letter (`BR-PLT-02`):**
   - Dùng nguyên cơ chế của `OutboxBatchProcessor` (`OutboxBatchProcessor.cs:55-63,98-105`): tối đa `10` lần thử; lần `1..9` lỗi thì `available_at = UtcNow + min(2^attempts, 360)` phút (`2, 4, 8, 16, 32, 64, 128, 256, 360` phút); lần `10` lỗi thì chuyển `status = 'dead'`, `last_error = 'handler_failed'`.
4. **Timeout HTTP & SMTP:**
   - `TelegramNotificationChannel`: `HttpClient.Timeout = TimeSpan.FromSeconds(10)`, `SocketsHttpHandler.ConnectTimeout = TimeSpan.FromSeconds(5)`, `AllowAutoRedirect = false`.
   - `SmtpNotificationChannel`: `ConnectTimeout = 5s`, tổng thời gian thực thi mỗi email `15s` qua `CancellationTokenSource` liên kết với `stoppingToken`.
5. **Giới hạn tốc độ Telegram (Rate Limit):**
   - `TelegramRateLimiter` theo từng `chat_id` (dựa trên `TimeProvider` để fake clock test không bị sleep thật): giãn cách tối thiểu `1.1s`/tin trên cùng `chat_id` và tối đa `18` tin/phút/`chat_id` (dưới ngưỡng 20 tin/phút vào group của Telegram).
   - Khi Telegram trả HTTP `429 Too Many Requests`: không block giữ luồng dispatcher; ghi nhận `telegram_rate_limited` và ném lỗi để outbox đẩy lùi `available_at` bằng backoff bền vững trên DB.
6. **`DailyShopDigestJob` (`PeriodicJob` dưới `JobExecutionGuard`):**
   - Kế thừa `PeriodicJob("DailyShopDigest", TimeSpan.FromMinutes(5), ...)` theo đúng mẫu đã kiểm chứng của `ReconcileInventory` (`ReconcileInventory.cs:15-96`):
     - Giữ session lock `pg_try_advisory_lock` riêng theo khóa `PeriodicJob.AdvisoryLockKey("DailyShopDigest")`.
     - Trong `ExecuteJobAsync(AppDbContext db, CancellationToken ct)` chỉ chạy LINQ `AsNoTracking()` và `db.OutboxMessages.Add` trên đúng `db`/transaction bị `JobExecutionGuard` khóa chặt (`JobExecutionGuard.cs:116` chặn mọi raw SQL, connection phụ và side effect ngoài).
     - Kiểm tra `now >= BusinessCalendar.ToUtc(vietnamDate, new TimeOnly(8, 0))`, kiểm tra `notify.telegram_chat_id` đã cấu hình, và đối chiếu `db.OutboxMessages.AsNoTracking()` trong nửa mở `[dayStartUtc, nextDayStartUtc)` xem đã tồn tại message digest của `vietnamDate` chưa. Đã tồn tại → chốt `_pendingVietnamDate = vietnamDate` rồi thoát ngay (process restart lúc `08:05` hay 2 instance chạy song song đều không bao giờ tạo message thứ hai). Chỉ cập nhật `_lastCompletedVietnamDate` bên trong `OnJobCommitted()`.

---

## 4. Bảo mật: Secret, Logging, chống lộ PII / Giá vốn và Escaping

1. **Secret & Env (`02-architecture.md` §8 dòng `239-243`, `06-infrastructure.md` dòng `64, 135`):**
   - Chỉ đọc từ biến môi trường / `IConfiguration` (`TELEGRAM_BOT_TOKEN`, `SMTP_HOST`, `SMTP_PORT`, `SMTP_USERNAME`, `SMTP_PASSWORD`, `SMTP_FROM_EMAIL`, `SMTP_FROM_NAME`, `SMTP_USE_TLS`, `ADMIN_BASE_URL`), tuyệt đối không lưu DB `platform.settings`, không commit vào `appsettings*.json`.
   - Override `Notifications:Telegram:BaseUrl` hoặc tắt `SMTP_USE_TLS` chỉ được phép khi môi trường thuộc `Development`/`CI`/`Test`; ở `Production` bắt buộc HTTPS tới `api.telegram.org` và TLS cho SMTP.
   - **Chặn rò rỉ `TELEGRAM_BOT_TOKEN` qua log URI của `HttpClient`:** Telegram nhúng token vào đường dẫn `/bot<token>/sendMessage`; mặc định `IHttpClientFactory` gắn logger ghi nguyên URI ở mức `Information`. Trong triển khai G3, `TelegramHttpClient` dùng trực tiếp `SocketsHttpHandler` (`PooledConnectionLifetime = 2m`, `ConnectTimeout = 5s`, `AllowAutoRedirect = false`, không cài thêm `Microsoft.Extensions.Http` theo khóa `0` package mới) **kết hợp** thêm `builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None)` trong `Program.cs` (cùng chỗ với filter EF Core tại `Program.cs:45-48`).
   - `platform.notification_logs` chỉ lưu `recipient_hash = SHA-256` hex chữ thường (`schema.sql:125`) và `error` theo mã tĩnh; log stdout dùng `[LoggerMessage]` cố định không chứa địa chỉ nhận, nội dung tin hay exception thô.
2. **Không chứa PII khách hàng hay Giá vốn (trích dẫn đặc tả):**
   - `04-business-rules.md:306` (`BR-PLT-03` — `order_placed_shop`): cấm SĐT và địa chỉ; chỉ cho phép **mã đơn, tổng tiền (`bigint` VND format dấu chấm phân tách nghìn), phương thức thanh toán, số món, link Admin** (`/orders/{orderId}`). Cấm thêm tên khách, email khách, ghi chú đơn theo `NFR-04` (`01-requirements.md:201`).
   - `04-business-rules.md:311` (`BR-PLT-03` — `daily_digest_shop`): cấm toàn bộ **PII**. Phần tồn thấp / lệch kho chỉ xuất số đếm + mã `SKU` ASCII và `available/threshold` — **không xuất tên nghệ nhân hay tên sản phẩm** (vì tên nghệ nhân là tên người, mặc định phân loại `Pii` theo `02-architecture.md:126`).
   - `01-requirements.md:40` & `02-architecture.md:216,235`: nhân viên `sales` trong nhóm nhận tin shop không có `costs.read`, nên mọi model render thông báo bị khóa kiểu tĩnh ở tầng `Application` **không có trường `avgCostVnd`, `unitCostVnd`, `grossProfitVnd`**.
3. **Escaping chống Injection:**
   - **Telegram:** cố định `parse_mode = "HTML"`, `disable_web_page_preview = true`. Mọi biến động truyền vào template bắt buộc qua `TelegramHtml.Escape` (`&` → `&amp;`, `<` → `&lt;`, `>` → `&gt;`, `"` → `&quot;`, loại ký tự điều khiển); thuộc tính `href` chỉ nhận URL ghép từ `ADMIN_BASE_URL` đã validate `Uri.TryCreate` (không `UserInfo`) + đường dẫn nội bộ cố định. (Kèm hàm `TelegramMarkdownV2.Escape` cho bộ ký tự `_*[]()~`>#+-=|{}.!\` có unit test đầy đủ phòng trường hợp mở rộng).
   - **Email:** kiểm tra địa chỉ bằng `MailAddress.TryCreate` nghiêm ngặt (giống `SettingsCatalog.cs:88`), từ chối mọi chuỗi `Subject` / `FromName` / `To` chứa `\r`, `\n` hoặc control char để chặn **CRLF Header Injection**; phần HTML body mã hóa biến qua `HtmlEncoder.Default.Encode`.

---

## 5. Cơ sở dữ liệu (DB & Migration)

- **Các bảng cần dùng đã có đủ từ `P0-04` / `P0-09` trên `origin/main` (`e63e90b`):**
  - `platform.outbox_messages` ([schema.sql](../spec/schema.sql) dòng `85-96`, `AppDbContext.cs:83-92`).
  - `platform.notification_logs` ([schema.sql](../spec/schema.sql) dòng `121-133`, `AppDbContext.cs:113-124`, index `ix_notif_ref`, `tp_app` đã có đủ `SELECT, INSERT, UPDATE, DELETE` tại `GrantsSql.cs:41`).
  - `platform.settings` ([schema.sql](../spec/schema.sql) dòng `40-45`, `AppDbContext.cs:41-47`, có sẵn cột `updated_at` làm watermark mốc bật kênh).
- **Kết luận:** **KHÔNG cần bảng mới, KHÔNG cần cột mới, KHÔNG tạo EF Core migration, KHÔNG sửa `AppDbContextModelSnapshot.cs`.**
  - Về xung đột migration/snapshot: **KHÔNG (`G3 chạy ngay` nếu owner chọn Phương án 1A ở `GAP-01`; nếu owner chọn Phương án 1B để đọc trực tiếp bảng SQL của `P2-01` thì vẫn `0` migration mới ở `P2-13` nhưng rebase sau khi `P2-01` merge).**

---

## 6. API và Admin UI cấu hình

- **API cấu hình đã có sẵn từ `P0-09`:** `GET/PUT /api/v1/admin/settings/notify` ([05-api.md](../spec/05-api.md) dòng `245`, `SettingsEndpoints.cs:19-36`, `SettingsCatalog.cs:35,43,86-88`), yêu cầu `AdminSecurity.Ready` + `settings.write` (`owner`) + CSRF, có kiểm tra `version` chống ghi đè và audit tự động che `notify.telegram_chat_id`, `notify.shop_emails` thành `"[redacted]"` (`AuditValuePolicy.cs:176,187`).
- **Không thêm endpoint HTTP mới, không đổi `packages/api-client/openapi.json` hay `schema.ts`, không làm Admin UI** (`07-roadmap.md:98` không yêu cầu UI ở `P2-13`). Secret kênh gửi cấu hình thuần qua biến môi trường. *(Xem thêm `ASSUMPTION-01` ở Mục 9 về thao tác xóa rỗng `notify.telegram_chat_id`).*

---

## 7. Kế hoạch file dự kiến trong G3

Giữ trọn phạm vi trong `backend/src/`, `backend/tests/` và `docs/plans/p2-13-implementation.md` (không sửa `Directory.Packages.props`, `packages.lock.json`, `apps/`, `packages/` hay `infra/.env.example` để tuân thủ nguyên tắc tối giản và nhánh kiểm chứng CI backend tại `06-infrastructure.md:220`):

1. `backend/src/TeaPremium.Application/Notifications/INotificationChannel.cs` *(mới)* — cổng `INotificationChannel` (`02-architecture.md:162`) và model `NotificationMessage`.
2. `backend/src/TeaPremium.Application/Notifications/NotificationContracts.cs` *(mới)* — hằng số template/kênh, payload outbox `NotificationDelivery` & `OrderPlaced`, port `IDailyDigestRead` (tách Kho/Platform hiện hữu và port mở rộng CRM/Sales).
3. `backend/src/TeaPremium.Application/Notifications/NotificationTemplates.cs` *(mới)* — template tiếng Việt lưu trong code (`BR-PLT-03`), `TelegramHtml.Escape`, `TelegramMarkdownV2.Escape`, chống CRLF email.
4. `backend/src/TeaPremium.Infrastructure/Notifications/NotificationOptions.cs` *(mới)* — nạp/validate cấu hình `TELEGRAM_BOT_TOKEN`, `SMTP_*`, `ADMIN_BASE_URL`, timeouts từ `IConfiguration`.
5. `backend/src/TeaPremium.Infrastructure/Notifications/TelegramNotificationChannel.cs` *(mới)* — gửi Telegram Bot API qua `HttpClient` + `TelegramRateLimiter` dùng `TimeProvider`.
6. `backend/src/TeaPremium.Infrastructure/Notifications/SmtpNotificationChannel.cs` *(mới)* — gửi email qua giao thức SMTP (BCL + `ISmtpTransport` nội bộ phục vụ test không mạng), chống header injection.
7. `backend/src/TeaPremium.Infrastructure/Notifications/NotificationDeliveryHandler.cs` *(mới)* — `IOutboxHandler` cho `NotificationDelivery`, khóa `pg_advisory_xact_lock` + ghi `platform.notification_logs` (`recipient_hash` SHA-256) bằng scope độc lập chống gửi trùng khi rollback savepoint.
8. `backend/src/TeaPremium.Infrastructure/Notifications/OrderPlacedNotificationHandler.cs` *(mới)* — `IOutboxHandler` cho `OrderPlaced`, lọc `is_backfilled` và watermark `settings.updated_at`, fan-out ra `NotificationDelivery` cho Telegram + `notify.shop_emails`.
9. `backend/src/TeaPremium.Infrastructure/Notifications/DailyShopDigestJob.cs` *(mới)* — `PeriodicJob` dưới `JobExecutionGuard` chạy lúc/sau `08:00` giờ VN, đọc trạng thái hiện tại, chống gửi trùng khi restart.
10. `backend/src/TeaPremium.Infrastructure/Notifications/EfDailyDigestRead.cs` *(mới)* — truy vấn `AsNoTracking` cho tồn thấp, lệch kho, outbox `dead` (và các bảng P2 theo chốt `GAP-01`).
11. `backend/src/TeaPremium.Infrastructure/Notifications/NotificationRegistration.cs` *(mới)* — đăng ký DI, `TelegramHttpClient` dùng trực tiếp `SocketsHttpHandler` (`PooledConnectionLifetime = 2m`, `ConnectTimeout = 5s`, `AllowAutoRedirect = false`) thay vì `AddHttpClient`/`IHttpClientFactory` (repo không cài `Microsoft.Extensions.Http` và khóa `0` package NuGet mới, đồng thời tránh hoàn toàn logger URI mặc định của `HttpClientFactory`).
12. `backend/src/TeaPremium.Application/Settings/SettingsCatalog.cs` *(sửa)* — cho phép `notify.telegram_chat_id` nhận `null` hoặc `""` để tắt kênh Telegram (`ASSUMPTION-01 = A`, không đổi OpenAPI).
13. `backend/src/TeaPremium.Infrastructure/Inventory/InventoryAlertHandler.cs` *(sửa)* — nối cảnh báo Telegram tức thời có watermark + chống lặp theo ngày VN cho `reconciliation_mismatch` (`GAP-03 = 3A`); giữ nguyên `low_stock` chỉ log nội bộ (không gửi tin lẻ).
14. `backend/src/TeaPremium.Infrastructure/Outbox/OutboxBatchProcessor.cs` *(sửa)* — gọi hook thông báo Telegram tức thời có watermark khi message **khác** `NotificationDelivery` chuyển sang `dead` (`GAP-03 = 3A`), đồng thời lưu mã lỗi tĩnh an toàn cho `NotificationDeliveryException`.
15. `backend/src/TeaPremium.Api/Program.cs` *(sửa)* — thêm log filter chặn `System.Net.Http.HttpClient`, gọi `AddNotifications` và đăng ký hosted service `DailyShopDigestJob`.
16. `backend/tests/TeaPremium.UnitTests/NotificationTemplateAndSecurityTests.cs` *(mới)* — bộ unit test template, escaping, watermark, rate limit, `notify.telegram_chat_id` null/rỗng.
17. `backend/tests/TeaPremium.IntegrationTests/NotificationFakes.cs` *(mới)* — fake `HttpMessageHandler` cho Telegram + loopback `TcpListener` fake SMTP server trên `127.0.0.1:0`.
18. `backend/tests/TeaPremium.IntegrationTests/NotificationsOutboxAndDigestTests.cs` *(mới)* — bộ integration test PostgreSQL 17 Testcontainers (đủ 6 chỉ số digest P2-01 + cảnh báo tức thời 3A + chống lặp/watermark/bảo mật).
19. `docs/plans/p2-13-implementation.md` *(cập nhật ở G3)* — điền kết quả chạy thật và manifest SHA-256.

---

## 8. Kế hoạch kiểm thử (Unit + Integration PostgreSQL 17, 0 gọi mạng thật)

### 8.1 Unit Tests (`NotificationTemplateAndSecurityTests.cs` — `12` tests)
1. `BR_PLT_03_OrderPlacedShopTemplateRendersVietnameseFieldsWithoutPiiOrCosts`: kiểm đúng mã đơn, tổng tiền VND, phương thức, số món, link Admin; không có SĐT/địa chỉ/tên khách/giá vốn.
2. `BR_PLT_03_DailyDigestShopTemplateRendersAllSectionsAndCapsLowStockSkuListWithoutArtisanOrProductNames`: kiểm đủ 6 mục của `BR-PLT-03`, cắt gọn danh sách SKU tồn thấp, không chứa tên người/giá vốn và tổng độ dài `< 4096` ký tự Telegram.
3. `TelegramHtmlEscapeNeutralizesTagsQuotesAmpersandsAndControlCharacters`: chống HTML/link injection (`<a href=...>`, `<script>`, `&`, `"`, ký tự điều khiển) trên mọi biến động.
4. `TelegramMarkdownV2EscapeCoversAllReservedCharacters`: kiểm đủ tập ký tự đặc biệt `_*[]()~`>#+-=|{}.!\`.
5. `EmailHeaderValidationRejectsCrlfAndControlCharactersInSubjectRecipientAndSender`: chặn CRLF header injection (`\r\nBcc:`).
6. `RecipientHashComputesDeterministicLowercaseSha256WithoutPlaintext`: khớp quy ước `schema.sql:125`.
7. `ChannelWatermarkSkipsEventsOlderThanSettingUpdatedAtAndAllowsNewerEvents`: chứng minh logic chặn backlog cũ trước mốc bật kênh.
8. `BackfilledOrderIsIgnoredByOrderPlacedShopFanOut`: đơn `is_backfilled = true` (`BR-ORD-09`) không sinh thông báo.
9. `ShopEmailsDeduplicatesCaseInsensitivelyAndSkipsWhenEmpty`: khử trùng lặp `Shop@x.vn` / `shop@x.vn`.
10. `TelegramRateLimiterEnforcesPerChatSpacingAndMinuteWindowWithFakeTimeProvider`: kiểm giãn cách tốc độ theo `chat_id` không cần `Thread.Sleep` thật.
11. `NotificationOptionsRejectsInsecureOverridesInProductionAndValidatesTimeouts`: chặn HTTP không TLS hoặc custom Telegram host ở `Production`.
12. `DailyDigestScheduleBoundaryUsesAsiaHoChiMinh0800`: kiểm biên `07:59:59` VN (chưa chạy) vs `08:00:00` VN (`01:00:00` UTC — chạy) qua `BusinessCalendar`.

### 8.2 Integration Tests trên PG17 Testcontainers (`NotificationsOutboxAndDigestTests.cs` — `10` tests)
1. `BR_PLT_03_BR_PLT_04_OrderPlacedFansOutToFakeTelegramAndLoopbackSmtpAndPersistsHashedNotificationLogs`: luồng đầy đủ `OrderPlaced` → `OutboxBatchProcessor` → fake Telegram HTTP + loopback `TcpListener` SMTP → `platform.notification_logs` ghi `status = 'sent'`, `sent_at != null`, `recipient_hash` đúng SHA-256, không lưu plaintext email/`chat_id`.
2. `PartialChannelFailureDoesNotResendSucceededTelegramWhenSmtpFailsAndRetries`: fake Telegram trả `200 OK`, fake SMTP trả `421` ở lần 1 rồi `250 OK` ở lần 2 → Telegram chỉ nhận đúng **1** request, SMTP nhận 2 lần thử và gửi thành công ở lần retry sau backoff.
3. `IdempotentRedeliveryOfSameNotificationMessageAfterSimulatedPostSendCrashDoesNotDuplicate`: giả lập lỗi sau khi `TelegramNotificationChannel`/`SmtpNotificationChannel` đã gửi thành công và `notification_logs` đã lên `sent` nhưng trước khi `OutboxMessage` kịp commit `processed` → chạy lại `OutboxBatchProcessor` chuyển `OutboxMessage` sang `processed` và số request tới fake server vẫn bằng **1**.
4. `BR_PLT_02_TransientFailuresBackoffUpToTenAttemptsThenTransitionToDeadWithSafeErrorCodes`: fake Telegram trả `429` (`retry_after`) / `500` và timeout → kiểm chính xác `attempts = 1..10`, `available_at = min(2^attempts, 360)` phút, lần 10 sang `status = 'dead'`, `notification_logs.status = 'failed'`, không thử lần 11.
5. `BR_INV_11_BR_PLT_03_DailyDigestRunsAt0800VietnamUnderAdvisoryLockAndSurvivesRestartWithoutDuplicate`: fake clock ở `07:59` VN (`0` message) → `08:00` VN (`1` digest gồm đúng tồn thấp hiện tại + `outbox dead`) → tạo mới instance `DailyShopDigestJob` giả lập restart lúc `08:05` VN + 2 instance tranh `pg_try_advisory_lock` song song → tổng số digest của ngày VN đó vẫn đúng **1**, sang `08:00` ngày VN kế tiếp mới gửi bản mới.
6. `BR_INV_11_BR_INV_13_NoStaleP1BacklogReplayWhenEnablingChannels`: tạo sẵn các dòng `InventoryAlert(low_stock)` đã `processed`/`pending`, `InventoryAlert(reconciliation_mismatch)` của ngày cũ hoặc đã sửa khớp, và `OrderPlaced` có `occurred_at` trước thời điểm `PUT /api/v1/admin/settings/notify` bật kênh → chạy `OutboxBatchProcessor` và `DailyShopDigestJob` → chứng minh 0 tin lẻ lỗi thời nào bị phát lại, chỉ trạng thái tồn thấp **hiện còn hiệu lực** xuất hiện trong digest `08:00`.
7. `UnconfiguredChannelsCompleteCleanlyWithoutBlockingOutboxOrFailingExistingP110Flows`: khi chưa cấu hình `notify.*` hoặc chưa cấp `TELEGRAM_BOT_TOKEN`/`SMTP_HOST`, outbox xử lý gọn không treo retry.
8. `SecurityZeroSecretPiiOrCostLeakAcrossPayloadsLogsDatabaseAndExceptions`: gắn `ILoggerProvider` thu toàn bộ JSON log trong cả ca `200 OK` và ca lỗi `500`/timeout/auth-fail; kiểm tra toàn bộ request gửi đi, `platform.outbox_messages`, `platform.notification_logs`, `platform.audit_logs` và log ứng dụng **tuyệt đối không chứa** `TELEGRAM_BOT_TOKEN` (kể cả qua `HttpClient` URI log), `SMTP_PASSWORD`, SĐT/địa chỉ/tên khách, tên nghệ nhân (`Chu Bân`) hay `avg_cost_vnd`.
9. `SettingsNotifyGroupPutRequiresOwnerAuditsRedactedValuesAndUpdatesWatermark`: kiểm `403` với `sales`/`content`, `200` với `owner`, `audit_logs` che `"[redacted]"` cho cả `notify.telegram_chat_id` và `notify.shop_emails` (`AC-P0-04`).
10. `RuntimeRoleTpAppCanReadSettingsAndWriteNotificationLogsAndAcquireAdvisoryLock`: kết nối thực bằng role `tp_app` xác minh quyền đọc/ghi `platform.notification_logs`, `platform.outbox_messages`, `platform.settings` và `pg_advisory_xact_lock` không dùng `tp_owner`.

---

## 9. Rủi ro, ASSUMPTION và GAP cần Owner quyết định

### 9.1 Các mục GAP / ASSUMPTION đã được Owner chốt ngày `23/11/2026`

1. **`GAP-01` → ĐÃ CHỐT `1B` (`23/11/2026`):** Rebase lên `origin/main` đã merge `P2-01` (`85218d1`) và truy vấn thật đủ 6 chỉ số `daily_digest_shop` trên `sales.orders`, `sales.payments`, `crm.interactions`, `crm.customers`, `catalog.variants`, `inventory.stock_levels`, `inventory.stock_movements`, `platform.outbox_messages`; `0` migration mới, không đụng `AppDbContextModelSnapshot.cs`.
2. **`GAP-02` → ĐÃ CHỐT `2A` cửa sổ `24 giờ` (`ASSUMPTION` owner duyệt `23/11/2026`):**
   - *Chờ thanh toán sắp hết hạn:* `status = 'pending' AND is_backfilled = false AND payment_method = 'bank_transfer' AND payment_status = 'unpaid' AND now < payment_due_at <= now + 24h` (hằng số dùng chung `NotificationContracts.OrderPaymentExpiringWindow = TimeSpan.FromHours(24)`).
   - *Đơn chờ xác nhận:* `status = 'pending' AND is_backfilled = false` và loại các đơn đã thuộc nhóm chờ thanh toán (`payment_method = 'bank_transfer' AND payment_status IN ('unpaid', 'partially_paid')`).
3. **`GAP-03` → ĐÃ CHỐT `3A` (`23/11/2026`):** Gửi Telegram tức thời có chống lặp/watermark cho `reconciliation_mismatch` mới (`InventoryAlertHandler`, template `inventory_reconciliation_shop`) và outbox chuyển `dead` (`OutboxBatchProcessor`, loại trừ `Type = NotificationDelivery`, template `outbox_dead_shop`) + tổng hợp trong `daily_digest_shop`; `low_stock` chỉ nằm trong digest `08:00`. Không replay backlog P1.
4. **`ASSUMPTION-01` → ĐÃ CHỐT `A` (`23/11/2026`):** `notify.telegram_chat_id` nhận `null` hoặc `""` trong `SettingsCatalog.cs` để tắt kênh Telegram, không đổi OpenAPI.
5. **`ASSUMPTION-02` → ĐÃ CHỐT (`23/11/2026`):** Giao vận SMTP bằng BCL `.NET 10` sau interface `ISmtpTransport`, `0` package NuGet mới, không sửa lockfile.

### 9.2 Rủi ro kỹ thuật, giảm thiểu và khắc phục G4 (`25/11/2026`)
- **Lộ `TELEGRAM_BOT_TOKEN` qua default logger của `HttpClient`:** giảm thiểu bằng `SocketsHttpHandler` trực tiếp + `LogLevel.None` cho `"System.Net.Http.HttpClient"` + integration test thu toàn bộ log kiểm chứng không xuất hiện chuỗi token.
- **Vòng lặp dead-letter vô tận khi Telegram lỗi:** không bao giờ phát tiếp cảnh báo `outbox_dead` cho các dòng outbox có `Type == "NotificationDelivery"`.
- **Xung đột song song khi merge sau `P1-13` / `P2-01` / `P2-02`:** `P2-13` chỉ chạm `Program.cs` trong nhóm file nóng (`WORKFLOW.md` §3 dòng `42`); trước publish chạy `base-integration` + focused G4 theo đúng quy trình.
- **Remediation sau G4 BLOCK (manifest cũ `247b671c…`):**
  - `F1 [P1]`: `NotificationOptions.Validate(environment.EnvironmentName)` đồng bộ `MediaRegistration.cs:19` — chỉ cho `SmtpUseTls = false` hoặc Telegram `http://` khi môi trường thuộc `Development`/`CI`/`Test` **và** host là loopback (`localhost`/`127.0.0.1`/`::1`); mọi môi trường khác (kể cả `Staging`) bắt buộc TLS + `https://api.telegram.org`. Bổ sung phòng thủ chiều sâu trong `BclSmtpTransport.SendAsync`: nếu chưa TLS mà có `Username` với host không phải loopback → ném `NotificationDeliveryException("smtp_tls_required_for_auth")` trước khi gửi `AUTH`.
  - `F2 [P1]`: Bổ sung `STARTTLS` cho `LoopbackSmtpServer` dùng chứng chỉ tự sinh hoàn toàn trong bộ nhớ (`RSA` + `CertificateRequest`, không ghi `.pfx`/`.pem` ra đĩa) và seam `internal` duy nhất cho test cấu hình `X509ChainPolicy` `CustomRootTrust` (không dùng `RemoteCertificateValidationCallback` trả `true`, production không có đường bỏ qua kiểm chứng chỉ). Đã test đủ 3 kịch bản: từ chối tự ký mặc định (`smtp_tls_failed`, 0 `AUTH`), thành công qua `CustomRootTrust` (`EHLO` → `STARTTLS` → `EHLO_TLS` → `AUTH` → `DATA`), và từ chối lệch `TargetHost`/SAN.
  - `F3 [P2]`: Siết `EmailSecurity.IsValidEmailAddress` chặn khoảng trắng, `<`, `>`, `"`, thiếu dấu chấm tên miền hoặc dấu chấm đầu/cuối/`..`, khớp tinh thần regex `schema.sql:563`, kèm bộ `InlineData` đầy đủ.
  - `F4 [P2]`: `NotificationOptions.Validate` từ chối tường minh `SmtpPort == 465` với thông báo dùng STARTTLS cổng `587`; ghi nhận follow-up `P3-10` nếu nhà cung cấp SMTP thực tế cần implicit TLS `465`.
- **Base-integration lên `862fbf05fc8b9f9c34fc0441e559bb13e24604a3` (`PR #45 P2-02` + `PR #46 P2-05`):**
  - Hợp nhất `Program.cs` (giữ đủ `CustomerOpenApi`/`AddCrm`/`MapCustomers`, `AddOrders`, và bộ lọc `System.Net.Http.HttpClient = LogLevel.None`/`AddNotifications`/`DailyShopDigestJob`).
  - Hợp nhất `InventoryAlertHandler.cs` (giữ kiểm lệch cả `on_hand` lẫn `Σ reservations(active) == reserved` của `P2-05` + gọi `IInventoryMismatchNotifier` của `P2-13`; đồng bộ `EfDailyDigestRead.cs` và `InventoryReconciliationAlertModel`/`NotificationTemplates.cs` để hiển thị lệch `reserved`, kèm test `P205ReservedMismatchTriggersReconciliationTelegramAlertAndDigestCountWithAntiDuplicate`).
  - Tích hợp `OrderPlacedNotificationHandler` vào hợp đồng `G22` của `P2-05` (`IOrderOutboxStep` dưới `CompositeOrderOutboxHandler` thay vì đăng ký `IOutboxHandler` trùng `MessageType = "OrderPlaced"` gây lỗi `SingleOrDefault`), đọc chỉ cột an toàn không PII/giá vốn từ `sales.orders` + `sales.order_lines`.
