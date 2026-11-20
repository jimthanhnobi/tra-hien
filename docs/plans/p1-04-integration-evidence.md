# P1-04 — tích hợp sau G4 với main đã merge P1-03

Ngày 19/11/2026, worktree `D:\tra-hien-p1-04`, branch
`feature/p1-04-catalog-taxonomy`. Owner xác nhận P1-04 independent G4 PASS,
các finding cũ CLOSED, chấp nhận một P2 performance không blocking cho tối ưu sau;
cho phép đúng một checkpoint local, rebase và integration fixes/tests. Không push,
PR, merge, deploy hay production apply. Kết quả G4 do owner cung cấp trong yêu cầu;
không có report PASS riêng tìm thấy trong docs/reviews, không tự nhận đã review.

## Snapshot, checkpoint và base

Trước staging: đúng **25 modified tracked + 21 untracked = 46 files**, staged rỗng;
đúng branch/HEAD original base `f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7`.
So sánh SHA-256 từng file và path với manifest cuối G4 fix: **46/46 khớp**;
không có unexpected source change. Stage đúng 46 path từ manifest, kiểm staged
inventory/blob với git hash-object, diff --cached --check, loại path artifacts,
bin/obj/node_modules/.env/keys/dumps và scan private-key/token signatures: PASS.
Các credential fixture là synthetic; không có runtime artifact/secret thật được stage.

| Bằng chứng | Giá trị |
|---|---|
| Manifest reviewed gốc | `artifacts/p1-04-integration/original-g4-sha256.json`, sao từ artifacts/p1-04-g4-fix/after-sha256.json |
| SHA-256 manifest bytes | `976EF0330853BD3708A13DD35D7D7F9AF61DD3B9A9A74C987810C29DA5F201DC` |
| Checkpoint commit | `c7f279ec687e444d733284b847dc72359c8d511c` |
| Checkpoint tree | `8970f818c716e3ae16268cd622127bc3fedec3cc` |
| Commit message | feat(catalog): implement P1-04 taxonomy and publication history |
| Main sau fetch | `0737dea96fa0e93d34b840d2bc8db5e56e4ffe13` |
| Rebased integrated HEAD | `81afe9e3e837f7f5df347368bffb092bc978f5b8` |
| Rebased tree | `145bd7c3321bd7d4f3567ed6fe51bec53b07e4bd` |

`git fetch origin --prune` exit 0: main đúng expected mới, chỉ có P1-03 commit
`f6e7bf4` và merge PR #29 `0737dea`, không thêm thay đổi ngoài thông tin owner.
Diff docs/spec original base→main rỗng, tái sử dụng G1/G2/amendment. Rebase
`git rebase origin/main` dừng conflict exit 1 đúng dự kiến; resolve rồi
`git -c core.editor=true rebase --continue` exit 0. Không reset/clean/chọn ours-theirs
mù hoặc tạo merge commit. Checkpoint gốc còn truy xuất được; rebase rewrite thành
HEAD mới, **một commit** trong log origin/main..HEAD, không có commit tích hợp thứ hai.
Test/evidence bổ sung sau rebase giữ local unstaged/untracked cho integration G4.

## Conflict inventory và cách resolve

| Conflict thực tế | Resolution semantic |
|---|---|
| `backend/src/TeaPremium.Api/Catalog/CatalogEndpoints.cs` add/add | Hợp nhất DTOs/20 taxonomy operations của P1-04 với Root/helpers/12 reference operations của P1-03 trong một static class. Giữ MapCatalog và MapCatalogReference khác nhau, không route duplicate. |
| `backend/src/TeaPremium.Application/Catalog/CatalogContracts.cs` add/add | Giữ toàn bộ typed input/reference interfaces P1-03 và taxonomy entry/commands/validators/ports P1-04. CatalogPage<T> và CatalogPage arity khác nhau, không xóa feature hoặc override validation. |
| `backend/src/TeaPremium.Api/Program.cs` content | Giữ seed-catalog-reference preflight/entrypoint, CatalogOpenApi transformer và MapCatalogReference của main; thêm AddCatalog/MapCatalog của taxonomy, không bỏ import-provinces/bootstrap/IAM/media. |
| `packages/api-client/openapi.json` content | Sinh lại từ integrated API thật, gồm cả hai feature sets, không lấy nguyên một bên. |
| `packages/api-client/src/generated/schema.ts` content | Sinh lại cùng JSON; giữ catalog-contract.test-d.ts P1-03 và kiểm typecheck. |

