# P1-03 — Product types, typed attributes, options, reference seed

**Cập nhật G4 finding fix 19/11/2026:** owner báo G4 BLOCK vì unit mutability (P1) và NUL input
(P2), cho phép sửa đúng hai finding tại local. Hồ sơ G3 dưới đây được giữ làm lịch sử.
Handoff sửa finding và kết quả hiện hành ở mục cuối [local evidence](p1-03-local-evidence.md).
Không tự thay trạng thái G4/publish thành PASS.

G3 local, 19/11/2026. Base/HEAD `f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7`.
Branch `feature/p1-03-product-types-attributes`, worktree `D:/tra-hien-p1-03`.
Owner giao P1-03; chỉ triển khai/test local. Không commit/push/PR/merge/deploy.
Worktree gốc P1-02 sạch tại `0eb09dd`; giữ nguyên. Không thao tác vào worktree CI.

## G0 / targeted G1 / scoped G2: GO

`git fetch origin --prune` thành công; `origin/main` đúng expected SHA. Baseline full G1
`cadce575597738bf40ad7c45689482de5a5fbc3d`, amendment A01
`024ea998c43baf0afde30b069ed6b4876cb96f4a`, G2 tại
[conformance-review](../architecture/conformance-review.md), B01 và hồ sơ P1-01/P1-02.
`git diff 8791e8ac61f5de42815a84edbc0a0602ff177c26 origin/main -- docs/spec` rỗng.
Đây là audit TARGETED, không nhận là full audit mới. P1-01 đã có bảng, constraints,
grants, entities, mappings và product persistence; P1-03 không phụ thuộc P1-02.

| Nguồn | Coverage |
|---|---|
| `07-roadmap.md:52` §P1, P1-03 | Đúng một task CRUD + typed validator + CLI seed; dependency P1-01 |
| `01-requirements.md:64` FR-CAT-01 | Bộ thuộc tính theo type; text/number/option/boolean/dimensions |
| `03-database.md:51,95,168` §3.1, §3.7, §6; `schema.sql:184,197,216,352,366` catalog reference/value tables | Regex, uniqueness theo parent, cờ, numeric(12,2), đúng một giá trị; dimensions tại `schema.sql:359` |
| `04-business-rules.md:35,60,65` BR-CAT-02/10/14 | Metadata không tái sinh SKU; đổi tên type qua outbox rebuild tìm kiếm |
| `05-api.md:3,103` §1, §4.2; `02-architecture.md:122,131,205` audit/outbox/quyền | Nested routes, camelCase, pagination, concurrency, cookie/quyền/CSRF, IUnitOfWork, outbox, audit |
| DEC-01/04/14/15/16/26/30; B01 | Modular monolith, typed EAV, AdminCookie, SKU bất biến, tp_app; audit giới hạn theo task |

NFR-04/09: che free text khỏi diagnostics/audit, test PostgreSQL 17 thật. AC-P1-01/02
chỉ được hỗ trợ bằng metadata/validator, không nhận hoàn tất publish thuộc P1-05.
Không mở lại Q1–Q8 hoặc backlog P1-07; không sửa authoritative specs.

## Hành vi, quyết định và ranh giới

Trước task: catalog chỉ có persistence/schema, chưa có reference APIs/seed/value validator.
Sau task: ba họ resource dùng đủ GET list/detail, POST create, PATCH metadata; không DELETE.
Base API `/api/v1/admin/product-types`, nested `/{id}/attributes`,
`/{id}/attributes/{attrId}/options`; detail thêm ID tương ứng. Read `catalog.read`,
write `catalog.write`, mọi route yêu cầu StaffReady/AdminCookie và CSRF middleware hiện có.
List dùng `page/pageSize`, sort theo `sort_order, code`, bao gồm inactive để Admin quản lý.

PATCH là partial update: bỏ field thì giữ nguyên; null xóa field nullable; chỉ writable
fields, không cho sửa ID/parent/timestamps. `rowVersion` là hash opaque của identity + metadata
hiện tại, theo cách Settings hiện có, không thêm xmin mapping/migration. Tải lại trước khi PATCH;
stale trả 409. Hash biểu diễn trạng thái hiện tại, không phải revision ledger; A→B→A có thể
trở về cùng token. Timestamps API được cắt đúng microsecond PostgreSQL.

