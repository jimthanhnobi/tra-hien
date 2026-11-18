# Rà soát độc lập: đóng P0 và sẵn sàng P1 — Trà Hiên V2

- **Repo/commit rà soát:** `jimthanhnobi/tra-hien` @ `288ca8b55cab2017c9ee42bd35036e2d2e5bd87c` (main, 57 commit)
- **Ngày:** 18/11/2026 · **Loại:** phân tích chỉ đọc. Không sửa spec, không code, không commit, không chạm DB.
- **Phạm vi đã đọc:** `docs/spec/*` (đối chiếu với bản spec gốc 16/11), `AGENTS.md`, `docs/WORKFLOW.md`, `docs/analysis/*`, `docs/architecture/*`, `docs/plans/*` (P0-01…P0-12, task-readiness, F-02), mã nguồn backend (Api, Application, Infrastructure, Domain, migrations, tests), `infra/*`, `.github/workflows/ci.yml`, 2 app Next.js.

## Mức độ kiểm chứng

| Hạng mục | Trạng thái |
|---|---|
| Đọc code + đối chiếu spec/test | **VERIFIED** (đọc trực tiếp) |
| Frontend `pnpm install --frozen-lockfile`, `lint`, `typecheck` | **PASS** (chạy thật) |
| Frontend unit test (store 2/2, admin 8/8) | **PASS** (chạy thật) |
| Admin Playwright E2E | **NOT RUN** — máy rà soát thiếu đúng bản Chromium Playwright cần (lỗi môi trường, không phải lỗi code) |
| Backend build + ~264 test (.NET 10 + Testcontainers) | **NOT RUN** — môi trường rà soát không tải được .NET SDK/NuGet |
| GitHub Actions 3 job PASS tại `288ca8b` | **OWNER-REPORTED**, không tự xác minh được (API GitHub bị chặn trong phiên) |
| Compose full-stack, HTTPS Admin, TOTP thật, production | **NOT RUN** |

---

## 1. Tóm tắt điều hành

1. **Chất lượng P0 cao hơn mức thường gặp.** UoW/transaction, audit cùng transaction và che PII, idempotency chịu đồng thời, outbox `SKIP LOCKED`, job dùng advisory lock, IAM (lockout cửa sổ 15 phút, TOTP chống replay, `security_stamp` thu hồi phiên, chặn xóa owner cuối cùng), CSRF chạy trước binding. Mỗi phần có test tích hợp trên PostgreSQL thật. Cả 5 AC-P0 đều có test tương ứng.
2. **P0 chưa nên đóng hoàn toàn.** Có 2 khoảng trống làm DoD P0 chưa được chứng minh theo một đường chạy thống nhất:
   - **(a) Quyền DB (`tp_app`/`tp_readonly`/`tp_owner`) chỉ có trong test harness**, không có artifact triển khai. Migration P0-09 sẽ **lỗi** trên DB dev/production nếu role chưa tồn tại.
   - **(b) Compose dev không đăng nhập Admin được** (cần HTTPS). Phải chạy Caddy native + migrate tay + bootstrap tay.
3. **Có 1 vấn đề nền tảng sẽ ảnh hưởng thẳng P1:** chính sách giá trị audit (`AuditValuePolicy`) là allowlist **toàn cục theo tên field**. Với catalog, nó sẽ ghi `[redacted]` cho `name`, `slug`, `sku`, `sale_price_vnd`, `avg_cost_vnd`, `on_hand`, các trạng thái `visible/hidden/published`… Kết quả là audit đổi giá/đổi tên sản phẩm (FR-PRC-04, BR-PRC-03) gần như vô dụng. Cần chuyển sang phân loại theo entity trước P1-04/P1-05.
4. **P1 khả thi, không cần đổi kiến trúc Modular Monolith.** Chia migration theo mốc FK là đủ: reviews để P5-07, reservations để P2-05, redirects tối thiểu vào P1-01.
5. **Quyết định P1-01: CONDITIONAL GO**, kèm 3 điều kiện ở §6.

