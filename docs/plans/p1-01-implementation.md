# P1-01: Migration schema `media`, `catalog`, `inventory`, `pricing.sale_prices`, `content.redirects`

- **Gate/status:** PROPOSED — **HOLD / NOT AUTHORIZED G3** đến khi B01 amendment qua independent G4/CI/merge, Closure #1 merge SHA/CI xác minh và owner giao việc riêng. Không còn gate Closure #2 MFA recovery/hai owner/manual HTTPS cho migration trên dữ liệu giả. Nghiệm thu HTTPS/TOTP/backup trước dữ liệu thật/go-live theo B01. C1 A01 đã xong, C2/C3 kiểm lại theo §0.
- **Baseline review A01 (lịch sử):** PR #20 merge/spec SHA `024ea998c43baf0afde30b069ed6b4876cb96f4a`, PR #21 evidence MERGED. **Closure #1:** [PR #22](https://github.com/jimthanhnobi/tra-hien/pull/22) merge SHA `606fd2e279eb766976db18473cd45e7cbd142108`; G4 PASS theo reviewer owner cung cấp, CI PR run 44270404911 và merge run 44271243633 đều completed/success, 3/3 jobs PASS mỗi run. Base P1-01 phải là `origin/main` mới nhất sau B01, không dùng SHA lịch sử để cấp G3.
- **Source/traceability:** `docs/spec/07-roadmap.md:51` P1-01 đã amendment theo A01 và merge qua PR #20; `schema.sql` các khối MEDIA/CATALOG/INVENTORY/PRICING(sale_prices)/CONTENT(redirects) là DDL đích đã amendment; chia bảng/DDL mới đã owner-approved, implementation còn HOLD. `03-database.md` §1/§3.1–3.3/§3.7–3.8/§4/§8; `02-architecture.md` §3/§5.2/§5.4; DEC-01/04/05/06/20/21/22, NFR-04/08/09; FR-CAT-01…09/11, FR-INV-01…06; BR-CAT-02/04/08/10/13, BR-INV-01/02/07/10, BR-PRC-01/02. Nền cho AC-P1-01…08, không tự nghiệm thu các AC nghiệp vụ trong P1-01. BR-CAT-14/15, BR-IAM-07/08, DEC-25…30, AC-P0-06/AC-P1-09/10 đã được định nghĩa trong authoritative specs được duyệt và merge.
- **Evidence reuse:** G1 `docs/analysis/spec-inventory.md` @ `cadce575597738bf40ad7c45689482de5a5fbc3d`; G2 `docs/architecture/conformance-review.md`, `docs/plans/task-readiness.md`. A01 đã kiểm diff tới baseline (02/03/07/schema), rà scoped P0/P1/SEO: **TARGETED**, không full audit mới. Phiên cập nhật hồ sơ xác minh spec tại PR HEAD không khác merge revision; không audit/review lại A01. Các trạng thái trước G4 trong hồ sơ G1/G2 là lịch sử; nguồn hiện hành ở [evidence A01](../reviews/a01-amendment-evidence.md). G2 lịch sử không cấp approval P1-01.
- **Đối chiếu nguồn bổ sung:** `docs/reviews/P0-closure-and-P1-readiness-review.md` §5.2/§6 và `C:/Users/ADMIN/Downloads/PROMPTS-p0-closure.md` bước 0–5; các sửa prompt đề nghị ghi tại ADR §7. Không sửa hai nguồn này. A/B đã được amendment trong workflow, vẫn G4 độc lập/CI/owner publish; không cắt evidence theo giới hạn trang.

## 0. Điều kiện vào G3 (phải đủ cả 3 và owner giao task)

| # | Điều kiện | Bằng chứng cần có |
|---|---|---|
| C1 | Owner ACCEPTED A01, cho phép amendment; đã áp/review ranh giới bảng, D5 active/inactive/giá NULL, SKU, video, count/manual noindex (không nới metadata), grants và rowVersion trong authoritative specs | **Hoàn tất cho amendment A01:** owner approval + targeted G1/G2; G4 tầng A PASS trước publish/merge theo bằng chứng reviewer do owner cung cấp; PR #20 merge/spec SHA `024ea998c43baf0afde30b069ed6b4876cb96f4a`. CI PR HEAD run 44250706587 và merge SHA run 44251560930: completed/success, ba jobs PASS, đã xác minh riêng. Chi tiết nguồn/log tại evidence A01; không chứng minh runtime/closure |
| C2 | Gate B01 (thay A01 D9): Closure #1 đã merge, G4/CI đúng PR/merge SHA; amendment B01 đã G4/CI/merge; scoped readiness P0/role/preflight đủ cho migration DB giả. Không yêu cầu Closure #2, hai owner hay diễn tập HTTPS thủ công trước P1-01 | **PENDING B01/owner task:** PR #22 merged và CI PR/merge PASS; B01 G4/CI/merge chưa thực hiện. Owner chưa giao G3. Nghiệm thu HTTPS/owner TOTP/backup trước dữ liệu thật/go-live |
| C3 | Contract opaque rowVersion/xmin, mapping kiểu/constraint; role/helper D11 có trong base | **PARTIAL:** role SQL, helper GrantsSql, migration closure và dev-migrate đã được merge PR #22; CI merge-head PASS, mapping/naming evidence triển khai còn cần ở đúng slice P1-01. Không tái triển khai Closure #1 hoặc sửa migration lịch sử |

