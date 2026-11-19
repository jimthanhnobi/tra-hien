# P1-03 — Local G3 evidence

**Trạng thái hiện hành sau review owner cung cấp:** G4 **BLOCK**, chưa được publish. Owner cho
phép sửa local hai finding P1 (unit) và P2 (NUL). Nội dung G3 ban đầu dưới đây là lịch sử;
trạng thái/tests của finding fix ở mục **G4 finding fix** cuối file. Chưa có focused re-review PASS.

Ngày 19/11/2026. Base/HEAD/origin/main tại preflight:
`f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7`.
Branch `feature/p1-03-product-types-attributes`, worktree `D:/tra-hien-p1-03`.
Owner chỉ cho phép local G3. Không commit/stage/push/PR/merge/revert/deploy/production apply.
G4 độc lập chưa chạy. Hồ sơ requirements, G1/G2, quyết định seed, migration/rollback:
[p1-03-implementation.md](p1-03-implementation.md).

## Phạm vi và endpoint

P1-03 / FR-CAT-01; BR-CAT-02/10/14; NFR-04/09; DEC-01/04/14/15/16/26/30,
B01 và owner decision fixed seed UUID trong session. AC-P1-01/02: chỉ foundation,
không nhận hoàn tất publish/P1-05.

Base `/api/v1/admin/product-types`; tất cả yêu cầu `AdminCookie` + StaffReady.

| Method | Route sau base | Permission / success |
|---|---|---|
| GET | rỗng; `/{id}` | catalog.read / 200 |
| POST | rỗng | catalog.write + CSRF / 201 + Location |
| PATCH | `/{id}` | catalog.write + CSRF / 200 |
| GET | `/{id}/attributes`; `/{id}/attributes/{attrId}` | catalog.read / 200 |
| POST | `/{id}/attributes` | catalog.write + CSRF / 201 + Location |
| PATCH | `/{id}/attributes/{attrId}` | catalog.write + CSRF / 200 |
| GET | `/{id}/attributes/{attrId}/options`; `/{id}/attributes/{attrId}/options/{optId}` | catalog.read / 200 |
| POST | `/{id}/attributes/{attrId}/options` | catalog.write + CSRF / 201 + Location |
| PATCH | `/{id}/attributes/{attrId}/options/{optId}` | catalog.write + CSRF / 200 |

12 operations, không DELETE. List theo contract page/pageSize. Invalid 400, thiếu cookie 401,
thiếu quyền 403, missing/wrong-parent 404, duplicate/stale/incompatible mutation 409.
Theo middleware hiện hữu, thiếu/sai CSRF trả **400 csrf_invalid**. ProblemDetails có code/traceId;
không lộ SQL, constraint, parent thật hoặc dữ liệu nhạy cảm.

Typed validator dùng lại cho P1-05: đúng một representation và đúng data_type; false hợp lệ;
number giới hạn numeric(12,2), không round mất giá trị; option đúng attribute và active; đúng
product type/scope/activity. Dimensions chỉ object d/h/mouth/l/w, decimal/null, không duplicate/
unknown key hoặc malformed JSON; không bắt buộc chiều nào. Chưa có product/variant value APIs.

Seed manifest: 9 types, 23 definitions, 14 options, 13 materials draft; đúng 9 prefix/13 mã material,
không XX. UUID v7 cố định theo owner decision; chạy transaction + cùng table locks với CRUD,
chỉ insert identity thiếu, không overwrite owner metadata. Code/parent identity conflict rollback
và trả `catalog_seed_identity_conflict`. CLI không chạy lúc API startup/migration, chặn Production,
CLI config override, remote/non-disposable target trước DB; exit 0/1/2/3 được test bằng process thật.

BR-CAT-10 đã có integration thực: rename type enqueue cùng transaction, outbox handler rebuild
đủ name/subtitle/all SKUs/variant names/artisan/material/type qua f_unaccent, redelivery an toàn.
Test giữ nguyên sku_base/SKU; mutation lỗi không commit metadata, outbox hoặc audit.

## Test evidence và cách tái lập

Artifacts local ignored: `artifacts/p1-03/`. Windows Application Control chặn chạy DLL mới
(`0x800711C7`); không thay host policy. Dùng SDK 10.0.403 Linux runner, FFmpeg, Docker socket,
PostgreSQL 17 Testcontainers và restricted `tp_app` từ provisioning/migrations hiện có.
Không DB production, không real business data/R2 credentials.

