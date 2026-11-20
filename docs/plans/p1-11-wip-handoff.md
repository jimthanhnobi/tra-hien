# P1-11 WIP checkpoint · Admin Catalog

Ngày 20/11/2026 (Asia/Saigon). Nhánh `feature/p1-11-admin-catalog`, repository `jimthanhnobi/tra-hien`. Parent và base G4 là `93630999a537662195a75c4837433ee76fd67e6f`. Owner cho phép commit và push checkpoint này để tiếp tục trên máy khác. Quyết định publish cuối và merge cần owner phê duyệt riêng.

**G4: CONDITIONAL. READY_FOR_OWNER_PUBLISH: NO. READY_FOR_OWNER_MERGE: NO. FR-CAT-08: ACCEPTED_LOCAL.**

## Kết quả G4 đã chốt

- **F1/P1 CLOSED.** Đã tái hiện độc lập lỗi hai draft cùng khóa nút Lưu trên snapshot trước sửa. Bản hiện tại loại trừ chỉnh metadata và quan hệ đồng thời, giữ đường lưu/hủy draft và giữ draft khi lỗi. Stale yêu cầu tải lại có xác nhận hoặc hủy. Đã kiểm pending, `rowVersion` hai chiều PATCH → PUT và PUT → PATCH, giữ dữ liệu và chống gửi lặp.
- **F2/P2 CLOSED về hành vi UI.** Checkbox của hai sản phẩm cùng tên có tên truy cập riêng theo SKU. Probe bàn phím độc lập chọn SKU thứ hai bằng Space và xác nhận PUT chứa đúng ID. Ca regression browser vẫn mở ở mục dưới.
- **F3/P2 CLOSED.** Search và detail được chiếu trước khi vào `QueryClient`. Đã kiểm entry search, detail điền từ search và detail tải riêng. Cache không giữ `priceVnd` hoặc `lowStockThreshold`.
- **Finding gốc 1 CLOSED.** Chỉ `409 concurrency_conflict` ở record hiện có là stale. Lỗi trùng mã/slug giữ input, cho sửa và gửi lại thủ công, không GET `/new`.
- **Finding gốc 2 CLOSED.** PUT chậm không làm mất metadata hay remount form, chặn submit trùng. PATCH sau PUT dùng token mới.
- **FR-CAT-08 ACCEPTED_LOCAL.** Picker tìm theo tên và SKU, thêm/bỏ/đổi thứ tự và lưu. E2E thật xác nhận dữ liệu sau reload, sản phẩm bị bỏ khỏi bộ sưu tập vẫn tồn tại và PUT stale không ghi đè.

## P2 còn mở