---

## 2. Đánh giá đóng P0

### 2.1 Đối chiếu AC-P0

| AC | Test bằng chứng (tên test) | Implementation | Ghi chú |
|---|---|---|---|
| AC-P0-01 khóa 5 lần sai / 15 phút | `T04T05BR_IAM_01FiveFailuresLockExactly15MinutesAndRevokeSessions` | PASS (đọc code `StaffIdentity.FailedAsync`: cửa sổ trượt, khóa 15 phút, đổi stamp) | Backend test NOT RUN tại máy rà soát |
| AC-P0-02 owner chưa TOTP ở Production chỉ gọi `auth/*` | `T10T11T12T13BR_IAM_02ProductionOwnerEnrollment…` | PASS (`AdminSecurity.Principal`: `staff_ready` chỉ khi đủ factor; policy permission đòi `staff_ready`) | |
| AC-P0-03 POST thiếu CSRF → 400/403 | `T18T19MissingInvalidAndCrossUserCsrfRejectedBeforeEffects…`, `T13RealAuthorizationAndAntiforgeryRunBeforeEveryReplay` | PASS (trả 400 `csrf_invalid`) | |
| AC-P0-04 sửa setting có audit trước/sau | `ApiUpdateIsAuditedAndNoopDoesNotWrite`, `SensitiveValuesAreRedactedAndAuditFailureRollsBack` | PASS | Giá trị nhạy cảm bị che đúng 02 §5.4 |
| AC-P0-05 test kiến trúc chặn EF ở Application và `DateTime.Now` | `DomainHasNoPackageReferencesAndApplicationHasNoEfCoreReference`, `ClockRuleRejectsDeliberateCompiledViolation` | PASS | |

### 2.2 Đối chiếu DoD P0 (`07-roadmap.md` §P0)

| DoD | Kết luận | Bằng chứng |
|---|---|---|
| `docker compose -f infra/compose.dev.yml up` chạy được | **Chạy được nhưng chưa đủ dùng.** API không tự migrate (đúng spec); DB dev do Compose tạo **không có role `tp_app`/`tp_readonly`**, nên migration `202611180001_SettingsAdministrativeData` sẽ lỗi ở `GRANT … TO tp_app` | `Migrations/202611180001_SettingsAdministrativeData.cs:12,55–58`; `infra/compose.dev.yml` không có init script; `infra/` không có `postgres/init-roles.*` |
| Owner đăng nhập Admin bằng TOTP | **Chưa chứng minh theo đường Compose.** `infra/README.md` ghi Compose HTTP không đăng nhập được; phải dùng Caddy native (`infra/caddy/Caddyfile.admin-local`) + API chạy ngoài container | `infra/README.md` đoạn đầu; `docs/plans/p0-12-implementation.md:109–130` |
| CI xanh | **OWNER-REPORTED** | — |

### 2.3 Ba trạng thái tách bạch

- **Implementation:** PASS. Mọi P0-01…P0-12 có code + test tương ứng.
- **CI:** PASS theo owner báo. Rà soát này tự xác nhận được phần frontend (lint/typecheck/unit).
- **Production readiness:** **NOT READY** (đúng kỳ vọng, thuộc P3-10). Chưa có `compose.prod`, role provisioning, backup/restore, chứng chỉ Data Protection, quy trình khôi phục TOTP.

**Khuyến nghị:** đóng P0 sau một đợt **"P0 closure" ngắn (≈ 1–2 ngày)** gồm I-01, I-02, I-03 ở §4: thêm artifact role/grant, chạy một runbook nghiệm thu tích hợp có ghi kết quả thật, cập nhật tài liệu trạng thái. Không cần mở lại task P0 nào.

---

## 3. Rủi ro theo thứ tự ưu tiên

