# P1-11 — focused G4 finding fix

**FOCUSED_FIX: PASS. Finding 1: CLOSED. Finding 2: CLOSED. READY_FOR_FOCUSED_G4: YES.** Đây là kết quả implementer sửa/test local, **chưa phải independent G4 PASS**. Independent G4 trước đó BLOCK theo bằng chứng owner cung cấp. Không commit/push/PR/merge/deploy.

**FR-CAT-08: OPEN — DEFER_ACCEPTED cho picker, chưa nghiệm thu đầy đủ.** Owner đã chấp nhận phạm vi từng phần trong thông điệp “OWNER DECISION — P1-11” của phiên này. Không còn là DEFER_PROPOSED; approval này không miễn sửa hai lỗi P1 hoặc thay thế G4.

## Scope, nguồn và baseline

Task duy nhất P1-11, `docs/spec/07-roadmap.md:60`; FR-CAT-01/02/03/08 (`01-requirements.md:64–72`), BR-CAT-01/08/09, NFR-07/09/10/11; DEC-14/15/16. Contract concurrency/409/permission ở `05-api.md:10,26,103` §1/4.2. Không có AC riêng AC-P1-11; không tạo ID/roadmap mới. Giữ nguyên traceability và G1/G2 ở [hồ sơ G3](p1-11-implementation.md).

Worktree `C:/Users/ADMIN/source/tra-hien`, branch `feature/p1-11-admin-catalog`. Expected base = HEAD = fetched origin/main: `bb9a901044d41a0783d226fcd678e3865390fa30`. Working tree có đúng local G3 đã giao; giữ các thay đổi đó. `git fetch origin --prune`: exit 0. Spec không đổi so HEAD và revision `81afe9e3e837f7f5df347368bffb092bc978f5b8` (git diff exit 0); tái dùng G1 TARGETED/G2 scoped, không full audit/planning lại. Owner giao focused fix riêng sau G4; không mở scope backend/P1-05/P1-12.

## Finding 1 — CLOSED theo regression local

Trước sửa, CatalogForm/ProductOrder và publish kiểm mọi HTTP 409 như stale version. POST material bị slug_exists khóa Save, hiện reload dù id=new. Kiểm contract thực từ `Application/Catalog/CatalogHandlers.cs`, `CatalogRules.cs` và `Infrastructure/Catalog/EfCatalogReference.cs` (chỉ đọc).

- Helper `isConcurrencyConflict` chỉ trả true khi ApiError có status 409 **và** code concurrency_conflict. Không phân loại theo status đơn thuần, không parse token.
- slug_exists, sku_code_exists, product_type_code_exists, attribute_code_exists, option_code_exists có thông báo/field error phù hợp, cho sửa và submit lại thủ công. Các code structural/history/allocation hiện có có thông báo riêng. 409 chưa biết là lỗi recoverable, giữ token cũ và values, không tự lấy token mới hoặc replay mutation.
- Form mới không nhận callback reload; taxonomy reload còn guard id=new trước mọi GET. Form record hiện có stale giữ draft/khóa Save; chỉ explicit reload có xác nhận discard mới reset bằng **giá trị actual response**. Hủy xác nhận không GET/không đổi values. Reload lỗi có data cũ không unmount form.
- Bỏ rowVersion React key ở cả reference/taxonomy form; publication cập nhật riêng trạng thái sau thành công. CSRF/provider/session behavior giữ nguyên.

## Finding 2 — CLOSED theo delayed-network regression local

Trước sửa, PUT collection gọi reload parent rồi đổi rowVersion key, làm mất metadata vừa nhập lúc PUT chờ. Bản sửa:

- Metadata dirty → giữ controls reorder nhưng disabled, giải thích “Hãy lưu hoặc hủy thay đổi thông tin trước khi sắp xếp sản phẩm.” Không PUT ngầm.
- Trong PUT, khóa metadata fieldset/Save/Cancel và hiện giải thích chờ; ref chống submit trùng cùng pending state. Metadata submit đang chạy hoặc stale conflict cũng chặn reorder.
- PUT thành công trả CollectionProductList; cập nhật relation state/cache và **chỉ rowVersion** của parent cache. Không GET parent tự động, không key/remount/reset metadata; lần PATCH tiếp theo gửi token mới và giữ values.
- Chỉ dùng token quan hệ để reorder khi nó bằng snapshot parent. Relation mới hơn snapshot metadata → chặn và yêu cầu explicit refresh, tránh nhận token mới rồi gửi metadata cũ đè thay đổi người khác.
- PUT thất bại giữ thứ tự dự kiến và metadata. concurrency_conflict khóa reorder đến explicit refresh; lỗi khác không giả là stale. Refresh có xác nhận bỏ cả metadata/thứ tự chưa lưu; reset chỉ xảy ra khi người dùng đã chọn bỏ. Hai lần đọc có version khác nhau vẫn bị chặn, không silent rebase.