Endpoint qua ICommandHandler/IQueryHandler và validation/transaction decorators hiện có;
validator và luật metadata ở Application. Adapter dùng cùng IUnitOfWork/DbContext, flush trong
transaction để map uniqueness trước rollback; không mở nested transaction. CLI seed cũng qua
command handler; port hỗ trợ standalone UoW cho test/internal caller. ProblemDetails/correlation
giữ contract hiện có. Khóa reference tables theo cùng thứ tự cho CRUD và seed; khóa SHARE hai value tables
trong mutation để value insert không chen giữa kiểm tra và sửa schema. Chặn data_type/scope/
variant-axis và unit khi có giá trị; chặn đổi khỏi option khi còn options. Đổi label/code/is_active giữ
option ID, giá trị cũ không bị xóa; inactive option không được gán cho giá trị mới.
Unique constraints thực trả `product_type_code_exists`, `attribute_code_exists`, `option_code_exists`.
Sai parent hoặc không tồn tại cùng trả 404 `not_found`, không lộ parent thật.

BR-CAT-10: đổi tên type enqueue `RebuildProductSearchText` theo batch ≤200 product IDs cùng
transaction. Handler được đăng ký với outbox hiện có, tính lại từ dữ liệu hiện tại gồm name,
subtitle, mọi SKU/tên variant, artisan/material/type qua `public.f_unaccent`; redelivery an toàn.
Không thay SKU, không tạo generator/value-write API/publish/Materials CRUD.

Audit ba reference entities là yêu cầu của task owner; dùng interceptor hiện có, UUID identity,
actor/correlation cùng transaction. Không audit read. Free text và field chưa phân loại vẫn
`[redacted]`; không whitelist tên/code/description toàn cục. Materials seed không mở audit CRUD.

Typed validator thuần Application: exactly-one, đúng type/parent/scope/activity; false có mặt;
number nằm trong ±9,999,999,999.99 và không mất phần lẻ khi biểu diễn 2 chữ số. Dimensions JSON
object chỉ d/h/mouth/l/w, number decimal hoặc null; chặn duplicate keys, array/string/bool,
unknown keys, malformed/overflow; không thêm rule bắt buộc chiều hoặc số dương. P1-05 phải load
definition/option thật và gọi validator trong transaction value-write, kiểm lại dưới khóa để
không dùng metadata cũ; không được hiểu validator nhận DTO là bằng chứng FK đã load từ DB.
Hợp đồng tích hợp P1-05: trước lần đọc reference đầu tiên, giữ SHARE lock trên reference tables
theo cùng thứ tự `EfCatalogReference.LockAsync` tới commit. SHARE tương thích giữa các product
writes, xung đột với SHARE ROW EXCLUSIVE của metadata/seed; sau đó mới đọc definition/option và
ghi product/variant/values. Điều này cũng tránh create product đọc tên type cũ rồi commit sau
rename/outbox đã lấy danh sách IDs. P1-05 phải kiểm concurrency này khi triển khai value-write.

## Seed: quyết định owner và an toàn

Owner đã chọn trong session: **UUID v7 cố định; code mặc định thuộc UUID khác thì dừng**.
Gap task đã giải quyết: `03-database.md` §6 (`:174–196`) yêu cầu reference sửa được trong
Admin, nhưng chưa quy định identity khi chạy seed lại sau sửa code. Natural key có thể thêm
lại bản mặc định. Hai phương án đã trình owner: fixed UUID + conflict, hoặc natural key với
khả năng thêm lại mặc định. Owner chọn fixed UUID trong session; không sửa authoritative spec.
Manifest immutable `CatalogReferenceDefaults`: 9 types / 23 definitions / 14 options / 13 materials.
ID không được đổi/gán lại khi bổ sung seed sau này. Nhận diện bằng UUID giữ được tên/code/prefix/
label/flags/slug/kind owner đã sửa; không sửa bất kỳ row hiện có nào. Cùng local code được dùng
ở parent khác. Parent sai hoặc code/slug/material SKU identity collision trả
`catalog_seed_identity_conflict`, rollback toàn transaction. Partial seed chỉ điền identity thiếu;
dữ liệu ngoài manifest trùng code cần owner xử lý riêng, không tự adopt/overwrite.

Seed theo `03` §6: cờ không ghi dùng mặc định DDL product/non-axis/not-required/not-filterable,
visible/active. `capacity_ml`, `set_size`, `dimensions` viết tắt dùng cùng type/unit đã nêu;
chỉ teapot/cup capacity là variant axis; chỉ teapot capacity required. Tên hiển thị option lấy từ
code tiếng Việt; material slug theo BR-CAT-01, kind theo schema enum, status draft, không insert XX.
Không gán Google category khi spec để P3-15 chọn danh mục.

CLI chỉ nhận đúng `seed-catalog-reference`, không nhận override config qua argv; chỉ Development/Test,
loopback/disposable `teapremium_dev*`/`teapremium_test*`, runtime `tp_app`. Production/CI/remote bị
chặn trước resolve DB. Không auto-seed API/startup/migration. Exit 0 thành công/no-op; 2 usage/target/
environment hold; 3 identity conflict; 1 infrastructure failure với diagnostic tĩnh, không SQL/secret.

