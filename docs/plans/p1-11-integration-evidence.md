# P1-11 — integration sau P1-05, local G3

20/11/2026, Asia/Saigon. **BASE_UPDATE: PASS. FR-CAT-08: IMPLEMENTED_LOCAL.** Chưa có independent G4 PASS; không gọi đây là nghiệm thu cuối FR-CAT-08. Không commit/push/PR/merge/production apply. Hồ sơ focused G4 fix cũ được giữ nguyên, không thay bằng self-review.

## Base, recovery và scoped G1/G2

- Branch: `feature/p1-11-admin-catalog`; old base `bb9a901044d41a0783d226fcd678e3865390fa30`; new base = HEAD = fetched origin/main `f39a78a11f9ad5c954e43aa430bf08de3882f504`. Không có commit riêng P1-11; hai commit mới trong branch là lịch sử P1-05 đã merge.
- Backup ngoài repo: `C:/Users/ADMIN/source/tra-hien-local-recovery/p1-11-20261120-155005/`. `manifest.json` lưu branch/HEAD, path/size/SHA-256 của đủ **17 file** tracked + untracked; có bản sao bytes và staged/unstaged binary patches. Copy và restore đều **17/17 raw SHA-256 MATCH**. Chuẩn hóa EOL do stash được phục hồi về bytes bản gốc trước khi sửa tiếp. Không đọc/ghi đè `.env` hoặc secrets của developer.
- Recovery stash vẫn giữ: `9d8f9a05f5d708a61fb53687ae3c5528747e9061`, tên `p1-11-pre-p1-05-integration-2026-11-20-verified-backup`. Không pop/drop. Giữ cả hai recovery copies đến owner chấp nhận integration.
- `git fetch origin --prune`, `git merge --ff-only origin/main`, `git stash apply 9d8f9a05f5d708a61fb53687ae3c5528747e9061`: exit **0**, không conflict. So diff cũ→mới: 51 paths, không có `apps/admin/**`; gồm backend/spec/client và evidence P1-05. Không dùng branch P1-05, reset/clean hoặc sửa backend.
- [PR #31](https://github.com/jimthanhnobi/tra-hien/pull/31) được connector xác minh merged với merge SHA trên. [Push-main CI 44703570513](https://github.com/jimthanhnobi/tra-hien/actions/runs/44703570513): event push, head_branch main, **head_sha đúng new base**, completed/success; cả bốn job Classify changes and validate docs, Backend and API contract, Frontend verification, Docker regression builds đều success. Đây là CI backend/base; delta P1-11 chưa publish nên chưa có CI riêng.

**G1 TARGETED / G2 scoped: GO.** Tái sử dụng full audit `cadce575597738bf40ad7c45689482de5a5fbc3d` (`docs/analysis/spec-inventory.md`), A01/B01, `docs/architecture/conformance-review.md`, `docs/plans/task-readiness.md` và scoped evidence P1-11 trước đó. Audited input hiện hành: new base ở trên, TeaPremium v2.0. Đã đọc toàn bộ spec diff old→new: 00 §10, 03 §3.7, 05 §4.2, 07 AC-P1-07, 09 BR-SEO-22. Thay đổi hẹp là AND-token search và archive redirect; search được dùng trong picker, archive không thuộc P1-11. DDL/kiến trúc/dependency P1-11 không đổi; không cần full audit hoặc roadmap mới.

Traceability: `07-roadmap.md:60` **P1-11**, dependencies P1-03/P1-04 đã merge; P1-05 mới giải quyết discovery contract trước đây. `01-requirements.md:64–72` FR-CAT-01/02/03/**08**; `04-business-rules.md:53,61` BR-CAT-08/10; `05-api.md` §1/4.2 opaque rowVersion, permission và CSRF; `03-database.md` §3.7 và AC-P1-07 cho tên/SKU token-AND; NFR-07/09/10/11; DEC-14/15/16. Không tạo AC-P1-11 hay requirement mới. Không có mâu thuẫn task-relevant mới cần owner quyết định; không sửa specs.

## Contract và implementation

Đọc source merged: `ProductEndpoints.cs`, `ProductContracts.cs`, `CatalogContracts.cs`, `CatalogHandlers.cs`, `EfCatalogStore.cs`, `EfProductStore.cs`, OpenAPI JSON và generated schema. `ProductData`, `ProductImage`, `CollectionProductList`/items dùng trực tiếp generated types; không custom DTO copy, override hoặc sửa generated files.

- GET products nhận q/page/pageSize/status; picker dùng **20**/page, query key gồm toàn bộ search/filter/page, truyền AbortSignal. Backend token-AND search gồm mọi variant SKU; UI không tự lọc client hoặc thêm stock/price/cost requests. Status draft/active/archived đều hợp lệ: backend `ProductsExistAsync` chỉ kiểm tồn tại, không hạn chế trạng thái khi gán collection.
- GET relations trả id/rowVersion/items. Quan hệ cũ hydrate qua GET product detail, chỉ **20 dòng đang hiển thị**, dùng React Query cache chung với search, staleTime 60s/gcTime 5m, session invalidation hiện có. Loading/lookup error giữ UUID và quan hệ; có retry rõ ràng. Tên/SKU prominent, status tiếng Việt, UUID phụ, cover thực từ DTO khi có; ảnh lỗi có fallback.
- Dialog Radix hiện có: search tên/SKU phía server, status, trang trước/sau, nhiều checkbox, loading/error/retry/empty, đánh dấu existing/khóa duplicate. Lựa chọn dialog giữ qua trang/search; xác nhận bỏ lựa chọn. “Thêm” chỉ cập nhật draft local, không PUT trên checkbox.
- Draft quan hệ hỗ trợ thêm/bỏ/đổi thứ tự bằng nút bàn phím, nhìn thấy chưa lưu, hủy có xác nhận, save rõ ràng; bỏ relationship không DELETE/archive product. PUT gửi **toàn bộ** items, giữ mọi quan hệ cũ và chuẩn hóa `sortOrder=0..n-1`; empty list được lưu. Backend chấp nhận int32 sortOrder; convention contiguous từ 0 phù hợp response ordering. Giới hạn 5.000, không duplicate; kiểm cả lúc chọn và lúc gộp draft.
- **Finding 1 regression: PASS local.** Chỉ `409 concurrency_conflict` ở record tồn tại là stale; uniqueness và 409 chưa biết vẫn cho sửa/gửi lại thủ công; không GET `/new`, không auto replay.
- **Finding 2 regression: PASS local.** Không rowVersion React key. Metadata dirty/pending/conflict khóa quan hệ; PUT pending khóa fieldset/Save/Cancel metadata, chống double-submit. Quan hệ dirty hoặc dialog mở chặn metadata Save/reload; có thể soạn metadata nhưng phải lưu/hủy draft quan hệ trước, không silently bỏ selection. Cancel editor và beforeunload cảnh báo cả draft quan hệ. Success chỉ cập nhật rowVersion parent từ PUT response, không refetch/remount/reset metadata. Stale giữ lựa chọn/metadata và khóa replay; explicit reload có xác nhận discard/reconcile. Version snapshot lệch vẫn chặn, không lấy token mới để ghi metadata cũ.
- Tái dùng AdminApi/AdminCookie, StaffReady/session, forced password/TOTP, fresh CSRF từng unsafe request, catalog.read/write và publish guards. Không đổi shell hoặc dependency/lockfile. CSS chỉ áp các row/dialog mới; mobile/tablet wrap, mục chọn lớn, semantic labels, keyboard và axe.

## Verification

Kết quả thực, log local ignored tại `artifacts/p1-11-integration/commands.json` và các `.log` cùng thư mục; mọi lệnh từ root:

| Lệnh | Exit / kết quả |
|---|---|
| `corepack pnpm install --frozen-lockfile` | **0 PASS**, không lockfile drift |
| `corepack pnpm --filter @teapremium/admin lint` | **0 PASS**, zero warnings |
| `corepack pnpm --filter @teapremium/admin typecheck` | **0 PASS** |
| `corepack pnpm --filter @teapremium/admin test:unit` | **0 PASS 32/32**, fail/skip 0 |
| `corepack pnpm --filter @teapremium/admin test:browser` | **0 PASS 71/71**, fail/skip 0, 44.3s |
| `corepack pnpm --filter @teapremium/admin build` | **0 PASS**, Next 16.4.6, dynamic catalog route |
| `corepack pnpm --filter @teapremium/api-client typecheck` | **0 PASS** |
| `corepack pnpm --filter @teapremium/store lint` | **0 PASS** |
| `corepack pnpm --filter @teapremium/store typecheck` | **0 PASS** |
| `corepack pnpm --filter @teapremium/store test` | **0 PASS 2/2** |
| `git diff --check`; `git diff --cached --check` | **0 PASS** mỗi lệnh, staged rỗng |
| `git diff --exit-code origin/main -- backend apps/store packages/api-client docs/spec .github infra package.json pnpm-lock.yaml pnpm-workspace.yaml` | **0 PASS**, scope cấm không đổi |

32 unit = giữ 30 cũ + 2 ca adapter product discovery/AbortSignal và empty full replacement/opaque token/fresh CSRF. 71 browser = giữ 58 scenario cũ + 13 ca FR-CAT-08 (gồm 3 failure statuses). Thay hợp lệ một assertion cũ “picker hoãn” bằng empty collection có discovery; cập nhật nhãn Save thành “Lưu danh sách sản phẩm”. Không xóa scenario uniqueness/new-record/delayed PUT/mismatch/IAM/material-artisan publication hoặc module nào.

Coverage FR-CAT-08: empty→add, append giữ existing, remove/empty replacement, reorder và qua ranh giới trang, duplicate, tên/base/variant SKU, status (không loại draft/archived), selection xuyên trang/search, response race với request gate, lookup lỗi/retry, cancel không mutate, full-list payload/version mới, stale/403/CSRF/401 giữ draft hoặc clear session đúng guard, metadata dirty/pending ở cả hai chiều, read-only, keyboard, axe và viewport 390px. Ca 4.999→5.000 chứng minh cap/khóa chọn, full payload 5.000 unique IDs và chỉ 20 lookups trên trang đầu; không hydrate toàn catalog. Các suite shell hiện có giữ cả tablet/desktop.

Sau lượt full suite, QA ảnh mobile dẫn tới chỉnh nhỏ bố cục dialog/focus và tái dùng class Catalog để input/select có cùng style. Full suite đã chạy sau thay đổi bố cục; sau bổ sung assertion focus và class style cuối, chạy focused `corepack pnpm --filter @teapremium/admin exec playwright test --grep 'keyboard selection/reorder|cancel picker and relation'`: **0 PASS 2/2**; real E2E chạy lại **0 PASS 1/1**; lint/typecheck/build chạy lại mỗi lệnh **0 PASS**. Không cộng reruns vào tổng test. Hai ảnh `product-dialog-mobile.png`, `collection-picker-mobile.png` đã xem trực tiếp; không overflow ngang, search/filter/actions thấy rõ, cover giả tải được, focus trả về control mở dialog. Không coi đây là real R2/media proof. Next tự sinh thay đổi next-env.d.ts/tsconfig khi dùng distDir riêng: đã giữ output trong ignored artifacts và khôi phục **chỉ hai file tự sinh này** về bytes HEAD (ban đầu không có P1-11 delta), không sửa cấu hình lint/ignore để che lỗi.

**Real backend E2E: PASS**, tách riêng suite mock. `ADMIN_CATALOG_FIXTURE=<absolute ignored synthetic fixture>` rồi `corepack pnpm --filter @teapremium/admin exec playwright test --config playwright.catalog-live.config.ts`: **exit 0, 1/1 PASS**, không mock API, skip/retry 0. Test một scenario tổng hợp nhiều assertions:

1. Bootstrap fixture dựa trên `tests/iam/seed`, migrate DB rỗng với tp_owner; Production API dùng tp_app; UI password change bắt buộc, enroll TOTP và login thật, Secure/HttpOnly/SameSite Strict cookie.
2. Tạo product type, hai draft products và collection qua API thật; tìm tên, SKU base và variant SKU. Không cần R2 để tạo draft hoặc gán quan hệ.
3. Thêm bằng UI → PUT → reload/persist; thêm sản phẩm thứ hai bằng SKU; reorder, bỏ quan hệ; GET xác minh sản phẩm bị bỏ khỏi collection vẫn tồn tại. Kiểm rowVersion mới và full replacement thứ tự.
4. PATCH cạnh tranh thật làm UI stale → PUT trả 409, lựa chọn chưa lưu vẫn còn, dữ liệu server không bị ghi đè. PUT thiếu CSRF → 400 csrf_invalid, không đổi quan hệ.
5. Tạo sales staff giả qua IAM, đổi password bắt buộc/login; GET quan hệ 200, PUT 403, UI read-only không có nút thêm. Server giữ nguyên danh sách.

Environment riêng: container **teapremium-p111-picker-pg17**, image postgres:17, database **p111_picker_test**, loopback PG **55441**, API **5087**, Admin **3117**, Caddy HTTPS **9443**. Dùng provisioning `infra/postgres/init-roles.sh`/SQL/grants hiện có; không shared database/volume, không chạy schema.sql. Container có storage mới do Docker tạo, không mount volume developer. Caddy executable/harness và CA local đã tin cậy có sẵn được tái dùng qua **bản sao storage riêng**; Chromium xác minh TLS bình thường, không `ignoreHTTPSErrors`, SPKI exception hoặc bypass IAM. PFX Data Protection và credentials đều sinh mới, chỉ nằm trong ignored artifacts, không trong publish inventory. API/UI/Caddy và container riêng đã **stop**, dữ liệu giả/recovery không bị xóa; không dừng stack khác.

Lượt chuẩn bị có lỗi thật đã sửa: build `--no-restore` dùng assets cũ thiếu AngleSharp/Ganss → `dotnet restore backend/src/TeaPremium.Api --locked-mode` và `dotnet build backend/src/TeaPremium.Api --no-restore` mỗi lệnh **exit 0**; expectation staff POST 201 sai (contract thực 200) → sửa assertion, real E2E chạy lại PASS. Browser fixture mới bị PowerShell ASCII pipe → sửa encoding UTF-8; locator lookup khớp cả loading transient → đợi retry control cụ thể; image src được Next normalize absolute → kiểm đúng path và ảnh tải thành công. Lint lượt đầu quét distDir live tự sinh chưa ignore → di chuyển output đó vào ignored artifacts sau stop; không nới lint rules. Không nhận các lượt lỗi là PASS.

**NOT RUN:** R2/media thật và real cover upload/display (cover UI được kiểm với mock URL/ảnh giả), full backend suites local vì backend không đổi (reuse main CI đúng SHA), production/deploy, CI của local delta, independent G4, screen reader thủ công/thiết bị mobile vật lý. Rich editor theo đoạn và cảnh báo draft khi chuyển route nội bộ vẫn theo giới hạn P1-11 lịch sử; không mở scope Tiptap/P1-12/P1-07. Không tuyên bố full product/SEO/stock acceptance.

## Inventory, rollback và local handoff

Staged rỗng; committed P1-11 delta rỗng. Exact full inventory so origin/main: **21 paths = 4 modified + 17 untracked**. `git diff origin/main` chỉ hiển thị 4 tracked files; reviewer phải mở cả 17 untracked files. Không chứa build outputs, secret/credential, backend/store/generated/spec/infra/CI hoặc lockfile drift.

```text
 M apps/admin/src/app/globals.css
 M apps/admin/src/components/admin-screen.tsx
 M apps/admin/src/components/ui.tsx
 M apps/admin/src/lib/api.ts
?? apps/admin/playwright.catalog-live.config.ts
?? apps/admin/src/app/catalog/[resource]/page.tsx
?? apps/admin/src/components/catalog/catalog-form.tsx
?? apps/admin/src/components/catalog/catalog-screen.tsx
?? apps/admin/src/components/catalog/collection-products.tsx
?? apps/admin/src/components/catalog/media-picker.tsx
?? apps/admin/src/components/catalog/product-picker.tsx
?? apps/admin/src/components/catalog/reference-panel.tsx
?? apps/admin/src/components/catalog/taxonomy-panel.tsx
?? apps/admin/src/lib/catalog-api.ts
?? apps/admin/src/lib/catalog-forms.ts
?? apps/admin/tests/browser/catalog.spec.ts
?? apps/admin/tests/catalog-live/collection.spec.ts
?? apps/admin/tests/catalog.test.tsx
?? docs/plans/p1-11-focused-g4-fix.md
?? docs/plans/p1-11-implementation.md
?? docs/plans/p1-11-integration-evidence.md
```

File `p1-11-focused-g4-fix.md` giữ nguyên SHA-256 `43ccdf07544e2911a71c0107b184f5f2db09e683c4eb8865f11b76576416f122`. `post-integration-manifest.json` và `integrated-files/` dưới cùng recovery directory bổ sung path/size/SHA-256 cùng bản sao đủ **21/21 file đã xác minh** để reviewer đối chiếu snapshot; không thay recovery manifest 17 file ban đầu. Python kiểm allowlist, strict UTF-8, trailing whitespace và SHA-256: **exit 0 PASS**; staged rỗng, HEAD vẫn bằng origin/main; container riêng `exited`.

Migration/rollback: không có migration hoặc API/DDL thay đổi. Recovery dùng backup/stash sau khi lưu draft mới cần giữ; hoàn nguyên riêng source P1-11, không reset/clean toàn worktree. PUT thành công không bị đảo bởi source rollback; khôi phục quan hệ qua UI/API với version mới nếu owner muốn. Giữ stack giả đã stop và recovery đến owner chấp nhận. Rủi ro còn lại: G4 độc lập chưa PASS, media/R2 thật chưa chạy, metadata và relations là hai lần lưu có phối hợp, giới hạn editor/navigation lịch sử. Bước tiếp theo: reviewer session mới đọc cùng worktree, full diff origin/main + staged/unstaged/**untracked contents**, rồi owner quyết định publish riêng.

**Backup verified: YES. P1-11 source preservation: PASS (17/17 trước sửa tiếp). P1-05 API compatibility: PASS. Original finding 1/2: regression PASS/PASS local. READY_FOR_LOCAL_G4: YES.** Dừng ở local G3; chưa independent G4 PASS/chưa owner nghiệm thu cuối FR-CAT-08.
