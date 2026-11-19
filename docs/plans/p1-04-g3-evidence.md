# P1-04 — G3 local implementation và evidence

**Cập nhật:** owner báo implementation G4 BLOCK (1 P1 + 3 P2) sau handoff này.
Kết quả sửa và verification hiện hành tại [focused finding fix](p1-04-g4-fix-evidence.md).
Số test/READY bên dưới là lịch sử G3 trước review, không phải self-approved G4.

Ngày 19/11/2026. Worktree `D:\tra-hien-p1-04`, branch
`feature/p1-04-catalog-taxonomy`. Base/HEAD/origin/main:
`f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7`. Không commit/push/PR/merge/apply production.

## Gate và nguồn đã duyệt

Owner xác nhận amendment independent G4 **PASS**, P1/P2 CLOSED, rồi duyệt amendment
và giao tiếp tục G3 qua prompt OWNER APPROVAL AND G3 IMPLEMENTATION CONTINUATION.
Đây là bằng chứng/phê duyệt do owner cung cấp, không phải implementer tự chạy G4.
Tái sử dụng targeted G1/G2 và [record amendment](p1-04-implementation.md),
[ADR](../decisions/ADR-P1-04-publication-history-and-publish.md). Các nhãn pending/
BLOCK trong lịch sử amendment mô tả các lượt trước, không mở lại quyết định owner.
G2 **GO cho G3 đúng P1-04**; không restart audit/architecture/roadmap.

Coverage TARGETED: P1-04, FR-CAT-02/03/08; BR-CAT-01/06/08/09/10/14;
BR-CNT-02; BR-SEO-03/23/31/32/35; NFR-04/07/08/09;
DEC-01/04/14/15/16/21/26/30. Source anchors: 07 roadmap P1-04;
04 §CAT/§CNT; 05 §1/§4.2; 09 §4; 03 §3.10/§8; schema.sql bốn bảng taxonomy.
AC-P1-03 product thuộc P1-05; không nhận toàn AC đó hoặc AC-P1-04 inventory PASS.

Giữ nguyên byte các spec amendment đã duyệt (SHA-256):

| File | SHA-256 |
|---|---|
| 00-README.md | A0037E8804C6525C9C4486D07C69295F7D1A0A1FA24C724BD3AA744FB9A5D1C1 |
| 03-database.md | BF49557419306615975254F041C5EFA31DA6F604E473C982F4EDC9B4BF2EC54F |
| 04-business-rules.md | 4B7E7847CD5E68879E6E62EB1AEB3736CC24CE62539111F1CF7343A192A857C4 |
| 05-api.md | DC62A917822663E003F120BD2BBD1D465453975852BF2BC35AFDD7E9C2DB7F48 |
| 09-seo.md | DA648C07E77E318DE1FBC4F9B7234EBAE9684D984B31906B99EAE512D4C37DF8 |
| schema.sql | 4FEBFB27BDE109BA00AB048D88B46DC65DA5CAD917F57C6B0D02C885573EF5A2 |

## Hành vi và endpoint

20 operations dưới `/api/v1/admin`, AdminCookie + staff_ready; global CSRF,
HTTPS/no-store, correlation/ProblemDetails dùng hạ tầng sẵn có:

| Resource | Operations | Permission |
|---|---|---|
| categories | GET list/detail; POST; PATCH detail | catalog.read / catalog.write |
| collections | GET list/detail; POST; PATCH detail | catalog.read / catalog.write |
| collections/{id}/products | GET; PUT `{rowVersion,items:[{productId,sortOrder}]}` | catalog.read / catalog.write |
| materials | GET list/detail; POST; PATCH detail; POST {id}/publish | catalog.read / catalog.write / catalog.publish |
| artisans | GET list/detail; POST; PATCH detail; POST {id}/publish | catalog.read / catalog.write / catalog.publish |

