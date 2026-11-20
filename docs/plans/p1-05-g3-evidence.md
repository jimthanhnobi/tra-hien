# P1-05 — bằng chứng G3 local

**Cập nhật:** owner cung cấp independent G4 BLOCK (F1/P1 + F2/P2 + F3/P2).
Remediation và triage tại [focused fix evidence](p1-05-g4-fix-evidence.md).
Giữ nguyên số931/931 và inventory39file bên dưới là evidence lịch sử G3; không
dùng chúng thay verification của remediation hoặc claim independent G4 PASS.

Ngày 20/11/2026, `D:\tra-hien-p1-05`, branch `feature/p1-05-products-variants`.
Base/HEAD `bb9a901044d41a0783d226fcd678e3865390fa30`; tất cả thay đổi còn local,
không commit/stage/push/PR/merge/production apply. G1 TARGETED/G2 đã owner-approved;
giữ ngoại lệ draft active NULL theo BR-CAT-15/AC-P1-09. Source/traceability, failure
matrix, lock protocol, migration và rollback tại [task record](p1-05-implementation.md).

**G3 PASS. READY_FOR_LOCAL_G4: YES.** Slices A/B/C/D hoàn tất local.
Full backend **931/931 =174 unit +34 architecture +723 integration**, zero
failed/skipped. Frontend **26/26**. Totals lấy từ run source cuối, không cộng
focused reruns hoặc run bị hủy. Đây không là independent G4.

## Kết quả và lệnh thực

Prefix các lệnh trong bảng: `docker exec teapremium-p105-g3`.
Runner riêng, SDK10.0.403, Node22.22.2, ffmpeg, PostgreSQL17 Testcontainers và tp_app.

