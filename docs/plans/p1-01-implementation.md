# P1-01: Migration schema `media`, `catalog`, `inventory`, `pricing.sale_prices`, `content.redirects`

- **Gate/status:** PROPOSED — **HOLD / NOT AUTHORIZED** vào G3 tới khi đủ §0 và owner giao việc riêng. A01 đã OWNER APPROVED; amendment specs/workflow local READY_FOR_LOCAL_G4, chưa G4 PASS. P1-01 cần review tầng A độc lập và owner giao G3 riêng.
- **Baseline review:** HEAD = `origin/main` = `288ca8b55cab2017c9ee42bd35036e2d2e5bd87c`, nhánh `planning/a01-p0-closure`. **Base/spec SHA triển khai:** chưa có; điền SHA sau amendment và **toàn bộ P0 closure** đã merge, không chỉ grants. Không lấy SHA review làm bằng chứng closure đã xong.
- **Source/traceability:** `docs/spec/07-roadmap.md:51` P1-01 đã sửa local theo A01, chờ independent G4 amendment; `schema.sql` các khối MEDIA/CATALOG/INVENTORY/PRICING(sale_prices)/CONTENT(redirects) là DDL đích đã amendment; chia bảng/DDL mới đã owner-approved, implementation còn HOLD. `03-database.md` §1/§3.1–3.3/§3.7–3.8/§4/§8; `02-architecture.md` §3/§5.2/§5.4; DEC-01/04/05/06/20/21/22, NFR-04/08/09; FR-CAT-01…09/11, FR-INV-01…06; BR-CAT-02/04/08/10/13, BR-INV-01/02/07/10, BR-PRC-01/02. Nền cho AC-P1-01…08, không tự nghiệm thu các AC nghiệp vụ trong P1-01. BR-CAT-14/15, BR-IAM-07/08, DEC-25…30, AC-P0-06/AC-P1-09/10 đã được định nghĩa trong authoritative specs local theo approval; còn chờ G4 amendment.
- **Evidence reuse:** G1 `docs/analysis/spec-inventory.md` @ `cadce575597738bf40ad7c45689482de5a5fbc3d`; G2 `docs/architecture/conformance-review.md`, `docs/plans/task-readiness.md`. Đã kiểm diff tới baseline (02/03/07/schema), rà scoped P0/P1/SEO: **TARGETED**, không full audit mới hoặc G4. Sau amendment phải kiểm revision/gaps lại; G2 lịch sử không cấp approval P1-01.
- **Đối chiếu nguồn bổ sung:** `docs/reviews/P0-closure-and-P1-readiness-review.md` §5.2/§6 và `C:/Users/ADMIN/Downloads/PROMPTS-p0-closure.md` bước 0–5; các sửa prompt đề nghị ghi tại ADR §7. Không sửa hai nguồn này. A/B đã được amendment trong workflow, vẫn G4 độc lập/CI/owner publish; không cắt evidence theo giới hạn trang.

## 0. Điều kiện vào G3 (phải đủ cả 3 và owner giao task)

| # | Điều kiện | Bằng chứng cần có |
|---|---|---|
| C1 | Owner ACCEPTED A01, cho phép amendment; đã áp/review ranh giới bảng, D5 active/inactive/giá NULL, SKU, video, count/manual noindex (không nới metadata), grants và rowVersion trong authoritative specs | Owner approval đã có; revision local UNCOMMITTED, DDL PostgreSQL 17 PASS. Còn G4 amendment/spec SHA sau publish; không tự nhận C1 hoàn tất. Cần scoped G1/G2/G4 và log; câu mẫu "Owner đã DUYỆT" trong prompt không thay approval |
| C2 | Đóng P0 theo **D9/prompt bước 2–5**: closure #1 role/grant/runtime/runbook, #2 MFA/coverage warning; owner nghiệm thu HTTPS/password/TOTP/settings/audit và hai owner/thiết bị; cập nhật hồ sơ trạng thái | SHA merge #1/#2/hồ sơ + G4/CI tại từng published head; `docs/plans/p0-closure-acceptance.md` có kết quả thật |
| C3 | Contract opaque rowVersion/xmin, mapping kiểu thực và cách xử lý implicit constraint names được chốt; role/helper D11 đã có trước migration | Đoạn spec được duyệt + artifact chung; danh sách naming theo §3.6, không dùng quyền owner cho runtime |