## Validation / migration / recovery

Tests và kết quả thực ở [p1-03-local-evidence.md](p1-03-local-evidence.md).
Các ca chính: happy CRUD, partial/null/active, regex/required fields, unknown/wrong-parent,
typed negative boundaries, permission/CSRF, concurrent duplicates, real constraints, SKU giữ nguyên,
outbox/redelivery, seed counts/idempotency/owner edits/conflict/transaction rollback/concurrency/CLI.

Không migration/DDL/grants mới; reuse P1-01 và kiểm `HasPendingModelChanges` + EF drift.
Rollback source: dừng dùng local branch; không tự revert/reset. Seed chỉ insert reference trong
transaction; failure không commit. Sau commit trên DB có business references, recovery là chỉnh
metadata/roll-forward được owner duyệt, không DELETE/cascade hoặc chạy migration Down để xóa seed.
Production apply không được triển khai trong task này.

READY_FOR_LOCAL_G4: YES. Focused 51 unit + 30 integration PASS; regression 712/712 PASS,
0 skipped; Media/R2 subset 90/90 PASS. Drift, OpenAPI/client, lint/typecheck, frontend tests/build
PASS theo evidence; có workaround MSBuild worker local và browser checks chạy Windows.
G4 độc lập chưa chạy, không tự approve.

## G4 finding fix — phạm vi owner cho phép

P1-03 / FR-CAT-01, NFR-04/09, DEC-04; `03-database.md` §3.1, `schema.sql` reference/value tables;
BR-CAT-02/10/14 giữ nguyên theo regression. Đây là sửa invariant lưu dữ liệu theo yêu cầu owner,
không thêm task/roadmap, không thay authoritative specs, migration hoặc API contract.

- P1: `SchemaChange` trước đây bỏ qua Unit. Vì numeric values không có snapshot đơn vị riêng,
  đổi ml→l sẽ đổi nghĩa giá trị 180. Guard giờ so sánh Unit đúng giá trị ordinal, gồm null↔text,
  trước Apply/SaveChanges. Có giá trị product **hoặc** variant thì effective change trả
  `attribute_in_use`/409; cùng unit hoặc unrelated metadata vẫn sửa được. Chưa có giá trị thì
  unit hợp lệ vẫn sửa được. Không trim/convert/xóa values.
- P2: name/label dùng kiểm tra NUL cụ thể thay cho chặn mọi control character; description,
  googleProductCategory và unit dùng cùng `IsPostgresText`. Code/prefix/dataType/scope đã được
  regex/enum kiểm chặt. POST qua validation decorator; PATCH validate DTO sau merge. Typed text
  và raw dimensions JSON kiểm NUL; dimensions chỉ cho numeric/null nên escaped NUL trong key/
  string/nested representations cũng bị từ chối bởi shape validation. Literal `\\u0000` trong
  text thường vẫn hợp lệ. Không strip ký tự, không map generic Npgsql errors thành validation.

Khóa hiện hữu đủ cho fix: cùng UoW, reference SHARE ROW EXCLUSIVE rồi SHARE hai value tables,
giữ tới commit/rollback. SHARE của value table xung đột INSERT/UPDATE RowExclusive, nên không có
value write chen giữa hasValues và commit. P1-05 phải lấy SHARE trên các reference tables **trước
khi đọc definition/unit/option**, cùng thứ tự product_types → attribute_definitions →
attribute_options → materials, giữ cùng connection/transaction đến sau value write commit. Phải
đọc lại metadata và validate trong transaction đó; không tái dùng definition/unit đọc trước lock
hoặc lấy lock sau khi đã ghi value. Metadata-first: writer chờ rồi đọc đơn vị mới. Writer-first:
metadata chờ writer commit rồi thấy value và trả 409. Hai test PG17 cho product/variant xác minh
trường hợp INSERT uncommitted chặn metadata, rồi value 180 và unit ml được giữ nguyên.

Failure rollback dùng UoW có sẵn; không schema migration/down mới. Evidence cũ được giữ nguyên;
fix artifacts riêng tại `artifacts/p1-03-g4-fix/`. Chờ focused G4 độc lập, không self-approve.

Fix handoff: P1/P2 implementation FIXED. Tests mới 42/42; focused Catalog 70 unit + 53 integration
PASS, full backend 754/754 (142 unit, 578 integration, 34 architecture), Media/R2 90/90; 0 skipped.
Locked restore/build, format, migration drift và OpenAPI semantic equality PASS. Không đổi
schema/contracts/client/CI. **READY_FOR_FOCUSED_G4: YES**, chưa có reviewer re-review PASS;
owner publish vẫn NO. Exact commands/TRX/inventory ở mục finding fix trong local evidence.