Mức độ theo `AGENTS.md`: P0 = mất dữ liệu/lộ bí mật/vượt quyền; P1 = hỏng nghiệp vụ bắt buộc/toàn vẹn schema/thiếu test quan trọng; P2 = bảo trì/biên/quan sát; P3 = tùy chọn.

| # | Mức | Rủi ro | Bằng chứng | Ảnh hưởng | Sửa tối thiểu | Trước |
|---|---|---|---|---|---|---|
| R1 | **P1** | Quyền DB chỉ tồn tại trong test harness; không có artifact triển khai versioned | `IntegrationTests/P009Harness.cs:46` (GRANT toàn bộ + REVOKE audit/ledger); `PlatformDatabaseTests.cs:33` (CREATE ROLE); `infra/` không có script role; migration chỉ GRANT ledger | Migration P0-09 lỗi trên DB không có role. Dev/prod có thể chạy API bằng superuser, khiến audit/sổ kho "append-only" chỉ còn là lời hứa. P1-01 thêm 4 schema mới cũng cần grants | Một nguồn duy nhất: `infra/postgres/roles.sql` (tạo role, idempotent) + một migration "grants" idempotent cho mọi schema (kèm REVOKE theo 03 §4). Test harness **dùng lại chính script đó** thay vì chuỗi GRANT riêng | **P1-01** |
| R2 | **P1** | `AuditValuePolicy` là allowlist toàn cục theo tên field; khớp chuỗi con `name`/`account`/`note` | `Persistence/AuditValuePolicy.cs:30–43,63–75,128–143` | Catalog/pricing/inventory sau khi gắn `[Audited]` sẽ ghi `[redacted]` cho tên, slug, SKU, giá KM, giá vốn, trạng thái `visible/hidden/published`. Mâu thuẫn ý đồ 02 §5.4 ("tiền, trạng thái, giá, giá vốn ghi giá trị thật"). Về sau `customer_account_id`, `product_name` cũng bị che | Thêm phân loại **theo entity** (`entityType → {field: Safe | Pii | Secret}`), mặc định vẫn che. Mỗi task gắn `[Audited]` phải khai báo phân loại và có test | **P1-04** (entity audited đầu tiên của catalog) |
| R3 | **P1** | DoD P0 chưa có một đường nghiệm thu tích hợp lặp lại được | §2.2 | Chưa ai chứng minh "máy sạch → migrate → bootstrap → TOTP → settings → audit" bằng một runbook | Runbook `docs/plans/p0-closure-acceptance.md` + script; ghi output thật | Đóng P0 |
| R4 | **P1 (vận hành)** | Owner duy nhất mất thiết bị TOTP thì không có đường khôi phục | `docs/plans/p0-08-implementation.md:50–55`; `StaffIdentity.BootstrapAsync` từ chối khi đã có owner; `PatchAsync` chặn xóa owner cuối | Bị khóa khỏi Admin vĩnh viễn, chỉ còn cách sửa DB tay (đúng thứ spec cấm) | Chốt **break-glass CLI** chạy trên server: cần quyền shell + `tp_owner`, ghi audit, đổi stamp, buộc cài lại TOTP. Hoặc bắt buộc có 2 owner TOTP độc lập trước khi có dữ liệu thật | Trước dữ liệu thật (cuối P1/P2) |
| R5 | P1 | Chia DDL P1-01 theo mốc FK chưa chốt (G21, G02) | `schema.sql`: `catalog.product_reviews` FK `sales.order_lines`/`crm.customers`; `inventory.reservations` FK `sales.order_lines` (thêm sau); BR-CAT-08 cần `content.redirects` từ P1-04 | Migration P1-01 hỏng, hoặc tạo bảng mồ côi khó sửa | Xem §5.2 | **P1-01** |
| R6 | P1 | Ràng buộc hàng độc bản không có ở DB (G15); lock/điều kiện chưa thiết kế | `schema.sql` `inventory.stock_levels` chỉ CHECK số dư; BR-INV-09 giao cho app | Hai phiếu nhập/kiểm kê đồng thời có thể làm `on_hand = 2` cho hàng độc bản | Mọi thay đổi `on_hand` đi qua **một** `InventoryLedger` (Infrastructure): `SELECT … FOR UPDATE` dòng `stock_levels` theo `variant_id` tăng dần → gọi `StockLevel.Apply()` thuần Domain → ghi movement + cập nhật trong cùng transaction. Test 2 phiếu nhập đồng thời | P1-08 |
| R7 | P2 | `xmin`/`rowVersion` chưa được hiện thực ở đâu; staff dùng `ConcurrencyStamp` (string), còn 05 §1 nói `rowVersion` là `uint` | `grep xmin` không có kết quả ngoài tài liệu; `StaffContracts.cs:8,19` | Aggregate đầu tiên của P1 sẽ đặt tiền lệ; mỗi task tự chọn thì API không thống nhất | Chốt sớm ở P1-03: map `xmin` bằng `uint Version` + `IsRowVersion()`; API trả `rowVersion` là **chuỗi mờ** (opaque) để tương thích staff. Báo gap spec 05 §1 (uint vs string) | P1-03 |
| R8 | P2 | Mapping kiểu cột của `AppDbContext` ném lỗi với `long`, `decimal`, `short`, `double` | `AppDbContext.cs:136–147` | P1-01 cần `bigint` (tiền), `numeric(12,2)` (thuộc tính số), `smallint` | Bổ sung mapping tường minh + test parity | P1-01 |
| R9 | P2 | Chưa có mẫu Domain/repository nghiệp vụ. Logic IAM nằm ở Infrastructure (ngoại lệ hợp lý vì Identity gắn framework) | `Application/` chỉ có `ISettingsStore`, `IStaffIdentity`; `StaffIdentity.cs` chứa luật owner cuối | Module catalog có thể chép kiểu "store chứa luật" của IAM, trái 02 §4 | P1-03 là **slice mẫu**: entity Domain có invariant, repository chỉ load/save, handler trong Application, test Domain thuần. Ghi thành reference trong `docs/architecture/` | P1-03 |
| R10 | P2 | P1-10 gửi cảnh báo Telegram (BR-INV-11/13) nhưng kênh Telegram thuộc P2-13 | `07-roadmap.md` P1-10 vs P2-13 | P1-10 không nghiệm thu được đầy đủ | P1-10 chỉ ghi outbox + hiển thị dashboard; handler Telegram gắn ở P2-13. Báo gap roadmap | P1-10 |
| R11 | P2 | Upload: video bằng chứng (G05) nhưng `media.upload_sessions` CHECK ≤ 15 MB; `slugHint` thiếu trong API (G14); cần đọc kích thước ảnh ở server | `schema.sql:159`; `05-api.md` upload-intents | Video quay bằng điện thoại thường > 15 MB; tên file SEO không sinh được | Owner chốt: (a) bằng chứng chỉ ảnh, hoặc (b) video ≤ N MB chỉ cho `purpose=evidence`. Thêm `slugHint`. Đọc width/height từ header ảnh (thư viện nhẹ, kiểm license) | P1-02 |
| R12 | P2 | Lộ tồn kho chi tiết cho vai trò `content` (G13) | 02 §7 vs 05 §4.3 | Content thấy `onHand/reserved` và sổ kho | DTO theo quyền + test JSON | P1-08/P1-14 |
| R13 | P2 | Quy trình rất nặng so với quy mô shop | Hồ sơ P0-09 + F-02 > 570 dòng cho một importer bảng tham chiếu đổi vài năm một lần (ledger recovery/re-advance/high-water) | Tốc độ P1–P3 chậm; chi phí review lớn hơn giá trị ở các slice rủi ro thấp | Phân tầng gate theo rủi ro: G4 đầy đủ cho tiền/kho/auth/migration; G4 rút gọn (checklist + diff) cho UI/CRUD đơn giản. **Quyết định của owner**, không phải lỗi code | Từ P1 |
| R14 | P3 | Tài liệu trạng thái cũ (task-readiness "PROPOSED", P0-04 "NOT AUTHORIZED"…) | `docs/plans/task-readiness.md` | Agent đọc nhầm trạng thái | Cập nhật một bảng trạng thái hiện hành ở đầu file | Đóng P0 |