Owner đã chỉ định trong yêu cầu finalize: **closure #1, #2 và nghiệm thu trước P1-01**. C2 thống nhất mốc đó, mạnh hơn review gốc §6 (grants trước/cùng P1-01, MFA trước dữ liệu thật), không gán gate mới cho reviewer gốc. ADR đã ACCEPTED nhưng amendment G4 NOT RUN, chưa cho phép G3. D12 audit theo entity giữ hạn **trước P1-04** theo R2/I-04, không là blocker P1-01. Audit/recovery state tối thiểu cho CLI phải có ở closure #2.

## 1. Dependencies

P0-01…P0-12 đã có implementation ở baseline; DoD P0 còn closure/evidence R1/R3/R14, review ghi CI **OWNER-REPORTED**. Không ghi toàn P0 DONE khi chưa đủ C2. Closure #1 remediation P0-02/P0-04 (dùng test P0-09/10), #2 P0-08/P0-12; không tạo ID roadmap mới. Mỗi lần giao một slice, không gộp closure và P1-01.

D1 hoãn **cả** reservations và `fk_reservations_line` tới P2-05 sau sales.order_lines P2-01; reviews/review_media tới P5-07 sau sales/CRM. G03 marketing FK vẫn cần chốt trước P2-01, không chặn P1-01. R2 token thật không cần để chạy migration bằng dữ liệu giả; generator/publish/upload/SSR nghiệm thu đúng task sau.

**Ranh giới hành vi sau A01:** P1-05 kiểm SKU bất biến/retry có giới hạn, giá khi publish/kích hoạt, thin-description/manual noindex và metadata hiện hành; P1-07 không tự upsert thiếu khóa ổn định, chi tiết import/key còn OPEN tại đó. P1-08/14 lọc người đọc inventory hạn chế chỉ còn/hết, không lượng/giá vốn. P1-10 xử lý InventoryAlert nội bộ an toàn/idempotent, retry có giới hạn; P2-13 không phát lại cảnh báo P1 cũ. Đây là acceptance/dependency của task sau, không thêm endpoint, importer hoặc consumer vào P1-01.

## 2. Current coverage (tái sử dụng)

- Đã có `backend/src/TeaPremium.Infrastructure/Persistence/AppDbContext.cs`, `EfUnitOfWork.cs` cùng thư mục; `backend/tests/TeaPremium.IntegrationTests/PlatformSchemaParityTests.cs` và `PlatformDatabaseTests.cs` làm mẫu, chưa chứng minh DDL P1/quyền D11.
- **Chưa tồn tại, dự kiến closure #1:** `infra/postgres/init-roles.sql` (SQL chung), `infra/postgres/init-roles.sh` (wrapper init truyền biến), `backend/src/TeaPremium.Infrastructure/Persistence/GrantsSql.cs` (thống nhất path, không dùng ví dụ Persistence/Migrations trong prompt), `scripts/dev-migrate.ps1` (preflight trước toàn chuỗi). Không gọi helper dự kiến là coverage đã có.
- `docs/plans/p0-closure-acceptance.md` đã được bổ sung như thiết kế/runbook amendment, toàn bộ runtime NOT RUN; owner B tạo và kiểm bằng Admin API bảo vệ, không Staff UI. tp_readonly chỉ backup kiểm soát; điều tra bằng admin/audit hoặc identity read riêng tối thiểu, không IAM credentials/DP keys mặc định.
- `scripts/migrations.ps1` tồn tại, chỉ sinh SQL idempotent; startup EF design-time dùng Infrastructure.
- **Thiếu cần bổ sung trong task này:**
  - Mapping `long/long?→bigint`, `decimal/decimal?→numeric(12,2)` đúng cột EAV (vòng cuối AppDbContext hiện ném lỗi nếu không có mapping). Không đặt precision chung cho mọi decimal hoặc tạo cột giả smallint/double; rating smallint thuộc review P5-07.
  - `uint Version` ↔ **system xmin** (`IsRowVersion()`), không để convention snake_case ghi đè tên thành version/cột uint thường.

## 3. Scope

**Trong phạm vi**

1. Một migration bổ sung `P1_01_CatalogInventoryFoundation` (không sửa migration cũ).
2. Đúng 25 bảng ở 5 schema dưới + sequence SKU. Cột/kiểu/nullability/default/CHECK/FK/UNIQUE/EXCLUDE/index tương đương **schema.sql sau amendment được duyệt**; approval A01 chỉ cho amendment, chưa cho triển khai code.

| Schema | Bảng |
|---|---|
| `media` | `assets`, `upload_sessions` |
| `catalog` | `product_types`, `attribute_definitions`, `attribute_options`, `materials`, `artisans`, `categories`, `products`, `product_categories`, `product_relations`, `variants`, `product_attribute_values`, `variant_attribute_values`, `product_media`, `collections`, `collection_products` + sequence `sku_number_seq` |
| `inventory` | `stock_levels`, `stock_movements`, `receipts`, `receipt_lines`, `stocktakes`, `stocktake_lines` |
| `pricing` | `sale_prices` |
| `content` | `redirects` |