`snapshot.ps1` đóng gói `git ls-files --cached --others --exclude-standard`, không bin/obj/node_modules/
artifacts hoặc ignored secrets; lưu SHA-256 từng source file. Snapshot không phải commit hoặc G4.
Lần final dùng `linux-handoff/` và source hash manifest local; compiled backend phải khớp worktree.

Lệnh orchestration (PowerShell, từ worktree):

```powershell
powershell -NoProfile -File artifacts/p1-03/snapshot.ps1
docker run --rm --name tra-hien-p1-03-handoff --mount 'type=bind,source=D:/tra-hien-p1-03/artifacts/p1-03/source.tar,target=/source.tar,readonly' --mount 'type=bind,source=D:/tra-hien-p1-03/artifacts/p1-03/run-validation.sh,target=/run.sh,readonly' --mount 'type=bind,source=D:/tra-hien-p1-03/artifacts/p1-03/linux-handoff,target=/evidence' --mount 'type=bind,source=D:/tra-hien-p1-03/artifacts/p1-03/generated,target=/output' -v tra-hien-p1-03-nuget:/root/.nuget/packages -v /var/run/docker.sock:/var/run/docker.sock -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal tra-hien-p1-03-tools:local sh /run.sh
```

Các command chạy ở runner Linux, riêng `pnpm test/build` bên dưới chạy Windows với Chromium
đã có. Log primary: `linux-handoff/`, contract độc lập cùng snapshot: `linux-handoff-contract/`.
Backend 172 file trong manifest có **0 khác biệt SHA-256** so worktree sau kiểm thử.
Hai generated file trong worktree khớp hash output Linux đã qua `check:api`.

| Check / exact command | Kết quả final |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS, exit 0 |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, exit 0, 0 warning/error; Windows cũng PASS |
| `dotnet test backend/tests/TeaPremium.UnitTests --no-build --no-restore --filter FullyQualifiedName~CatalogValidationTests --logger 'trx;LogFileName=focused-unit.trx' --results-directory /evidence` | PASS 51/51, exit 0, skipped 0 |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~CatalogReference --logger 'trx;LogFileName=focused-integration.trx' --results-directory /evidence` | PASS 30/30, exit 0, skipped 0 |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/regression` | PASS 712/712: unit 123, integration 555, architecture 34; exit 0, skipped 0. Workaround dưới đây |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS, exit 0 |
| `dotnet tool restore` | PASS, exit 0 |
| `env ConnectionStrings__Default='Host=127.0.0.1;Port=1;Database=ci_design_only;Username=ci_dummy;Password=ci_dummy' dotnet ef migrations has-pending-model-changes --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --no-build` | PASS, exit 0, no model changes |
| `dotnet list backend/TeaPremium.sln package --vulnerable --include-transitive` | PASS, exit 0; current feeds không báo vulnerable package |
| `corepack pnpm install --frozen-lockfile` | PASS, exit 0 |
| `corepack pnpm generate:api` | PASS, exit 0 |
| `corepack pnpm check:api` | PASS, exit 0 |
| `corepack pnpm lint` | PASS, exit 0, api-client/admin/store |
| `corepack pnpm typecheck` | PASS, exit 0, gồm catalog-contract.test-d.ts |
| `corepack pnpm test` (Windows) | PASS, exit 0: store 2 + admin unit 8 + browser 16 = 26 tests |
| `corepack pnpm build` (Windows) | PASS, exit 0, api-client/admin/store |
| `corepack pnpm --filter @teapremium/admin run test:browser` (Windows) | PASS 16/16, exit 0; tái sinh Next dev declarations sau build, không giữ diff UI |
| `node artifacts/p1-03/compare-contract.mjs` | PASS, exit 0; 0 existing path/schema thay đổi so origin/main |
| `git diff --check` (worktree) | PASS, exit 0 |

