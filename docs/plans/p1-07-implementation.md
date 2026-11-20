# P1-07 — Product Import Excel/CSV

Base/HEAD `f39a78a11f9ad5c954e43aa430bf08de3882f504`, branch `feature/p1-07-product-import`, worktree `D:\tra-hien-p1-07`. P1-05 đã merge qua PR #31, CI push trên main tại đúng SHA là [44703570513](https://github.com/jimthanhnobi/tra-hien/actions/runs/44703570513).

G1 TARGETED dùng lại full baseline `cadce575597738bf40ad7c45689482de5a5fbc3d` và đối chiếu A01/B01, các delta P1-04/05. Owner duyệt 5 hợp đồng và amendment phạm vi tại BR-CAT-13. G2 giữ DEC-01/04/25/26 và phụ thuộc P1-05, các cấu phần P1-01/03/04 có sẵn. Giữ AC-P1-09, BR-CAT-14/15 nguyên văn.

| Phạm vi | Nguồn và xác minh |
|---|---|
| Preview/apply, quyền, định dạng/lỗi | FR-CAT-09 [01 §CAT](../spec/01-requirements.md#42-cat--catalog), BR-CAT-13 [04 §CAT](../spec/04-business-rules.md), [05 §4.2](../spec/05-api.md#42-catalog), NFR-04/09. `ProductImportTests` kiểm cả hai mode, CSV/XLSX, giá, quyền owner, Auth/CSRF, file/ô quá giới hạn, mã không tồn tại và thay đổi metadata giữa hai lần gửi. |
| Mô hình, SKU và giao dịch | DEC-04/25/26, BR-CAT-01/02/13–15, AC-P1-09 [07 §P1](../spec/07-roadmap.md), [DDL Catalog](../spec/schema.sql). Bảo toàn SKU tay toàn file, nhóm độc lập, cạnh tranh slug/SKU, sequence boundary, rollback sau variant đầu và dừng khi không xác định kết quả. |
| Search, audit, outbox và kho | BR-CAT-10/13/15, [02 §5](../spec/02-architecture.md), [03 §3.1/3.7](../spec/03-database.md). Kiểm search ngay sau commit, actor, số audit/outbox, fingerprint sequence/DB và adapter media trong fixture PostgreSQL 17. |

## API và ranh giới ghi

`POST /api/v1/admin/products/import?dryRun=true|false`, multipart đúng một `file`. Kết quả hợp lệ có `summary`, `groups`, `rows`, lỗi theo `row/column/code/message`. Xem [mẫu và hướng dẫn cột](../templates/p1-07-product-import.md). Preview chỉ dùng query, mã tự cấp hiển thị `generateOnApply`. File được parse và rà SKU trùng trước khi mở command ghi đầu tiên. Dữ liệu chỉ ghi vào Product mới, giữ trạng thái draft.

Apply mở scope riêng theo nhóm, lấy khóa reference và product namespace theo P1-05, đọc lại dữ liệu, cấp sequence và lưu một lần qua ProductWorkflow, ProductStore và UnitOfWork hiện có. Slug có thể đổi giữa preview/apply khi catalog thay đổi. Khi nhóm lỗi, nhóm khác tiếp tục. Mất xác nhận commit thì trả nhóm `unknown`, giữ kết quả đã biết và dừng các nhóm sau. Replay tệp có cả slug và SKU trống có thể tạo draft mới. Sequence có thể có khoảng trống sau rollback.

## Migration và rollback

**Migration: NO.** Không đổi EF model, DDL hoặc grants. Có thể bỏ route/import code và contract khi rollback release. Các draft đã commit giữ lại để owner đối chiếu theo ID, slug và SKU; dữ liệu catalog hiện hữu không được import sửa. Không thao tác cơ sở dữ liệu production. P1-11 giữ ownership Admin Catalog UI. P1-07 chỉ thay contract dùng chung cho endpoint import, cần kiểm contract delta nếu tích hợp main sau P1-11.


## File inventory

Tracked sửa (17), không có staged.

- `backend/src/TeaPremium.Api/Catalog/ProductEndpoints.cs`
- `backend/src/TeaPremium.Api/Catalog/ProductOpenApi.cs`
- `backend/src/TeaPremium.Api/Iam/AdminSecurity.cs`
- `backend/src/TeaPremium.Application/Catalog/ProductContracts.cs`
- `backend/src/TeaPremium.Application/Catalog/ProductWorkflow.cs`
- `backend/src/TeaPremium.Infrastructure/Catalog/CatalogRegistration.cs`
- `backend/src/TeaPremium.Infrastructure/Catalog/EfProductStore.Save.cs`
- `backend/tests/TeaPremium.IntegrationTests/ProductOutboxContractTests.cs`
- `docs/analysis/spec-gaps.md`
- `docs/analysis/traceability.md`
- `docs/spec/00-README.md`
- `docs/spec/04-business-rules.md`
- `docs/spec/05-api.md`
- `docs/spec/07-roadmap.md`
- `packages/api-client/openapi.json`
- `packages/api-client/src/generated/schema.ts`
- `packages/api-client/src/products-contract.test-d.ts`

Untracked (9).

- `backend/src/TeaPremium.Api/Catalog/ProductImportEndpoint.cs`
- `backend/src/TeaPremium.Api/Catalog/ProductImportFile.cs`
- `backend/src/TeaPremium.Application/Catalog/ProductImport.cs`
- `backend/src/TeaPremium.Infrastructure/Catalog/EfProductImportReference.cs`
- `backend/tests/TeaPremium.IntegrationTests/ProductImportTests.cs`
- `docs/plans/p1-07-implementation.md`
- `docs/templates/p1-07-product-import.md`
- `docs/templates/p1-07-products.csv`
- `docs/templates/p1-07-products.xlsx`

## Kiểm chứng

Backend chạy trên snapshot source local `artifacts/p107-final.tar`, SHA-256 `787AF9B377FC76A640C75EF82035F4F6A4DA4A504903B384249FED42F2A191F5`, trong Linux SDK 10.0.403, PostgreSQL 17 Testcontainers và ffmpeg. Windows Application Control chặn nạp test assembly nên dùng runner Linux. Logs và TRX nằm trong `artifacts/`.

| Lệnh | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warnings/errors |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~ProductImportTests\|FullyQualifiedName~OpenApiKeepsProductAndTaxonomyContracts" --logger trx --results-directory /evidence/p107-final-focused-trx` | 11/11 PASS, gồm 10 test import và test contract OpenAPI đã cập nhật |
| `dotnet tool restore` | PASS, EF 10.0.15 |
| `dotnet ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS, không model drift. Connection design-only trên loopback port 1. |
| `corepack pnpm install --frozen-lockfile`, `corepack pnpm generate:api`, `corepack pnpm check:api` | PASS, có một route mới và giữ nguyên contract của 41 route cũ so với base |
| `corepack pnpm --filter @teapremium/api-client typecheck`, `lint`, `build` | PASS |
| `dotnet run --project artifacts/sample-check/SampleCheck.csproj` | PASS trên Linux. Parser thật đọc CSV/XLSX thành cùng ba dòng. |
| `git diff --check origin/main` | PASS |

Ca cạnh tranh gửi 8 request cùng thời điểm. Bốn request tranh một slug cho một nhóm commit và ba nhóm bị từ chối. Bốn slug khác cùng commit. Sau hai nhóm kiểm biên 9999–10000, DB có 7 SKU khác nhau, sequence đạt 10005 và giữ nguyên SKU tay ở nhóm xử lý sau.

G4 cần reviewer độc lập cùng worktree theo mức A do có ghi giá, quyền và CSRF. Giữ thay đổi local, chưa cấp quyền publish.

## Handoff G3

**G1 TARGETED PASS · G2 GO · G3 PASS · READY_FOR_LOCAL_G4 YES.**

`dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/p107-final-full-trx` hoàn tất **1.065/1.065 PASS** (174 unit, 34 architecture, 857 integration), 0 fail/skip. `./scripts/check-test-results.ps1 -Directory artifacts/p107-final-full-trx` xác nhận đủ ba project. Log suite tại `artifacts/p107-final-regression.log`.

Lượt regression trước phát hiện test baseline cố định số route Product. Bản cuối giữ nguyên kiểm tra 11 route và 16 operation cũ, thêm assertion cho multipart import. Trong lượt cuối, `docker exec --env DOTNET_PROCESSOR_COUNT=4 tra-hien-p107-final dotnet build-server shutdown` giải phóng tiến trình build nền trong runner Linux, theo cách khôi phục đã ghi ở P1-05. Không bỏ test. EF drift PASS khi chạy riêng với Infrastructure làm startup project theo quy trình repo.