3. EF entity persistence (POCO trong Infrastructure hoặc Domain tối giản **không có hành vi**) đủ để snapshot khớp DDL. Hành vi Domain thuộc P1-03 trở đi.
4. Grants qua helper closure #1 cho đúng 5 schema, gồm USAGE SKU sequence, quyền gọi public.f_unaccent và REVOKE UPDATE/DELETE/TRUNCATE stock_movements; giữ quyền âm audit/ledger/readiness cũ. Không tạo lại roles hoặc viết grants thay thế trong test.
5. Mapping kiểu (§2), xmin cho `products`, `variants`, `receipts`, `stocktakes`; test EF conflict, không thêm endpoint chỉ để test HTTP 409. Cột `stock_levels.reserved` có ngay P1-01 theo DDL, không hoãn sang P2-05 như câu ở review §5.3.
6. Constraint **chưa đặt tên** trong DDL: đặt tên theo 03 §1, ghi mục **Chênh lệch đặt tên** với tên PostgreSQL tự sinh tương ứng. Tên explicit khớp chính xác; tên implicit chỉ ngoại lệ naming đã báo reviewer/owner, không đổi cấu trúc/ý nghĩa. Khác biệt cấu trúc phải HOLD, không tự sửa spec.

**Ngoài phạm vi:** reservations/FK (P2-05), reviews/review_media (P5-07), vouchers/redemptions (P2-01); use case/endpoint/UI, seed P1-03; generator SKU/publish/word count/luật giá NULL P1-05/07, upload R2 P1-02, SSR/robots/canonical/sitemap/feed P3; `[Audited]` catalog/phân loại D12 trước P1-04. Chỉ tạo persistence/constraints mới đã duyệt, không tuyên bố các hành vi trên đã chạy.

P1-01 không nới validation metadata, không triển khai upsert/import matching, MFA recovery hoặc InventoryAlert. Các quyết định finalize trong ADR §8 chỉ ảnh hưởng DDL nền/điều kiện vào task và acceptance ở đúng pha.

## 4. Acceptance criteria (tiêu chí task, không cấp AC authoritative mới)

| # | Tiêu chí | Nguồn |
|---|---|---|
| A1 | **Given** PostgreSQL 17 rỗng đã provision role bằng `infra/postgres/init-roles.sql`, **when** migrate bằng tp_owner, **then** đúng 25 bảng/sequence §3, không reservations/reviews/review_media/vouchers. Nâng DB ở head **P0 closure** có dữ liệu giả → giữ IAM/settings/audit/ledger/history | P1-01, D1/D11 |
| A2 | Parity DB schema đích được duyệt (DB tham chiếu riêng) và DB EF: PK/FK/UNIQUE/CHECK/EXCLUDE/index, cột/kiểu/nullability/default/sequence khớp cấu trúc/biểu thức, không chỉ đếm. Tên explicit khớp, implicit chỉ ngoại lệ §3.6. Chỉ so tập P1, không bỏ qua FK lạc sang bảng deferred | 03 §1 |
| A3 | `dotnet ef migrations has-pending-model-changes` → không còn thay đổi | 03 §8 |
| A4 | SQLSTATE: ck_pav_one_value/ck_vav_one_value → 23514; ux_product_media_cover, SKU/sku_base UNIQUE → 23505; ex_sale_prices_no_overlap → 23P01; ck_stock_reserved/ck_mv_direction/ck_mv_reason/regex slug-SKU-sku_base/ck_variants_price/ck_products_word_count/ck_products_published/CHECK posted receipt → 23514. Giá NULL được CHECK cho phép, kể cả fixture inactive của product active; không kiểm nhầm bằng CHECK liên bảng. MIME/purpose/bytes kiểm trên **cả** upload_sessions/assets theo DDL đã duyệt | schema.sql, A01 |
| A5 | Login thật `tp_app`: DML catalog fixture, nextval SKU và f_unaccent được; UPDATE/DELETE/TRUNCATE stock_movements, CREATE TABLE schema mới → 42501; không owner/superuser/CREATEROLE/CREATEDB. `tp_readonly` backup: SELECT được, INSERT/UPDATE/DELETE/nextval/DDL bị từ chối; credential không dùng điều tra. Quyền điều tra riêng không thừa kế backup/owner, mặc định không đọc IAM credentials/DP xml. Quyền âm audit/ledger không bị nới; chạy lại không đổi dữ liệu/quyền | 03 §4, D11 |
| A6 | ix_products_search GIN gin_trgm_ops; search_text fixture chuẩn hóa chữ thường/bỏ dấu theo BR-CAT-10; query qua f_unaccent trả đúng dữ liệu. Không khẳng định planner dùng index trên bảng nhỏ hoặc đã benchmark | 03 §3.7 |
| A7 | long/long? ↔ bigint, decimal/decimal? EAV ↔ numeric(12,2), uint Version ↔ **system xmin**, không ADD COLUMN xmin/version thường. Hai DbContext tải cùng variant → lần lưu đầu đổi xmin, lần hai DbUpdateConcurrencyException, không ghi đè | 03 §1, D13 |
| A8 | Down trên DB dùng một lần, không writer: từ chối **trước khi xóa** nếu bất kỳ bảng §3 có dữ liệu (kể cả media/redirects/sale_prices), hoặc có dependency/object ngoài scope. Nếu rỗng, bỏ đúng object migration sở hữu, giữ platform/IAM/snapshot trước, Up lại được. Không DROP SCHEMA CASCADE làm mất object khác | 03 §8, NFR-08, review §6.3 |
| A9 | Sinh script idempotent và **thực thi hai lần** trên DB test, history không trùng; regression P0 PASS. Local G3/G4 ghi CI NOT RUN tới khi được phép publish; G4 PASS + owner cho publish rồi mới kiểm 3 job tại **published head SHA** | 06 §7, WORKFLOW §5 |