CatalogInventoryEntities.cs auto-merge sạch nhưng đã kiểm semantic: các Audited
annotations P1-03 của ProductType/AttributeDefinition/Option còn; bốn taxonomy
FirstPublishedAt/Version P1-04 còn. AppDbContext/model/snapshot/migration không có
conflict hay shape change do P1-03. PersistenceRegistration giữ ICatalogReference,
ICatalogReferenceSeed và canonical handler P1-03; AddCatalog chỉ đăng ký taxonomy
workflow/store/content. Không sửa worktree P1-03/CI, không copy unfinished code.

## Canonical search rebuild và batch contract

Giữ **một** RebuildProductSearchText record với Guid[] Ids và Message constant của
P1-03, cùng wire JSON/MessageType `RebuildProductSearchText`, giới hạn consumer
**1..200 IDs**. Chuyển record từ Infrastructure sang Application/Catalog để cả
producer Application lẫn adapter reference dùng chung mà không đảo dependency.
Đây là di chuyển CLR location phục vụ layer boundary, không đổi payload/limit.

Giữ nguyên consumer body P1-03 ở Infrastructure/Catalog/RebuildProductSearchText.cs:
rebuild theo current persisted names, variant order position/id, không sửa SKU.
Xóa implementation trùng RebuildProductSearchTextHandler.cs của P1-04; bỏ handler
registration trùng trong CatalogRegistration. Runtime DI tests xác nhận **một**
IOutboxHandler cho MessageType, không SingleOrDefault failure do duplicate.

P1-03 type producer giữ foreach ids.Chunk(200) vốn có. P1-04 material/artisan
producer đổi thành Distinct/Order/Chunk(200), enqueue **mọi** chunk cùng UoW rename;
0 IDs không event, không empty messages, không Take/truncate. Nếu batch bất kỳ
fail, cả rename/audit/outbox rollback. Không thêm worker/framework/product CRUD.

`CatalogIntegrationTests` kiểm cả ba resource, mỗi case so union IDs đúng toàn
products, không duplicate unintended IDs, mọi batch 1..200; thay persisted tên
type/material/artisan mới hơn trước consume, rồi kiểm search_text dùng tên mới,
replay mọi batch idempotent và SKU fingerprint giữ nguyên:

| Affected IDs | Messages / resource | Số resource được test |
|---|---|---|
| 0 | 0 | 3 |
| 1 | 1 | 3 |
| 200 | 1 | 3 |
| 201 | 2 | 3 |
| 350 | 2 | 3 |
| 401 | 3 | 3 |

Thêm ba failure cases: fixture sequence không transactional chứng minh trigger đã
chạy tới **insert thứ hai**, trigger raise; HTTP 500 không lộ fixture details,
fingerprint type/material/artisan/products/variants/audit/outbox giữ nguyên,
rowVersion không đổi, không phantom event. Gỡ fixture trigger và retry cùng version
→3 messages, consumer rebuild đủ401 products. Không sửa provider-error contract.

## Seed và Materials CRUD compatibility

Chạy use case seed thật qua ICommandHandler trên PG17/runtime role, không fake
seed values: empty→**9 types/23 attributes/14 options/13 materials**. Kiểm từng
default UUID/slug/sku_code/kind, toàn materials draft/first_published_at NULL;
seed không publish hoặc tự sinh lịch sử.

Qua Materials API sửa tên/summary/description/SEO của seeded UUID rồi seed lại:
0 additions, fingerprint không đổi, không overwrite metadata và rowVersion.
Gắn product/variant FK vào material seed; publish qua endpoint đúng quyền → timestamp
được set; seed lại giữ nguyên timestamp/sku_code/assigned SKUs/FKs. DELETE không có
operation (405), không hard-delete referenced material.

Admin tạo material code riêng trước seed →13 seeded +1 custom, lặp seed không
duplicate/overwrite custom record. Trường hợp Admin-created material code TN nhưng
ID khác default: seed trả catalog_seed_identity_conflict; transaction rollback cả
types/attrs/options mới, giữ prior values/material/audit/outbox. Các seed CLI/
identity/metadata-guard tests P1-03 giữ nguyên và chạy trong integrated suite.

## API inventory: 32 operations

Base `/api/v1/admin`, thực đo OpenAPI **17 paths / 32 operations =12+20**, không
DELETE. File machine-readable: artifacts/p1-04-integration/catalog-endpoints.json.

