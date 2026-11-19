# P1-04 — sửa bốn finding implementation G4

Ngày 19/11/2026. Worktree `D:\tra-hien-p1-04`, branch
`feature/p1-04-catalog-taxonomy`; base/HEAD/origin/main
`f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7`. Input: owner báo G4 **BLOCK**,
1 P1 + 3 P2; giao sửa local, không commit/push/PR/merge/deploy/production apply.

Tái sử dụng G1/G2 và amendment đã duyệt, không mở lại contract hay lập roadmap.
Nguồn: P1-04 (`07-roadmap.md` §P1), BR-CAT-08/BR-SEO-03 (transaction/redirect),
BR-CNT-02/BR-SEO-31/32/35 (HTML/visible text/word count), `05-api.md` §1/§4.2
(validation_failed, collection replacement), NFR-07/09, DEC-01/21. Giữ nguyên
[43-file G3 input](p1-04-g3-evidence.md); snapshot bytes đầu lượt nằm tại
`artifacts/p1-04-g4-fix/before-sha256.json`. Không nhận implementer tự G4 PASS.

## Root cause và thay đổi

| Finding | Root cause | Sửa và evidence |
|---|---|---|
| P1 shared redirect graph | CatalogKind locks khác nhau trong khi graph content.redirects dùng chung; mỗi transaction có thể lấy snapshot cũ rồi sửa mất reclaim của transaction khác | EfCatalogStore.RedirectsAsync lấy **pg_advisory_xact_lock(8104,100)** trước SELECT snapshot, giữ đến outer UoW commit/rollback. SaveRedirectsAsync từ chối nếu chưa có locked snapshot trong cùng TransactionId. Mọi graph writer production P1-04 hiện đi qua cặp port này; không có writer thứ hai ngoài test fixtures. Test PG17 chặn T1 sau SELECT rồi quan sát T2 đợi advisory lock trước snapshot, kiểm cả graph/entity/history sau commit, stale rowVersion và rollback/retry. |
| P2-A sanitizer block boundaries | KeepChildNodes unwrap div/section rồi nối text trước Visible() | Chèn space text nodes quanh unsupported block elements **trước** sanitizer unwrap. Whitelist không nới; script/style/template/noscript subtrees bị xóa trước. Stored HTML giữ separators nên sanitize lại/read/SEO/count dùng cùng text; test adjacent/nested blocks, headings/list, entities/Unicode/NBSP, XSS cũ và persisted 399/400/401. |
| P2-B null collection item | Validator đọc i.ProductId khi i=null | Check i is not null trước dereference/Distinct. [null], valid+null → 400 validation_failed; duplicate/invalid ID giữ behavior; empty list vẫn được. Fingerprint mọi taxonomy/relationships/audit/outbox và rowVersion không đổi khi reject. |
| P2-C NUL text/query/JSON | Chưa chặn decoded U+0000 trước PostgreSQL text/jsonb/query | CatalogInput.WithoutNul duyệt đệ quy mọi JSON string **và property name** ở SaveCatalogValidator; ListCatalogValidator chặn NUL trong q. Status/rowVersion/UUID/pagination giữ validator/binder hiện hành. Không strip NUL, không thêm rule cấm mọi control, không catch tùy ý database exceptions. POST/PATCH mọi text field, nested JSON, query trên bốn resources đều có HTTP tests; Unicode/newline/tab/null hợp lệ vẫn dùng được theo contract từng field. |

Lượt fix chỉ đổi production **CatalogContracts.cs, CatalogContent.cs, EfCatalogStore.cs**;
thêm hai test files và mở rộng TaxonomyContentTests. Không đổi CatalogHandlers/API
DTOs, số operations/quyền/rowVersion/ProblemDetails, migration/EF mappings/snapshot,
spec hay generated client. Validation decorator chạy trước transactional handler,
vì vậy NUL/null-item failure không đi vào query/mutation của catalog.

## Redirect concurrency protocol và reproduction

Thứ tự cố định: namespace nếu cần → owning aggregate row FOR UPDATE → graph lock
`(8104,100)` → redirect rows. Không acquire namespace/aggregate khác sau graph lock.
Graph lock transaction-scoped, mọi prefix dùng cùng key, lấy trước đọc snapshot,
không giữ qua HTTP/R2/external call/user input; validation/sanitize trước đoạn này.
Guard writer gắn TransactionId để không dùng snapshot của transaction đã kết thúc.
READ COMMITTED sau khi lấy lock thấy graph đã commit của predecessor; không tự retry
transaction, vẫn stale version 409. Không thêm table/framework hoặc global app lock.
Future writer graph (kể cả prefix ngoài P1-04) phải dùng **cùng protocol/key**; thay
đổi các entity ngoài scope không được implement ngầm ở fix này.