## 5. Cases

- **Happy:** migrate từ rỗng và DB head P0 closure có dữ liệu giả; fingerprint dữ liệu cũ giữ nguyên.
- **Bad:** thiếu role → preflight dev-migrate trước **toàn chuỗi**, helper kiểm đầu migration mới, nêu `infra/postgres/init-roles.sql`. Không sửa P0-09 cũ để giả vờ helper chạy trước nó. Ownership/quyền bất thường trên existing DB → HOLD remediation theo object, không xóa dữ liệu/chạy runtime bằng owner.
- **Boundary:** price NULL được ở DB, 1/10 000 000 000 được, 0/âm/10 000 000 001 lỗi. Inactive được NULL dù product active; draft được thiếu giá. Publish đòi giá của active, kích hoạt phải validate và active đã publish không được sửa giá về NULL: use case P1-05 kiểm, không suy ra từ CHECK. Cả upload_sessions/assets: 0/+1 vượt ngưỡng bị chặn; ảnh đúng 15 728 640, video evidence đúng 52 428 800 được; video catalog/content/branding, purpose lạ, SVG/HTML bị từ chối. Count âm bị chặn.
- **SKU:** test UNIQUE/CHECK/nextval song song không trùng số, giữ đầy đủ số qua 9999→10000. Không thêm DEFAULT giả cho sku_base hoặc tuyên bố đã có app generator; retry/manual collision/stability test ở P1-05/07 theo ADR.
- **Abuse/permission:** quyền `tp_app` ở A5.
- **Concurrency:** A7 (EF xmin); chưa có HTTP 409 use case. Tồn độc bản P1-08, publish/import race và manual noindex P1-05/07, không thêm endpoint chỉ để test P1-01.

## 6. Tests/evidence (kế hoạch G3 — hiện NOT RUN)

```powershell
dotnet restore backend/TeaPremium.sln --locked-mode
dotnet build backend/TeaPremium.sln --no-restore -warnaserror
dotnet test backend/TeaPremium.sln --no-build
dotnet tool restore
dotnet tool run dotnet-ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure
./scripts/migrations.ps1
corepack pnpm run check:api
git diff --check
```

Ghi **PASS/FAIL/NOT RUN**, exit code, số test và log/path cho từng nhóm empty/existing/missing-role/parity/permission/Down/idempotent execution. EF/script generation dùng design-time connection loopback không truy cập DB thật như scripts/migrations.ps1. Sinh được SQL không thay cho đã áp trên PostgreSQL 17. Các lệnh này chưa chạy trong review tài liệu; frontend/CI report cũ không là kết quả của thay đổi tương lai.

## 7. Migration/recovery