**Không phát hiện** P0 mức "lộ bí mật/vượt quyền đang hoạt động" trong code đã đọc.

---

## 4. Danh sách cải thiện theo pha

| ID | Pha | Việc | Lý do (rủi ro) | Kích thước |
|---|---|---|---|---|
| I-01 | **P0-closure** | Artifact role + grants versioned, test dùng chung | R1 | S |
| I-02 | **P0-closure** | Runbook nghiệm thu tích hợp (DB sạch → roles → migrate → `bootstrap-owner` → Caddy HTTPS → login + TOTP → sửa setting → xem audit → logout); ghi output | R3 | S |
| I-03 | **P0-closure** | Cập nhật `task-readiness.md`, README, báo cáo đóng P0 | R14 | S |
| I-04 | P1 (trước P1-04) | Audit classification theo entity | R2 | S–M |
| I-05 | P1 (P1-01) | Mapping kiểu `bigint/numeric/smallint`, quy ước `xmin` | R7, R8 | S |
| I-06 | P1 (P1-03) | Slice mẫu Domain/repository/handler + ghi reference | R9 | trong P1-03 |
| I-07 | P1 (P1-08) | `InventoryLedger` một cửa + test đồng thời hàng độc bản | R6 | trong P1-08 |
| I-08 | Trước dữ liệu thật | Break-glass TOTP hoặc 2 owner | R4 | S |
| I-09 | P2 | Phân tầng gate review | R13 | quy trình |
| I-10 | P3 | Compose dev có profile Caddy HTTPS để đăng nhập Admin bằng một lệnh | R3 | S |

