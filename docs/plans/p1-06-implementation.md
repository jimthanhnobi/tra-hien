# P1-06 — Giá khuyến mãi

**G1 TARGETED PASS · G2 GO · G3 PASS · READY_FOR_LOCAL_G4 YES (Tier A).**

## G0, G1 và G2

- Worktree `D:\tra-hien-p1-06` sạch lúc bắt đầu, nhánh `feature/p1-06-pricing`. Sau `git fetch origin --prune`, HEAD và `origin/main` cùng là `c88069d58241fd51f5f6a76da2276c41f7a3830a`. GitHub xác nhận run push-main đúng SHA, 4/4 job thành công. [CI 44777442188](https://github.com/jimthanhnobi/tra-hien/actions/runs/44777442188)
- **G1 TARGETED PASS** tại SHA trên. Đối chiếu full audit `cadce575597738bf40ad7c45689482de5a5fbc3d`, hợp đồng A01 đã merge tại `024ea998c43baf0afde30b069ed6b4876cb96f4a`, B01 và diff từng phần đến HEAD. Các amendment P1-04/P1-05/P1-07 và CI sau A01 đã có hồ sơ riêng. Phần DDL giá khuyến mãi và endpoint giá khuyến mãi giữ cùng hợp đồng từ A01. Pricing dùng token mờ theo G34 đã căn chỉnh. [Inventory](../analysis/spec-inventory.md) · [Gap register](../analysis/spec-gaps.md) · [Traceability](../analysis/traceability.md) · [A01](../decisions/ADR-PROPOSAL-A01-p0-closure-p1-owner-decisions.md) · [B01](../decisions/ADR-B01-right-sized-operations.md)
- **G2 GO**. P1-06 phụ thuộc P1-05, đã merge #31. P0 closure và nền bảng/grants P1-01 đã merge #22/#27. P1-07 đã merge #33 và được dùng làm regression. Giữ modular monolith .NET/PostgreSQL 17 (DEC-01), tiền VND bigint (DEC-20), điều kiện giá bản nháp/kích hoạt (DEC-25) và provisioning theo role (DEC-30). [Readiness](task-readiness.md) · [G2 baseline](../architecture/conformance-review.md) · [P1-01 evidence](p1-01-local-evidence.md) · [P1-05 evidence](p1-05-implementation.md) · [P1-07 evidence](p1-07-implementation.md)

## Yêu cầu và nghiệm thu

| Nguồn | Hành vi và bằng chứng |
|---|---|
| [FR-PRC-01/02/04](../spec/01-requirements.md#44-prc--giá-và-khuyến-mãi), [BR-PRC-01/02 và BR-CAT-11/15](../spec/04-business-rules.md), [03 §3.8](../spec/03-database.md#38-giá-khuyến-mãi) | `PricingCalculator` trả giá gốc và hiệu lực bằng `long` tại thời điểm caller cung cấp, hoặc `null` khi variant inactive hoặc giá gốc NULL. Khoảng `[start,end)`, áp `min(base,sale)` kể cả khi giá niêm yết giảm. `PricingCalculatorTests` kiểm ngưỡng 10 tỷ, thời điểm biên, hai khoảng kề và độ chính xác. BR-CAT-11 chọn giá đại diện theo tồn thuộc P1-14, dùng kết quả calculator này. |
| [05 §1/§4.2](../spec/05-api.md) và [AC-P1-09](../spec/07-roadmap.md) | GET 200 trả token variant và danh sách theo startsAt/id. POST 201 dùng token variant, kiểm variant active có giá và giá KM dương/thấp hơn giá hiện tại. DELETE 204 dùng token khuyến mãi và kiểm đúng variant. Token sai dạng 400, cũ 409, khác variant 404. AC-P1-09 giữ hành vi thiếu giá của P1-05/P1-07 và được kiểm ở nhánh giá KM. Các ca quyền/CSRF, trạng thái, biên và concurrency nằm trong `SalePriceApiTests` và `SalePriceConcurrencyTests`. |
| [FR-PLT-01 và NFR-04/08/09](../spec/01-requirements.md), [BR-PRC-03](../spec/04-business-rules.md#4-prc--giá-và-khuyến-mãi), [02 §5.2/§5.4/§5.5/§5.8](../spec/02-architecture.md) | StaffReady, AdminCookie và `pricing.write` cho cả ba thao tác. POST/DELETE yêu cầu `X-CSRF-TOKEN`. Audit lưu actor, thời điểm, hành động, mã variant, tiền và khoảng thời gian trước/sau. Ghi chú được redacted trong audit. Outbox dùng `StorefrontInvalidate` với tag product/category/catalog/sitemap/feed. Test kiểm dữ liệu trả về, actor, audit, outbox và rollback bằng DB fixture thực. |
| [DDL `pricing.sale_prices`](../spec/schema.sql#L516), [03 §4](../spec/03-database.md#4-vai-trò-database-tạo-bởi-infrapostgresinit-rolessql) | Fixture provision role từ script chính thức rồi chạy API bằng `tp_app` trên PG17. GiST constraint `ex_sale_prices_no_overlap` trả `23P01`; API ánh xạ đúng constraint sang 409 `sale_price_overlap`. Hai variant có thể có cùng khoảng. |

## Các chi tiết hợp đồng được áp dụng

| Điểm cần soát | Căn cứ và cách triển khai |
|---|---|
| Giá hiệu lực khi giá gốc hạ | BR-PRC-01 ở `04-business-rules.md:99` mô tả lịch đang hiệu lực. BR-PRC-02 ở dòng 100 và `03-database.md:102` giới hạn giá bởi giá gốc hiện tại. Hai đoạn được áp cùng nhau. |
| Vòng đời và xóa | `05-api.md:117` yêu cầu DELETE, `schema.sql:516` dùng bảng lịch không có cờ xoá mềm. Xóa bản ghi theo variant/id, kể cả lịch đã qua. Lịch sử giữ trong audit theo BR-PRC-03. BR-CAT-06 áp lên product/variant/category, nên GET và DELETE lịch vẫn dùng được sau archive. |
| Eligibility và archive | BR-CAT-15 nói BR-PRC-01/02 cùng tiêu chí bán được, nên tạo lịch cần variant active có giá. BR-CAT-05 điều khiển công khai theo trạng thái product. Chính sách quản trị giá hiện hành cho phép sửa giá của variant đủ điều kiện dưới product draft hoặc archived. P1-14 áp điều kiện công khai của product trước khi sử dụng giá hiệu lực. |
| Token và thời gian | Theo 05 §1, POST so `xmin` của variant dưới lock để xác thực giá/trạng thái người dùng đã thấy. DELETE dùng `xmin` của bản ghi lịch, cùng hình thức body JSON như DELETE ảnh product. Thay đổi lịch độc lập không ghi lại variant. Input cần ISO 8601 có offset hoặc Z. Chuẩn hóa UTC và làm tròn xuống microsecond trước khi so sánh và lưu. Ghi chú nullable theo DDL, từ chối NUL. |
| Cache khi lịch tự bắt đầu/kết thúc | Ghi/xóa phát invalidation ngay trong transaction. P1-14 cần xét mốc gần nhất ở cả góc nhìn detail/list/feed khi dùng cache, thực hiện hợp đồng 02 §5.5/`ActivateScheduledPrices` và §5.8 (giá detail trễ tối đa 5 phút, feed tối đa 1 giờ). Kiểm thử chuyển mốc mà không cần ghi Admin, bao gồm cache đã được làm nóng. P2 luôn tính lại tại một instant và giữ giá snapshot của đơn đã đặt. |

## Luồng ghi và migration

Decorator `IUnitOfWork` mở một transaction. Store tìm khóa cha theo variant ID rồi lấy `FOR NO KEY UPDATE` của product bằng truy vấn dùng chung với P1-05, lấy `FOR UPDATE` của variant và đọc giá/trạng thái đã commit. DELETE khóa lịch sau variant. Store chỉ đọc slug danh mục để tạo tag, không ghi taxonomy hay redirect graph. Competing requests được kiểm bằng PostgreSQL `pg_blocking_pids` ở cả hai thứ tự với cập nhật giá/trạng thái variant. EXCLUDE là kiểm tra overlap cuối ở PostgreSQL. Flush cùng audit interceptor và outbox trong transaction đó. Lỗi dự kiến trả Result lỗi để UnitOfWork rollback. Lỗi hạ tầng đi qua ProblemDetails chung.

Migration/grants mới không cần thiết. `SalePrice.Version` chỉ ánh xạ cột hệ thống `xmin` có sẵn. EF snapshot thêm mapping tương ứng. Probe tạo migration tạm trên runner Linux rồi sinh SQL từ migration P1-04 đến probe cho thấy chỉ có câu ghi history, không có DDL. Loại probe trước khi kiểm lại snapshot và chạy integration. Khi cần rollback G3, có thể revert source/client mà giữ các bản ghi giá, audit và outbox trong DB. Không áp dụng SQL probe.

## Inventory

| Trạng thái | Đường dẫn |
|---|---|
| Mới | `backend/src/TeaPremium.Domain/Pricing/PricingCalculator.cs` |
| Mới | `backend/src/TeaPremium.Application/Pricing/SalePriceContracts.cs`, `SalePriceHandlers.cs` |
| Mới | `backend/src/TeaPremium.Api/Catalog/SalePriceEndpoints.cs` |
| Sửa | `backend/src/TeaPremium.Api/Program.cs` |
| Mới | `backend/src/TeaPremium.Infrastructure/Pricing/EfSalePriceStore.cs` |
| Mới | `backend/src/TeaPremium.Infrastructure/Catalog/ProductInvalidation.cs` |
| Sửa | `backend/src/TeaPremium.Infrastructure/Catalog/CatalogRegistration.cs`, `EfProductStore.cs`, `EfProductStore.Save.cs` |
| Sửa | `backend/src/TeaPremium.Infrastructure/Persistence/AuditValuePolicy.cs`, `CatalogInventoryEntities.cs`, `CatalogInventoryModel.cs`, `Migrations/AppDbContextModelSnapshot.cs` |
| Mới | `backend/tests/TeaPremium.UnitTests/PricingCalculatorTests.cs` |
| Mới | `backend/tests/TeaPremium.IntegrationTests/SalePriceApiTests.cs`, `SalePriceConcurrencyTests.cs`, `SalePriceContractTests.cs` |
| Sinh lại | `packages/api-client/openapi.json`, `packages/api-client/src/generated/schema.ts` |
| Mới | `docs/plans/p1-06-implementation.md` |

Tổng inventory gồm 10 file tracked sửa và 11 file untracked mới. Staged rỗng. Source và client trên runner được đối chiếu SHA-256 với 20 file cùng phạm vi trong worktree.

## Kiểm chứng local

Runner Linux SDK 10.0.403, PostgreSQL 17 Testcontainers riêng, DB giả provision bằng role/migration có sẵn. Windows build được, nhưng Application Control chặn nạp assembly khi sinh OpenAPI nên chạy các lượt kiểm chứng cuối trên Linux. Trước full regression đã kiểm tra process Windows theo worktree P1-12 và danh sách container đang chạy, không thấy tải kiểm thử khác. Runner giới hạn `DOTNET_PROCESSOR_COUNT=4`. Log, TRX và hash nằm trong `artifacts/p1-06/` của worktree này.

| Lệnh chạy từ root repo trên runner | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warning/error |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS |
| `dotnet test backend/tests/TeaPremium.UnitTests/TeaPremium.UnitTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PricingCalculatorTests' --logger trx --results-directory /evidence/pricing-unit` | 12/12 PASS |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~SalePrice\|FullyQualifiedName~Product\|FullyQualifiedName~CatalogInventory\|FullyQualifiedName~CatalogReference' --logger trx --results-directory /evidence/pricing-product-catalog` | 359/359 PASS, gồm 16 pricing/contract, 28 import P1-07, 177 product và 138 catalog/inventory/reference |
| `dotnet tool restore`, `dotnet ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS, không model drift. Connection design-time loopback port 1 |
| `corepack pnpm --filter @teapremium/api-client install --frozen-lockfile`, `corepack pnpm generate:api`, `corepack pnpm check:api`, `corepack pnpm --filter @teapremium/api-client typecheck` | PASS. Đối chiếu JSON với base giữ 42 path, 65 operation và 66 schema cũ, kể cả multipart import. Thêm 2 path, 3 operation và 4 schema pricing |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/full` | **1.111/1.111 PASS** trong một lượt full. 186 unit, 34 architecture, 891 integration. 0 fail, 0 skip |
| `pwsh -NoProfile -File scripts/check-test-results.ps1 -Directory /evidence/full` | PASS, xác nhận đủ ba TRX và zero skip |
| `git diff --check origin/main`, `git diff --cached --check` | PASS |

Trong lượt full, một số tiến trình MSBuild tách khỏi CLI test trong runner P1-06. Các lần `docker exec tra-hien-p106-verify dotnet build-server shutdown` giải phóng build server để lượt hiện tại tiếp tục, theo cách đã dùng ở P1-07. Cả ba project hoàn tất và giữ nguyên TRX của lượt này. Workload PG/API riêng của P1-12 khởi động sau khi P1-06 đã chạy và dùng ít tài nguyên trong các lần quan sát.

## Bàn giao

HEAD local vẫn là base, với delta trong inventory trên. G4 độc lập Tier A thực hiện ở cùng worktree. CI của delta local, Admin E2E và nghiệm thu production chưa chạy. Kiểm thử cache chuyển mốc tự động thuộc P1-14, kiểm thử snapshot đơn thuộc P2. Không có blocker kỹ thuật còn mở cho G3.

## Focused G4 remediation

Owner xác nhận Tier A PASS trên snapshot `68a80eeb0b14ca1a472bbd5a29e151397a8d054d86a8c855458d64da98305ed8` và giao đúng G4-F1/F2 (P2). Worktree, nhánh, base và inventory ban đầu khớp yêu cầu (10 tracked, 11 untracked, index rỗng). Manifest của reviewer chưa thấy trong worktree hoặc artifacts. Đối chiếu byte theo manifest G3 xác nhận đủ 20 file backend/client khớp trước sửa. Manifest local của cả 21 file trước sửa là `before.sha256`, có hash `6eec6b7d0e42178c1d9f2f919139a868bcbf62b478d2729e9b214cf9ca7768c6` theo định dạng của lượt này.

| Finding | Trước và sau |
|---|---|
| G4-F1 | Test POST và DELETE với cookie/CSRF hợp lệ và `text/plain` tái hiện 415 thiếu content type phản hồi. Hai endpoint dùng ProblemDetails chung sau auth/CSRF, với 415 `unsupported_media_type` và `traceId`. OpenAPI khai báo 415 và giữ DTO JSON cho cả hai. Test kiểm fingerprint dữ liệu/audit/outbox và gửi lại JSON thành công. |
| G4-F2 | Test chèn hai khoảng kề nhau trong cùng một transaction PG17 xác nhận cùng `xmin`. Trước sửa, token B gửi vào URL A nhận 204. Sau sửa, hai token khác nhau và request đó nhận 409 `concurrency_conflict`, giữ cả hai bản ghi và fingerprint audit/outbox. Token A xóa đúng A. Token sai định dạng 400, token stale 409 và sai variant 404 được kiểm lại. So token theo ID và `xmin` của hàng đang khóa. POST tiếp tục dùng token variant theo 05 §1. |

Chín file thay đổi so với inventory trước remediation, không thêm path vào G3:

- `backend/src/TeaPremium.Api/Catalog/SalePriceEndpoints.cs`
- `backend/src/TeaPremium.Application/Pricing/SalePriceContracts.cs`
- `backend/src/TeaPremium.Application/Pricing/SalePriceHandlers.cs`
- `backend/src/TeaPremium.Infrastructure/Pricing/EfSalePriceStore.cs`
- `backend/tests/TeaPremium.IntegrationTests/SalePriceApiTests.cs`
- `backend/tests/TeaPremium.IntegrationTests/SalePriceContractTests.cs`
- `packages/api-client/openapi.json`
- `packages/api-client/src/generated/schema.ts`
- `docs/plans/p1-06-implementation.md`

Fingerprint SHA-256 của manifest 20 file backend/client `617cdcc397eabc1dcfa42410f0189ecdf064f39a9e85b6b7741b73c04dbf4133`. `sha256sum -c /evidence/candidate-code.sha256` xác nhận runner khớp worktree. Manifest toàn bộ 21 file là `candidate.sha256` và fingerprint nằm trong `candidate-fingerprint.txt`. Cả hai ở `artifacts/p1-06/focused-g4/` cùng log và TRX. Manifest dùng path sắp xếp, một dòng mỗi file theo định dạng `sha256  path`, UTF-8 và LF.

Runner Linux SDK 10.0.403, PostgreSQL 17 disposable, giới hạn bốn processor. Dùng cache dependency có sẵn khi registry chậm. Kiểm tra tải P1-12 trước khi chạy và theo dõi khi runner P1-12 xuất hiện, dùng container DB riêng cho P1-06.

| Lệnh | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode --source /cache/p106-nuget/packages` | exit 0 |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~UnsupportedContentTypeReturnsProblemWithoutMutation\|FullyQualifiedName~DeleteRejectsAnotherRowsToken' --logger trx --results-directory /evidence/red` | exit 1 trên bản trước sửa, 3/3 ca tái hiện đúng lỗi |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror -t:Rebuild` | exit 0, 0 warning/error |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | exit 0 |
| `dotnet ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | exit 0, không model drift. Dùng connection design-time loopback port 1 |
| `node packages/api-client/generate.mjs`, `node packages/api-client/generate.mjs --check` | exit 0. So với OpenAPI trước sửa, giữ 68 operation gồm multipart import P1-07, thêm đúng hai response 415 |
| `node node_modules/typescript/bin/tsc --noEmit` từ `packages/api-client` | exit 0 |
| `dotnet test backend/tests/TeaPremium.UnitTests/TeaPremium.UnitTests.csproj --no-build --no-restore --filter FullyQualifiedName~PricingCalculatorTests --logger trx --results-directory /evidence/unit` | exit 0, 12/12 PASS |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~SalePrice\|FullyQualifiedName~ProductOutboxContractTests\|FullyQualifiedName~ProductConcurrencyTests\|FullyQualifiedName~AuditInterceptorTests' --logger trx --results-directory /evidence/integration` | exit 0, 55/55 PASS, 0 fail/skip. Gồm 19 pricing/contract, 19 audit, 9 product concurrency và 8 outbox/contract |
| `git diff --check origin/main`, `git diff --cached --check` | exit 0 |

**G4-F1 FIXED · G4-F2 FIXED · READY_FOR_FOCUSED_INDEPENDENT_G4 YES.** Migration và grants giữ theo G3. Hai finding đã sửa cục bộ, chờ xác nhận độc lập trên cùng worktree. Dùng lại regression G3 và bằng chứng G4 do owner cung cấp. Full backend regression cho delta này, Admin E2E và CI published HEAD là NOT RUN.