Không che lần lỗi trước: Windows focused unit/OpenAPI generation exit 1 do Application Control;
focused integration lần đầu 25/26 vì test kỳ vọng CSRF 403 thay vì convention 400; đã sửa.
Full regression trước correction OpenAPI PASS 712/712 (123 unit + 555 integration + 34 architecture),
0 skipped; subset Media/R2 90/90 PASS. Lệnh drift đầu dùng startup API exit 1 (thiếu Design package
ở project đó), chạy lại đúng Infrastructure exit 0. Client lint/type assertions phát hiện optional
fields có OpenAPI default bị generator TS coi là required; đã sửa riêng catalog schema, không đổi
default runtime hoặc generator chung. Final run kiểm lại source sau correction.

**Workaround runner local, không sửa CI:** lần full cuối đã chạy hết 712 tests nhưng chờ lâu ở
test nền `DatabaseRolesTests.DevMigrateRestoresCleanCheckoutAndStopsOnLockedRestoreFailure`.
Async dump xác nhận `RunToolAsync` chờ `StreamReader.ReadToEndAsync`; orphan MSBuild worker
(PPid=1, nodemode:1) còn giữ stdout pipe. Chạy `docker exec -w /work
tra-hien-p1-03-handoff dotnet build-server shutdown` (exit 0), rồi bounded cleanup bằng
`artifacts/p1-03/cleanup-orphan-msbuild.py` chỉ TERM orphan MSBuild worker trong chính runner,
PPid=1 + nodemode:1 + stdout pipe; không dừng testhost hoặc worker có parent. Sau đó suite trả
exit 0, TRX **555 integration PASS**, không skip; 19m26s bao gồm thời gian chờ/chẩn đoán.
`async-stacks.txt`, `build-server-shutdown.log`, `orphan-cleanup.log` giữ dấu vết. Heap dump chỉ
ở /tmp container disposable, không copy vào repo. Không sửa test/tool/CI source để né test.

Đã khởi động thêm serial diagnostic bằng xunit.runner.json trong output bin Linux; dừng lần
chẩn đoán này sau khi primary full suite hoàn tất, không tính thành PASS/coverage bổ sung.
Lần `corepack pnpm test` trong Linux exit 1 vì runner thiếu Chromium; không phải browser PASS.
Đã chạy cùng suite ở Windows có browser cài sẵn: 26/26 PASS. Linux frontend build chưa chạy;
Windows full build exit 0 là evidence build. Worktree CI optimization không bị thao tác.

TRX final tại `artifacts/p1-03/linux-handoff/regression/`:
`_b91f44bcc374_2026-11-19_13_54_57_net10.0.trx` (123 unit),
`...net10.0[1].trx` (34 architecture), `...net10.0[2].trx` (555 integration).
Subset bốn class MediaApiTests/MediaContentTests/R2MediaStorageTests/MediaValidationTests:
**90/90 PASS** trong regression final. Focused 81 tests đã nằm trong 712, không cộng trùng.

NOT RUN: GitHub Actions cho delta local (không publish); independent G4 (owner yêu cầu dừng G3);
deploy/Production apply/live R2 (ngoài scope và không được phép). Không nhận những mục này là PASS.

## Change set / status / giới hạn

Không migration/DDL/grants/dependency lockfile mới, không sửa docs/spec, CI hoặc Media/R2 source.
5 tracked files modified, 17 untracked files mới; staged diff rỗng. Inventory/status ở cuối file.

Giới hạn đã công khai: hash rowVersion biểu diễn current state, không monotonic ledger; metadata
locks khá rộng nhưng reference catalog nhỏ; P1-05 phải load/validate dưới transaction/khóa để không
dùng metadata cũ. UUID seed là manifest bất biến; dữ liệu ngoài manifest trùng code không được tự
adopt. Mọi publish/product value-write/SKU generator/Materials CRUD vẫn thuộc task tương ứng.

Rollback/recovery: chưa có publish; không tự revert source hoặc xóa seed đã có references.
Failure trong transaction rollback toàn bộ; dữ liệu đã commit dùng owner-approved metadata
correction/roll-forward. Không chạy Down trên dữ liệu thật.

READY_FOR_LOCAL_G4: YES. Reviewer phải dùng fresh context và đọc cùng worktree,
đối chiếu origin/main, committed/staged/unstaged **và toàn bộ nội dung untracked**. Không self-approve G4.

## Inventory cuối G3

Tracked modified: `backend/src/TeaPremium.Api/Program.cs`,
`backend/src/TeaPremium.Infrastructure/Persistence/CatalogInventoryEntities.cs`,
`backend/src/TeaPremium.Infrastructure/Persistence/PersistenceRegistration.cs`,
`packages/api-client/openapi.json`, `packages/api-client/src/generated/schema.ts`.
Chỉ thêm attribute audit, DI, CLI/API mapping và generated contract vào các file này.