- Baseline có **3 migration**: InitialPlatform, StaffIam, SettingsAdministrativeData; closure sẽ thêm migration riêng. P1-01 chỉ bổ sung, không sửa bất kỳ migration nào đã có ở **base triển khai thực tế**.
- Down chỉ trên dev/test dùng một lần đủ A8. Revert PR/source **không** tự rollback DB; có dữ liệu thì migration sửa bổ sung hoặc recovery backup/runbook được owner duyệt, không xóa volume/DB để áp role/grant.
- Từ lần dữ liệu thật đầu tiên (ảnh P1-02/API P1-05/import P1-07/UI P1-12): chỉ roll forward, backup trước migration; không chờ riêng P1-12/go-live. Kiểm ownership/data trước Up.
- Helper sinh **SQL cố định lúc tạo migration**; sửa helper tương lai không làm đổi migration lịch sử. Idempotent re-run giữ dữ liệu/sequence/quyền, Down P1 không thu hồi quyền nền platform/IAM.

## 8. Observability/security/privacy

- Không kết luận "không có PII": supplier_name, artisan name, free-text note/reason, original_name và UUID nhân viên có thể nhận diện người. Chỉ fixture giả, không log payload/credential; D12 phân loại cụ thể khi audit, mặc định che.
- Giá vốn (`avg_cost_vnd`, `unit_cost_vnd`) nằm ở `inventory`. Quyền xem giá vốn được kiểm ở API (P1-08/P1-14), không ở DB.
- `stock_movements` append-only bằng quyền DB (A5).
- D4 giữ robots_noindex thủ công và count dẫn xuất; không thêm trigger/job hoặc reset cờ owner trong migration. Nếu có product cũ, phải review backfill count/sku_base riêng, không suy lịch sử hoặc gán count=0 rồi tuyên bố index đúng.

## 9. Checklist review G4 (tầng A)

- [ ] Diff gồm tracked + staged + **untracked** so với `origin/main`.
- [ ] Đúng danh sách bảng §3; bảng hoãn không lọt vào; không có use case/endpoint/UI.
- [ ] C1–C3/owner giao G3, base/spec SHA thật; không gán gate mới cho reviewer gốc §6.
- [ ] Đồng bộ ADR §3/§8: inactive được NULL, metadata không nới; không upsert thiếu khóa/logic InventoryAlert hoặc use case khác lọt vào migration task.
- [ ] Parity test so cấu trúc từng constraint/index, không chỉ đếm.
- [ ] Role SQL/helper chung đúng path; test không có GRANT thay thế; existing data/ledger/sequence giữ nguyên.
- [ ] Test quyền dùng `tp_app` đăng nhập thật, không `SET ROLE`.
- [ ] Tiền là `long`/`bigint`; không có `decimal` cho tiền; `numeric(12,2)` chỉ cho giá trị thuộc tính.
- [ ] Không ADD COLUMN xmin/version thường; xmin là system column, test EF concurrency A7 có.
- [ ] `Down` từ chối khi có dữ liệu (A8).
- [ ] Danh sách "Chênh lệch đặt tên" đầy đủ, không tự sửa `schema.sql`.
- [ ] Migration cũ không đổi; grants lịch sử cố định, script idempotent chạy hai lần; không DROP CASCADE/revert source thay cho DB rollback.
- [ ] VERIFIED / NOT RUN rõ; G4 không tự cấp publish/merge, CI kiểm riêng đúng published SHA khi owner cho phép.

## 10. Prompt giao việc

```text
Chỉ dùng prompt này SAU owner phê duyệt/giao task, amendment được review và đủ C1–C3.
Use $implementation-slice for approved P1-01 in docs/spec/07-roadmap.md.
Task record: docs/plans/p1-01-implementation.md. Kiểm §0/base/spec SHA thực; thiếu thì HOLD và báo.
Làm đúng §3, đáp ứng A1–A9, chạy lệnh §6 và ghi kết quả thật vào §6.
Một main thread. Không sửa spec/migration cũ hoặc gộp P0 closure vào slice này.
Không commit/push/open-update PR/merge. Trả READY_FOR_LOCAL_G4 rồi dừng.
```

```text
Use $review-gate cho P1-01, session mới, cùng worktree. Dùng checklist §9 của docs/plans/p1-01-implementation.md.
Chạy lại test; tự kiểm A4/A5 bằng SQL trên container PostgreSQL 17 dùng một lần.
Trả PASS / CONDITIONAL / BLOCK, finding có file:line. Read-only.
```

**OPEN:** chi tiết khóa variant/import lại/ô trống/nhóm mâu thuẫn/tính nguyên tử thuộc P1-07 (ADR §8), không chặn P1-01. Operator/allowlist, R2 và test evidence hoàn tất theo runbook/đúng task; không ghi là quyết định sản phẩm chưa chốt của P1-01.

**Bàn giao amendment:** READY_FOR_LOCAL_G4. ADR ACCEPTED bởi owner; task record thiết kế P1-01 chưa cấp quyền G3, closure #1/#2/nghiệm thu và G4 amendment vẫn là điều kiện phải hoàn tất.