Owner đã phê duyệt thay gate D9 bằng B01: P1-01 được phép chuẩn bị trên DB giả khi B01 merge/G4/CI và #1 CI đủ, nhưng vẫn phải có lệnh G3 riêng. TOTP, HTTPS, backup cho môi trường dùng dữ liệu thật là gate vận hành, không phải điều kiện chạy migration DDL với fixture giả. D12 giới hạn audit thực tế theo FR-PLT-01 trước P1-04, không chặn P1-01.

## 1. Dependencies

P0-01…P0-12 đã có implementation ở baseline. Closure #1 PR #22 đã merge; B01 bỏ yêu cầu implement Closure #2 MFA recovery/coverage. Chưa ghi P0 DONE hoặc production-ready vì nghiệm thu HTTPS/owner/backup thực tế NOT RUN. Không gộp P1-01 với sửa P0; scoped C1–C3 + owner G3 vẫn bắt buộc.

D1 hoãn **cả** reservations và `fk_reservations_line` tới P2-05 sau sales.order_lines P2-01; reviews/review_media tới P5-07 sau sales/CRM. G03 marketing FK vẫn cần chốt trước P2-01, không chặn P1-01. R2 token thật không cần để chạy migration bằng dữ liệu giả; generator/publish/upload/SSR nghiệm thu đúng task sau.

**Ranh giới hành vi sau A01:** P1-05 kiểm SKU bất biến/retry có giới hạn, giá khi publish/kích hoạt, thin-description/manual noindex và metadata hiện hành; P1-07 không tự upsert thiếu khóa ổn định, chi tiết import/key còn OPEN tại đó. P1-08/14 lọc người đọc inventory hạn chế chỉ còn/hết, không lượng/giá vốn. P1-10 xử lý InventoryAlert nội bộ an toàn/idempotent, retry có giới hạn; P2-13 không phát lại cảnh báo P1 cũ. Đây là acceptance/dependency của task sau, không thêm endpoint, importer hoặc consumer vào P1-01.

## 2. Current coverage (tái sử dụng)

- Đã có `backend/src/TeaPremium.Infrastructure/Persistence/AppDbContext.cs`, `EfUnitOfWork.cs` cùng thư mục; `backend/tests/TeaPremium.IntegrationTests/PlatformSchemaParityTests.cs` và `PlatformDatabaseTests.cs` làm mẫu, chưa chứng minh DDL P1/quyền D11.
- **Đã merge Closure #1 qua PR #22:** `infra/postgres/init-roles.sql`, `init-roles.sh`, `preflight.sql`, `protect-credentials.sql`; `backend/src/TeaPremium.Infrastructure/Persistence/GrantsSql.cs`; `scripts/dev-migrate.ps1`; migration `202611180710_ReconcileRuntimeGrants`. CI merge-head PASS; không tự coi tests của P1 đã PASS.
- `docs/plans/p0-closure-acceptance.md`: DB disposable automation đã có chứng cứ Closure #1; thao tác operator HTTPS/backup/owner thật vẫn NOT RUN và là gate trước dữ liệu thật. Owner B/Staff UI không bắt buộc; `tp_readonly` chỉ backup, điều tra bằng quyền riêng không đọc IAM/DP keys mặc định.
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

- Base hiện có **4 migration**: InitialPlatform, StaffIam, SettingsAdministrativeData, ReconcileRuntimeGrants (Closure #1). P1-01 chỉ bổ sung migration mới, không sửa migration nào đã có ở **base triển khai thực tế**.
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

**Trạng thái amendment:** A01 MERGED, G4 tầng A PASS theo evidence reviewer do owner cung cấp; CI PR HEAD và merge SHA xác minh riêng PASS. Task record P1-01 vẫn HOLD / NOT AUTHORIZED G3 vì closure #1/#2/nghiệm thu và C3 chưa đủ. Bản cập nhật hồ sơ sau merge bàn giao **READY_FOR_LOCAL_G4 tầng B** riêng; không yêu cầu chạy lại G4 tầng A của A01.