---

## 5. Phân tích P1-01 … P1-14

### 5.1 Phụ thuộc thực tế (bổ sung so với roadmap, không đổi roadmap)

| Task | Roadmap | Phụ thuộc thực tế cần ghi | Ghi chú |
|---|---|---|---|
| P1-01 | P0 | + I-01 (grants), quyết định chia DDL | |
| P1-02 | P1-01 | + R2 bucket/keys (hoặc S3 giả lập cho dev), chính sách video (R11) | Phụ thuộc ngoài repo |
| P1-03 | P1-01 | — | Slice mẫu (R9) |
| P1-04 | P1-01 | + `content.redirects` (G02) + I-04 | |
| P1-05 | P1-03, P1-04 | **+ P1-02** (ảnh bìa để publish, G32) | |
| P1-06 | P1-05 | `IPricingCalculator(variantIds, at)` là contract cho Orders/Checkout | |
| P1-07 | P1-05 | Chuẩn SKU/cột file với owner | Nên đưa sớm nếu đã có catalog Excel |
| P1-08 | P1-05 | + I-07 | |
| P1-09 | P1-08 | Approve dùng `inventory.approve` | |
| P1-10 | P1-08 | Telegram ở P2-13 (R10) | |
| P1-11 | P1-03, P1-04 | | |
| P1-12 | P1-05 | + P1-02 (UI ảnh) | |
| P1-13 | P1-09 | | |
| P1-14 | P1-05, P1-08 | + G13, quy tắc ẩn số tồn công khai (05 §2) | |

### 5.2 Ranh giới DDL cho P1-01 (đề xuất)