Create 201; các GET/PATCH/PUT/publish 200. PATCH phân biệt omitted với explicit NULL,
reject field sai entity/unknown/duplicate JSON keys. Draft/archived→published chỉ qua
/publish, POST/PATCH write không bypass. Không DELETE hay public/store/product API.
List phân trang ≤100, thứ tự sortOrder/name/id (artisan name/id), q/status filter.
View có explicit và resolved SEO, rowVersion opaque, không có storage key/cost/stock.

Domain giữ slug/cây/redirect invariants; Application handlers/workflow giữ nghiệp vụ,
validation/UoW và port persistence; Infrastructure EF/HTML/outbox; API chỉ bind/map.
Partial update material không ghi đè metadata bị omitted; NULL sku_code hợp lệ,
XX/regex/duplicate bị từ chối, đủ tám kinds. Không đổi product/variant SKU hoặc ID/FK.

## Integrity, publication và redirects

- Cây kiểm toàn bộ ancestor/subtree, ≤3 cấp, parent tồn tại, không self/cycle.
  Structural/category và slug/state mutations khóa advisory transaction namespace
  `(8104, CatalogKind)` trước đọc; metadata-only khóa aggregate row FOR UPDATE.
  Không global lock ứng dụng/kho. Test ép transaction đầu ngủ trong trigger trước
  commit và khởi chạy reparent cạnh tranh trong lúc giữ lock để bắt write-skew.
- Bốn entity map xmin uint sang opaque base64; malformed 400, stale 409. PUT quan hệ
  collection khóa/bump version parent dù chỉ đổi list; validate product IDs/duplicate,
  update ownership/sort nguyên tử. UNIQUE/FK/CHECK DB là lớp bảo vệ cuối.
- first_published_at set bằng IClock ở public transition đầu tiên, cùng UoW;
  giữ nguyên qua hide/archive/republish/rename. Hiện-public legacy row có NULL bị
  chặn sửa/publish bằng publication_history_unverified, không tự đoán timestamp.
- Slug generate tiếng Việt/đ→d, ≤80 cả suffix, bounded 1000 candidates dưới namespace
  lock; slug nhập tay không tự sửa. Draft rename không tạo public history.
- Ever-public rename tạo old→new, flatten mọi inbound (kể cả prefix khác), xóa
  source được reuse. Draft lấy lại historical slug chỉ reclaim redirect khi public.
  Giữ unrelated redirects; state/slug/redirect/audit/outbox cùng transaction.
  Trigger-injected failures chứng minh không partial commit.
- BR-CAT-10 rename material/artisan enqueue RebuildProductSearchText(ids) cùng UoW.
  Consumer khóa product IDs có thứ tự, recompute f_unaccent từ current product/
  subtitle/SKU/variant names/type/material/artisan. At-least-once safe, không sửa SKU.

Audit bật cho taxonomy create/update/state qua interceptor và entity/path allowlist;
name/bio/HTML/JSON mặc định redacted. BR-SEO-23 yêu cầu noindexReason khi category
bật manual noindex; actual reason lưu encrypted bằng Data Protection purpose
`teapremium.catalog.noindex-reason.v1` trong audit JSON, dùng repository/certificate
sẵn có. Không plaintext PII; test unprotect xác nhận reason vẫn giữ được. Audit đọc
thông thường chỉ thấy ciphertext, chưa thêm UI/decrypt endpoint ngoài scope.

## HTML, SEO và warning

HtmlSanitizer 11.0.26 + AngleSharp parse HTML, whitelist BR-CNT-02; loại script/style
subtrees, unsafe URL/attributes/private images, external links rel=noopener nofollow.
JSON Tiptap render server qua node/mark whitelist; HTML client không override JSON.
Image content chỉ nhận origin public media cấu hình; media FK chỉ public-use image,
không evidence. Không thêm upload/R2 mechanism.

Fallback đúng BR-SEO-31/32 đã duyệt: explicit ưu tiên, material summary→description
text→neutral template; artisan bio→neutral template, không suy main material.
Brand seo.brand_name thiếu thì bỏ; generated text cắt ở word/Unicode boundaries,
không overwrite explicit SEO. Không bịa nội dung/sản phẩm/thành tích.

