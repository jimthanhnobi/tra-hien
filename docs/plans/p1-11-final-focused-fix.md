# P1-11 — Sửa ba finding sau G4 tích hợp

**FOCUSED_FIX: PASS. F1/P1, F2/P2, F3/P2: CLOSED local. READY_FOR_FOCUSED_G4: YES.** FR-CAT-08 ở trạng thái IMPLEMENTED_LOCAL. Kết luận này là bàn giao để re-review độc lập.

Task P1-11 (roadmap dòng 60), FR-CAT-08 (requirements dòng 72), BR-CAT-08/10, NFR-07/09/10/11, DEC-14/15/16 và AC-P1-07 ở phần tìm tên/SKU. Giữ G1/G2 trong [hồ sơ tích hợp](p1-11-integration-evidence.md) tại base đã review `f39a78a11f9ad5c954e43aa430bf08de3882f504`. Dependencies P1-03/04 đã merge, discovery do P1-05 cung cấp. Hai finding gốc đã được reviewer đóng theo thông tin owner cung cấp. Regression local vẫn PASS cho phân loại 409, chống replay, giữ input và cập nhật version sau PUT.

- F1. Khi mở picker hoặc có draft quan hệ, khóa fieldset metadata và guard các handler ghi. Metadata bẩn hoặc PATCH đang xử lý khóa thao tác quan hệ. Lưu thành công giữ Collection editor, cập nhật rowVersion từ response và xóa draft vừa lưu. PATCH chỉ cập nhật token relation cache nếu snapshot khớp version trước PATCH. Lỗi giữ draft để thử lại hoặc hủy. Stale conflict yêu cầu tải lại có xác nhận, không tự ghi lại.
- F2. Checkbox đọc tên và SKU. Nếu thiếu SKU, dùng mã sản phẩm. Browser kiểm hai sản phẩm trùng tên và xác nhận phím Space chọn đúng ID.
- F3. Adapter chiếu cả search lẫn detail về id, tên, SKU, trạng thái và URL/alt của ảnh bìa trước khi đưa vào QueryClient. Search giữ metadata phân trang. Test dùng query options của UI, kiểm entry search, detail được điền từ search và detail tải độc lập. Payload HTTP P1-05 vẫn trả DTO đầy đủ.

| Lệnh | Kết quả |
|---|---|
| `corepack pnpm --filter @teapremium/admin test:unit` | Exit 0, 33/33 |
| `corepack pnpm --filter @teapremium/admin test:browser` | Exit 0, 73/73 API mock |
| `corepack pnpm --filter @teapremium/admin lint` | Exit 0 |
| `corepack pnpm --filter @teapremium/admin typecheck` | Exit 0 |
| `corepack pnpm --filter @teapremium/admin build` | Exit 0 |
| `corepack pnpm --filter @teapremium/api-client typecheck` | Exit 0 |
| `corepack pnpm --filter @teapremium/admin exec playwright test --config playwright.catalog-live.config.ts` | Exit 0, 1/1 PG17/HTTPS/IAM thật |
| `git diff --check` và `git diff --cached --check` | Exit 0, loại trừ trailing whitespace của untracked bằng kiểm manifest riêng |

Giữ bộ regression trước đó, cập nhật assertion cho hành vi khóa metadata mới và nhãn SKU. Lượt mock đầu exit 1, 72/73 do locator tìm nhãn nút khi nút đã hiện “Đang lưu…”. Dùng input giữ nguyên DOM để thử gửi form lần nữa khi PATCH đang chờ. Lượt cuối PASS cả ca này. Test backend thật tái dùng container giả `teapremium-p111-picker-pg17` và harness đã duyệt, kiểm hủy draft, PATCH rồi PUT bằng version mới, reload/persist, stale 409, CSRF và quyền sales. API/Admin/Caddy/PostgreSQL riêng đã dừng sau chạy. R2 thật, full backend suite và CI cho delta local chưa chạy.

Branch `feature/p1-11-admin-catalog` vẫn ở reviewed base. `git ls-remote origin refs/heads/main` xác nhận `93630999a537662195a75c4837433ee76fd67e6f`. Diff thực gồm `.github/workflows/ci.yml`, `docs/spec/06-infrastructure.md`, `scripts/ci-fastpath.py`, `scripts/test-ci-fastpath.py`. Thay đổi là phân loại phạm vi CI và workflow chạy tay full, không chạm Admin/backend/client. Worktree giữ bốn file theo reviewed base. Đối chiếu lại đầu remote trong gate tiếp theo. Theo bộ phân loại mới, full P1-11 có file cấu hình Playwright ngoài src/tests/public nên dự kiến full CI.

Tám file thay đổi từ snapshot G4 và một hồ sơ mới:

```text
apps/admin/src/components/catalog/catalog-form.tsx
apps/admin/src/components/catalog/collection-products.tsx
apps/admin/src/components/catalog/product-picker.tsx
apps/admin/src/components/catalog/taxonomy-panel.tsx
apps/admin/src/lib/catalog-api.ts
apps/admin/tests/browser/catalog.spec.ts
apps/admin/tests/catalog-live/collection.spec.ts
apps/admin/tests/catalog.test.tsx
docs/plans/p1-11-final-focused-fix.md
```

Inventory đầy đủ so reviewed base gồm 22 đường dẫn (4 tracked modified, 18 untracked), staged rỗng. Manifest `before.json` và `after.json` liệt kê từng đường dẫn, SHA-256 và base tại `C:/Users/ADMIN/source/tra-hien-local-recovery/p1-11-final-focused/`. Checkpoint trước sửa khớp 21/21 SHA-256 với snapshot bàn giao tích hợp. Giữ stash và snapshot cũ. Rollback focused bằng bản sao `before/` cho tám file sửa và xử lý riêng hồ sơ mới. Không có migration. Dừng ở local G3, chờ focused G4 độc lập.