## Quyết định owner về FR-CAT-08 và follow-up bắt buộc

**ĐÃ DUYỆT HOÃN PICKER / CHƯA NGHIỆM THU ĐỦ FR-CAT-08.** Owner chấp nhận P1-11 từng phần, hoãn chọn thêm sản phẩm thủ công trong Collection đến khi P1-05 có Product discovery API ổn định. Không sửa authoritative spec; lưu nguyên nghĩa requirement và ghi thiếu capability.

Hiện hỗ trợ list/create/edit metadata collection, hidden/visible/slug/order/SEO; GET quan hệ sản phẩm đã lưu và PUT sắp xếp các product IDs thực từ response, concurrency/permission/CSRF theo API hiện có. **Chưa có** tìm/chọn/thêm sản phẩm. Base có GET/PUT collections/{id}/products, nhưng generated client và backend merged chưa có GET admin/products cho discovery (spec §4.2 mô tả endpoint tương lai P1-05). Không copy code chưa merge, endpoint giả, hardcoded UUID list hoặc mock data production.

UI đã kiểm: không có nút/select tìm/thêm product giả. “Thêm mới” ở đầu panel tạo collection metadata; trong vùng “Sản phẩm trong bộ sưu tập” chỉ có Lên/Xuống/Lưu thứ tự. Note ghi rõ chọn thêm đang tạm hoãn. Empty state chỉ nói collection chưa có sản phẩm và picker tạm hoãn; không khẳng định toàn catalog rỗng từ relation list rỗng.

**Follow-up trong phạm vi P1-11 sau P1-05**, không tạo task/roadmap mới: xác minh P1-05 đã merge với contract discovery ổn định; dùng client được sinh qua workflow chuẩn từ OpenAPI backend đã merge; thêm picker đọc candidates và ghi quan hệ qua PUT hiện hữu. Không lấn P1-12 product form. Bắt buộc kiểm:

1. Discovery thật: search/phân trang, loading/error/empty, hiển thị đúng tên/ID, không suy toàn catalog rỗng từ một collection rỗng.
2. Chọn/thêm và sắp xếp IDs tồn tại; kiểm duplicate/missing/unavailable records theo contract P1-05 đã merge; reload giữ đúng relations/order, không tạo product hoặc sửa SKU/stock/pricing.
3. catalog.read/write và thiếu quyền; CSRF; 400/401/403/404/409; stale version, đồng thời metadata/reorder, double-submit; giữ dữ liệu chưa lưu/không silent overwrite.
4. Keyboard/labels/mobile và IAM/navigation regression; generated client typecheck; E2E với backend cô lập có dữ liệu giả. Chỉ sau evidence và owner nghiệm thu riêng mới đóng phần thiếu FR-CAT-08.

## Test evidence thực chạy

Lệnh từ root; không test production. Red/green: `corepack pnpm --filter @teapremium/admin exec playwright test --grep 'G4'` trước sửa **exit 1, 10 failed/0 passed** (known-code feedback/recovery, dirty block explanation, metadata còn enabled trong PUT chậm, mismatch guard thiếu). Sau sửa **exit 0, 10/10 PASS**. Thêm 3 focused cases vào full suite sau đó.