Đếm riêng description_html/bio_html sau sanitize và decode entities đúng một lần,
ngăn cách block/br/cell, normalize Unicode/NBSP, đếm token không rỗng; không summary,
metadata/lists/articles/boilerplate. 0/399 từ → HTTP 200 published, warning
`content_below_recommended_words`, field descriptionHtml/bioHtml, actualWords,
recommendedWords=400. 400/401 → warnings rỗng. Response id/status/warnings/rowVersion.
Không thin_description_noindex, không tự set/clear robots_noindex, không thêm
điều kiện publish BR-CAT-04 của product.

## Migration, assessment và recovery

`202611191437_P1_04_TaxonomyPublicationHistory`: Up **chỉ bốn AddColumn** nullable
timestamptz, không default/UPDATE/backfill/rename/grant. Entity/model/snapshot/designer
đồng bộ; xmin virtual, không tạo/drop physical xmin column; không sửa migration cũ.

Assessment chỉ trên disposable PG17: trước migration chưa có bốn cột; fixture cũ có
material published/category visible/artisan archived và product/variant/collection
FKs. Fingerprint data ngoài cột mới giữ nguyên; mọi timestamp mới vẫn NULL (không
inference). Script idempotent chạy hai lần; runtime tp_app insert được/cấm ALTER;
full schema parity và HasPendingModelChanges kiểm drift. Đây là fixture preservation,
**không chứng minh lịch sử public hoặc readiness của target thật**.

Real target chưa assessment: **HOLD production/apply/application rollout** nếu
lịch sử bất kỳ legacy row chưa xác minh, kể cả hidden/draft/archived. Runtime guard
chỉ bắt được hiện-public thiếu timestamp; không thể xác định mọi lịch sử từ status.
Phải inventory/evidence từng row trên đúng target và owner-approved backfill/recovery
trước rollout; không coi NULL trên legacy là never-public. Chưa kết nối DB thật.

Down chỉ tp_owner trên teapremium_test hoặc teapremium_test_* dùng một lần, từ chối
nếu có bất kỳ first_published_at khác NULL; DROP COLUMN không CASCADE, dependency
sẽ làm rollback migration. Dữ liệu thật chỉ backup/roll forward/controlled recovery;
revert source không rollback DB và không được xóa lịch sử public.

## Tests và giới hạn

Container riêng `teapremium-p104-linux-g3`, image SDK 10.0.403, mount chỉ worktree
này; Testcontainers postgres:17 dùng random ports + database riêng + tp_app.
TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal; Docker socket phục vụ chính các
disposable fixtures. Không dùng shared dev DB hoặc worktree/container P1-03/CI.
Windows Application Control không bị tắt; Linux là environment owner cho phép.
Node 24.18.1 (SHA-256 tarball đối chiếu SHASUMS256 official), pnpm 12.16.1, FFmpeg,
Docker CLI/Pwsh/Chromium chỉ được chuẩn bị trong container, không đổi CI infrastructure.

Các lệnh `dotnet` trong bảng chạy với prefix thực tế
`docker exec teapremium-p104-linux-g3`; các lệnh `corepack` với prefix
`docker exec --env PATH=/opt/p104-node/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin teapremium-p104-linux-g3`.
Không chạy Windows DLL bị Application Control chặn. Kết quả cuối:

