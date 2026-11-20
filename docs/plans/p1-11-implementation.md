# P1-11 — Admin Catalog UI, local G3

**Resume sau P1-05, 20/11/2026:** base đã fast-forward từ `bb9a901044d41a0783d226fcd678e3865390fa30` tới `f39a78a11f9ad5c954e43aa430bf08de3882f504`; checkpoint 17/17 file được xác minh SHA-256 trước khi sửa tiếp. Collection picker đã triển khai local bằng generated Product API: tìm tên/SKU phía server, trạng thái, phân trang, chọn nhiều, thêm/bỏ/sắp xếp, lưu/hủy danh sách đầy đủ và hydrate theo trang. **FR-CAT-08: IMPLEMENTED_LOCAL, chưa nghiệm thu cuối qua G4 độc lập.** Kết quả hiện hành, inventory, lệnh và giới hạn nằm tại [p1-11-integration-evidence.md](p1-11-integration-evidence.md). Các mục bên dưới giữ nguyên làm lịch sử G3/focused fix trước P1-05; ghi chú thiếu discovery/hoãn picker không còn mô tả bản hiện hành. Không commit/push/PR/merge.

**Final local G3: READY_FOR_LOCAL_G4: YES.** Unit **32/32**, browser API-mocked **71/71**, backend thật PG17/Production IAM/trusted TLS **1/1**; focused mobile/focus **2/2** sau UI chỉnh cuối. Lint/typecheck/build/client/store checks PASS, mọi lệnh exit 0; hai finding G4 cũ regression PASS local. Inventory **21 paths (4 modified + 17 untracked)**; recovery backup/stash vẫn giữ. Real R2/media và G4 độc lập chưa chạy. Không self-approve hoặc publish.

**Cập nhật focused fix:** owner báo independent G4 **BLOCK** với hai finding P1 (phân loại 409 và mất metadata khi reorder). Bản sửa, kết quả regression và quyết định owner chấp nhận phạm vi từng phần nằm tại [p1-11-focused-g4-fix.md](p1-11-focused-g4-fix.md). Các số test/READY dưới đây là lịch sử G3 trước G4, không phải G4 PASS. Owner đã duyệt hoãn picker đến P1-05; FR-CAT-08 vẫn chưa nghiệm thu đầy đủ.

Ngày 20/11/2026 (Asia/Saigon). **G1 TARGETED: GO. G2 scoped: GO. READY_FOR_LOCAL_G4: YES** cho change set frontend và các giới hạn bên dưới. Chưa có independent G4; chưa commit/push/PR/merge/deploy. Tìm/chọn thêm sản phẩm cho collection chưa hoàn thành vì thiếu API discovery ở base này; không nhận toàn bộ FR-CAT-08 đã nghiệm thu end-to-end.

## G0, baseline và nguồn