`TaxonomyG4FindingTests.RedirectGraphSnapshotSerializesCrossPrefixReclamationInEitherOrderAndSamePrefix`:

- Public material a, public artisan x, redirect `/nghe-nhan/y → /chat-lieu/a`.
- EF test interceptor dùng TaskCompletionSource pause đúng sau T1 graph SELECT;
  T2 bắt đầu trong khi T1 giữ transaction/row/graph lock. Quan sát `pg_locks`
  database riêng với waiting advisory lock; không grant quyền xem SQL/PII khác.
- Sau release barrier, cả hai HTTP 200: material a→b, artisan x→y;
  `/nghe-nhan/y` không là source, x→y (không sang material), a→b; unrelated link
  giữ nguyên, không chain/loop, first_published_at không đổi.
- Chạy cả hai thứ tự prefix; same-prefix variant dùng hai materials; timeout
  bounded 10s cho completion để phát hiện deadlock/hang, không dựa vào sleep race.
- `RedirectLockIsReleasedAfterRollbackAndRetryPreservesTheOtherPrefixCommit`:
  T1 paused/giữ graph; T2 chờ; trigger làm T1 failure 500/rollback. T2 reclaim
  thành công; graph/slug/version T1 giữ nguyên, lock được release. Gỡ test trigger,
  retry T1 thành công, không ghi đè commit T2 và không phantom redirects.
- Existing TaxonomyApiTests tiếp tục kiểm sequential A→B→C/reuse, slug/status
  concurrency, draft/history, rollback redirect/audit/outbox và hierarchy.

## Red/green và verification thực chạy

Linux runner riêng có sẵn `teapremium-p104-linux-g3`, .NET SDK 10.0.403;
Testcontainers postgres:17/database riêng/random ports, tp_app + IAM/CSRF thật.
Không dùng shared development DB, không tắt Windows Application Control.
Prefix lệnh dotnet thực: `docker exec teapremium-p104-linux-g3`.
TRX output directory: `artifacts/p1-04-g4-fix/tests` (ignored, không commit).

| Lệnh sau prefix | Exit / kết quả |
|---|---|
| `dotnet test backend/tests/TeaPremium.UnitTests/TeaPremium.UnitTests.csproj --no-restore --filter FullyQualifiedName~TaxonomyG4ValidationTests --logger 'trx;LogFileName=red-validation.trx' --results-directory artifacts/p1-04-g4-fix/tests` | **1, red 9 FAIL/0 PASS** trước fix: null item exception, NUL chưa bị validator reject |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~TaxonomyContentTests --logger 'trx;LogFileName=red-content.trx' --results-directory artifacts/p1-04-g4-fix/tests` | **1, red 3 FAIL/10 PASS**; actual onetwo / nối nested blocks / NBSP blocks |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~TaxonomyG4FindingTests.RedirectGraphSnapshot' --logger 'trx;LogFileName=red-redirect.trx' --results-directory artifacts/p1-04-g4-fix/tests` | **1, red 2 FAIL/1 PASS**; khác prefix không chờ graph snapshot, same-prefix đã có namespace lock |
| `dotnet test backend/tests/TeaPremium.UnitTests/TeaPremium.UnitTests.csproj --no-restore --filter FullyQualifiedName~TaxonomyG4ValidationTests --logger 'trx;LogFileName=green-validation.trx' --results-directory artifacts/p1-04-g4-fix/tests` | **0, green 9/9 PASS** |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~TaxonomyG4FindingTests\|FullyQualifiedName~TaxonomyContentTests' --logger 'trx;LogFileName=focused-g4-findings-final.trx' --results-directory artifacts/p1-04-g4-fix/tests` | **0, 28/28 PASS**, gồm 15 PG17 HTTP/concurrency + 13 content cases, 0 skip |
| `dotnet restore backend/TeaPremium.sln --locked-mode` | **0 PASS**, không lockfile/dependency thay đổi |
| `dotnet build backend/TeaPremium.sln --no-restore` | **0 PASS**, 0 warnings/errors |
| `dotnet test backend/tests/TeaPremium.UnitTests/TeaPremium.UnitTests.csproj --no-restore --logger 'trx;LogFileName=unit-full.trx' --results-directory artifacts/p1-04-g4-fix/tests` | **0, 92/92 PASS**, 0 skip |
| `dotnet test backend/tests/TeaPremium.ArchitectureTests/TeaPremium.ArchitectureTests.csproj --no-build --no-restore --logger 'trx;LogFileName=architecture.trx' --results-directory artifacts/p1-04-g4-fix/tests` | **0, 34/34 PASS**, 0 skip |