| Lệnh (sau prefix trên nếu có) | Exit / kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | 0 PASS |
| `dotnet build backend/TeaPremium.sln --no-restore` | 0 PASS; 0 warnings/errors |
| `dotnet test backend/tests/TeaPremium.UnitTests/TeaPremium.UnitTests.csproj --no-build --no-restore --logger 'trx;LogFileName=p104-unit.trx' --results-directory artifacts/p1-04/tests` | 0 PASS **83/83**, 0 skipped |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --no-restore --settings artifacts/p1-04/isolated.runsettings --logger 'trx;LogFileName=p104-integration-full.trx' --results-directory artifacts/p1-04/tests` | 0 PASS **574/574**, 0 skipped, 7m59s; gồm **49 test mới P1-04** |
| `dotnet test backend/tests/TeaPremium.ArchitectureTests/TeaPremium.ArchitectureTests.csproj --no-build --no-restore --logger 'trx;LogFileName=p104-architecture.trx' --results-directory artifacts/p1-04/tests` | 0 PASS **34/34**, 0 skipped |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~T01T02MigrationAndExactSeedsAreRepeatableAndRollbackPreservesPlatform --logger 'trx;LogFileName=p104-iam-regression.trx' --results-directory artifacts/p1-04/tests` | 0 PASS **1/1** focused trước final full run |
| `dotnet list backend/TeaPremium.sln package --vulnerable --include-transitive --no-restore` | 0; NuGet feed hiện hành không báo vulnerable packages cho bảy projects, không là security clearance tổng thể |
| `corepack pnpm install --frozen-lockfile` | 0 PASS; pnpm 12.16.1 |
| `corepack pnpm generate:api` | 0 PASS; OpenAPI từ API thật + TS client sinh lại |
| `corepack pnpm check:api` | 0 PASS; generated contract current |
| `corepack pnpm typecheck` | 0 PASS; api-client/admin/store |
| `corepack pnpm lint` | 0 PASS; api-client/admin/store |
| `corepack pnpm test` | 0 PASS; **2 store + 8 admin Node tests + 16 browser tests** |
| `corepack pnpm build` | 0 PASS; api-client/admin/store production build |
| `git diff --check origin/main`; `git diff --cached --check` | 0 PASS; staged rỗng |

Tổng backend **691 PASS**, không skipped. Các test mới là 11 unit + 49 integration;
574 integration bao gồm media P1-02, IAM, grants, migration/DDL parity và nền hiện có.
Không nhận benchmark p95 hay coverage % từ số lượng test.

Model/SQL commands thực, không kết nối target (design-only connection được truyền
qua env cho từng docker exec, không lưu credential thật):

```powershell
docker exec --env 'ConnectionStrings__Default=Host=localhost;Database=teapremium_test_p104_design;Username=tp_owner;Password=unused-design-only' teapremium-p104-linux-g3 dotnet ef migrations script 202611190201_P1_01_CatalogInventoryFoundation 202611191437_P1_04_TaxonomyPublicationHistory --idempotent --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --no-build --output artifacts/p1-04/p104-up-idempotent.sql
docker exec --env 'ConnectionStrings__Default=Host=localhost;Database=teapremium_test_p104_design;Username=tp_owner;Password=unused-design-only' teapremium-p104-linux-g3 dotnet ef migrations has-pending-model-changes --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --no-build
```

Cả hai **exit 0**; EF báo không pending model changes. SQL artifact assertion
PowerShell: đúng bốn `ALTER TABLE ... ADD first_published_at timestamp with time zone`,
không DEFAULT/UPDATE/DROP/xmin/GRANT. Đây bổ sung cho actual PG17 migration tests,
không thay execution proof. Đối chiếu OpenAPI với HEAD bằng parsed JSON: **13 path
cũ và 19 component schemas cũ giữ nguyên**, thêm 11 path chứa 20 operations.

Lượt full integration đầu: exit 1, 554 PASS/20 FAIL; nguyên nhân guard Down chưa
nhận test DB naming, thiếu Docker CLI trong runner lúc các case preflight bắt đầu,
và expected migration count cũ 5 thay vì 6. Đã sửa guard/test expectations và bổ
sung runner. Lượt thứ hai: exit 1, 573 PASS/1 FAIL; fixture IAM tên iam_* chưa theo
allowlist test DB. Đổi riêng fixture thành teapremium_test_iam_*; focused 1/1 rồi
full 574/574 PASS. Không nới guard sang DB thật. Hai TRX FAIL vẫn được giữ để review.