`git ls-files --others --exclude-standard` — reviewer phải đọc nội dung cả 17 file:

```text
backend/src/TeaPremium.Api/Catalog/CatalogEndpoints.cs
backend/src/TeaPremium.Api/Catalog/CatalogOpenApi.cs
backend/src/TeaPremium.Api/Catalog/SeedCatalogReferenceConsole.cs
backend/src/TeaPremium.Application/Catalog/CatalogContracts.cs
backend/src/TeaPremium.Application/Catalog/CatalogRules.cs
backend/src/TeaPremium.Application/Catalog/CatalogUseCases.cs
backend/src/TeaPremium.Application/Catalog/TypedAttributeValidator.cs
backend/src/TeaPremium.Infrastructure/Catalog/CatalogReferenceDefaults.cs
backend/src/TeaPremium.Infrastructure/Catalog/EfCatalogReference.cs
backend/src/TeaPremium.Infrastructure/Catalog/EfCatalogReferenceSeed.cs
backend/src/TeaPremium.Infrastructure/Catalog/RebuildProductSearchText.cs
backend/tests/TeaPremium.IntegrationTests/CatalogReferenceApiTests.cs
backend/tests/TeaPremium.IntegrationTests/CatalogReferenceSeedTests.cs
backend/tests/TeaPremium.UnitTests/CatalogValidationTests.cs
docs/plans/p1-03-implementation.md
docs/plans/p1-03-local-evidence.md
packages/api-client/src/catalog-contract.test-d.ts
```

`git status --short`:

```text
M backend/src/TeaPremium.Api/Program.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/CatalogInventoryEntities.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/PersistenceRegistration.cs
 M packages/api-client/openapi.json
 M packages/api-client/src/generated/schema.ts
?? backend/src/TeaPremium.Api/Catalog/
?? backend/src/TeaPremium.Application/Catalog/
?? backend/src/TeaPremium.Infrastructure/Catalog/
?? backend/tests/TeaPremium.IntegrationTests/CatalogReferenceApiTests.cs
?? backend/tests/TeaPremium.IntegrationTests/CatalogReferenceSeedTests.cs
?? backend/tests/TeaPremium.UnitTests/CatalogValidationTests.cs
?? docs/plans/p1-03-implementation.md
?? docs/plans/p1-03-local-evidence.md
?? packages/api-client/src/catalog-contract.test-d.ts
```

`git diff --stat origin/main` (Git chưa tính file untracked):

```text
backend/src/TeaPremium.Api/Program.cs              |   22 +-
 .../Persistence/CatalogInventoryEntities.cs        |    3 +
 .../Persistence/PersistenceRegistration.cs         |    3 +
 packages/api-client/openapi.json                   | 1940 ++++++++++++++++++--
 packages/api-client/src/generated/schema.ts        | 1102 +++++++++++
 5 files changed, 2962 insertions(+), 108 deletions(-)
```

`git diff --cached --stat`: rỗng. HEAD vẫn bằng base, chưa có commit task.
Artifacts/log/TRX/snapshot local ignored không xuất hiện trong Git inventory.
Bước tiếp theo: fresh independent G4 trên **D:/tra-hien-p1-03**; dừng local G3 tại đây.

## G4 finding fix — 19/11/2026

Owner cung cấp G4 **BLOCK**, READY_FOR_OWNER_PUBLISH **NO**, giao sửa đúng hai finding tại
cùng worktree/branch/base. Tái dùng G1/G2 P1-03: spec diff từ baseline `8791e8ac...` tới HEAD
rỗng, không thay thiết kế/schema. G0: HEAD/origin/main vẫn
`f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7`, nhánh `feature/p1-03-product-types-attributes`.
Traceability: P1-03, FR-CAT-01, NFR-04/09, DEC-04; `03` §3.1 và `schema.sql` typed values.
BR-CAT-02/10/14 và toàn bộ G3 được giữ để regression, không khởi động lại implementation.

**Root cause / correction:**