| Lệnh sau prefix | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | exit0 PASS |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | exit0 PASS, 0 warnings/errors |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | exit0 PASS sau sửa một whitespace tại ProductOpenApi.cs |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --settings artifacts/p1-05/isolated.runsettings --logger trx --results-directory /workspace/artifacts/p1-05/full` | INTERRUPTED chủ động khi thêm archive JSON guard; unit174/174 và architecture34/34 PASS, không dùng run này claim full PASS |
| Full source cuối, command bên dưới | exit0 PASS **931/931**, integration723/723 PostgreSQL17 trong8m49s, unit174/174, architecture34/34; không skip |
| `dotnet tool restore` | exit0 PASS, dotnet-ef10.0.15 theo manifest |
| `corepack pnpm install --frozen-lockfile` | exit0 PASS; lockfile không đổi |
| `corepack pnpm generate:api` | exit0 PASS |
| `corepack pnpm check:api` | exit0 PASS, OpenAPI/client current |
| `corepack pnpm lint` | exit0 PASS |
| `corepack pnpm typecheck` | exit0 PASS, gồm products-contract.test-d.ts |
| `corepack pnpm test` | exit0 PASS sau sửa runner: 10 Node +16 browser, 26/26; lượt đầu thiếu Chromium được giữ lịch sử bên dưới |
| `corepack pnpm build` | exit0 PASS api-client/Admin/Store production build |

Model/migration commands thêm env **design-only**, không kết nối DB thật:

```text
docker exec --env 'ConnectionStrings__Default=Host=127.0.0.1;Port=1;Database=ci_design_only;Username=ci_dummy;Password=ci_dummy' teapremium-p105-g3 dotnet ef migrations has-pending-model-changes --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --no-build
docker exec --env 'ConnectionStrings__Default=Host=127.0.0.1;Port=1;Database=ci_design_only;Username=ci_dummy;Password=ci_dummy' teapremium-p105-g3 dotnet ef migrations script --idempotent --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --no-build --output artifacts/p1-05/migrations.sql
```

Cả hai exit0 PASS; model không drift. Migration requirement **NO**. Diff spec/
migrations/snapshot rỗng; existing PG parity/roles/migration tests thuộc full suite.
`git diff --check`, `git diff --cached --check` exit0. `scripts/check-test-results.ps1 -Directory artifacts/p1-05/full-final`
exit0 xác nhận ba TRX đủ tests, zero skip. Idempotent migration SQL85486bytes.
Build/frontend chạy sinh next-env.d.ts tạm thời, final `git diff --exit-code -- apps`
exit0; không UI delta. Final inventory39 paths=12 tracked modified+27 untracked,
staged rỗng, HEAD không đổi, `origin/main..HEAD` không commit mới. Main worktree sạch.
Exact inventory bên dưới; final status/source hashes/TRX summary ở
`artifacts/p1-05/final-status.txt`, `final-source-sha256.json`, `trx-summary.json`.
UTF-8 strict decode39paths PASS; Markdown fences/local links hai task/evidence PASS.
Fetch cuối vẫn origin/main=base nêu trên. Runner riêng đã stop sau tests; không
đụng container/worktree của task khác.

Full source cuối dùng command như CI, giới hạn CPU4 cho runner disposable:

```text
docker exec --env DOTNET_PROCESSOR_COUNT=4 teapremium-p105-g3 dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /workspace/artifacts/p1-05/full-final
```

## Focused proof và lịch sử lượt fail

Commands focused dùng cùng integration csproj:
`dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-restore --filter <filter> --logger 'trx;LogFileName=<name>.trx' --results-directory /workspace/artifacts/p1-05`.

| Filter / artifact | Kết quả thực |
|---|---|
| `FullyQualifiedName~ProductLockOrderTests`, lock-before.trx | 1 PASS/1 FAIL, xác nhận material giữ graph trong khi chờ RowExclusive materials |
| cùng filter, lock-after.trx | 2/2 PASS sau early table-lock fix |
| `FullyQualifiedName~ProductApiTests\|FullyQualifiedName~ProductLockOrderTests`, slice-a.trx | 12/12 PASS |
| Unit csproj, `FullyQualifiedName~ProductRulesTests`, slice-b-unit.trx | 12/12 PASS |
| `FullyQualifiedName~ProductVariantTests\|FullyQualifiedName~ProductPublicationTests`, slice-b-c-retest.trx | 13/13 PASS tại lượt đó |
| `FullyQualifiedName~ProductConcurrencyTests\|FullyQualifiedName~ProductValueTests`, concurrency-values-final.trx | 11/11 PASS tại lượt đó |
| `FullyQualifiedName~ProductOutboxContractTests`, slice-d.trx | 7/7 PASS tại lượt đó |
| `FullyQualifiedName~Product`, products-focused.trx | 50/50 PASS tại lượt đó; có match một số test nền, không gọi là 50 tests mới |
| `FullyQualifiedName~MalformedArchiveRedirect`, archive-json-guard.trx | 4/4 PASS: number/bool/object/array redirectTo →400 ProblemDetails, không partial write |

Lượt đầu SKU có 3PASS/2FAIL do test đếm thiếu event của lần PATCH và fixture
movement thiếu occurred_at; đã sửa assertions/fixture. Lượt price/lifecycle đầu
7PASS/1FAIL do version của unchanged variant bị cập nhật khi product publish; đã
thu hẹp persistence để chỉ cập nhật variant thực sự thay đổi và cập nhật test token.
Media/security lượt đầu3PASS/1FAIL vì expected CSRF403 trong khi contract nền trả
400 csrf_invalid; sửa expectation giữ API nền. Full-source verification sau cùng
bao gồm các ca trên và các tests bổ sung sau focused run. Không claim các lượt
đầu đều PASS. Soát input cuối phát hiện redirectTo sai kiểu có thể ném
InvalidOperationException; đã thêm type guard và chuẩn hóa malformed/body errors
của Product endpoints về ProblemDetails. Full run serial được hủy để rebuild/run
lại source cuối theo cấu hình CI; không tính run bị hủy vào total cuối.

Browser lượt đầu16 FAIL vì executable Chromium headless1255 chưa có. Đã giữ error
contexts ở artifacts/p1-05/browser-before-setup; lấy cached browser cùng revision
từ runner cũ bằng copy chỉ đọc và cài dependencies trong runner mới. Không sửa
worktree cũ/main để sửa môi trường, không đổi browser tests cho qua.

## API inventory và acceptance

Base `/api/v1/admin/products`; 11 paths/16 operations mới, OpenAPI thực đo.
Các mutations trả ProductData mới gồm opaque parent/variant rowVersion.

| Path tương đối | Methods được test qua HTTP |
|---|---|
| rỗng | GET, POST |
| /{id} | GET, PATCH |
| /{id}/publish | POST |
| /{id}/archive | POST |
| /{id}/restore-draft | POST |
| /{id}/variants | GET, POST |
| /{id}/variants/{childId} | GET, PATCH |
| /{id}/media | POST |
| /{id}/media/{childId} | PATCH, DELETE |
| /{id}/media/{childId}/cover | POST |
| /{id}/media/order | PUT |

So JSON từng existing path và common schema với origin/main: **0 changed, 0 missing**.
Client có ProductCreate/ProductPatch/VariantCreate/VariantPatch schemas; PATCH có
rowVersion required, nullable price, không writable skuBase/status/count của product.
P1-03/P1-04 APIs không bị thay schema/permission.

Actual PG17 proofs: product/material HTTP cả hai thứ tự không HTTP500/deadlock,
cross-prefix inbound redirects vẫn flatten; reference metadata/value writes ở
product và variant scopes; parent serialization publish vs NULL-price; reciprocal
related products không deadlock FK; cover concurrent/rollback giữ một bìa.

SKU: first=base, giữ mã cũ, nextval concurrency/9999→10000/gap, manual/inactive
collision, max40, bounded64 exhaustion, savepoint không stale EF/audit. Usage kiểm
movement thật và injected order-usage adapter; **không** nhận Sales integration thật.

Publish: all errors422 (cover ALT + required values + prices + metadata cùng list),
axes, draft exception/inactive NULL/activation; count149/150/151, manual/site,
history/redirect/archive/restore; real P1-02 complete/public asset reuse. Audit/
outbox/redirect failures rollback fingerprint và rowVersion sau early saves.

## Exact writable inventory

G3 main implementer sở hữu các paths dưới; G4 phải đọc cả untracked contents.

```text
backend/src/TeaPremium.Api/Program.cs
backend/src/TeaPremium.Api/Catalog/ProductEndpoints.cs
backend/src/TeaPremium.Api/Catalog/ProductOpenApi.cs
backend/src/TeaPremium.Application/Catalog/CatalogContracts.cs
backend/src/TeaPremium.Application/Catalog/CatalogHandlers.cs
backend/src/TeaPremium.Application/Catalog/IVariantUsage.cs
backend/src/TeaPremium.Application/Catalog/ProductContracts.cs
backend/src/TeaPremium.Application/Catalog/ProductRules.cs
backend/src/TeaPremium.Application/Catalog/ProductWorkflow.cs
backend/src/TeaPremium.Application/Inventory/ICatalogInventoryRead.cs
backend/src/TeaPremium.Application/Media/IPublicImageAssets.cs
backend/src/TeaPremium.Application/Outbox/StorefrontInvalidate.cs
backend/src/TeaPremium.Infrastructure/Catalog/CatalogRegistration.cs
backend/src/TeaPremium.Infrastructure/Catalog/EfCatalogStore.cs
backend/src/TeaPremium.Infrastructure/Catalog/EfProductStore.cs
backend/src/TeaPremium.Infrastructure/Catalog/EfProductStore.Save.cs
backend/src/TeaPremium.Infrastructure/Catalog/ProductAudit.cs
backend/src/TeaPremium.Infrastructure/Catalog/RebuildProductSearchText.cs
backend/src/TeaPremium.Infrastructure/Inventory/EfCatalogInventoryRead.cs
backend/src/TeaPremium.Infrastructure/Media/MediaRegistration.cs
backend/src/TeaPremium.Infrastructure/Media/EfPublicImageAssets.cs
backend/src/TeaPremium.Infrastructure/Outbox/StorefrontInvalidateHandler.cs
backend/src/TeaPremium.Infrastructure/Persistence/AuditValuePolicy.cs
backend/src/TeaPremium.Infrastructure/Persistence/CatalogInventoryEntities.cs
backend/tests/TeaPremium.IntegrationTests/ProductApiTests.cs
backend/tests/TeaPremium.IntegrationTests/ProductConcurrencyTests.cs
backend/tests/TeaPremium.IntegrationTests/ProductLockOrderTests.cs
backend/tests/TeaPremium.IntegrationTests/ProductMediaSecurityTests.cs
backend/tests/TeaPremium.IntegrationTests/ProductOutboxContractTests.cs
backend/tests/TeaPremium.IntegrationTests/ProductPublicationTests.cs
backend/tests/TeaPremium.IntegrationTests/ProductValueTests.cs
backend/tests/TeaPremium.IntegrationTests/ProductVariantTests.cs
backend/tests/TeaPremium.UnitTests/ProductRulesTests.cs
docs/analysis/traceability.md
docs/plans/p1-05-implementation.md
docs/plans/p1-05-g3-evidence.md
packages/api-client/openapi.json
packages/api-client/src/generated/schema.ts
packages/api-client/src/products-contract.test-d.ts
```

## Risks, NOT RUN và next gate

- Real R2/provider permissions, production/history/backfill assessment, cache receiver
  thực/SSR/canonical/sitemap/feed và NFR-01 benchmarks: NOT RUN, ngoài local G3 này.
- Receiver chưa tồn tại: backend delivery thử bằng fake HTTP; thiếu config/503
  giữ pending/backoff, không no-op success. P3 cần receiver và xử lý pending/dead
  trước go-live. Header contract: X-Revalidate-Secret, JSON `{tags:[...]}`.
- Sales order-line adapter thật chưa có tables ở P1; nối IVariantUsage cùng P2 trước
  tạo giao dịch. P1 chỉ read inventory facts, không mutate stock.
- Populated target chưa xác minh public history không được coi NULL là never-public;
  deployment assessment/recovery giữ gate riêng. Không production apply.
- Published feature CI, independent G4, publish/merge: NOT RUN/NOT AUTHORIZED trong
  handoff G3. Main CI PASS không thay CI của change set chưa publish.

Rollback: giữ DB hiện hành, SKU/sequence gap/audit/outbox/history; source rollback
có kiểm soát hoặc roll-forward/recovery cho data đã commit, không destructive Down.
Review depth A; next gate là fresh independent G4 trên đúng worktree này và toàn bộ
committed/staged/unstaged/untracked diff so origin/main.