Pipe trong filter được escape chỉ để render Markdown table; argument thực là
`FullyQualifiedName~TaxonomyG4FindingTests|FullyQualifiedName~TaxonomyContentTests`.
Full integration kết quả cuối được ghi ở phần handoff dưới đây. Trước final
focused suite đã chạy 27/27 PASS, sau thêm
rollback/retry case chạy nguyên focused suite 28/28 PASS.

OpenAPI command thực:

```powershell
docker exec --env PATH=/opt/p104-node/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin teapremium-p104-linux-g3 corepack pnpm check:api
docker exec --env 'ConnectionStrings__Default=Host=localhost;Database=teapremium_test_p104_design;Username=tp_owner;Password=unused-design-only' teapremium-p104-linux-g3 dotnet ef migrations has-pending-model-changes --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --no-build
```

Cả hai **exit 0 PASS**: generated OpenAPI/client current, EF không pending model
changes. SHA-256 so đầu lượt xác nhận 11 protected files (sáu spec, migration/new
designer/snapshot, OpenAPI/TS client) không đổi byte; không có migration/schema delta
trong finding fix. Không cần chạy destructive migration/re-backfill trên DB thật.

## Tích hợp P1-03 / PR #29 — hợp đồng tối thiểu đề xuất

Thông tin PR #29 và behavior batch 200 là input owner; không merge/copy code hay
sửa worktree P1-03/CI. Rủi ro cần reconcile sau khi hai slice đã review:

- CatalogEndpoints.cs, CatalogContracts.cs, Program.cs, DI registration có tên/
  responsibility chồng nhau: ghép route/registration, giữ tổng contract hai task,
  tránh route duplicate hoặc ghi đè validators/handlers.
- OpenAPI/client phải sinh **một lần sau tích hợp**, không chọn nguyên một bên.
- RebuildProductSearchText message/handler trùng định nghĩa: giữ **một canonical
  message type** và **một IOutboxHandler** cho MessageType đó, thống nhất payload/
  serializer của P1-03 đã duyệt; không đăng ký hai handlers cùng MessageType.
- P1-03 consumer giới hạn **200 IDs/message**, P1-04 producer có thể lấy >200 IDs.
  Hợp đồng đề xuất: normalize/deduplicate/sort toàn bộ affected IDs, chia chunk
  **≤200** ở producer/điểm enqueue canonical, enqueue **mọi chunk** cùng transaction
  rename. Không Take(200)/truncate, không chỉ gửi chunk đầu; outbox rollback tất cả
  nếu enqueue bất kỳ chunk lỗi. Consumer giữ current-state/idempotent retry của
  P1-03; boundary tests 199/200/201/nhiều chunk, duplicate/redelivery/rollback và
  bảo đảm union processed IDs đúng toàn affected IDs (kể cả >200).
- Chưa triển khai chia batch/đổi consumer chéo nhánh trong focused fix. P1-04
  hiện gửi đủ IDs, không truncate. Thực thi/review tích hợp canonical contract là
  bước riêng sau owner-authorized integration, không nhận đã tương thích PR #29.

## Giới hạn và rollback

Finding fixes không đổi dữ liệu/schema đang lưu. Revert source về pre-fix sẽ mở
lại race/HTTP500/count bug nên không đề xuất deploy bản cũ. DB thật vẫn HOLD khi
lịch sử public chưa xác minh theo amendment; không bịa timestamps. Future graph
writers phải phối hợp lock key/order; lock scope cố ý serialize graph mutations,
không serialize toàn ứng dụng. Không đo throughput/p95 hoặc claim production load.
NOT RUN trong lượt này: production apply/live provider, P1-03 integration/seed,
GitHub CI ở published HEAD và focused G4 độc lập. Frontend runtime tests không
chạy lại vì không có UI/generated-client delta; evidence G3 giữ nguyên.