- P1: `CatalogRules.SchemaChange` chỉ xét dataType/scope/isVariantAxis. Unit là metadata duy nhất
  diễn giải numeric value, nên sửa ml→l làm 180 đổi nghĩa. Đã thêm so sánh Unit ordinal, gồm
  null→text/text→null/text→text. Có value ở một trong hai bảng thì trả 409 `attribute_in_use`
  trước Apply; cùng unit, metadata không liên quan, hoặc unit hợp lệ khi chưa có values vẫn cho sửa.
- P2: optional description/googleProductCategory/unit thiếu boundary check, typed text chỉ kiểm
  presence; NUL tới PostgreSQL text/JSONB làm request lỗi 500. `IsPostgresText` kiểm cụ thể U+0000
  và được dùng ở name/label, các optional strings kể trên, typed text và raw dimensions JSON.
  Code/prefix/dataType/scope đã có regex/enum chặt; 12 writable text fields đều được test. Name/
  label vẫn bắt buộc non-whitespace, không còn chặn toàn bộ control chars; newline/tab, tiếng Việt,
  optional null và literal backslash-u text được giữ nguyên. Dimensions chỉ nhận known keys +
  number/null nên escaped NUL trong key/string/nested values cũng không vượt shape validation.
  POST dùng decorator có sẵn, PATCH validate DTO đã merge; lỗi giữ ProblemDetails/errors/traceId
  và message Việt. Không strip NUL, không đổi generic Npgsql exception mapping.

**Side effects / locking:** không đổi persistence/locking code. Metadata mutation lấy reference
SHARE ROW EXCLUSIVE rồi SHARE trên product_attribute_values và variant_attribute_values trước
hasValues; cùng connection/UoW tới commit/rollback. SHARE xung đột value INSERT/UPDATE nên không
thể chèn value giữa check và commit. Tests PG17 giữ transaction insert 180 chưa commit, quan sát
ShareLock đang chờ bằng pg_locks, rồi commit writer: PATCH trả 409; ml/180 và audit/outbox giữ nguyên.
P1-05 phải lấy SHARE reference locks theo thứ tự product_types → attribute_definitions →
attribute_options → materials **trước khi đọc metadata/unit/option**, validate và ghi values trong
cùng transaction, giữ locks tới commit. Writer không dùng metadata đọc trước lock. Metadata-first
thì writer chờ và đọc unit mới; writer-first thì metadata chờ commit và phát hiện values. Không
triển khai P1-05 ở đây. Không migration/constraint/data conversion/delete/backfill mới.

**Inventory riêng finding fix:**

| Path | Delta so với G3 trước sửa |
|---|---|
| `backend/src/TeaPremium.Application/Catalog/CatalogRules.cs` | Unit guard + NUL checks, bỏ blanket control-character rejection trong name/label |
| `backend/src/TeaPremium.Application/Catalog/TypedAttributeValidator.cs` | NUL typed text/raw dimensions JSON |
| `backend/tests/TeaPremium.UnitTests/CatalogG4FixValidationTests.cs` | Mới, 19 cases |
| `backend/tests/TeaPremium.IntegrationTests/CatalogReferenceG4FixTests.cs` | Mới, 23 cases qua cookie/quyền/CSRF và PG17 |
| `docs/plans/p1-03-implementation.md` | Traceability/invariant/P1-05 locking contract và trạng thái G4 mới |
| `docs/plans/p1-03-local-evidence.md` | Giữ evidence G3, thêm finding-fix evidence |

Snapshot/hashes trước sửa và 5 bản sao các file liên quan tại `artifacts/p1-03-g4-fix/before*`;
red/green snapshots, TRX, logs ở thư mục riêng cùng root, không overwrite artifacts G3 cũ.
Red trên source trước fix: unit 12 pass / 7 fail (19), integration 11 pass / 12 fail (23), từng
test command exit 1. Các lỗi tái hiện gồm unit edit trả 200 và NUL trả 500; test cleanup race
đã sửa finally rollback sau commit trước green. Red wrapper exit 0 chỉ để thu cả hai log,
không phải acceptance PASS.

**Green commands/results** (Linux SDK 10.0.403, PostgreSQL 17 Testcontainers, tp_app thật):