| Bảng | P1-01 | Lý do |
|---|---|---|
| `media.assets`, `media.upload_sessions` | ✓ | `product_media`, cover FK cần |
| `catalog.product_types`, `attribute_definitions`, `attribute_options`, `materials`, `artisans`, `categories`, `products`, `product_categories`, `product_relations`, `variants`, `product_attribute_values`, `variant_attribute_values`, `product_media`, `collections`, `collection_products` | ✓ | |
| `catalog.product_reviews`, `catalog.review_media` | ✗ → **P5-07** | FK `sales.order_lines`, `crm.customers` |
| `inventory.stock_levels`, `stock_movements`, `receipts`, `receipt_lines`, `stocktakes`, `stocktake_lines` | ✓ | |
| `inventory.reservations` | ✗ → **P2-05** | Chỉ có nghĩa khi có đơn; FK `order_line_id` |
| `pricing.sale_prices` | ✓ | Có EXCLUDE GiST (`btree_gist` đã có ở P0-04) |
| `pricing.vouchers/redemptions` | ✗ → P2-01 | Theo roadmap |
| `content.redirects` | ✓ **(cần owner chốt)** | BR-CAT-08 từ P1-04; UI quản lý vẫn ở P3-07 |

### 5.3 Aggregate, bất biến, đồng thời

| Aggregate | Bất biến chính | Đồng thời / transaction |
|---|---|---|
| `ProductType` (+ attributes, options) | `code` duy nhất; `is_variant_axis ⇒ scope=variant`; không xóa attr đang có giá trị | `xmin` |
| `Category` (cây ≤ 3 cấp) | Không vòng; slug đổi → redirect cùng transaction (BR-CAT-08) | Khóa cây khi đổi cha (advisory lock theo namespace catalog) |
| `Product` + `Variant` (+ values, media, categories) | BR-CAT-02/03/04/06/07; SKU bất biến sau movement; 1 cover | `xmin` trên product và variant; publish kiểm toàn bộ rồi trả **tất cả** lỗi |
| `StockLevel` (+ movement ledger) | BR-INV-01/02/05/09; `Σ movement = on_hand` | `FOR UPDATE` theo `variant_id` tăng dần; movement append-only (REVOKE UPDATE/DELETE) |
| `Receipt`, `Stocktake` | `posted`/`approved` bất biến; BR-INV-10 tính delta theo `system_qty_at_count` | `Idempotency-Key` khi post; `xmin` |
| `SalePrice` | Không chồng thời gian (DB EXCLUDE) + `< price_vnd` | Bắt lỗi exclusion → 409 có mã |

**Hướng mở rộng sang CRM/Orders:** `Variant.Id` ổn định và SKU bất biến (đơn snapshot cả hai). `IPricingCalculator` và `IInventoryReservations` công bố ở `Application/<Module>/Contracts`. P2-05 thêm `reservations` và cột `reserved` dùng trong `InventoryLedger` (cùng khóa dòng) để giữ hàng và nhập kho tuần tự hóa với nhau.

### 5.4 Thứ tự triển khai đề xuất

Mục tiêu là sớm nhập được catalog thật. Mọi thứ tự dưới đây đều đúng dependency của roadmap.

```
P0-closure (I-01..I-03)
→ P1-01 (DDL + grants + mapping)
→ P1-03 (slice mẫu: product types/attributes)
→ P1-04 (+redirects, +I-04 audit theo entity)
→ P1-02 (media)               ← cần R2/thiết lập ngoài
→ P1-05 (products/variants/publish)
→ P1-11 → P1-12 (UI nhập catalog)      ⇒ bắt đầu nhập dữ liệu thật song song
→ [P1-07 import nếu đã có Excel]
→ P1-08 → P1-09 → P1-13 (kho + UI kho)  ⇒ tồn đầu kỳ, kiểm kê
→ P1-06 (giá KM) → P1-10 → P1-14
```

Lý do đưa UI lên ngay sau backend tương ứng: nhập vài trăm sản phẩm kèm ảnh, thông số và mô tả là **việc tốn thời gian nhất** của P1, nên cần bắt đầu sớm.

---

## 6. Quyết định cho P1-01: **CONDITIONAL GO**