[apps/admin/tests/browser/catalog.spec.ts, dòng 542](../../apps/admin/tests/browser/catalog.spec.ts#L542). Ca `F2 identical product names have distinct checkbox names and keyboard selects the intended SKU` mở picker, focus checkbox của SKU thứ hai, nhấn Space rồi bấm **Thêm 1 sản phẩm đã chọn**. Picker đóng và focus trở về nút **Thêm sản phẩm**. Ô `Tên` đang disabled nên `name.press("Enter")` gửi Enter tới nút đang có focus, mở lại picker. Locator nút **Lưu danh sách sản phẩm** ở dòng 544 đợi phía sau dialog rồi timeout sau 45 giây. Lượt đầy đủ gặp lỗi này và cả ba lượt chạy riêng đều tái hiện.

```powershell
corepack pnpm --filter @teapremium/admin exec playwright test --grep 'F2 identical product names' --repeat-each 3
```

## Kết quả kiểm thử độc lập của snapshot G4

| Kiểm tra | Kết quả |
| --- | --- |
| `corepack pnpm --filter @teapremium/admin test:unit` | **PASS 33/33**, exit 0 |
| `corepack pnpm --filter @teapremium/admin test:browser` | **FAIL 72/73**, exit 1. Ca F2 ở trên. |
| Playwright F2 `--repeat-each 3` | **FAIL 0/3**, exit 1. Cùng timeout. |
| Probe độc lập | Tái hiện F1 trên snapshot cũ. Hai probe F1 và F2 trên snapshot hiện tại **PASS 2/2**. |
| `corepack pnpm --filter @teapremium/admin lint` | **PASS**, exit 0 |
| `corepack pnpm --filter @teapremium/admin typecheck` | **PASS**, exit 0 |
| `corepack pnpm --filter @teapremium/admin build` | **PASS**, exit 0 trên bản sao đã đối chiếu hash |
| `corepack pnpm --filter @teapremium/api-client typecheck` | **PASS**, exit 0 |
| `git diff --check`, `git diff --cached --check`, `git diff --check origin/main` | **PASS**, từng lệnh exit 0 |
| `corepack pnpm --filter @teapremium/admin exec playwright test --config playwright.catalog-live.config.ts` | **PASS 1/1**, exit 0. PG 17.12, HTTPS tin cậy, IAM Production, CSRF và quyền sales. |
| CI trên main tại SHA base | **PASS 4/4**. [GitHub Actions](https://github.com/jimthanhnobi/tra-hien/actions/runs/44711614919) |
| R2 E2E thật, full backend suite local, CI cho delta P1-11 | **NOT RUN** |

Kết quả trên đến từ lượt G4 độc lập. Checkpoint chỉ xác minh lại inventory, SHA-256 và Git diff.

## Tiếp tục trên máy khác

1. Fetch `origin`, checkout `feature/p1-11-admin-catalog` từ remote hoặc cập nhật fast-forward. Xác minh parent và 22 SHA-256 của blob theo bảng dưới. Cài dependency theo lockfile với `corepack pnpm install --frozen-lockfile` và chuẩn bị Chromium của Playwright nếu thiếu.
2. Sửa riêng ca regression. Chờ picker đóng rồi dùng bàn phím lưu danh sách và giữ assertion PUT đúng ID, không PATCH. Nếu cần kiểm guard submit metadata, dùng `form.requestSubmit()` trong ca F1 để đích submit xác định.
3. Chạy ca F2 lặp lại, sau đó chạy toàn bộ Admin browser và xác nhận **73/73 PASS**. Chạy các kiểm tra Admin phù hợp với phần thay đổi. Ghi lại kết quả thực chạy và yêu cầu G4 focused độc lập trên snapshot đã sửa.
4. Sau G4 PASS, xin owner duyệt publish riêng, kiểm CI tại đúng published HEAD và chờ quyết định merge của owner.

## Manifest đã review

22 tệp gồm 4 tracked modified và 18 untracked lúc G4. Cột cuối ghi SHA-256 của blob Git khi khác hash worktree do quy tắc `eol=lf`. Dấu `—` nghĩa là blob có cùng hash worktree. Cả 22 nội dung được giữ nguyên, với CRLF chuyển sang LF trong Git cho bốn tệp. Tệp handoff là tệp thứ 23 của checkpoint.

SHA-256 của manifest G4 sau đồng bộ base là `209b6a4d0e1023d3055ca30033fe7a14bbc97ac251cd21f4b8ef7ee4937ffea0`.

| Tệp | SHA-256 worktree G4 | SHA-256 blob nếu khác |
| --- | --- | --- |
| `apps/admin/playwright.catalog-live.config.ts` | `695cb4647db51b919e73fe4931024d71ad0f50a938fa702009c74a63f672868c` | — |
| `apps/admin/src/app/catalog/[resource]/page.tsx` | `194420a6cd278cb1292e543fa726a089d2924450217dcc03f61b1c0e601104e8` | — |
| `apps/admin/src/app/globals.css` | `cba27ac54fb173e64cdde8c1c193811de70fadc104854a7c57e2c0934068fa1d` | — |
| `apps/admin/src/components/admin-screen.tsx` | `595b1a5b333b41dae4834e5728d0f43ff78a1eb47f4e1494508ae7b983adf3c0` | — |
| `apps/admin/src/components/catalog/catalog-form.tsx` | `fec4f76f1be38f069937f754710caefe07c297d449d2e31b5990243ae91e5087` | — |
| `apps/admin/src/components/catalog/catalog-screen.tsx` | `ad743cabd474ed0eb5baf99d2c93e4f2bbe6b600199102efeaee58457917d7fc` | — |
| `apps/admin/src/components/catalog/collection-products.tsx` | `08127b0f5b1f1c618ac84c8b33c33c38b42c5ccd60adc9b62b7e31a4dacc95ae` | `3012bd843a409885b33ec5c50e8a3774e9858994b4d43e72787a8e20e23ba63f` |
| `apps/admin/src/components/catalog/media-picker.tsx` | `7e8079bb3acced785f6d53bc269d2100db9c2b2d24f3ceff9b4b32c2407639e1` | — |
| `apps/admin/src/components/catalog/product-picker.tsx` | `fca96e50001c6c326a67b1ebfb30ebfba588143d6bf76a3f085a44b02ffb8fad` | — |
| `apps/admin/src/components/catalog/reference-panel.tsx` | `a95d01bfbf1c2ccc7e1c40af89719d18a8f4337e3887c9365efe7b9f64679b8d` | — |
| `apps/admin/src/components/catalog/taxonomy-panel.tsx` | `51b510e341632a07d49a44594d96ee85a37a9b6e982ef1c63aa7de42289799ff` | `73521cba5088c34886aec5554220310b28c4854bdd8c6477a0f848bf77ed66d6` |
| `apps/admin/src/components/ui.tsx` | `8d88b69105d5446d6ac8461d686abdb054938d154661051e3ac7dbf5233c9202` | — |
| `apps/admin/src/lib/api.ts` | `6490ad36e704d95904f6f1aa65e5a7c002b0d0a3d1181c5c74655d26bedf452f` | — |
| `apps/admin/src/lib/catalog-api.ts` | `1f78d7e43c519e2687a2648f7126834e09d652bceb2834a8d035df4e6fbd4b7c` | — |
| `apps/admin/src/lib/catalog-forms.ts` | `f8fab08edaa506132d67b0eba951ee1ddc34c047cc0f5a521343cf925e1a8e79` | — |
| `apps/admin/tests/browser/catalog.spec.ts` | `7219fd067732dacd824718b3392f1064f1cd92acd994ee6fd2197fec90511d0c` | `3d6d615cb62ca958c0d78e6c3ecb333268c7a5e57ccc5be248eff6ccde75bab6` |
| `apps/admin/tests/catalog-live/collection.spec.ts` | `28e5930d277c45625b39c6c46fe0e805e07e474bd2f2c12fe539c740c6e9deba` | `212c76eac750d0b66608c2b415ed1b3353bfe59013210644b859e213ee79c6d9` |
| `apps/admin/tests/catalog.test.tsx` | `8a4e49ec76bf07fe09052e05dcb37444120dd12f64af7c4baa60e4c65c09cbac` | — |
| `docs/plans/p1-11-final-focused-fix.md` | `05cb42790e47104841ec5132441c2dcd5118c2540630f96ed65bc9b0d17ebd5e` | — |
| `docs/plans/p1-11-focused-g4-fix.md` | `43ccdf07544e2911a71c0107b184f5f2db09e683c4eb8865f11b76576416f122` | — |
| `docs/plans/p1-11-implementation.md` | `efe1c6b3c8b62ae7cec22c64a1237d8f27c4602c5eb93b87354d86a9178c91c1` | — |
| `docs/plans/p1-11-integration-evidence.md` | `35019a97b733784039f5a3a42076a380529b1f37b67862703b4f031d02028dcd` | — |