| Path | Methods |
|---|---|
| product-types | GET, POST |
| product-types/{id} | GET, PATCH |
| product-types/{id}/attributes | GET, POST |
| product-types/{id}/attributes/{attrId} | GET, PATCH |
| product-types/{id}/attributes/{attrId}/options | GET, POST |
| product-types/{id}/attributes/{attrId}/options/{optId} | GET, PATCH |
| categories | GET, POST |
| categories/{id} | GET, PATCH |
| collections | GET, POST |
| collections/{id} | GET, PATCH |
| collections/{id}/products | GET, PUT |
| materials | GET, POST |
| materials/{id} | GET, PATCH |
| materials/{id}/publish | POST |
| artisans | GET, POST |
| artisans/{id} | GET, PATCH |
| artisans/{id}/publish | POST |

AdminCookie/ready, GET catalog.read, write catalog.write, taxonomy publish
catalog.publish và CSRF/rowVersion giữ đúng spec. TypedAttributeValidator, reference
guards/seed CLI P1-03, tree/publication/SEO/warnings/HTML/NUL/null-list/redirect graph
lock `(8104,100)` P1-04 giữ nguyên. Không triển khai P2 performance optimization.

## Migration, traceability và phạm vi

P1-04, FR-CAT-01/02/03/08; BR-CAT-01/06/08/09/10/14; BR-CNT-02;
BR-SEO-03/23/31/32/35; NFR-04/07/08/09; DEC-01/04/21/26/30.
Reused sources: docs/spec 00, 02 layers/UoW/outbox, 03 §3.10/§8, 04 §CAT,
05 §4.2, 07 P1-03/P1-04, 09 §4; không tạo requirement/task mới.

`git diff --quiet c7f279ec687e444d733284b847dc72359c8d511c HEAD -- docs/spec backend/src/TeaPremium.Infrastructure/Persistence/Migrations`
exit 0. Bốn nullable/no-default first_published_at columns, migration/designer/
snapshot/spec **giữ nguyên** so checkpoint; không sửa migration lịch sử.
EF has-pending-model-changes trên integrated build exit 0, không drift.
Integrated regression kiểm PG17 data/FKs/idempotence/runtime-role/Down guards,
publication immutability, cross-prefix reclaim/flatten/rollback và category concurrency.

Historical-data production/application rollout vẫn **HOLD** khi chưa có evidence
lịch sử đúng target; không đoán timestamp/backfill, không production apply. Không
rollback destructive: source checkpoint là recovery anchor; DB thật backup + roll
forward/recovery theo amendment. Rebase không thay dữ liệu hay resources worktree khác.

## Verification trên code kết hợp

Runner riêng `teapremium-p104-linux-g3`, SDK10.0.403, postgres:17 Testcontainers/
random port/DB riêng và tp_app. Không cộng số test hai nhánh đã báo trước.
Prefix thực cho dotnet: `docker exec teapremium-p104-linux-g3`.
TRX output: `artifacts/p1-04-integration/tests` (ignored).