**Điều kiện bắt buộc trước khi giao G3 P1-01:**
1. **C1:** owner chốt ranh giới DDL ở §5.2 (đặc biệt `content.redirects` vào P1-01, `reservations` → P2-05, reviews → P5-07).
2. **C2:** có quyết định về cơ chế role/grant (I-01). Có thể làm trong P1-01 hoặc ngay trước; P1-01 phải cấp quyền cho schema mới và có test `tp_app` đăng nhập thật.
3. **C3:** quy ước `rowVersion` (chuỗi mờ từ `xmin`) và mapping kiểu cột được ghi trong task record.

**Không phải điều kiện của P1-01** (nhưng phải xong trước task ghi bên cạnh): I-04 audit theo entity (trước P1-04); R2/video/slugHint (trước P1-02); quy tắc 150 từ khi publish (trước P1-05); chuẩn SKU và nguồn giá vốn (trước P1-07/P1-08); break-glass TOTP (trước dữ liệu thật).

### 6.1 Scope P1-01

- Một migration `P1_01_CatalogInventoryFoundation`: tạo schema `media`, `catalog`, `inventory`, `pricing`, (`content` nếu C1) và đúng các bảng ✓ ở §5.2. Tên constraint/index trùng `schema.sql`. Constraint chưa có tên trong `schema.sql` thì đặt tên theo quy ước 03 §1 và **báo chênh lệch** (không tự sửa spec).
- EF mapping (persistence) cho các bảng đó; chưa có use case, endpoint hay UI.
- Grants/REVOKE cho schema mới (C2), gồm `REVOKE UPDATE, DELETE, TRUNCATE ON inventory.stock_movements FROM tp_app`.
- Mapping kiểu (R8) và quy ước `xmin` (R7) có test.

### 6.2 Acceptance tests P1-01

1. Migrate từ DB rỗng qua toàn bộ chuỗi migration → danh sách bảng theo từng schema **đúng bằng** tập dự kiến; bảng hoãn (reviews, reservations) **không** tồn tại.
2. Parity: tên/cột/biểu thức của mọi PK/FK/UNIQUE/CHECK/EXCLUDE/index trong phạm vi khớp `schema.sql` (cùng cách làm với `PlatformSchemaParityTests`).
3. `dotnet ef migrations has-pending-model-changes` = không có thay đổi.
4. Test hành vi ràng buộc (SQLSTATE): `ck_pav_one_value` (0 hoặc 2 giá trị → 23514); `ux_product_media_cover` (2 cover → 23505); `ex_sale_prices_no_overlap` (chồng thời gian → 23P01); `ck_stock_reserved`; `ck_mv_direction`, `ck_mv_reason`; regex SKU và slug; `ck_products_published`; CHECK posted của receipt.
5. Quyền: đăng nhập thật bằng `tp_app` → CRUD được bảng catalog; `UPDATE/DELETE/TRUNCATE stock_movements` → 42501; không được DDL. `tp_readonly` chỉ SELECT.
6. Index tìm kiếm: `ix_products_search` (GIN trigram) tồn tại; truy vấn `search_text LIKE '%am tu sa%'` dùng được `f_unaccent`.
7. Down migration trên DB dùng một lần: quay về đúng snapshot trước P1-01; Up lại thành công.
8. `migrations.sql` (idempotent) sinh được và chứa migration mới; CI 3 job PASS ở đúng head SHA.
9. Hồi quy: toàn bộ test P0 vẫn PASS.

### 6.3 Migration và rollback

- **Trước go-live:** chỉ là migration bổ sung. Rollback = `Down` (drop các schema mới) trên DB dev/test, hoặc revert PR. Không có dữ liệu thật.
- **Nếu đã có dữ liệu catalog thật** (khi bắt đầu nhập liệu ở P1-12): từ thời điểm đó chỉ được **roll forward**. Mọi sửa schema bằng migration mới, backup trước khi migrate (06 §6 rút gọn cho môi trường đang nhập liệu).
- `Down` phải từ chối nếu các bảng catalog/inventory có dữ liệu (cùng tinh thần `Down` của F-02).

