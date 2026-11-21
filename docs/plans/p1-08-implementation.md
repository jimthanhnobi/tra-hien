# P1-08 — Inventory core và tồn đầu kỳ

## G0, G1 và G2

- Worktree `D:\tra-hien-p1-08`, branch `feature/p1-08-inventory`. Đầu phiên sạch. `HEAD`, `origin/main` sau `git fetch origin` và merge base đều là `99c6810b54d151d6a30334c9384524e3ba2b3552`. P1-05 (#31) và P1-06 (#35) đã vào main. CI push-main chạy thực cả backend/API/PG17, frontend và Docker tại SHA này. [CI main #44877053827](https://github.com/jimthanhnobi/tra-hien/actions/runs/44877053827)
- G1 TARGETED PASS, G2 GO. Lưu nguồn, diff spec, G13/G15, quyết định owner cho preview và giao dịch cả tệp, thiết kế và nghĩa vụ các pha tại [thiết kế Inventory](p1-inventory-architecture.md).
- Scope G3 gồm P1-08 theo 07 §P1 dòng 57, FR-INV-01/03/06, FR-PLT-01, BR-INV-01/02/05/06/09/12, BR-CAT-02, NFR-04/08/09, DEC-01/04/05/06/14–16. Dependency P1-05 cho variant và SKU, P1-01 cho schema/grants, hạ tầng audit/outbox/use case có sẵn. Review độc lập Tier A.

## Hợp đồng API và ghi kho

- `GET /api/v1/admin/inventory` cần `inventory.read`, phân trang `page=1&pageSize=20`, tối đa 100. `type` theo UUID loại sản phẩm. `q` tìm token AND đã bỏ dấu trên Catalog hoặc SKU, tối đa 200 ký tự, khớp literal. Thứ tự SKU rồi variant ID. Status `in` là available dương, `out` là 0. `low` cần `inventory.write`, lọc theo BR-INV-11, có thể giao với `out` nếu threshold dương. Variant vắng stock row có số dư 0 khi đọc.
- `GET /api/v1/admin/inventory/{variantId}/movements` cần cả `inventory.read` và `inventory.write`. Phân trang như trên, movement mới nhất trước, ID giảm dần phá hòa. Filter `type` theo loại movement trong DDL. `from` bao gồm, `to` loại trừ, theo ISO timestamp có offset hoặc ngày thuần bắt đầu 00:00 VN. ID vắng trả 404. Đọc hạn chế chỉ có availability nhị phân cùng định danh Catalog. Thông tin lượng/low cần quyền ghi và giá vốn cần thêm `costs.read`. Giá vốn chưa biết không có field, giá vốn 0 được serialize là 0. Catalog lọc ngưỡng trong JSON theo cùng quyền để người đọc hạn chế không kết hợp ngưỡng với trạng thái hết hàng rồi suy ra low. Mọi endpoint đòi AdminCookie và StaffReady.
- `POST /api/v1/admin/inventory/opening-import?dryRun=true|false` cần `inventory.approve`, CSRF và đúng một trường multipart `file`. Ba cột chính xác `sku,quantity,unit_cost_vnd` theo BR-INV-06, cho sắp xếp lại. Tái dùng giới hạn kỹ thuật của P1-07 §4.2 cho parser. CSV UTF-8 với BOM tùy chọn hoặc XLSX một worksheet, tối đa 5 MiB, 500 dòng dữ liệu, 32 MiB khi giải nén XLSX, chặn macro và công thức. Dữ liệu header/form lỗi trả 400, quá giới hạn 413, loại tệp sai 415.
- Parser bỏ khoảng trắng ngoài ô, bỏ dòng hoàn toàn rỗng, giữ số dòng gốc. SKU khớp chính xác phân biệt hoa thường theo BR-CAT-02/14. Tất cả dòng trùng SKU đều lỗi. Số lượng 1…2.147.483.647, giá vốn 0…9.223.372.036.854.775.807 theo kiểu 03 §3.3/schema. Với Excel, giá lớn vượt độ chính xác Excel phải lưu ô dạng text. BR-INV-06 kiểm lịch sử movement theo variant. BR-CAT-05/15 quy định bán được, nên variant inactive và product archived vẫn có thể ghi nhận tồn vật lý khi đủ điều kiện kho.
- File đọc được trả 200 với `dryRun`, `committed`, `validRows`, `invalidRows` và `rows` gồm số dòng, SKU hợp lệ, variant ID khi xác định được và lỗi theo cột. `committed=false` trong preview hoặc khi bất kỳ dòng apply sai. Preview hợp lệ toàn tệp trả thêm `previewToken`. API xác minh header `X-Inventory-Preview-Token` khi apply, gắn hash bytes SHA-256, nhân viên và UUID từng dòng, hết hạn sau 30 phút, dùng Data Protection hiện có. Token thiếu/sai hoặc hết hạn trả 400 `opening_preview_invalid`. Apply đọc lại SKU, lịch sử và bất biến dưới khóa. Sau một apply thành công, replay bị từ chối bằng `opening_already_recorded` mà không tạo thêm movement.
- Điều phối và phép tính tại `InventoryMutation`/`StockRules`. Tất cả thay đổi số dư, ledger, audit và invalidation qua `EfUnitOfWork`. PostgreSQL khóa parent → variant → stock theo ID tăng ở từng tầng. PATCH chuyển sang độc bản đọc lại on_hand dưới khóa Catalog, giữ guard SKU và usage hiện hữu. Khi available chuyển 0 ↔ dương, enqueue `StorefrontInvalidate` cùng transaction theo BR-INV-12. Lần apply có `ref_type=import` và một UUID dùng chung cho mọi movement của tệp. Audit số dư gắn actor nguồn HTTP và che giá vốn, ledger lưu giá vốn theo nghiệp vụ.

## Nghiệm thu và recovery

| Tiêu chí | Bằng chứng P1-08 |
|---|---|
| AC-P1-04 | Số học 115.000 VND cùng ledger hai bút toán và SUM = on_hand được chứng minh qua opening và primitive nhận hàng. Receipt post end-to-end thuộc P1-09. |
| AC-P1-05 | Core từ chối cộng tiếp khi độc bản đã có 1. PG17 thử hai thứ tự khóa Catalog/Inventory và đồng thời apply. Luồng chứng từ nhập thuộc P1-09. |
| AC-P1-06 | HTTP JSON kiểm content/sales/owner, inventory và Catalog lồng nhau, movement 403, bộ lọc low/totals, trường tiền bị loại theo permission. |
| AC-P1-08 | Kế hoạch P1-09 dùng delta đếm trên số dư hiện tại. Ca nghiệm thu end-to-end thuộc P1-09. |
| BR-INV-01/02/06/12, FR-PLT-01 | CSV/XLSX, dry-run, replay, lỗi dữ liệu và thay đổi sau preview, lịch sử với tồn 0, lỗi tại COMMIT, reserved, NULL/0, actor/audit trước sau, chuyển mốc cache, role tp_app và grants append-only. |

**Migration.** P1-01 đã có sáu bảng, CHECK/FK và grants. Delta P1-08 không có migration. Khôi phục code/API dùng revert delta đã review. Balance/ledger có thể đọc bởi code cũ. Giữ ledger/audit đã commit khi khôi phục, dùng quy trình điều chỉnh có audit nếu có dữ liệu nghiệp vụ sai.

## Kiểm chứng local

Windows Application Control chặn nạp test assembly (`0x800711C7`). Các lệnh nghiệm thu chạy trên runner Linux SDK 10.0.403 cạnh nguồn local, PostgreSQL 17.11 Testcontainers và DB dùng một lần. Runner đặt `DOTNET_PROCESSOR_COUNT=4`. Đã đối chiếu SHA-256 cả 26 file backend/client của runner với worktree. SHA-256 snapshot đầu vào `11E3AB8EB829B04F77BCF56EC0A3F51A4DEC7BFBB1E61ED7023456E2F0B39229`. Log/TRX và script điều phối nằm tại `artifacts/p1-08/`.

| Lệnh ở root repo trên runner | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warning/error |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS |
| `dotnet test backend/tests/TeaPremium.UnitTests --no-build --no-restore --filter 'FullyQualifiedName~StockRulesTests' --logger trx --results-directory /evidence/final-unit` | 23/23 PASS |
| `dotnet test backend/tests/TeaPremium.ArchitectureTests --no-build --no-restore --logger trx --results-directory /evidence/final-architecture` | 34/34 PASS |
| `dotnet tool restore` và `dotnet tool run dotnet-ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS. EF model không drift, dùng connection design-time loopback port 1 |
| `corepack pnpm --filter @teapremium/api-client install --frozen-lockfile`, `corepack pnpm generate:api`, `corepack pnpm check:api`, `corepack pnpm --filter @teapremium/api-client typecheck` | PASS |
| `node artifacts/p1-08/api-delta.mjs` | PASS. Giữ nguyên 44 path, 68 operation, 70 schema cũ, gồm Pricing và multipart P1-07. Thêm 3 path/operation và 7 schema. Xác nhận multipart, header preview và trường tùy quyền đúng hợp đồng |

Lệnh integration tập trung và kết quả 79/79 PASS, 0 skip. Gồm 17 Inventory PG17, 14 Product API, 28 Product Import, 12 Pricing API/contract và 8 schema parity.

```sh
dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter 'FullyQualifiedName~InventoryApiTests|FullyQualifiedName~SalePriceApiTests|FullyQualifiedName~SalePriceContractTests|FullyQualifiedName~ProductImportTests|FullyQualifiedName~ProductApiTests|FullyQualifiedName~CatalogInventoryParityTests' --logger trx --results-directory /evidence/final-focused
```

Race tests chặn ở parent lock, quan sát phiên chờ bằng `pg_stat_activity`/`pg_blocking_pids` với role runtime và kiểm trạng thái sau commit. Ca rollback dùng deferred constraint trigger trên outbox DB test để ép lỗi tại COMMIT.

Full backend chạy một lượt trên candidate cuối. **1.154/1.154 PASS, 0 fail, 0 skip** (209 unit, 34 architecture, 911 integration). Script kiểm đủ ba TRX PASS.

```sh
dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/final-full
pwsh -NoProfile -File scripts/check-test-results.ps1 -Directory /evidence/final-full
```

## Bàn giao local G3 ban đầu

**READY_FOR_LOCAL_G4: YES**, review độc lập Tier A trên cùng worktree. `HEAD` vẫn ở base. Staged rỗng. `git diff --check origin/main` và `git diff --cached --check` PASS.

[Manifest SHA-256](p1-08-candidate.sha256) liệt kê chính xác 28 file candidate, gồm 15 tracked sửa và 13 untracked mới, chưa tính chính manifest. Phạm vi gồm backend Inventory, guard và JSON Catalog, reuse parser P1-07, test backend, OpenAPI/client và hai hồ sơ Inventory. Reviewer đọc trực tiếp file untracked và đối chiếu manifest. `docs/spec/`, app frontend, workflow CI, dependency lockfiles và migration không có delta.

- **P1-09** nhận biên ghi/khóa và test core. Kiểm receipt/adjustment/stocktake end-to-end theo AC-P1-04/05/08 ở P1-09.
- **P1-10** nhận quy tắc ngưỡng, alert nội bộ cùng consumer và thiết kế đối soát theo [kiến trúc chung](p1-inventory-architecture.md). P1-14 nhận availability và invalidation, cần chốt cách diễn đạt tồn chính xác ≤ 5 ở Store DTO theo hai nguồn ghi trong gap register.
- **NOT RUN** gồm review G4 độc lập, CI cho candidate local, Admin E2E, thao tác dữ liệu thật, ca cạnh tranh với tác vụ rebuild search text hàng loạt và nghiệm thu các phase sau. Frontend evidence tái sử dụng từ CI main tại base.

## Focused remediation G4 Tier A

**Baseline integration PASS.** Fast-forward riêng `feature/p1-08-inventory` từ `99c6810b54d151d6a30334c9384524e3ba2b3552` đến `b45558fd054e9b7d3ba0a28d93caf4ef100f0eed` bằng `git merge --ff-only origin/main` sau fetch. Main thêm hai commit P1-12 với 32 file UI/test/docs và lockfile. Delta không đụng backend, API client, spec hoặc file đã review. CI trên base mới chạy thật backend/PG17/API, frontend và Docker, tất cả PASS. [CI #44883539184](https://github.com/jimthanhnobi/tra-hien/actions/runs/44883539184)

**Snapshot G4 ban đầu.** Fingerprint reviewer `D089C453F9A3BF185FEBD2F5BEC83B465237BE2E730124727D37F28551AC63F0`. Cả 28 file candidate và metadata manifest khớp trước/sau fast-forward theo SHA-256 raw, Git blob raw, Git blob có filter và base blob cho file sửa. Đã giải nén và kiểm phục hồi 29 file từ `artifacts/p1-08/remediation/reviewed-candidate.tar`, SHA-256 `F24AD18CFF6620B2C6620ACF6193BD00BA3235A95CCFC3118040D8A870AEFE29`. Bảng correspondence trước/sau ở cùng thư mục. Giữ lịch sử G4 BLOCK trong `D:/tra-hien-p1-08-g4-99c6810/final-review.md`.

- **G4-F1 / G13 / AC-P1-06.** Policy owner-approved ngày 21/11/2026 ở 05 §4.2. `ProductWorkflow.ApplyVariant` dựa trên state variant hiện hành dưới khóa, áp 403 trước kiểm lịch sử hoặc tồn cho chuyển thường → độc bản khi thiếu một trong hai quyền Inventory. Actor đủ quyền tiếp tục chịu BR-INV-09. Product PATCH giữ quy tắc chỉ nhận `defaultVariant` khi create. P1-12 giữ hành vi lưu những trường đã sửa, tắt checkbox chỉ khi chuyển thường → độc bản trên variant hiện hữu bị cấm và giải thích quyền cần thiết. Ma trận role, quyền giá vốn và hợp đồng SKU giữ nguyên.
- **G4-F2 / BR-CAT-10 / BR-INV-01/09.** Bulk rebuild lấy parent lock tăng theo UUID PostgreSQL trước UPDATE trong transaction outbox. Phép tính canonical, payload, giới hạn batch, retry và đường save đồng bộ giữ nguyên. PG17 regression điều phối hai thứ tự bằng khóa hàng cao và `pg_blocking_pids`, kiểm cả hai tác vụ commit, deadlock counter, search text, ledger, AVG, audit và outbox. [Giao thức bàn giao P1-09](p1-inventory-architecture.md)
- **Rollback.** Delta F1/F2 chỉ có mã ứng dụng, test, tài liệu và spec quyền đã duyệt. Khôi phục candidate cũ qua archive đã kiểm hoặc phục hồi đúng các file delta từ base và archive. Schema/grants/migration giữ nguyên.

### Kiểm chứng remediation

Backend chạy trong runner Linux .NET SDK 10.0.403 và PostgreSQL 17.11 Testcontainers riêng, theo cách thay thế Windows Application Control đã ghi ở G3. Snapshot đầu vào `67D467282FFE8541AF6F19D15FB6B73297E5BF152D746DC54A2ED1F9CC1BAD7A`. Đã đối chiếu SHA-256 của 32 file source/test/client/spec giữa runner và worktree. Log, TRX và lệnh khởi chạy lưu tại `artifacts/p1-08/remediation/`.

| Lệnh | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warning/error |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS |
| `dotnet test backend/tests/TeaPremium.ArchitectureTests --no-build --no-restore --logger trx --results-directory /evidence/architecture` | 34/34 PASS |
| `dotnet tool restore` và `dotnet tool run dotnet-ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS, không drift. Connection design-time loopback port 1 |
| `corepack pnpm --filter @teapremium/api-client install --frozen-lockfile`, `corepack pnpm generate:api`, `corepack pnpm check:api`, `corepack pnpm --filter @teapremium/api-client typecheck` | PASS. Sinh lại khớp byte hai file đã review. Giữ đủ 71 operation, gồm Pricing và hai multipart import |
| `corepack pnpm install --frozen-lockfile`, `corepack pnpm --filter @teapremium/admin typecheck` | PASS |
| `corepack pnpm --filter @teapremium/admin exec eslint src/components/products/product-editor.tsx src/components/products/product-variants.tsx tests/browser/products.spec.ts --max-warnings=0` | PASS |
| `corepack pnpm --filter @teapremium/admin exec node --import tsx --test tests/products.test.tsx` | 5/5 PASS |

Focused backend **100/100 PASS**, zero skip. Gồm 20 Inventory (3 case F1/F2 mới), 14 Product API, 19 Catalog Reference, 19 Pricing API/concurrency/contract và 28 Product Import. Browser **4/4 PASS** gồm 2 case F1 và 2 hồi quy P1-12.

```sh
dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter 'FullyQualifiedName~InventoryApiTests|FullyQualifiedName~ProductApiTests|FullyQualifiedName~ProductImport|FullyQualifiedName~SalePrice|FullyQualifiedName~CatalogReferenceApiTests' --logger trx --results-directory /evidence/focused
corepack pnpm --filter @teapremium/admin exec playwright test tests/browser/products.spec.ts --grep 'G4-F1|variant create|catalog writer'
```

Chạy thêm một lượt full backend sau khi cả hai bản sửa và focused PG17 ổn định. F1 đổi guard trong workflow Catalog và F2 đổi đường ghi dùng chung giữa save và outbox nên cần kiểm cả transaction/audit và các use case ngoài focused filter. Tái sử dụng evidence G3/G4 về opening parser, token, grants và giá vốn cho phần nguồn không đổi.

Full backend **1.157/1.157 PASS**, 0 fail, 0 skip. Gồm 209 unit, 34 architecture và 914 integration. Script kiểm đủ ba TRX PASS.

```sh
dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/full
pwsh -NoProfile -File scripts/check-test-results.ps1 -Directory /evidence/full
```

### Candidate bàn giao

**READY_FOR_FOCUSED_INDEPENDENT_G4: YES** trên cùng worktree. G4-F1/F2 đã sửa và kiểm local. HEAD, merge base và origin/main cùng ở `b45558fd054e9b7d3ba0a28d93caf4ef100f0eed`, index rỗng. [Manifest SHA-256 mới](p1-08-candidate.sha256) liệt kê 34 file gồm 20 file tracked sửa và 14 file untracked, chưa tính manifest. Bảng SHA-256/raw Git blob/Git blob của candidate ở `artifacts/p1-08/remediation/candidate-inventory.csv`. Fingerprint của manifest ghi riêng ở `candidate-fingerprint.txt` trong cùng thư mục bằng chứng.

**KNOWN GAPS.** Giao diện tồn chính xác trên P1-14 vẫn thuộc phạm vi chốt riêng. **NOT RUN** gồm re-review độc lập, CI của candidate local, full Admin E2E và thao tác trên dữ liệu thật. Dừng ở bản local để re-review.

