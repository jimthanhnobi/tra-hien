# P1-12 — Quản lý sản phẩm trong Admin

21/11/2026 · Tier A · **G1 TARGETED PASS · G2 GO · G3 PASS · READY_FOR_LOCAL_G4: YES.**

## Baseline và phạm vi đối chiếu

- Worktree `D:/tra-hien-p1-12`, nhánh `feature/p1-12-admin-products`. Worktree sạch lúc bắt đầu. `HEAD` và `origin/main` sau `git fetch origin --prune` cùng là `c88069d58241fd51f5f6a76da2276c41f7a3830a`. GitHub Actions trả 4/4 job thành công. Đã kiểm SHA trong log checkout. [Main CI 44777442188](https://github.com/jimthanhnobi/tra-hien/actions/runs/44777442188)
- G1 full baseline `cadce575597738bf40ad7c45689482de5a5fbc3d`. Đối chiếu diff đến kết luận P1-05 `1a3525b`, rồi đến base hiện tại. Từ P1-05, `00`, `04`, `05`, `07` chỉ đổi hợp đồng import P1-07. `06` đổi phân loại CI. Spec tree được audit `8e54535ff1f3f3ae9a7e1f00db1001857f3d3395`. [G1 inventory](../analysis/spec-inventory.md) · [G2 conformance](../architecture/conformance-review.md) · [Readiness](task-readiness.md)
- Nguồn TARGETED `docs/spec/00-README.md`, `01-requirements.md`, `02-architecture.md`, `03-database.md`, `04-business-rules.md`, `05-api.md`, `06-infrastructure.md`, `07-roadmap.md`, `09-seo.md`, `schema.sql`. Đã so với generated OpenAPI, Admin shell, AdminApi/CSRF, QueryClient, form và picker P1-11, harness Playwright và tài liệu App Router/Link cài cùng Next.js. [P1-05](p1-05-implementation.md) · [P1-11](p1-11-implementation.md) · [P1-11 closure](p1-11-final-closure.md)
- P1-12 phụ thuộc P1-05, dùng P1-02/03/04 qua các API đã merge và tái sử dụng UI P1-11. P1-06 nằm ở task giá khuyến mãi. FR-CAT-09 và phần import của AC-P1-09 thuộc hợp đồng P1-07 đã merge. Roadmap P1-12 chỉ yêu cầu UI sản phẩm, năm tab và checklist publish. [Roadmap §2](../spec/07-roadmap.md#L50) · [API §4.2](../spec/05-api.md#L103)

## G2 và các điểm cần đối chiếu

**Editor FR-CAT-04, lát B — đã giải quyết.** `01-requirements.md:68` yêu cầu mô tả rich text. `02-architecture.md:246`, §9 Frontend, chỉ định Tiptap. Baseline P1-11 chuyển nội dung đã chỉnh sang văn bản theo đoạn và không có dependency Tiptap. Owner duyệt rõ Tiptap và cập nhật lockfile cho P1-12 trong phiên này. G2 chuyển HOLD → GO. Admin pin Tiptap `3.34.0` và tải chunk editor theo route chi tiết. Tiptap nhận HTML đã sanitize từ API, gửi HTML khi người dùng thực sự sửa. Nội dung chưa sửa được bỏ khỏi PATCH. API tái sanitize HTML và xóa JSON cũ khi chính trường mô tả được lưu bằng HTML. [FR-CAT-04](../spec/01-requirements.md#L68) · [Kiến trúc §9](../spec/02-architecture.md#L246) · [Editor P1-11](../../apps/admin/src/lib/catalog-forms.ts#L27) · [ProductWorkflow](../../backend/src/TeaPremium.Application/Catalog/ProductWorkflow.cs#L47) · [Tiptap cho Next.js](https://tiptap.dev/docs/editor/getting-started/install/nextjs)

- **MISSING_CONTRACT, metadata OpenAPI.** `05-api.md` §4.2 mô tả ID sản phẩm/biến thể/ảnh trong path. Các action ghi dùng `RouteValues` ở server nên generated OpenAPI thiếu `parameters.path`. Typecheck trước adapter trả TS2322. Adapter dùng đường dẫn, DTO và response đã sinh. Chỉ gắn UUID đã validate qua `pathSerializer` theo request và cố định bằng unit test phương thức/path/body/CSRF. Sửa metadata generator là đề xuất cho lượt bảo trì API riêng. [API §4.2](../spec/05-api.md#L108) · [Backend binding](../../backend/src/TeaPremium.Api/Catalog/ProductEndpoints.cs#L100) · [Generated PATCH](../../packages/api-client/src/generated/schema.ts#L3127) · [Adapter](../../apps/admin/src/lib/products-api.ts)
- **BR-CAT-02, trạng thái khóa SKU.** API không chiếu cờ đã có giao dịch. UI giới hạn sửa SKU ở owner có `catalog.write`. Backend kiểm usage và trả `sku_locked`. Khi nhận lỗi, UI hiển thị SKU gốc và khóa trường trong lần xử lý lỗi đó. [BR-CAT-02](../spec/04-business-rules.md#L35) · [Variant workflow](../../backend/src/TeaPremium.Application/Catalog/ProductWorkflow.cs#L190)
- **SEO theo DEC-21.** BR-SEO-22 thay hướng dẫn archive trong BR-CAT-05. UI chuyển nguyên lựa chọn đích vào API, dùng fallback của server khi bỏ trống. Auto-fill metadata do người dùng kích hoạt. BR-SEO-34 cho phép công khai nội dung mỏng khi đủ điều kiện khác và giữ mọi chặn validation của BR-CAT-04. Thẻ robots, canonical render, sitemap, feed và 301 trên storefront thuộc pha P3. [BR-SEO-22](../spec/09-seo.md#L75) · [BR-SEO-34](../spec/09-seo.md#L112) · [AC-P1-10](../spec/07-roadmap.md#L78)

## Lát triển khai và nghiệm thu

| Lát | Hợp đồng và nguồn | Xác minh dự kiến | Trạng thái |
| --- | --- | --- | --- |
| A · Danh sách và tạo nháp | P1-12, FR-CAT-04/11, BR-CAT-01/10/14, AC-P1-06/07. `/catalog/products`, `/catalog/products/new`, `/catalog/products/[id]`. List phân trang server, truyền `AbortSignal`, có debounce. Tạo dùng ID, SKU và variant phản hồi. [API](../spec/05-api.md#L108) · [Search](../spec/03-database.md#L95) | Browser tìm không dấu, đủ filter, đổi trang/search nhanh, duplicate slug và guard trực tiếp. | Đã triển khai |
| B · Thông tin và thuộc tính | FR-CAT-04, BR-CAT-03/04/08, DEC-04. Mô tả Tiptap, tham chiếu có nhãn, picker sản phẩm liên quan, xác nhận `confirmTypeChange`. EAV `text`, `number`, `option`, `boolean`, `dimensions`. [DDL](../spec/schema.sql#L190) · [BR-CAT](../spec/04-business-rules.md#L36) | Unit và browser kiểm 0, false, option, kích thước, đổi loại có xác nhận, giữ nội dung/tham chiếu chưa sửa. | Đã triển khai |
| C · Biến thể và giá | FR-CAT-05, FR-PRC-01, BR-CAT-02/14/15, BR-PRC-03 và phần giá AC-P1-09. Thêm dùng token product, cập nhật dùng token variant. Nhận aggregate mới sau ghi. `catalog.write` cho variant APIs và `pricing.write` cho `priceVnd`. Tài khoản chỉ đọc xem qua `ProductData.variants`. [API](../spec/05-api.md#L114) · [Token](../../backend/src/TeaPremium.Application/Catalog/ProductWorkflow.cs#L29) | Unit và browser kiểm giá nguyên 1–10.000.000.000 đồng, NULL, quyền giá/SKU, SKU ổn định và token đúng. | Đã triển khai |
| D · Ảnh và SEO | FR-CAT-06, BR-CAT-07/08, BR-SEO-23/31/32/34, DEC-21/27. `putUrl` là PUT theo §5.10. Signed upload omit credentials rồi complete trước attach. Media mutation dùng token product. Manual noindex có lý do, thin đọc từ server, auto-fill chủ động và preview. [API](../spec/05-api.md#L115) · [SEO](../spec/09-seo.md#L77) | Mock upload/complete/attach, lỗi retry, bìa/ALT/thứ tự/variant, gỡ relation, SEO auto-fill, thin tách riêng. | Đã triển khai |
| E · Publish, archive, restore | FR-CAT-07, BR-CAT-04, BR-SEO-22/34, AC-P1-01/02/03/10. Checklist sơ bộ trên dữ liệu lưu. `catalog.publish`, token product, map mọi lỗi từ 422 và focus về tab. Đích archive server xử lý. [BR-CAT-04](../spec/04-business-rules.md#L37) · [Archive](../spec/05-api.md#L124) · [AC](../spec/07-roadmap.md#L68) | Browser ALT và thuộc tính cùng lúc, trùng trục, lỗi mở rộng, thin 149 từ, archive default/invalid và restore. Redirect flattening theo bằng chứng API P1-05. | Đã triển khai |

DEC-01/14/15/16 và NFR-07/09/10/11 áp dụng xuyên suốt. Dùng lại StaffReady, permission, fresh CSRF và cơ chế phiên. Chọn một vùng chỉnh sửa tại một thời điểm, có đường lưu/hủy riêng, giữ draft khi refetch/lỗi. Serialize mutation, giữ token của snapshot đang sửa, tải lại theo lựa chọn rõ ràng khi `409 concurrency_conflict`, phân biệt 409 nghiệp vụ. Tách cache editor khỏi cache projection P1-11. Kiểm thử chuyển tab, rời trang, mutation chậm, bàn phím và viewport nhỏ ở browser. [NFR](../spec/01-requirements.md#L204) · [AdminApi](../../apps/admin/src/lib/api.ts#L73)

E2E dùng PG17 riêng, role/grant sẵn có, Production IAM, Admin HTTPS và fixture tổng hợp theo harness P1-11. Không có migration. Rollback bằng hoàn nguyên diff UI P1-12, manifest Admin và phần lockfile tương ứng. Các thay đổi dữ liệu đã lưu qua API cần xử lý qua thao tác nghiệp vụ với phiên bản mới.

## Kiểm tra đã chạy và bàn giao

Baseline đã qua 33 unit test, 73 browser test và build. Kết quả P1-12 ở bảng dưới.

| Lệnh | Kết quả |
| --- | --- |
| `corepack pnpm install --frozen-lockfile` | PASS, exit 0 |
| `corepack pnpm --filter @teapremium/admin test:unit` | PASS, 38/38 |
| `corepack pnpm --filter @teapremium/admin test:browser` | PASS, 84/84, gồm 73 hồi quy và 11 ca P1-12 |
| `corepack pnpm --filter @teapremium/admin lint` | PASS, exit 0 |
| `corepack pnpm --filter @teapremium/admin typecheck` | PASS, exit 0 |
| `corepack pnpm --filter @teapremium/admin build` | PASS, exit 0. App Router nhận đủ ba route Products và route Catalog hiện có. |
| `corepack pnpm --filter @teapremium/api-client typecheck` | PASS, exit 0 |
| `corepack pnpm --filter @teapremium/store typecheck` | PASS, exit 0 |
| `corepack pnpm --filter @teapremium/admin exec playwright test --config playwright.products-live.config.ts` | PASS, 1/1, 9,9 giây ở lượt cuối. Chạy trong `teapremium-p112-live` với `ADMIN_PRODUCTS_FIXTURE` trỏ file synthetic riêng bên trong container. |
| `git diff --check origin/main`; `git diff --cached --check`; kiểm `git diff --no-index --check -- NUL <path>` từng tệp untracked | PASS, không có lỗi whitespace. |

Lượt browser ban đầu phát hiện các locator trùng với route announcer, nhãn select bọc options và một assertion đọc payload trước khi lưu xong. Các lượt thất bại được sửa trước kết quả 84/84. Playwright kiểm axe và layout ở 390 px. Unit test ghi nhận `priceVnd`, SKU, 0/false/NULL, mọi publish error, path/CSRF, signed PUT và trường hợp upload thất bại. Browser kiểm thêm lưu chậm, snapshot bẩn qua refetch, reload rõ ràng khi stale, 403 và 409 nghiệp vụ.

### E2E thật và giới hạn

- `teapremium-p112-pg17` chạy PostgreSQL 17.11 trên loopback 55442, dữ liệu tổng hợp mới. Provision bằng `infra/postgres/init-roles.sh`, migrate bằng `tp_owner`, runtime Production API dùng `tp_app`. Windows Application Control chặn binary seed khi chạy trên Windows, nên dùng Docker Linux. `dotnet restore backend/src/TeaPremium.Api --locked-mode` và `dotnet build backend/src/TeaPremium.Api --no-restore` thành công. So sánh `diff -qr` nguồn Admin và `cmp` lockfile/test case xác minh bản chạy khớp worktree.
- Caddy loopback 9444 chuyển cùng origin sang API 5088 và Admin 3118 trong network namespace riêng. CA synthetic chỉ import vào NSS của container. Chromium xác minh TLS mặc định. Lượt E2E đầu dừng ở assertion cookie chạy quá sớm vì selector khớp heading trang login. Sau khi đợi đúng URL và heading, test xác minh `__Host-tp_admin` Secure/HttpOnly/SameSite Strict, owner đã enroll TOTP và tài khoản `content` qua đổi mật khẩu bắt buộc. Stack dành riêng P1-12 đã dừng sau kiểm chứng.
- E2E xác nhận tạo nháp và SKU mặc định, lưu danh mục và nội dung đậm qua sanitizer, thuộc tính 0, đọc lại giá 395.000 VND và biến thể inactive giá NULL. Asset UUID chưa tồn tại bị từ chối 400 và token/media giữ nguyên. Publish trả 422 đồng thời cho thiếu ảnh bìa ALT và thuộc tính bắt buộc. Phiên khác cập nhật trước khiến lưu UI trả 409 thật, bản nháp cục bộ còn để đối chiếu. Tài khoản `content` đọc JSON không có `avgCostVnd`, API từ chối ghi giá 403, sau đó UI đổi tên biến thể và giá/SKU/tham chiếu giữ nguyên.
- **R2 thật và công khai thành công với ảnh thật — NOT RUN.** Không có môi trường R2 được duyệt trong lượt này. Upload/complete/attach, cover, ALT, reorder và xóa relation có bằng chứng mock browser. AC-P1-02 và nhánh thành công/cảnh báo 149 từ của AC-P1-10 có bằng chứng UI mock. AC-P1-03 dựa vào hợp đồng và kiểm thử redirect của P1-05. SSR, 301 công khai, canonical render, sitemap và feed thuộc pha P3. Không chạy lại toàn bộ backend vì diff backend rỗng.

### Inventory local

8 tệp tracked sửa, 22 tệp untracked, 0 staged. G4 cần đọc nội dung tất cả untracked cùng worktree này.

```text
M apps/admin/package.json
M apps/admin/src/app/globals.css
M apps/admin/src/components/admin-screen.tsx
M apps/admin/src/components/catalog/catalog-screen.tsx
M apps/admin/src/components/catalog/media-picker.tsx
M apps/admin/src/components/catalog/product-picker.tsx
M apps/admin/src/lib/api.ts
M pnpm-lock.yaml
?? apps/admin/playwright.products-live.config.ts
?? apps/admin/src/app/catalog/products/[id]/page.tsx
?? apps/admin/src/app/catalog/products/new/page.tsx
?? apps/admin/src/app/catalog/products/page.tsx
?? apps/admin/src/components/products/product-attributes.tsx
?? apps/admin/src/components/products/product-editor.tsx
?? apps/admin/src/components/products/product-fields.tsx
?? apps/admin/src/components/products/product-information.tsx
?? apps/admin/src/components/products/product-media.tsx
?? apps/admin/src/components/products/product-navigation.ts
?? apps/admin/src/components/products/product-publication.tsx
?? apps/admin/src/components/products/product-references.tsx
?? apps/admin/src/components/products/product-rich-text.tsx
?? apps/admin/src/components/products/product-seo.tsx
?? apps/admin/src/components/products/product-variants.tsx
?? apps/admin/src/components/products/products-screen.tsx
?? apps/admin/src/lib/product-forms.ts
?? apps/admin/src/lib/products-api.ts
?? apps/admin/tests/browser/products.spec.ts
?? apps/admin/tests/products-live/products.spec.ts
?? apps/admin/tests/products.test.tsx
?? docs/plans/p1-12-implementation.md
```

**READY_FOR_LOCAL_G4: YES.** Bàn giao cho G4 độc lập Tier A trên cùng worktree, so với `origin/main` tại `c88069d58241fd51f5f6a76da2276c41f7a3830a`.