Root frontend test lần đầu exit 1 vì browser chưa cài xong lúc case đầu launch
(15 browser PASS/1 environment FAIL); sau cài Chromium, chạy lại nguyên lệnh root
exit 0 với 26/26 PASS. Drift next-env.d.ts do build được trả đúng trạng thái đầu vào;
`git diff --exit-code -- apps` exit 0, không có thay đổi UI/source frontend.

Artifacts (ignored, không commit): `artifacts/p1-04/tests/p104-unit.trx`,
`p104-architecture.trx`, `p104-integration-full.trx`, `p104-iam-regression.trx`;
hai full failed attempts có hậu tố `first-attempt`/`second-attempt`;
`artifacts/p1-04/p104-up-idempotent.sql`, `change-sha256.json`,
`git-status.txt`, `git-diff-stat.txt`, `untracked-files.txt`.
Frontend stdout và kết quả từng bước nằm trong tool history; browser output tại
`artifacts/p0-12-browser/`. Không bịa file log/coverage chưa tạo.

NOT RUN: production apply/real-target history assessment, live R2/provider evidence,
future P1-03 seed CLI, published-head GitHub Actions và independent implementation G4.
User cấm publish/apply; các mục đó không được ghi PASS từ local fixtures.

## Integration P1-03 và next step

Không đọc/copy/sửa unfinished code hay worktree P1-03. Không seed CLI/reference data.
Test dùng synthetic schema-compatible material/reference fixture; future P1-03 CLI
thực **NOT RUN**, không nhận đã test command đó. Shared conflict cần reconcile khi
hai slice ổn định: Program.cs, central packages/lock files, model/snapshot,
service registration, generated OpenAPI/client và migration-count tests.
G4 reviewer phải đọc cùng worktree, diff origin/main + staged/unstaged và nội dung
mọi file untracked, gồm new migration/designer/code/tests/evidence. Review depth A.

## Inventory cuối và local handoff

`git status --short --untracked-files=all`:

```text
 M backend/Directory.Packages.props
 M backend/src/TeaPremium.Api/Program.cs
 M backend/src/TeaPremium.Api/packages.lock.json
 M backend/src/TeaPremium.Infrastructure/Persistence/AuditValuePolicy.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/CatalogInventoryEntities.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/CatalogInventoryModel.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs
 M backend/src/TeaPremium.Infrastructure/TeaPremium.Infrastructure.csproj
 M backend/src/TeaPremium.Infrastructure/packages.lock.json
 M backend/tests/TeaPremium.ArchitectureTests/packages.lock.json
 M backend/tests/TeaPremium.IntegrationTests/CatalogInventoryDatabaseTests.cs
 M backend/tests/TeaPremium.IntegrationTests/CatalogInventoryRollbackTests.cs
 M backend/tests/TeaPremium.IntegrationTests/DatabaseRolesTests.cs
 M backend/tests/TeaPremium.IntegrationTests/StaffIamTests.cs
 M backend/tests/TeaPremium.IntegrationTests/packages.lock.json
 M docs/analysis/spec-gaps.md
 M docs/analysis/traceability.md
 M docs/spec/00-README.md
 M docs/spec/03-database.md
 M docs/spec/04-business-rules.md
 M docs/spec/05-api.md
 M docs/spec/09-seo.md
 M docs/spec/schema.sql
 M packages/api-client/openapi.json
 M packages/api-client/src/generated/schema.ts
?? backend/src/TeaPremium.Api/Catalog/CatalogEndpoints.cs
?? backend/src/TeaPremium.Application/Catalog/CatalogContracts.cs
?? backend/src/TeaPremium.Application/Catalog/CatalogHandlers.cs
?? backend/src/TeaPremium.Application/Catalog/CatalogSeo.cs
?? backend/src/TeaPremium.Domain/Catalog/TaxonomyRules.cs
?? backend/src/TeaPremium.Infrastructure/Catalog/CatalogContent.cs
?? backend/src/TeaPremium.Infrastructure/Catalog/CatalogRegistration.cs
?? backend/src/TeaPremium.Infrastructure/Catalog/EfCatalogStore.cs
?? backend/src/TeaPremium.Infrastructure/Catalog/RebuildProductSearchTextHandler.cs
?? backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611191437_P1_04_TaxonomyPublicationHistory.Designer.cs
?? backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611191437_P1_04_TaxonomyPublicationHistory.cs
?? backend/tests/TeaPremium.IntegrationTests/TaxonomyApiTests.cs
?? backend/tests/TeaPremium.IntegrationTests/TaxonomyContentTests.cs
?? backend/tests/TeaPremium.IntegrationTests/TaxonomyMigrationTests.cs
?? backend/tests/TeaPremium.UnitTests/TaxonomyRulesTests.cs
?? docs/decisions/ADR-P1-04-publication-history-and-publish.md
?? docs/plans/p1-04-g3-evidence.md
?? docs/plans/p1-04-implementation.md
```