| Exact command | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS exit 0 |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS exit 0, 0 warning/error; Windows build cũng exit 0 |
| `dotnet test backend/tests/TeaPremium.UnitTests --no-build --no-restore --filter FullyQualifiedName~Catalog --logger 'trx;LogFileName=catalog-unit.trx' --results-directory /evidence` | PASS 70/70, skipped 0, exit 0 |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~CatalogReference --logger 'trx;LogFileName=catalog-integration.trx' --results-directory /evidence` | PASS 53/53, skipped 0, exit 0 |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/regression` | PASS 754/754: unit 142, integration 578, architecture 34; skipped 0, exit 0 |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS exit 0 |
| `dotnet tool restore` | PASS exit 0 |
| `env ConnectionStrings__Default='Host=127.0.0.1;Port=1;Database=ci_design_only;Username=ci_dummy;Password=ci_dummy' dotnet ef migrations has-pending-model-changes --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --no-build` | PASS exit 0, no model changes |
| `env ASPNETCORE_ENVIRONMENT=CI ConnectionStrings__Default= dotnet build backend/src/TeaPremium.Api --no-restore -warnaserror -p:OpenApiGenerateDocuments=true -p:OpenApiDocumentsDirectory=/evidence/openapi` | PASS exit 0 |
| `python3 -c "import json; a=json.load(open('/evidence/openapi/openapi.json')); b=json.load(open('packages/api-client/openapi.json')); assert a==b, 'OpenAPI contract changed'; print('OpenAPI semantic contract unchanged')"` | PASS exit 0, JSON contract tương đương hoàn toàn; generated client giữ nguyên |
| `git diff --check` | PASS exit 0 |

Reproduction/orchestration:

```powershell
powershell -NoProfile -File artifacts/p1-03-g4-fix/snapshot.ps1 -Phase green
docker run --rm --name tra-hien-p1-03-g4-green --mount 'type=bind,source=D:/tra-hien-p1-03/artifacts/p1-03-g4-fix/green/source.tar,target=/source.tar,readonly' --mount 'type=bind,source=D:/tra-hien-p1-03/artifacts/p1-03-g4-fix/green.sh,target=/run.sh,readonly' --mount 'type=bind,source=D:/tra-hien-p1-03/artifacts/p1-03-g4-fix/green,target=/evidence' -v tra-hien-p1-03-nuget:/root/.nuget/packages -v /var/run/docker.sock:/var/run/docker.sock -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal -e MSBUILDDISABLENODEREUSE=1 -e DOTNET_CLI_USE_MSBUILD_SERVER=0 -e UseSharedCompilation=false tra-hien-p1-03-tools:local sh /run.sh
```

Runner env flags chỉ tắt build-server/node reuse trong lần test local, tránh pipe orphan đã gặp
ở G3; không sửa CI/infrastructure source hoặc worktree CI optimization. Không cần thay Windows
Application Control; backend thực thi trên Linux như G3. Concurrent tests trong test methods
vẫn chạy như cũ.

Coverage mới: mọi unit transition cho cả scope; same unit/unrelated metadata/no-values edit;
stale token; no audit/outbox/metadata/value side effects khi reject; uncommitted value writer;
POST/PATCH NUL cho 12 text fields; typed NUL và nested dimensions; Unicode/newline/tab/literal
escapes/optional null. Existing focused tests kiểm 12 endpoints, parent ownership, auth/CSRF,
numeric(12,2), seed 9/23/14/13, idempotency/owner metadata, BR-CAT-10 và SKU. Không đổi JSON DTO,
endpoint, permission, generated client hoặc DB model. Frontend/TS regeneration NOT RUN cho fix
này vì không đổi UI/client/contract; API contract equality được kiểm riêng ở trên. G4 re-review,
GitHub Actions, commit/push/PR/merge/production apply NOT RUN theo scope owner.

**Kết quả chốt:** P1 **FIXED**, P2 **FIXED** trong implementation. **42/42 tests mới PASS**
(19 unit + 23 integration); focused Catalog 70/70 + 53/53; full regression 754/754, skipped 0.
Subset Media/R2 **90/90 PASS**. Unit/architecture/integration chạy thật ở cùng green snapshot,
không lấy kết quả G3 thay cho fix. Không cần cleanup orphan hoặc sửa CI ở lần green này.

TRX full tại `artifacts/p1-03-g4-fix/green/regression/`:
`_983019ad6062_2026-11-19_14_47_51_net10.0.trx` (142), `...net10.0[1].trx` (34),
`...net10.0[2].trx` (578). `green/checks.txt` có exit code từng command. All source snapshot
hashes cho backend khớp worktree; chỉ docs được hoàn thiện sau freeze source.

