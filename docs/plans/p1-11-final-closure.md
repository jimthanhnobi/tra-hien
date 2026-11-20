# P1-11 — Bàn giao sửa regression F2

20/11/2026 (Asia/Saigon). **P2 CLOSED_LOCAL · BASE_INTEGRATION PASS · FR-CAT-08 ACCEPTED_LOCAL · READY_FOR_FOCUSED_G4 YES.** G4 trước đó vẫn là CONDITIONAL, chờ reviewer độc lập kiểm tra bản sửa trên cùng worktree.

## Phạm vi và base

- Worktree `D:/tra-hien-p1-07`, nhánh `feature/p1-11-admin-catalog`. Local HEAD và remote checkpoint cùng là `e80811b986d6382aeed76084bbf9e298d38141b2`. Parent, merge base và `origin/main` sau fetch đều là `93630999a537662195a75c4837433ee76fd67e6f`. Worktree ban đầu sạch. Manifest ban đầu khớp 22/22 SHA-256 của blob, gồm bốn tệp chuẩn hóa EOL, và 23/23 ID blob Git khi tính cả handoff.
- `origin/main` đã là tổ tiên của HEAD. CI push-main tại đúng SHA trên hoàn tất **4/4 PASS**. [GitHub Actions 44711614919](https://github.com/jimthanhnobi/tra-hien/actions/runs/44711614919)
- P1-07 vẫn ở PR mở, head `ed49d11d945ec27033489d4b834dd49b8c9bb6ed`, CI **4/4 PASS**. Đối chiếu OpenAPI với PR cho thấy 6 thao tác đọc sản phẩm, đọc/sửa collection và đọc/lưu quan hệ cùng 13 component tham chiếu đều khớp. `POST /api/v1/admin/products/import` có trong OpenAPI của PR và sẽ vào base khi PR được merge. Nhánh P1-11 dùng backend và generated client của `origin/main` hiện tại, nên lượt này dùng trực tiếp base sẵn có. [PR #33](https://github.com/jimthanhnobi/tra-hien/pull/33) · [GitHub Actions 44764028356](https://github.com/jimthanhnobi/tra-hien/actions/runs/44764028356)
- G1/G2 theo hồ sơ tích hợp và full audit `cadce575597738bf40ad7c45689482de5a5fbc3d`. Từ base trong hồ sơ đó đến base hiện tại, diff spec chỉ ở §7 CI/CD của `06-infrastructure.md`. P1-03/04 cung cấp catalog và P1-05 cung cấp discovery. [Hồ sơ tích hợp](p1-11-integration-evidence.md) · [P1-11 và AC-P1-07](../spec/07-roadmap.md) · [FR-CAT-08 và NFR-07/09/10/11](../spec/01-requirements.md) · [BR-CAT-08/10](../spec/04-business-rules.md) · [Admin API §4.2](../spec/05-api.md) · [DEC-14/15/16](../spec/00-README.md) · [ADR P1-04](../decisions/ADR-P1-04-publication-history-and-publish.md)

## F2 và kiểm chứng

[F2](../../apps/admin/tests/browser/catalog.spec.ts#L526) trước sửa gửi Enter từ trường Tên bị khóa. Focus đã về nút Thêm sản phẩm, nên picker mở lại và chặn thao tác lưu. Bản sửa đợi picker đóng, kiểm trường Tên còn khóa và giữ giá trị, rồi xác nhận nút Lưu danh sách sản phẩm khả dụng và có focus trước khi nhấn Enter. Test kiểm checkbox SKU thứ nhất bỏ chọn, SKU thứ hai được chọn, đúng một PUT chứa ID của SKU thứ hai, không có PATCH và metadata mở khóa sau khi lưu. Các ca lỗi, chống gửi lặp và quyền tiếp tục nằm trong bộ browser đầy đủ.

| Lệnh | Kết quả |
| --- | --- |
| `corepack pnpm --filter @teapremium/admin exec playwright test --grep 'F2 identical product names'` trên checkpoint | FAIL 0/1, exit 1, timeout ở thao tác lưu sau khi picker mở lại |
| `corepack pnpm --filter @teapremium/admin exec playwright test --grep 'F2 identical product names' --repeat-each 5` | PASS 5/5, exit 0 |
| `corepack pnpm --filter @teapremium/admin test:unit` | PASS 33/33, exit 0, 0 failed, 0 skipped |
| `corepack pnpm --filter @teapremium/admin test:browser` | PASS 73/73, exit 0, 0 failed, 0 skipped |
| `corepack pnpm --filter @teapremium/admin lint` | PASS, exit 0 |
| `corepack pnpm --filter @teapremium/admin typecheck` | PASS, exit 0 |
| `corepack pnpm --filter @teapremium/admin build` | PASS, exit 0 |
| `corepack pnpm --filter @teapremium/api-client typecheck` | PASS, exit 0 |
| `git diff --check`, `git diff --cached --check`, `git diff --check origin/main` | PASS, mỗi lệnh exit 0 |

Dependency và Chromium đã có sẵn. Sau build, khôi phục riêng `apps/admin/next-env.d.ts` do Next.js đổi đường dẫn type sinh tự động.

[G4 độc lập trước đó](p1-11-wip-handoff.md) đã đóng cả năm finding implementation, ghi nhận FR-CAT-08 ACCEPTED_LOCAL và E2E thực **1/1 PASS** trên PG17/HTTPS/IAM (search tên/SKU, thêm/bỏ/đổi thứ tự, dữ liệu sau reload, stale PUT, CSRF và quyền sales). Snapshot đó có browser 72/73 do F2. Tái sử dụng bằng chứng E2E vì bản sửa chỉ thay ca browser và backend/API tại base khớp checkpoint. R2 thật và upload/hiển thị ảnh bìa qua R2 vẫn chưa kiểm. Full backend local và CI cho delta P1-11 chưa chạy.

## Inventory và rollback

So với `origin/main` có 24 đường dẫn, gồm 23 tracked và hồ sơ này untracked. Staged rỗng. So với checkpoint, có một test unstaged và hồ sơ này. Bảng dùng ID từ `git hash-object -- <path>`, có áp dụng quy tắc EOL của Git. 22/23 ID tracked khớp checkpoint, chỉ ID test F2 thay đổi. Dòng cuối nhận diện tệp manifest hiện tại.

```text
A  apps/admin/playwright.catalog-live.config.ts                29cb53afe95478feb98413217e510e6aff864527
A  apps/admin/src/app/catalog/[resource]/page.tsx                ace20cdab7215f408cc2ff4119eec6fa64f93581
M  apps/admin/src/app/globals.css                               c8a63a30594625e8c6fac5c8e6fb4a91d241be37
M  apps/admin/src/components/admin-screen.tsx                   02fbdf443cf112bcca328a0e32d6288524b866e9
A  apps/admin/src/components/catalog/catalog-form.tsx           bbccf27389e7d4521d4ded022fa6a61bf39c4e66
A  apps/admin/src/components/catalog/catalog-screen.tsx         68afd613a8061db8d1127dbc899b76d7cae63e7c
A  apps/admin/src/components/catalog/collection-products.tsx    bcb8a56b26188e97743da3837b32ebac3d0557e5
A  apps/admin/src/components/catalog/media-picker.tsx            0db7ea4716a6a7cdb466a4d6362f0d64bc13c6dc
A  apps/admin/src/components/catalog/product-picker.tsx          97d27db809045b3dfa98da85c10eaab2cd50cd6d
A  apps/admin/src/components/catalog/reference-panel.tsx         9abf46280778945d1964a807876ac914dea26c89
A  apps/admin/src/components/catalog/taxonomy-panel.tsx          4311269551a3ef3a319e58d28d1c47921cfc8483
M  apps/admin/src/components/ui.tsx                             0ff4ace541fcf67aede2b6055eeda5a87843c58c
M  apps/admin/src/lib/api.ts                                     230279132dae49f74a095791aa9f1139d8e76121
A  apps/admin/src/lib/catalog-api.ts                             e9072c7a4c4eeebb7e12f0f63a557a5628304aa1
A  apps/admin/src/lib/catalog-forms.ts                           00cac4d00102ee46d53383cdead6d40d5cf2fb1d
A  apps/admin/tests/browser/catalog.spec.ts                     8968aed72710d90bcc8a11f7a3fb74bb19c43989
A  apps/admin/tests/catalog-live/collection.spec.ts             d0aba1fa317a3b660a0b28b74615e4011538611e
A  apps/admin/tests/catalog.test.tsx                            e8ae73afd61ec60bc4433994dba4ec02edb74d95
A  docs/plans/p1-11-final-focused-fix.md                        4611016510c826009615020e375d5a1a644ab642
A  docs/plans/p1-11-focused-g4-fix.md                            77604803e92cdd0617cc6d783a9f4d2c372c457f
A  docs/plans/p1-11-implementation.md                          a5629fbbdbc89ed16de89bedd2ffe494e1abdce4
A  docs/plans/p1-11-integration-evidence.md                     840808d4244a8eca21ff42d20445f50259f28d88
A  docs/plans/p1-11-wip-handoff.md                              af634ab0a69f9f2b498454bc5ccf7741674ecee5
?? docs/plans/p1-11-final-closure.md                             (manifest)
```

Migration không áp dụng. Nếu cần hoàn tác bản sửa focused, lấy lại riêng test từ checkpoint và lưu hồ sơ bàn giao ngoài change set. Reviewer G4 dùng toàn bộ diff so với `origin/main`, gồm cả nội dung hồ sơ untracked. Publish và merge vẫn chờ owner.