`git diff --stat origin/main` (không tính nội dung untracked):

```text
 backend/Directory.Packages.props                   |    3 +-
 backend/src/TeaPremium.Api/Program.cs              |    4 +
 backend/src/TeaPremium.Api/packages.lock.json      |   24 +
 .../Persistence/AuditValuePolicy.cs                |    7 +
 .../Persistence/CatalogInventoryEntities.cs        |   23 +-
 .../Persistence/CatalogInventoryModel.cs           |    8 +
 .../Migrations/AppDbContextModelSnapshot.cs        |   40 +
 .../TeaPremium.Infrastructure.csproj               |    1 +
 .../TeaPremium.Infrastructure/packages.lock.json   |   23 +
 .../packages.lock.json                             |   24 +
 .../CatalogInventoryDatabaseTests.cs               |    6 +-
 .../CatalogInventoryRollbackTests.cs               |   10 +-
 .../DatabaseRolesTests.cs                          |   10 +-
 .../TeaPremium.IntegrationTests/StaffIamTests.cs   |    2 +-
 .../TeaPremium.IntegrationTests/packages.lock.json |   24 +
 docs/analysis/spec-gaps.md                         |   18 +
 docs/analysis/traceability.md                      |   25 +-
 docs/spec/00-README.md                             |   18 +
 docs/spec/03-database.md                           |   26 +
 docs/spec/04-business-rules.md                     |    1 +
 docs/spec/05-api.md                                |   29 +-
 docs/spec/09-seo.md                                |   35 +-
 docs/spec/schema.sql                               |    4 +
 packages/api-client/openapi.json                   | 3027 +++++++++++++++++---
 packages/api-client/src/generated/schema.ts        | 1789 +++++++++++-
 25 files changed, 4665 insertions(+), 516 deletions(-)
```

25 tracked files + 18 untracked files = 43 paths. Staged rỗng; HEAD không thay đổi.
Inventory/hash đầy đủ local tại artifacts/p1-04; approved spec hashes giữ nguyên.
Container helper `teapremium-p104-linux-g3` đã dừng (exit 0); giữ environment để
reviewer có thể `docker start teapremium-p104-linux-g3` và chạy lại đúng command.
Không có service/DB/container nào của worktree khác bị dừng hoặc thay đổi.

**READY_FOR_LOCAL_G4: YES.** G3 local hoàn tất trên base đã giao; implementation
chưa được G4 độc lập. Next safe step: fresh review-gate session cùng worktree.
Không commit/push/PR/merge/production apply; không xin lại quyết định owner đã chốt.

Sau khi tiếp tục từ lần ngắt, đã đọc lại Git status/base/branch, TRX và trạng thái
container: đúng inventory trên, final integration 574/574, unit 83/83, architecture
34/34, container exited. PowerShell kiểm **10 Markdown files** bằng UTF-8 strict,
trailing whitespace/code fences/local file links/heading anchors: exit 0 PASS.
`git diff --check origin/main`, `git diff --cached --check`,
`git diff --exit-code -- apps`: đều exit 0; staged vẫn rỗng. Không thay application
code sau các lượt test cuối, không lặp runtime tests khi không có thay đổi mới.