**READY_FOR_FOCUSED_G4: YES.** G4 vẫn owner-reported **BLOCK** tới khi reviewer độc lập re-review;
READY_FOR_OWNER_PUBLISH **NO**. Handoff này không tự phê duyệt G4. Không commit, push, PR, merge,
revert hoặc production apply. Residual integration contract: P1-05 phải tuân thủ metadata lock
order trước read/validate/write như trên; không có P1-05 code trong bản sửa này.

### Git handoff sau finding fix

`git status --short`:

```text
 M backend/src/TeaPremium.Api/Program.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/CatalogInventoryEntities.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/PersistenceRegistration.cs
 M packages/api-client/openapi.json
 M packages/api-client/src/generated/schema.ts
?? backend/src/TeaPremium.Api/Catalog/
?? backend/src/TeaPremium.Application/Catalog/
?? backend/src/TeaPremium.Infrastructure/Catalog/
?? backend/tests/TeaPremium.IntegrationTests/CatalogReferenceApiTests.cs
?? backend/tests/TeaPremium.IntegrationTests/CatalogReferenceG4FixTests.cs
?? backend/tests/TeaPremium.IntegrationTests/CatalogReferenceSeedTests.cs
?? backend/tests/TeaPremium.UnitTests/CatalogG4FixValidationTests.cs
?? backend/tests/TeaPremium.UnitTests/CatalogValidationTests.cs
?? docs/plans/p1-03-implementation.md
?? docs/plans/p1-03-local-evidence.md
?? packages/api-client/src/catalog-contract.test-d.ts
```

`git diff --stat origin/main`:

```text
 backend/src/TeaPremium.Api/Program.cs              |   22 +-
 .../Persistence/CatalogInventoryEntities.cs        |    3 +
 .../Persistence/PersistenceRegistration.cs         |    3 +
 packages/api-client/openapi.json                   | 1940 ++++++++++++++++++--
 packages/api-client/src/generated/schema.ts        | 1102 +++++++++++
 5 files changed, 2962 insertions(+), 108 deletions(-)
```

`git ls-files --others --exclude-standard` (19 file; Git diff stat chưa tính các file này):

```text
backend/src/TeaPremium.Api/Catalog/CatalogEndpoints.cs
backend/src/TeaPremium.Api/Catalog/CatalogOpenApi.cs
backend/src/TeaPremium.Api/Catalog/SeedCatalogReferenceConsole.cs
backend/src/TeaPremium.Application/Catalog/CatalogContracts.cs
backend/src/TeaPremium.Application/Catalog/CatalogRules.cs
backend/src/TeaPremium.Application/Catalog/CatalogUseCases.cs
backend/src/TeaPremium.Application/Catalog/TypedAttributeValidator.cs
backend/src/TeaPremium.Infrastructure/Catalog/CatalogReferenceDefaults.cs
backend/src/TeaPremium.Infrastructure/Catalog/EfCatalogReference.cs
backend/src/TeaPremium.Infrastructure/Catalog/EfCatalogReferenceSeed.cs
backend/src/TeaPremium.Infrastructure/Catalog/RebuildProductSearchText.cs
backend/tests/TeaPremium.IntegrationTests/CatalogReferenceApiTests.cs
backend/tests/TeaPremium.IntegrationTests/CatalogReferenceG4FixTests.cs
backend/tests/TeaPremium.IntegrationTests/CatalogReferenceSeedTests.cs
backend/tests/TeaPremium.UnitTests/CatalogG4FixValidationTests.cs
backend/tests/TeaPremium.UnitTests/CatalogValidationTests.cs
docs/plans/p1-03-implementation.md
docs/plans/p1-03-local-evidence.md
packages/api-client/src/catalog-contract.test-d.ts
```

Staged diff rỗng. Tổng worktree: 5 tracked modified + 19 untracked, gồm G3 được giữ lại.
So với snapshot trước finding fix, chỉ 4 file G3 thay đổi (2 Application + 2 docs) và thêm
2 test files. Các file G3 khác giữ nguyên SHA-256; backend green snapshot 174 files có 0
hash mismatch. Không thay đổi CI worktree hoặc P1-04 worktree.


