# P1-14 — Store Catalog API

Ngày 21/11/2026. Branch `feature/p1-14-store-catalog`, worktree `D:\tra-hien-p1-14`. Base `667274cfcc4c7aae790100237ccc2de29a0484ee`. Chủ dự án duyệt hợp đồng A–F, các làm rõ vòng đời/SEO/search và cấp G3 sau khi CI main hoàn tất. G4 độc lập tầng A chờ reviewer.

## Dependency và đặc tả

- [P1-08 #37](https://github.com/jimthanhnobi/tra-hien/pull/37) đã merge. [CI PR 44898669614](https://github.com/jimthanhnobi/tra-hien/actions/runs/44898669614) PASS tại `909c6f541c29bd2e34a83845fa610bdddf720654`. [CI main 44899900821](https://github.com/jimthanhnobi/tra-hien/actions/runs/44899900821) attempt 1, bốn job PASS tại base.
- G1 TARGETED tái sử dụng audit P1-14 tại `b45558fd054e9b7d3ba0a28d93caf4ef100f0eed` và full baseline `cadce575597738bf40ad7c45689482de5a5fbc3d`. Delta P1-08 gồm 35 tệp. Spec incoming chỉ thêm quyền chuyển variant thường sang độc bản tại 05 §4.2. Xác minh `AvailableAsync` dùng `on_hand - reserved`, thiếu stock row đọc 0, `OnHandAsync` dùng nội bộ, invalidation tại 0 ↔ dương và bulk rebuild khóa product theo thứ tự UUID PostgreSQL.
- G2 GO theo DEC-01/02/03/04/05/15/16/20/21/25/27. Amendment owner-approved áp trước code trong [02 §5.5/§5.8](../spec/02-architecture.md), [04 BR-CAT-05/11](../spec/04-business-rules.md), [05 §2](../spec/05-api.md), [07 P1-14/P2-05/08/10/P3-09/15](../spec/07-roadmap.md) và [09 BR-SEO-21/34/71](../spec/09-seo.md). Bản sửa chính xác xem `git diff 667274c -- docs/spec`. Giữ quyền Admin incoming và ID hiện hữu.

## Scope và truy vết

| Lát cắt | Nguồn và bằng chứng kiểm thử |
|---|---|
| Chín GET anonymous, JSON public, Problem Details | 05 §1/2, NFR-04/07, AC-P1-06. `OpenApiTests.StoreCatalogPublishesExactlyNineReadOperations`, kiểm đệ quy JSON trong `StoreCatalogTests`. |
| Search, filter, sort, facets và phân trang | FR-CAT-10/11, 03 §3.7, BR-CAT-09/10/11/15, AC-P1-07. `StoreCatalogQueryTests`, `PublicCatalogFiltersPricesFacetsAndDisclosesOnlyPublicData`. SKU lịch sử có thể khớp product cha. Chỉ trả variant đủ điều kiện. |
| Giá và tồn | BR-PRC-01/02/03, BR-INV-02/12. `SqlPricesAgreeWithPricingCalculatorAtBoundariesWithoutAdminWrites`, `DetailKeepsPriceAndInventoryOnOneReadSnapshot` và ca tồn biên 0/1/3/4/5/6. |
| Detail, media, taxonomy và SEO | FR-CAT-02/03/06/08, BR-CAT-05/08/09/12/15, BR-SEO-20/21/22/31/32/34/71, AC-P1-10. Test HTTP PG17 với draft/archived, nhánh hidden, media, taxonomy public, 149/150/151 từ, manual/site noindex và tập variant rỗng. |

List dùng truy vấn PostgreSQL với tham số, chọn variant đại diện rồi phân trang, tính facets distinct trên toàn tập khớp và nạp ảnh cover theo lô. Detail lấy một mốc UTC, gọi calculator chuẩn và đọc tồn theo lô. Các truy vấn nhiều bước dùng snapshot `REPEATABLE READ`. Mô hình giá thẻ ưu tiên variant khớp còn hàng. Chỉ công bố giá gốc khi có giảm giá thực tế. Variant cùng giá phá hòa theo position/ID. Facet category tính cả tổ tiên public theo cùng quy tắc lọc category. Tồn và giá công khai tính lúc đọc, list/detail trả `no-store`.

P2-05 sản xuất bằng chứng bán. P2-08/10 xử lý đảo ngược liên quan. P3-13 nhận vòng đời/redirect sau bán. P3-09 nhận job `ActivateScheduledPrices` 5 phút, nghiệm thu cache đã làm nóng qua mốc giá và tồn 0 ↔ dương, lowStock 3 ↔ 4. P3-15 nhận Merchant Feed cache/parity. Kiểm HTML/robots/canonical/JSON-LD/sitemap ở pha P3. P1-14 giữ các đường ghi Inventory và outbox hiện hữu.

Không có migration. Khôi phục bằng revert lát cắt cùng OpenAPI/client, dữ liệu kho và lịch sử không cần phục hồi. Tích hợp sau P1-09 cần kiểm `Program.cs` và sinh lại contract từ API hợp nhất.

## Kiểm chứng

Môi trường PostgreSQL 17.11 Testcontainers dùng database/cổng động và runner Linux SDK 10.0.403 riêng cho P1-14. Runner dùng `TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal`. Windows build chạy được, công cụ sinh OpenAPI trên Windows bị Application Control chặn `0x800711C7`. API thật được dùng để sinh và kiểm lại contract trên Linux.

| Lệnh | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warning/error |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-restore --filter FullyQualifiedName~StoreCatalog --logger trx --results-directory /host/artifacts/p1-14/candidate` | 6/6 PASS, 0 skip. Có differential SQL/calculator, snapshot khi có ghi xen giữa, HTTP, disclosure, benchmark và OpenAPI. |
| `corepack pnpm generate:api` và `corepack pnpm check:api` | PASS trên Linux. Semantic diff từ base giữ 47 path/71 operation/77 schema, gồm Inventory. Thêm đúng 9 Store GET và 17 schema. |
| `corepack pnpm --filter <package> typecheck` | PASS cho `@teapremium/api-client`, `@teapremium/store`, `@teapremium/admin`. |
| `corepack pnpm --filter @teapremium/api-client lint` | PASS |
| `git diff --check origin/main` | PASS |

- Candidate cuối: `dotnet test backend/tests/TeaPremium.UnitTests --no-restore` PASS 216/216 (7 ca Store parser), `dotnet test backend/tests/TeaPremium.ArchitectureTests --no-restore` PASS 34/34. Hash của 11 tệp C# khớp bản kiểm thử Linux.
- Candidate cuối: `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter <filter>` PASS 490/490, 0 skip. Filter là `FullyQualifiedName~Product|FullyQualifiedName~Inventory|FullyQualifiedName~Media|FullyQualifiedName~Catalog|FullyQualifiedName~SalePrice|FullyQualifiedName~OpenApi`. TRX ở `artifacts/p1-14/affected/`.
- Bản ngay trước tối ưu cuối của facet và bổ sung case fixture: `dotnet test backend/TeaPremium.sln --no-build --no-restore` PASS 1.170/1.170, 0 skip (216 unit, 34 architecture, 920 integration). TRX ở `artifacts/p1-14/full/`. Candidate cuối đã chạy lại toàn bộ phạm vi bị tác động.

## Benchmark PG17

Fixture 500 sản phẩm × 4 variant, kèm EAV, ảnh cover, liên kết category, số dư và dữ liệu catalog chức năng. Hai mươi mẫu HTTP sau warmup cho mỗi kịch bản. Đếm câu SQL đọc qua interceptor, bỏ phần điều khiển transaction. Đo trên runner dev khi regression đang chạy. NFR-01 về SSR đã cache thuộc pha P3.

| Kịch bản | Số SQL đọc tối đa | HTTP p95 |
|---|---:|---:|
| List 20, tìm rộng | 3 | 89,18 ms |
| List 100, bộ lọc category/attr/price/stock và trang 4 | 4 | 114,07 ms |
| Product detail có ảnh và 4 variant | 8 | 59,51 ms |
| Category detail | 2 | 13,40 ms |

`EXPLAIN (ANALYZE, BUFFERS)` lưu tại `artifacts/p1-14/{list20,list100-filtered,detail,taxonomy}.txt`. Hai truy vấn chính của list mất 64,986 ms và 107,045 ms, có `ix_variants_product`, `pk_stock_levels` và `pk_variant_attribute_values`. `GroupAggregate` cho facets chạy một lần trên tập trước phân trang. Đưa type vào tập variant khớp loại bỏ lần quét lặp ở phép nối facet từng làm p95 list vượt 1 giây.

## Handoff

Giữ cấu hình `SITE_INDEXABLE` của API và Store đồng nhất khi nối storefront. Ca stress 20.000 variant và các acceptance P2/P3 chưa chạy. Review G4 độc lập diễn ra trên cùng worktree, gồm cả source untracked, spec amendment và contract sinh tự động. Danh mục đầy đủ và hash nằm trong [candidate manifest](p1-14-candidate.sha256).

## Focused G4 remediation · F1/P1, F2/P2, F3/P2

Base và remote main vẫn ở `667274cfcc4c7aae790100237ccc2de29a0484ee`. Candidate trước sửa có 10 tracked sửa, 10 untracked, 0 staged. Manifest SHA-256 `8bf0e0d1eb92b7b7cd81767e61f1e48dc079332d5974f7cb903d5edb91e3d12d`, xác minh 19/19 hash. Snapshot khôi phục cả 20 tệp ở `artifacts/p1-14/remediation/before/`. SHA-256 của danh sách `before-all.sha256` do phiên này lập là `dedff7a476a3d8e4e0c20685bb090f66a33963280f5364051ca1a1c4eb055856`. Fingerprint `db64dbba2abc56a5343a951136f0adfa1290de1d429cafc611c82348b09046d6` do reviewer cung cấp, thiếu quy cách tổng hợp để đối chiếu trực tiếp. Bảng hash trước/sau nằm ở `artifacts/p1-14/remediation/review-delta.json`.

- F1 (FR-CAT-10, BR-CAT-11/15, 05 §2). Parser dùng `CatalogRules.ReferenceCode()` cho type và attribute. `CatalogRules.OptionCodePattern` được chia sẻ với validator Admin. Test HTTP đọc facet và gửi lại mã `type=_review`, `attr._texture=smooth`, `attr.finish=_gloss`. Kiểm biên 2/40 và 1/60, ký tự sai và ambiguity trước/sau khi chỉ định type.
- F2 (BR-CAT-01/09, 05 §2). Dùng `TaxonomyRules.ValidSlug` cho category/collection/material/artisan. Slug sai trả `400 validation_failed`. Slug đúng cú pháp nhưng không tồn tại trả 200 với tập rỗng. Kiểm biên 1/80 và độ dài 81.
- F3 (05 §1/2). OpenAPI 3.1.1 ghi object query dùng form/explode (mặc định của OpenAPI) với `patternProperties` cho key `attr.<code>`. `openapi-typescript` 7.13.0 sinh object theo tên tham số. Bước AST chỉ flatten tham số này ở GET products thành key template `attr.${string}`, giá trị string. HTTP và kiểm `openapi-fetch` giữ URL phẳng. Enum `sort` và `availability` được khai báo dưới dạng chuỗi. Type assertions kiểm cả hợp đồng dương và âm trong `store-catalog-contract.test-d.ts`. Kiểm semantic diff giữ 71 operation, 77 schema baseline và đúng chín Store GET.
- Giữ hash SQL list/pricing `cd1786695f6713fff70d5d6529aa8c04ef5d609f4b3eb4a6bcb42b07e232baa2` và các tệp detail, taxonomy, public DTO, media, inventory, spec amendments đã review. Không có migration hay dependency mới. Rollback delta remediation dùng snapshot trên và base đã xác minh.

### Kiểm chứng remediation

RED trên candidate trước sửa: hai ca HTTP mới bắt được type hợp lệ bị 400 và category slug sai vẫn 200 (PG17, TRX `remediation/red/`).
Cả sáu ca HTTP gốc và các ca biên/round-trip mới PASS.

| Lệnh | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` và `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warning/error |
| `dotnet test backend/tests/TeaPremium.UnitTests --no-build --no-restore` | 216/216 PASS |
| `dotnet test backend/tests/TeaPremium.ArchitectureTests --no-build --no-restore` | 34/34 PASS |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter <filter>` | PostgreSQL 17.11, 108/108 PASS, 0 skip. Filter `FullyQualifiedName~StoreCatalogTests|FullyQualifiedName~OpenApiTests|FullyQualifiedName~CatalogReferenceApiTests|FullyQualifiedName~CatalogReferenceG4FixTests|FullyQualifiedName~TaxonomyApiTests|FullyQualifiedName~TaxonomyG4FindingTests`. |
| `corepack pnpm generate:api`, `corepack pnpm check:api` | PASS từ API thật trên Linux. |
| `corepack pnpm --filter @teapremium/api-client lint` và `typecheck` cho API client, Store, Admin | PASS, gồm type assertions dương/âm. |
| Node gọi `openapi-fetch` với fetch giả kiểm URL | PASS. URL query thực gửi đi `?attr.capacity=240-260&attr._texture=smooth&sort=price_asc&category=tea`. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore`, `git diff --check origin/main`, kiểm whitespace tệp untracked | PASS. |

Runner riêng P1-14 dùng database/cổng động và `TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal`. TRX, snapshot và bảng hash lưu dưới `artifacts/p1-14/remediation/`. Full backend và stress 20.000 variant không chạy lại cho remediation. Dùng lại evidence đã review cho tệp khớp hash. **READY_FOR_FOCUSED_G4: YES** trên cùng worktree.

## Final artifact check sau tích hợp P1-09 · 22/11/2026

Worktree `D:/tra-hien-p1-14`, branch `feature/p1-14-store-catalog`. HEAD, `origin/main` và remote `main` cùng ở `a46ca4d745d0c4fc7b67853f29fe95863d24ef7b`. Bằng chứng G4 độc lập do chủ dự án cung cấp: F1/F2 CLOSED, hành vi F3 và wire format PASS. Phiên này xử lý hai lỗi hiện vật còn lại.

- Trước sửa: 22 tệp candidate, index rỗng. Manifest cũ khớp 20/23, lệch `Program.cs` do tích hợp base và hai tệp generated còn theo base. Snapshot khôi phục đủ 22 tệp trong `artifacts/p1-14/final-artifact-check/before/`. Danh sách hash `before-all.sha256` có SHA-256 `3c308820dcc6dda1f97f9af3a14f8bb050ae790da09d7c1d9783887e530b241b`. Fingerprint `47c4a727266b3f053b2e0af86cd40b0b20a2c5664952a6bfa03c87b546bcbb53` là giá trị do reviewer cung cấp. Báo cáo này dùng SHA-256 của danh sách hash/path để có quy cách tái lập được.
- Tái sử dụng runner Linux riêng `tra-hien-p114-g3` do hạn chế Windows Application Control `0x800711C7` đã ghi ở trên. Sao chép và xác minh 474/474 hash đầu vào từ worktree. `dotnet restore backend/src/TeaPremium.Api --locked-mode`, cài package API client bằng `--frozen-lockfile` và `corepack pnpm generate:api` đều PASS. Đã chuyển cả hai output về worktree và đối chiếu từng byte với runner trước/sau `corepack pnpm check:api` (PASS).
- SHA-256 thực tế: `packages/api-client/openapi.json` = `efb95393a3409375a59cf18eefca7e76f9383077fcef17243e32467a636d3545`; `packages/api-client/src/generated/schema.ts` = `4942bf818d818ececc1edc6bd8c2059761f847e97c3d6680535c6198ea8d8f47`.
- Semantic diff đọc trực tiếp worktree: OpenAPI 3.1.1, 94 operation và 112 schema. Giữ nguyên 85 operation/95 schema baseline, gồm đủ 17 Inventory operation. Thêm đúng 9 Store GET và 17 Store schema. `git diff --name-only origin/main` liệt kê cả hai tệp generated. `git diff --exit-code origin/main -- packages/api-client/openapi.json packages/api-client/src/generated/schema.ts` trả 1 vì có delta.
- Lint API client và typecheck API client, Store, Admin đều PASS từ worktree, gồm type assertions cho dynamic attr keys. Kiểm enum sort/availability trong OpenAPI và kiểm `openapi-fetch` với fetch giả đều PASS. URL giữ query phẳng `?attr.capacity=240-260&attr._texture=smooth&sort=price_asc&category=tea`.
- 152/152 tệp C# production byte-identical với đầu phiên và đầu vào sinh contract đã kiểm. Đối chiếu với hash candidate đã review hoặc base tích hợp. Riêng `Program.cs` khớp đúng nội dung base cộng ba dòng đăng ký Store đã review, SHA-256 `25873072b0768595cfe9c7a7eafae3599b9c5e12531d991c33e2c408d82b32e1`. Pricing SQL giữ SHA-256 `cd1786695f6713fff70d5d6529aa8c04ef5d609f4b3eb4a6bcb42b07e232baa2`.
- Chỉ hai tệp generated, hồ sơ này và manifest thay đổi. Sau cập nhật hồ sơ, sinh lại manifest cho toàn bộ candidate trừ manifest và kiểm hash thực tế. Nhật ký đầu vào, hash generated và fingerprint cuối lưu trong `artifacts/p1-14/final-artifact-check/`. Full backend và benchmark dùng lại bằng chứng trước đó. Candidate sẵn sàng cho kiểm hiện vật độc lập.