| Lệnh | Kết quả cuối / exit |
|---|---|
| `corepack pnpm --filter @teapremium/admin test:unit` | **30/30 PASS**, skipped 0; exit 0. 8 ca mới + 22 ca G3 |
| `corepack pnpm --filter @teapremium/admin test:browser` | **58/58 PASS**, 35.4s; skipped 0; exit 0. 13 ca mới + 45 ca G3, gồm 16 IAM/shell hiện có |
| `corepack pnpm --filter @teapremium/admin lint` | PASS, zero warnings; exit 0 |
| `corepack pnpm --filter @teapremium/admin typecheck` | PASS; exit 0 |
| `corepack pnpm --filter @teapremium/admin build` | PASS Next 16.4.6/TypeScript/routes; exit 0 |
| `corepack pnpm --filter @teapremium/api-client typecheck` | PASS; exit 0 |
| `git diff --check`; `git diff --cached --check` | PASS; exit 0; staged rỗng |

Một lượt typecheck/build thật FAIL ở test do helper có type predicate quá rộng (false branch thu hẹp ApiError thành never). Đã đổi annotation sang boolean, không đổi logic runtime; typecheck/build/unit chạy lại PASS ở bảng trên. Không bỏ test hoặc nhận lượt FAIL là PASS.

8 unit cases mới: status+code classification; sáu uniqueness/allocation codes gắn đúng field và chỉ ghi lại khi gọi thủ công; structural/history errors không stale. 13 browser cases mới: bốn 409 create (slug/SKU/type/unknown); unknown 409 edit giữ version; metadata dirty chặn reorder; ba PUT giữ response bằng Promise gate (success/403/stale), thử nhập bàn phím khi khóa, không double PUT/PATCH ngầm, input DOM marker không bị remount, success PATCH tiếp theo đúng token; mismatch snapshot; attribute/option uniqueness (2); stale PATCH thật trong fixture rồi dismiss/accept refresh, actual values/version và input identity. Fixture kiểm version của PUT/PATCH, GET /new trả 404, không permissive fallback che lỗi.

**NOT RUN:** real backend/PG17/TLS/R2 E2E, production, CI local delta, independent focused G4. Browser chạy Chromium thật với API mocked; không suy ra production concurrency/sanitizer/DB/authorization PASS. Giới hạn editor văn bản theo đoạn và routing draft persistence của G3 vẫn còn; không mở rộng trong focused fix.

## File inventory, rollback và handoff

Trong lượt focused này chỉ sửa: `src/lib/api.ts`; `src/components/catalog/catalog-form.tsx`, `collection-products.tsx`, `reference-panel.tsx`, `taxonomy-panel.tsx`; `tests/catalog.test.tsx`, `tests/browser/catalog.spec.ts` (tất cả dưới apps/admin); cùng `docs/plans/p1-11-implementation.md` và record này. next-env.d.ts do build sinh đã khôi phục đúng baseline; không sửa dependency/lockfile/backend/store/generated client/spec/infra/CI.

Toàn change set local so origin/main, **gồm untracked content đã kiểm inventory**:

```text
 M apps/admin/src/app/globals.css
 M apps/admin/src/components/admin-screen.tsx
 M apps/admin/src/components/ui.tsx
 M apps/admin/src/lib/api.ts
?? apps/admin/src/app/catalog/[resource]/page.tsx
?? apps/admin/src/components/catalog/catalog-form.tsx
?? apps/admin/src/components/catalog/catalog-screen.tsx
?? apps/admin/src/components/catalog/collection-products.tsx
?? apps/admin/src/components/catalog/media-picker.tsx
?? apps/admin/src/components/catalog/reference-panel.tsx
?? apps/admin/src/components/catalog/taxonomy-panel.tsx
?? apps/admin/src/lib/catalog-api.ts
?? apps/admin/src/lib/catalog-forms.ts
?? apps/admin/tests/browser/catalog.spec.ts
?? apps/admin/tests/catalog.test.tsx
?? docs/plans/p1-11-implementation.md
?? docs/plans/p1-11-focused-g4-fix.md
```

Đây là `git status --short --untracked-files=all`; staged và committed delta rỗng. `git diff origin/main` không bao gồm untracked; reviewer phải mở các file liệt kê trên. Branch/base/HEAD không đổi. Không có migration. Rollback chỉ source delta focused sau khi lưu bản cần giữ; không reset/clean working tree, không đảo DB/public history. Những request thành công đã gửi backend không bị đảo bằng rollback UI.

Next safe step: focused G4 độc lập trên cùng worktree, kiểm hai finding + partial acceptance đã được owner duyệt; **READY_FOR_FOCUSED_G4: YES**. Không tự phê duyệt G4, không publish.