| Lệnh sau prefix | Exit / kết quả thực |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | 0 PASS |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | 0 PASS, 0 warnings/errors |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --no-restore --settings artifacts/p1-04/isolated.runsettings --filter 'FullyQualifiedName~CatalogReference\|FullyQualifiedName~Taxonomy' --logger 'trx;LogFileName=combined-focused-before.trx' --results-directory artifacts/p1-04-integration/tests` | 0 PASS **121/121** existing combined focused tests, 0 skip |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~CatalogIntegrationTests --logger 'trx;LogFileName=integration-new-focused.trx' --results-directory artifacts/p1-04-integration/tests` | 0 PASS **24/24** new integration tests, 0 skip |
| `dotnet test backend/tests/TeaPremium.UnitTests/TeaPremium.UnitTests.csproj --no-restore --logger 'trx;LogFileName=unit-full.trx' --results-directory artifacts/p1-04-integration/tests` | 0 PASS **162/162**, 0 skip |
| `dotnet test backend/tests/TeaPremium.ArchitectureTests/TeaPremium.ArchitectureTests.csproj --no-build --no-restore --logger 'trx;LogFileName=architecture.trx' --results-directory artifacts/p1-04-integration/tests` | 0 PASS **34/34**, 0 skip |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --no-restore --settings artifacts/p1-04/isolated.runsettings --logger 'trx;LogFileName=integration-full.trx' --results-directory artifacts/p1-04-integration/tests` | 0 PASS **670/670**, 0 failed/skip, 8m24s |

Tổng backend thực chạy trên integrated source: **866/866 =162 unit +670 integration
+34 architecture**, không tính focused reruns thêm lần nữa. Full integration gồm
PG17 migrations, Catalog Reference/Taxonomy, seed/CRUD, redirect concurrency,
media/R2 regressions và 24 integration tests mới.

Pipe được escape trong Markdown table, actual filter dùng ký tự `|`.
New focused lượt đầu exit1:17PASS/7FAIL do **test harness** so sánh SQL list không
ORDER BY (6 cases), và resolve DB ở OpenAPI CI host không connection (1 case).
Đã thêm ORDER BY id cho assertion replay và dùng PG harness cho DI resolution;
chạy lại nguyên24cases PASS. Artifact first-attempt TRX được giữ, không claim
lượt đầu PASS. Grant sequence test-only để trigger counting chạy với tp_app;
không có grant/migration production thay đổi.

Prefix thực các pnpm commands:
`docker exec --env PATH=/opt/p104-node/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin teapremium-p104-linux-g3`.
`corepack pnpm generate:api`, `check:api`, `lint`, `typecheck`: mỗi lệnh **exit0 PASS**
trên integrated source/client. `corepack pnpm test`: **exit0 PASS, 26 tests**
(2 store Node +8 admin Node +16 browser). `corepack pnpm build`: **exit0 PASS**
api-client/admin/store production build. Build tự sinh next-env.d.ts đã được trả
về nguyên nội dung trước chạy; `git diff --exit-code -- apps` exit0, không UI source delta.
So sánh JSON từng OpenAPI path với main và checkpoint: mỗi bên **0 missing paths,
0 changed existing paths**; client đã regenerate từ integrated API.

Model command (design-only env, không DB thật), **exit0 PASS**:

```powershell
docker exec --env 'ConnectionStrings__Default=Host=localhost;Database=teapremium_test_p104_design;Username=tp_owner;Password=unused-design-only' teapremium-p104-linux-g3 dotnet ef migrations has-pending-model-changes --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --no-build
```

Artifacts: original-g4-sha256.json, checkpoint.json, conflict-paths.txt,
rebase-range-diff.txt, catalog-endpoints.json và TRX. Runtime artifacts không staged.
Full G4 PASS report chỉ có owner summary; exact performance finding path/details
không có trong input. Ghi follow-up **P2 performance accepted/deferred**, chưa sửa
hoặc tự suy đoán optimization. Integration vẫn cần G4 độc lập mới trên HEAD+local diff.

## Local handoff cuối

**INTEGRATION: PASS. READY_FOR_INTEGRATION_G4: YES.** Đây là G3 integration evidence,
không phải independent G4 approval. Review mới phải đọc cùng worktree, toàn bộ diff
so origin/main, gồm commit đã rebase, unstaged và untracked.

`git status --short`:

```text
 M docs/analysis/traceability.md
?? backend/tests/TeaPremium.IntegrationTests/CatalogIntegrationTests.cs
?? docs/plans/p1-04-integration-evidence.md
```

`git log --oneline origin/main..HEAD`:

```text
81afe9e feat(catalog): implement P1-04 taxonomy and publication history
```

Staged rỗng. Ngoài checkpoint đã được owner cho phép và rebase rewrite, không
commit thêm; không push/PR/merge/production apply. Bước tiếp theo là independent
integration G4; publication cần owner cho phép riêng sau G4 PASS. Historical-data
rollout tiếp tục HOLD; accepted P2 performance giữ deferred.

Full review inventory: **49 paths =47 tracked changed so origin/main +2 untracked**.
UTF-8 strict decode/Markdown fences: **12 files PASS**; **21 local links PASS**.
Runner riêng đã stop sau khi tests hoàn tất; docker inspect xác nhận `exited`.

Final inventory, source SHA-256 manifest và TRX counters lưu local tại
`artifacts/p1-04-integration/final-state.json`, `final-source-sha256.json`,
`trx-summary.json`; không stage runtime artifacts. UTF-8/Markdown fences/local
links đã kiểm toàn bộ Markdown trong delta; `git diff --check` và
`git diff --cached --check` exit0. Không đổi spec, migration hoặc snapshot so
checkpoint; không có conflict marker còn sót trong changed source.