- Workspace thực tế: `C:/Users/ADMIN/source/tra-hien`. Đường dẫn `D:/tra-hien-admin` trong prompt không tồn tại. Tiếp tục workspace đang mở vì branch, remote và expected base khớp, đã thông báo trong phiên.
- Branch: `feature/p1-11-admin-catalog`.
- Expected base = HEAD = origin/main: `bb9a901044d41a0783d226fcd678e3865390fa30`. Working tree ban đầu sạch. Không tạo commit mới.
- `git fetch origin --prune`: exit 0; `git rev-parse origin/main`, `git log -1 --oneline`, `git branch --show-current` khớp.
- P1-03 merge #29 `0737dea`, P1-04 merge #30 `bb9a901` có trong lịch sử base. P1-11 chỉ phụ thuộc P1-03/P1-04 theo `docs/spec/07-roadmap.md:60`, không đợi P1-05.
- [Main CI run 44664344998](https://github.com/jimthanhnobi/tra-hien/actions/runs/44664344998): GitHub connector xác minh push-main tại đúng SHA trên, completed/success. Bốn jobs Classify changes and validate docs, Backend and API contract, Docker regression builds, Frontend verification đều success. Không coi đây là CI của delta local.
- Tái sử dụng full G1 `cadce575597738bf40ad7c45689482de5a5fbc3d`, delta A01/B01, G2 `docs/architecture/conformance-review.md`, `docs/plans/task-readiness.md`, hồ sơ P1-03 và P1-04 (`p1-04-g3-evidence.md`, `p1-04-g4-fix-evidence.md`, `p1-04-integration-evidence.md`) cùng ADR publication. Đọc spec diffs và phần bị tác động; không gọi đây là full audit mới. Các nhãn local pending trong hồ sơ cũ là lịch sử; merge/CI mới được xác minh riêng.
- Spec revision gần nhất: `81afe9e3e837f7f5df347368bffb092bc978f5b8`; `git diff --exit-code 81afe9e3e837f7f5df347368bffb092bc978f5b8 HEAD -- docs/spec`: exit 0. Audited input hiện hành là HEAD ở trên. Không sửa spec.

| Nguồn/ID | Coverage P1-11 |
|---|---|
| `07-roadmap.md:60`; `01-requirements.md:64–72` FR-CAT-01/02/03/08 | Đúng năm resources; types → attributes → options; category tree; editorial forms; collection relationships |
| `00-README.md` DEC-01/04/14/15/16/21/26 | Giữ kiến trúc, Admin riêng, cookie/permission riêng, API cùng origin, SEO precedence, SKU đã cấp bất biến |
| `02-architecture.md` §6/7, §9 (`:243`); `05-api.md:103` §4.2 và §1 | App Router, Query/Table, RHF/Zod, generated client, read/write/publish, CSRF và opaque rowVersion |
| `03-database.md` §3.1/3.10; `schema.sql` product_types/attribute_definitions/attribute_options/materials/artisans/categories/collections | Kiểu, scope, đơn vị, axis, flags, loại material/SKU, trạng thái, quan hệ và thứ tự |
| `04-business-rules.md:34,51,53,60,61,66` BR-CAT-01/06/08/09/10/14 | Slug backend sinh khi bỏ trống ở create; không delete; cây ≤3 cấp; rename/status qua API hiện có; không tái sinh SKU |
| `04-business-rules.md:256` BR-CNT-02; `09-seo.md` BR-SEO-23/31/32/35 | Không render HTML thô; server sanitize; explicit SEO/fallback preview; warnings không đổi noindex |
| `01-requirements.md:204–208` NFR-07/09/10/11 | CSRF/permission negative tests, mobile, nhãn và bàn phím/axe |

Không có AC riêng tên AC-P1-11 trong roadmap. Các scenario của owner được kiểm bằng test P1-11; không tạo AC/FR/BR/task ID mới. Không nhận AC-P1-01/02/03 (product publish/redirect), AC kho hoặc toàn bộ DoD P1 đã PASS.

G2 giữ shell/auth/CSRF, Query/Table và generated OpenAPI. Không framework/service/table/migration mới. Chỉ dùng dependency đang khóa. **Giới hạn editor:** chưa có Tiptap dependency; UI hỗ trợ văn bản theo đoạn, không cung cấp toolbar rich text. Nội dung HTML/JSON không sửa được bỏ khỏi PATCH để giữ nguyên; khi sửa, thông báo rõ thay định dạng thành đoạn văn, escape text và gửi qua sanitizer backend hiện hữu. Tiptap với định dạng đầy đủ cần quyết định dependency/lockfile riêng nếu owner yêu cầu; không tự thay kiến trúc rich text cho các task sau.

## UI, component và hành vi

`/catalog` mở Loại sản phẩm; năm route `/catalog/product-types`, `/catalog/categories`, `/catalog/collections`, `/catalog/materials`, `/catalog/artisans`. Route khác bên dưới catalog trả not-found. Tái dùng shell, navigation chính và session guard, không suy quyền từ role.

- `catalog-screen.tsx`: năm liên kết tiếng Việt, wrap trên mobile, active route.
- `reference-panel.tsx`: TanStack Table/phân trang server; tạo/xem/sửa type, drill vào attribute và option; hiển thị active/inactive, dataType/scope/unit/axis; form description, Google category, SKU prefix, flags/order. API reference không có q nên không gắn search giả.
- `taxonomy-panel.tsx`: search tên, filter status, phân trang API; form create/edit; categories dùng cây đầy đủ qua tất cả pages để hiển thị đường dẫn cha/con và lọc parent không hợp lệ (gồm chiều cao subtree). Server vẫn xử lý race. Material chọn tám kinds và SKU code; artisan danh xưng/tiểu sử/chân dung; category/collection hidden/visible, slug/order/SEO. Editorial draft/archived chỉ publish qua endpoint riêng có xác nhận.
- `catalog-form.tsx`: RHF/Zod, nhãn/field errors và summary, Save/Cancel, disable pending, ref chống submit trùng; không retry mutation. Form giữ nội dung khi 400/403/404/409/422/network lỗi. Sau focused fix, chỉ concurrency_conflict ở record hiện có khóa Save đến khi chủ động tải bản mới; lỗi uniqueness/domain cho sửa và gửi lại thủ công. Form tạo mới không có reload detail. Xác nhận bỏ form và cảnh báo beforeunload; chuyển route nội bộ không có cơ chế lưu draft tự động.
- `media-picker.tsx`: search/phân trang thư viện qua GET media; chỉ cho chọn image không evidence/không URL có hạn, giữ ID hiện hữu, bỏ chọn; backend kiểm public asset cuối cùng. Không tải upload/R2, không lộ preview evidence, không render URL/HTML tùy ý.
- `collection-products.tsx`: GET quan hệ đã lưu; hiển thị product IDs và thứ tự, Lên/Xuống bằng bàn phím, PUT nguyên danh sách/thứ tự với version của relation. Không nhập ID giả. Sau focused fix, metadata bẩn khóa reorder kèm giải thích; PUT đang chờ khóa metadata. Thành công cập nhật version parent từ response, không reload/remount/reset metadata; stale version giữ thứ tự dự kiến, có recovery xác nhận rõ việc bỏ thay đổi. Version relation khác snapshot metadata chặn reorder để tránh silent rebase.
- SEO preview dùng text escaped và đếm ký tự explicit; fallback đọc từ DTO đã lưu, nhắc lưu để nhận fallback mới. Không tự sinh/ghi đè fallback hoặc tự noindex theo số từ. Publish warning lấy từ response và hiển thị thành công + số từ, không chặn publication.
- `ui.tsx`: thêm confirmLabel tùy chọn, mặc định logout giữ nguyên. `api.ts`: thêm thông báo tiếng Việt cho 404/409/422; giữ middleware cookie/CSRF/401 hiện có.

## API / permission matrix

Tất cả đường dẫn dưới đây có prefix `/api/v1/admin/`; adapter tại `src/lib/catalog-api.ts` dùng `AdminApi.client` đã typed bởi generated schema, credentials/include và no-store. Không sinh client mới hoặc browser fetch riêng.

| API thực có | Quyền / UI |
|---|---|
| GET product-types, product-types/{id}, .../{id}/attributes[/{attrId}], .../options[/{optId}] | catalog.read; list/detail |
| POST/PATCH các resources reference trên | catalog.write; ẩn create/save nếu thiếu |
| GET categories/collections/materials/artisans[/{id}] | catalog.read; chặn cả direct nested route nếu thiếu |
| POST/PATCH bốn resources taxonomy | catalog.write; ẩn create/save nếu thiếu; PATCH không đưa draft/archived sang published |
| POST materials/{id}/publish, artisans/{id}/publish | catalog.publish; ẩn nếu thiếu; người chỉ có read+publish vẫn publish được bản đã lưu |
| GET / PUT collections/{id}/products | catalog.read / catalog.write; reorder quan hệ hiện có, opaque rowVersion riêng |
| GET media?q=&page=&pageSize= | catalog.read; chọn ảnh công khai hiện có |
| GET auth/me, auth/csrf | Tái dùng provider; fresh X-CSRF-TOKEN trước mọi POST/PATCH/PUT; 401 xóa session/query cache |

Validation: code/type/scope/unit/axis theo backend P1-03; sortOrder int32; SKU prefix 2–4 chữ hoa; material SKU 2–3 chữ hoa, khác XX; slug đúng mẫu và ≤80; required names; no NUL; parent cây ≤3 cấp; trạng thái theo resource. Lý do bật noindex chỉ bắt buộc cho category đúng BR-SEO-23/backend; không thêm điều kiện này cho material/artisan/collection. Không tự thêm điều kiện 400 từ/SEO minimum để publish. 400 validation_failed gắn field errors case-insensitive; csrf_invalid thông báo thử lại thủ công. 401 về login; 403/404/422 hiển thị lỗi; 409 không overwrite/replay. Các lỗi cross-field vẫn có summary của backend. Không parse/normalize rowVersion.

## Giới hạn API và tích hợp P1-05

**MISSING_CONTRACT tại base, không sửa backend:** `05-api.md` §4.2 có GET `admin/products?q=...` thuộc P1-05, nhưng `packages/api-client/src/generated/schema.ts` và endpoint đã merge chưa có đường dẫn này. GET `collections/{id}/products` chỉ trả `{ productId, sortOrder }`, không có tên hoặc candidate products.

Ảnh hưởng: chưa thể tìm/chọn thêm sản phẩm bằng tên, không chứng minh toàn catalog thật rỗng chỉ từ collection rỗng. UI thông báo collection chưa có quan hệ và discovery chưa kết nối; với quan hệ hiện có hiển thị IDs và reorder qua PUT thật. Thay đổi nhỏ nhất sau P1-05: dùng GET products đã merge + regenerated client để thêm picker, giữ PUT/version hiện tại; không tạo Product CRUD hoặc P1-12 form. Đây là capability chưa triển khai, không che bằng mock dữ liệu production.

P1-05 có thể thay đổi product DTO/client; sau merge cần typecheck và contract regression + real API E2E lại, kiểm khả năng collection chọn records và version đổi khi PUT. Không đọc/copy code chưa merge, không thao tác branch/worktree Developer A. Rich toolbar và live integration là giới hạn đã nêu; không claim đã nghiệm thu chúng.

## Verification thực chạy

Các lệnh từ repo root. Kết quả cuối cùng sau các sửa liên quan:

| Lệnh | Exit / kết quả |
|---|---|
| `corepack pnpm install --frozen-lockfile` | 0 PASS; lockfile không đổi |
| `corepack pnpm --filter @teapremium/admin lint` | 0 PASS, zero warnings |
| `corepack pnpm --filter @teapremium/admin typecheck` | 0 PASS; chạy lại sau khôi phục next-env.d.ts generated về baseline |
| `corepack pnpm --filter @teapremium/admin test:unit` | 0 PASS 22/22, skipped 0; 14 ca catalog mới + 8 ca nền hiện có |
| `corepack pnpm --filter @teapremium/admin test:browser` | 0 PASS 45/45, skipped 0, 28.3s; 29 ca catalog + 16 IAM/shell. Playwright Chromium thật, API mocked |
| `corepack pnpm --filter @teapremium/admin build` | 0 PASS Next 16.4.6; TypeScript/build/prerender hoàn tất; dynamic routes /, /[section], /catalog/[resource], not-found |
| `corepack pnpm --filter @teapremium/api-client typecheck` | 0 PASS |
| `corepack pnpm --filter @teapremium/store lint` | 0 PASS |
| `corepack pnpm --filter @teapremium/store typecheck` | 0 PASS |
| `corepack pnpm --filter @teapremium/store test` | 0 PASS 2/2, skipped 0 |
| `git diff --check`; `git diff --cached --check` | 0 PASS; staged rỗng |
| `git diff --exit-code origin/main -- backend apps/store packages/api-client docs/spec .github infra package.json pnpm-lock.yaml pnpm-workspace.yaml` | 0 PASS, scope cấm không đổi |
| PowerShell strict UTF-8/trailing-whitespace/allowlist trên mọi tracked delta và untracked content | 0 PASS; chỉ apps/admin/** và hồ sơ P1-11 này |

Lượt đầu có FAIL thật đã sửa: typecheck adapter generic/fixture type; lint ref callback; browser 37/38 vì locator alert khớp cả Next route announcer. Sau sửa + thêm ca create/conflict, kết quả cuối ở bảng trên. Không gọi lượt fail là PASS. next build tự đổi next-env.d.ts; đã khôi phục đúng nội dung baseline do chỉ là output phát sinh từ lần chạy này, `git diff --exit-code HEAD -- apps/admin/next-env.d.ts` exit 0.

Coverage gồm create/list/edit cả năm resource, attribute/options và bad unit/axis, category cycle/depth/subtree, flags/order/status, publish confirmation/422/409/warnings, public media, unchanged rich-content preservation, empty products/collection, read-only/write-only/publish-only/read denied, 400/401/403/404/409/422 và CSRF không retry, relation stale version, loading/error/empty, axe/bàn phím/viewport 390 và 1440. Screenshot `artifacts/p0-12-browser/catalog-P1-11-catalog-keyboard-a11y-responsive-390/catalog-artisan-390.png` và bản `1440` đã xem trực tiếp; không overflow ngang. Artifacts theo config browser hiện có, ignored, không thêm asset vào Git.

**NOT RUN:** real backend/PG17/TLS/R2 E2E (chưa xác định stack P1-11 cô lập; không dùng shared resources hoặc đọc credentials cũ), `test:iam` real TLS, backend test suites/migration, production/CI delta local, screen reader thủ công, thiết bị mobile vật lý. Không có test nào trong suite đã chạy bị skip. Tests mock không chứng minh backend sanitizer/SQL authorization/redirects/stock/PII hoặc concurrency thật.

## Change set và bàn giao

Tracked modified (không staged):

```text
apps/admin/src/app/globals.css
apps/admin/src/components/admin-screen.tsx
apps/admin/src/components/ui.tsx
apps/admin/src/lib/api.ts
```

Untracked, phải đọc nội dung trực tiếp khi G4 (git diff không bao gồm):

```text
apps/admin/src/app/catalog/[resource]/page.tsx
apps/admin/src/components/catalog/catalog-form.tsx
apps/admin/src/components/catalog/catalog-screen.tsx
apps/admin/src/components/catalog/collection-products.tsx
apps/admin/src/components/catalog/media-picker.tsx
apps/admin/src/components/catalog/reference-panel.tsx
apps/admin/src/components/catalog/taxonomy-panel.tsx
apps/admin/src/lib/catalog-api.ts
apps/admin/src/lib/catalog-forms.ts
apps/admin/tests/browser/catalog.spec.ts
apps/admin/tests/catalog.test.tsx
docs/plans/p1-11-implementation.md
```

`git status --short`:

```text
 M apps/admin/src/app/globals.css
 M apps/admin/src/components/admin-screen.tsx
 M apps/admin/src/components/ui.tsx
 M apps/admin/src/lib/api.ts
?? apps/admin/src/app/catalog/
?? apps/admin/src/components/catalog/
?? apps/admin/src/lib/catalog-api.ts
?? apps/admin/src/lib/catalog-forms.ts
?? apps/admin/tests/browser/catalog.spec.ts
?? apps/admin/tests/catalog.test.tsx
?? docs/plans/p1-11-implementation.md
```

Migration: không có. Rollback frontend: hoàn nguyên riêng các thay đổi P1-11 sau khi lưu snapshot cần giữ; không reset/clean toàn worktree. Các thao tác Admin đã gửi backend không tự đảo theo rollback source; sử dụng flow chỉnh sửa với rowVersion mới, không xóa DB/public history.

Rủi ro: capability chọn thêm sản phẩm/toolbar rich text còn giới hạn; chưa có live backend E2E; form snapshot có thể stale khi người khác sửa nhưng server version + UI conflict bảo vệ; reorder/metadata là hai lần lưu riêng. Cần G4 độc lập cùng worktree đọc đầy đủ diff so origin/main + staged/unstaged/untracked và đánh giá các giới hạn trên. Không self-approve G4 hoặc publish; owner quyết định publish sau G4 PASS. **READY_FOR_LOCAL_G4: YES.**