### 6.4 G4 Review Gate cho P1-01 (checklist)

- [ ] Diff gồm tracked + staged + **untracked** so với `origin/main`.
- [ ] Không có bảng ngoài §5.2; bảng hoãn có ghi chú task đích.
- [ ] Parity test bao phủ **mọi** constraint/index trong phạm vi, không chỉ đếm số lượng.
- [ ] Grants đến từ artifact chung (I-01), không chuỗi GRANT riêng trong test.
- [ ] `stock_movements` bị REVOKE đúng; test âm quyền chạy bằng `tp_app` đăng nhập thật, không `SET ROLE`.
- [ ] Mapping kiểu: tiền `bigint` ↔ `long`; không có `decimal` cho tiền.
- [ ] `xmin` không xuất hiện trong DDL (cột hệ thống), có test 409 tối thiểu trên một entity.
- [ ] Không có use case/endpoint/UI lọt vào P1-01.
- [ ] Kết quả test ghi rõ VERIFIED / NOT RUN; CI ở đúng SHA đã review.

---

## 7. Quyết định cần owner chốt

| # | Câu hỏi | Trước | Đề xuất |
|---|---|---|---|
| O1 | Ranh giới DDL P1-01 (§5.2), nhất là `content.redirects` | P1-01 | Như §5.2 |
| O2 | Cơ chế role/grant: script role + migration grants idempotent | P1-01 | Có |
| O3 | Catalog hiện có ở Excel không? Bao nhiêu mẫu? Mã sản phẩm hiện tại có dạng gì (ví dụ `8901`)? Bảng giá nhập có map được theo mã không? | P1-05/P1-07/P1-08 | Nếu có Excel → làm P1-07 ngay sau P1-05. Chốt quy tắc SKU khớp regex `^[A-Z0-9][A-Z0-9-]{1,39}$` |
| O4 | Mô tả ≥ 150 từ (BR-SEO-34) là **chặn publish** hay chỉ cảnh báo trong giai đoạn nhập liệu đầu? | P1-05 | Giữ chặn cho trang index, cho phép lưu nháp; hoặc tạm cảnh báo + `noindex` đến khi đủ |
| O5 | Lưu ảnh: đã có Cloudflare R2 (bucket, key, domain `media.`) chưa? Dev dùng R2 bucket riêng hay S3 giả lập? Bằng chứng đổi trả có cần video không, giới hạn bao nhiêu MB? | P1-02 | R2 bucket dev riêng; bằng chứng ảnh + video ≤ 50 MB chỉ cho `purpose=evidence` (sửa CHECK qua quyết định spec) |
| O6 | Khôi phục TOTP: break-glass CLI hay bắt buộc 2 owner? | Trước dữ liệu thật | Cả hai: 2 owner + CLI dự phòng có audit |
| O7 | Phân tầng gate review theo rủi ro | Từ P1 | Có |

## 8. Gap spec phát hiện thêm (báo, không sửa)

| Mã đề xuất | File/mục | Nội dung |
|---|---|---|
| G34 | `05-api.md` §1 vs code IAM | `rowVersion` là `uint` trong spec, staff API dùng chuỗi. Đề xuất spec: `rowVersion` là chuỗi mờ |
| G35 | `07-roadmap.md` P1-10 vs P2-13 | Cảnh báo Telegram của kho phụ thuộc kênh Telegram ở P2 |
| G36 | `schema.sql` `media.upload_sessions` | Giới hạn 15 MB mâu thuẫn video bằng chứng (liên quan G05) |
| G37 | `02-architecture.md` §5.4 vs `AuditValuePolicy` | Spec nói ghi thật tiền/trạng thái/giá/giá vốn; policy hiện tại che theo tên field toàn cục |
| G38 | `07-roadmap.md` P1-01 | Roadmap ghi "theo schema.sql" cho `catalog`/`inventory`, nhưng một phần bảng phải hoãn (G21). Nên ghi rõ danh sách bảng |