## Inventory local trước focused G4

Delta **so input G4**: sửa CatalogContracts.cs, EfCatalogStore.cs, CatalogContent.cs,
TaxonomyContentTests.cs, traceability.md và p1-04-g3-evidence.md; thêm
TaxonomyG4ValidationTests.cs, TaxonomyG4FindingTests.cs và record này. Chỉ ba file
production thay đổi; không mất file nào trong 43-file input. Full local delta:

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
?? backend/tests/TeaPremium.IntegrationTests/TaxonomyG4FindingTests.cs
?? backend/tests/TeaPremium.IntegrationTests/TaxonomyMigrationTests.cs
?? backend/tests/TeaPremium.UnitTests/TaxonomyG4ValidationTests.cs
?? backend/tests/TeaPremium.UnitTests/TaxonomyRulesTests.cs
?? docs/decisions/ADR-P1-04-publication-history-and-publish.md
?? docs/plans/p1-04-g3-evidence.md
?? docs/plans/p1-04-g4-fix-evidence.md
?? docs/plans/p1-04-implementation.md
```

`git diff --stat origin/main` (không tự hiện nội dung untracked):

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
 docs/analysis/traceability.md                      |   38 +-
 docs/spec/00-README.md                             |   18 +
 docs/spec/03-database.md                           |   26 +
 docs/spec/04-business-rules.md                     |    1 +
 docs/spec/05-api.md                                |   29 +-
 docs/spec/09-seo.md                                |   35 +-
 docs/spec/schema.sql                               |    4 +
 packages/api-client/openapi.json                   | 3027 +++++++++++++++++---
 packages/api-client/src/generated/schema.ts        | 1789 +++++++++++-
 25 files changed, 4678 insertions(+), 516 deletions(-)
```

25 tracked-modified + 21 untracked = **46 paths**. Staged rỗng
(`git diff --cached --name-only`), HEAD/base/branch không thay đổi. Reviewer phải
đọc trực tiếp nội dung untracked; không chỉ xem diff/stat trên tracked files.
Inventory/hash/TRX local tại artifacts/p1-04-g4-fix, không được commit evidence
chứa dữ liệu thật/secret. Không có dữ liệu thật trong fixtures.

## Kết quả cuối và handoff

Lệnh đầy đủ đã chạy:

```powershell
docker exec teapremium-p104-linux-g3 dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --no-restore --settings artifacts/p1-04/isolated.runsettings --logger 'trx;LogFileName=integration-full.trx' --results-directory artifacts/p1-04-g4-fix/tests
```

**Exit 0, 593/593 PASS, 0 skipped**, duration 6m25s, artifact
`artifacts/p1-04-g4-fix/tests/integration-full.trx`. Gồm tất cả sequential redirects,
category hierarchy, collection ordering, media/IAM/CSRF, outbox, audit/rollback,
migration/parity và 19 integration cases thêm so G3 (4 content + 15 HTTP/concurrency).
Unit **92/92** + integration **593/593** + architecture **34/34** = **719 PASS**,
0 skipped; thêm 9 unit và 19 integration cases, giữ toàn bộ bộ G3.

TRX red/green được giữ nguyên tên trong bảng để reviewer tái hiện. PowerShell
validation tài liệu UTF-8 strict/whitespace/fences/local links và heading anchors:
**11 Markdown files PASS**; `git diff --check origin/main` và
`git diff --cached --check` exit 0, staged rỗng. SHA-256 baseline comparison và
EF/OpenAPI checks PASS như trên; không có API contract hay schema drift.

| Finding | Trạng thái sửa local |
|---|---|
| P1 — redirect graph concurrency | **FIXED** |
| P2-A — sanitizer boundaries | **FIXED** |
| P2-B — null collection items | **FIXED** |
| P2-C — U+0000 writable/query/JSON | **FIXED** |

**READY_FOR_FOCUSED_G4: YES.** Đây là G3 finding-fix handoff với tests thực,
không phải self-approved G4/PASS hay quyền publish. Reviewer mới phải đọc cùng
worktree/diff và nội dung untracked; next step focused G4 cho bốn finding.
Runner riêng teapremium-p104-linux-g3 được dừng sau verification; giữ container
để reviewer có thể start/rerun. Không sửa/dừng resources thuộc worktree khác.
Không commit/push/PR/merge/deploy/production apply.
