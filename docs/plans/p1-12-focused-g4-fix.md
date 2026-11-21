# P1-12 — Sửa tập trung sau G4 Tier A

## Vòng 2 · Tích hợp P1-06 và sửa F1-A/F2

21/11/2026 · **BASE_INTEGRATION PASS · CONTENT_PRESERVATION PASS.** Worktree `D:/tra-hien-p1-12`, nhánh `feature/p1-12-admin-products`. Base cũ `c88069d58241fd51f5f6a76da2276c41f7a3830a`. HEAD và `origin/main` sau fast-forward cùng là `99c6810b54d151d6a30334c9384524e3ba2b3552`. Main CI có 4/4 job thành công và log checkout khớp chính xác SHA. Spec tree giữ `8e54535ff1f3f3ae9a7e1f00db1001857f3d3395`. [Main CI 44877053827](https://github.com/jimthanhnobi/tra-hien/actions/runs/44877053827)

- Snapshot vòng 1 có 32 path, 31/31 hash trong manifest và 0 staged. Incoming có 21 path, không giao với local. Backup đủ 32/32 hash tại `D:/tra-hien/.git/worktrees/tra-hien-p1-12/p1-12-round2-backup-20261121-071548/`. `git merge --ff-only 99c6810b54d151d6a30334c9384524e3ba2b3552` thành công trực tiếp. Sau tích hợp, trước sửa, 32/32 tệp local còn nguyên byte. Phần thay đổi P1-06 thuộc base. Backend, generated client và spec khớp base tích hợp. Manifest Admin và lockfile giữ hash vòng 1.
- P1-06 dùng chung khóa aggregate `FOR NO KEY UPDATE` và thứ tự product → variant. Đường ghi của P1-12 giữ hợp đồng token và gọi helper invalidation chung cho product/catalog/category/sitemap/feed. E2E kiểm endpoint danh sách sale price mới trên owner và 403 trên `content`, đồng thời kiểm lại draft/SKU mặc định, metadata/rowVersion, giá niêm yết, `pricing.write`, 409 thật và toàn bộ lỗi publish trên backend vừa build từ base tích hợp.

| Hạng mục | Bằng chứng vòng 2 |
| --- | --- |
| F1-A · Initialization gate | `setContent` chạy cùng plugin khi editor còn khóa. Kiểm cây thực tế sau các transaction nối tiếp của ProseMirror với bản nguồn tách biệt. Chỉ tạo baseline cho nguồn tương thích. So cả hình dạng cây, con theo thứ tự, text, marks và thuộc tính. Chấp nhận riêng giá trị mặc định `1` khi nguồn bỏ trống span ô bảng hoặc start của danh sách đánh số. |
| F1-A · Bảng không đều và rowspan | Probe gốc trên base mới tái hiện `[2,1] → [2,2]`, `rowspan=4 → 1`. Sau sửa, cả hai fixture đều chỉ đọc. Metadata PATCH chỉ gửi `rowVersion` và `subtitle`. GET giữ nguyên chính xác chuỗi `descriptionJson` và HTML. Bảng chữ nhật vẫn sửa được. |
| F1-B · Hồi quy | Undo về baseline vẫn bỏ hai trường mô tả khỏi PATCH và giữ nguyên JSON. Kiểm cả figure/ảnh/caption, HTML nhập CSV, thay định dạng, fallback, lưu lỗi và stale. |
| F2 · Hồi quy gốc | GET 503 rồi 200 giữ cùng input và giá trị 17. PATCH 200 và GET xác nhận 17 đã lưu. Ca tương thích còn lại sau khi bỏ xung đột vẫn có Lưu/Hủy và vẫn giữ chặn điều hướng khi bẩn. |
| F2 · Dirty còn sót | Trước sửa bỏ chỉnh sửa cuối cùng vẫn chặn chuyển tab. Sau sửa bỏ thay đổi cuối giải phóng form ngay, chuyển tab được và không gửi PATCH rỗng. Kiểm cách ly draft theo sản phẩm/loại. |
| F3 | `DEFERRED_NON_BLOCKING`. Generated client tích hợp chỉ thêm đường sale price/schema liên quan. 11 write route cũ vẫn thiếu metadata tham số đường dẫn. Adapter giữ nguyên và probe **11/11 PASS**. |

### Delta vòng 2

`apps/admin/src/components/products/{product-rich-text,product-attributes,product-editor}.tsx`, `apps/admin/tests/browser/products.spec.ts`, `apps/admin/tests/products-live/products.spec.ts`, tài liệu này và [manifest SHA-256](p1-12-focused-g4-fix.sha256). Tổng change set so với base tích hợp là 32 tệp, 0 staged. Manifest bao phủ 31 tệp và loại chính nó. Có thể hoàn nguyên riêng 7 tệp này từ backup đã xác minh để trở về nội dung vòng 1 trên base mới.

### Lệnh và kết quả vòng 2

| Lệnh | Kết quả |
| --- | --- |
| `corepack pnpm install --frozen-lockfile` | PASS |
| `corepack pnpm --filter @teapremium/admin test:unit` | PASS 38/38 |
| `corepack pnpm --filter @teapremium/admin test:browser tests/browser/products.spec.ts` | PASS 15/15 |
| `corepack pnpm --filter @teapremium/admin test:browser` | PASS 88/88, gồm 73 ca hồi quy cũ |
| `corepack pnpm --filter @teapremium/admin lint`, `typecheck`, `build`; `corepack pnpm --filter @teapremium/api-client typecheck` | PASS, exit 0 |
| `dotnet restore backend/src/TeaPremium.Api --locked-mode --ignore-failed-sources` và `dotnet build backend/src/TeaPremium.Api --no-restore` trong Docker | PASS. Build ra `/round2/backend/src/TeaPremium.Api/bin/Debug/net10.0`, so nguồn với worktree. Lượt build đầu thiếu tài nguyên SQL nhúng, đã copy đúng `infra` rồi build lại. |
| `corepack pnpm --filter @teapremium/admin exec playwright test --config playwright.products-live.config.ts` trong runner Docker P1-12 | PASS 1/1, 24,4 giây. PostgreSQL 17.11, HTTPS tin cậy và Production IAM. |
| `docker exec teapremium-p112-live node /tmp/p112-round2-original.cjs` | Probe độc lập gốc chạy nguyên bản trước sửa, tái hiện đủ ba lỗi. Log `artifacts/p1-12/round2-before.log`. |
| `docker exec teapremium-p112-live node /tmp/p112-round2-closure.cjs` | PASS 8/8 của implementer. Giữ năm probe nguyên bản, thay kỳ vọng ba probe lỗi bằng kiểm chỉ đọc và bảo toàn dữ liệu chính xác hoặc giải phóng form. Log `artifacts/p1-12/round2-closure.log`. |
| `git diff --check origin/main`, kiểm staged và whitespace từng tệp untracked | PASS |

R2 thật **NOT RUN**. Bằng chứng publish với PNG qua `FakeMediaStorage` thuộc G4 trước. Tái sử dụng CI cho backend tích hợp, không chạy lại suite Backend. Stack P1-12 đã dừng.

**READY_FOR_FOCUSED_G4: YES.** Chờ reviewer độc lập trên cùng worktree.

## Vòng 1 · Bằng chứng lịch sử

21/11/2026 · Phạm vi owner giao gồm F1/P0 và F2/P1. Nhánh `feature/p1-12-admin-products`, worktree `D:/tra-hien-p1-12`. HEAD và `origin/main` sau fetch cùng là `c88069d58241fd51f5f6a76da2276c41f7a3830a`. Spec tree `8e54535ff1f3f3ae9a7e1f00db1001857f3d3395`. Tái sử dụng G1/G2 của [hồ sơ P1-12](p1-12-implementation.md). Phạm vi FR-CAT-04, DEC-04, BR-CNT-02, NFR-07/09/11 và P1-12 trong [roadmap](../spec/07-roadmap.md#L61).

## Snapshot và tái hiện

Kiểm đủ 30/30 SHA-256 và đối chiếu tập path Git với manifest G4 trước sửa. Ban đầu có 8 tracked đã sửa, 22 untracked, 0 staged. SHA-256 manifest gốc `ea77453531c902800dd4fa0a8a11a60c58c2bc476d72712293798c9b653ba894`. Review, manifest và log gốc nằm trong metadata worktree `D:/tra-hien/.git/worktrees/tra-hien-p1-12/p1-12-g4-review/`.

Chạy lại script độc lập gốc trên snapshot và PostgreSQL 17/Production IAM/HTTPS cô lập trước khi sửa. Cả 10 probe chạy xong, trong đó ba phép kiểm sau xác nhận lỗi như reviewer mô tả. Log local `artifacts/p1-12/focused-before.log`.

| Finding | Trước sửa | Kiểm sau sửa |
| --- | --- | --- |
| F1-A · Cấu trúc | Chèn “Gốm” vào đoạn đầu. API trả 200 nhưng lưu `<figure></figure>`, mất `figcaption` và chuyển blockquote/caption ra ngoài. | E2E tạo fixture qua API thật gồm ảnh có ALT/width/height, blockquote, caption hai đoạn và ô bảng có colspan. Sau sửa đoạn đầu, tải lại và GET: toàn bộ phần figure/bảng khớp HTML trước đó. Đã kiểm thêm sửa sản phẩm import CSV HTML-only, giữ chữ đã escape và `br`. |
| F1-B · Hoàn tác | Undo về cùng nội dung, đổi tên phụ. PATCH vẫn chứa `descriptionHtml`, API xóa `descriptionJson`. | PATCH sau mở editor hoặc sau undo chỉ có `rowVersion` và `subtitle`. GET xác nhận chuỗi JSON gốc giữ nguyên. Đổi định dạng thật gửi object JSON, GET/reload có dấu đậm. Xóa có chủ đích lưu tài liệu JSON rỗng nội dung, server đếm 0 từ. |
| F2 · Draft | Nhập 17. GET định nghĩa 503 khiến form bị unmount, mất Lưu/Hủy. Retry 200 trả về form rỗng. | Giữ 17 khi 503 và khi retry. Hủy vẫn dùng được, Lưu mở lại sau khi tải thành công. Probe PATCH rồi GET trên backend thật ghi nhận 17. Browser bổ sung lỗi lần đầu, lỗi lặp lại, đổi đơn vị/vô hiệu lựa chọn, giữ trường tương thích và chuyển sản phẩm/loại. |

## Nguyên nhân và bản sửa

- F1: schema figure/caption không biểu diễn đủ cây HTML hợp lệ và form coi mọi `onUpdate` là thay đổi cần lưu. Editor giữ baseline ProseMirror và so sánh toàn cây bằng `Node.eq`. HTML-only qua kiểm tra cấu trúc DOM và lưu qua sanitizer hiện có. Ô bảng nguồn chứa nội dung inline giữ hình thức inline khi nội dung vẫn là một đoạn. JSON-backed kiểm round-trip cây và thuộc tính rồi gửi object JSON tương thích renderer. Cấu trúc không biểu diễn an toàn mở ở chế độ chỉ đọc với hướng dẫn tiếng Việt, metadata vẫn lưu được. Editor giữ cùng instance khi bắt đầu hoặc kết thúc lưu, nhận bản chuẩn từ API và đặt lại lịch sử cho baseline mới. Trong lúc lưu, editor khóa nhập. Đã kiểm con trỏ sau thao tác định dạng để tránh chuyển focus muộn vào editor.
- F2: nhánh `isError` trước đây thay cả form. Bản sửa render lỗi tải lại bên cạnh form khi query còn dữ liệu của đúng loại. Tạm khóa Lưu trong lúc chưa xác minh được định nghĩa hoặc lựa chọn. Nếu thuộc tính đã sửa đổi kiểu/đơn vị, biến mất, bị vô hiệu hoặc lựa chọn không còn hợp lệ, UI hiện bản nháp cần đối chiếu và yêu cầu xác nhận trước khi bỏ phần không tương thích. Các trường tương thích giữ nguyên.
- F3/P2 **DEFERRED_NON_BLOCKING**. OpenAPI thiếu metadata `id`/`childId` cho 11 thao tác: PATCH sản phẩm, POST biến thể, PATCH biến thể, POST publish/archive/restore-draft, POST attach media, PATCH/DELETE quan hệ media, POST cover và PUT media order. Giữ adapter `productWritePath` đã được probe độc lập **11/11 PASS**. Hướng xử lý sau là sửa metadata generator, sinh lại hợp đồng, bỏ adapter và chạy lại contract tests.

## Delta so với snapshot G4

- `apps/admin/src/components/products/product-attributes.tsx`
- `apps/admin/src/components/products/product-editor.tsx`
- `apps/admin/src/components/products/product-fields.tsx`
- `apps/admin/src/components/products/product-information.tsx`
- `apps/admin/src/components/products/product-references.tsx`
- `apps/admin/src/components/products/product-rich-text.tsx`
- `apps/admin/tests/browser/products.spec.ts`
- `apps/admin/tests/products-live/products.spec.ts`
- `docs/plans/p1-12-focused-g4-fix.md`
- `docs/plans/p1-12-focused-g4-fix.sha256`

Manifest kèm theo liệt kê SHA-256 nội dung toàn bộ change set trừ chính manifest. Rollback bằng hoàn nguyên tám tệp sửa về bản G4 đã xác minh và bỏ hai artifact bàn giao tương ứng. Không có migration.

## Kiểm chứng và giới hạn

| Lệnh | Kết quả |
| --- | --- |
| `corepack pnpm --filter @teapremium/admin test:unit` | PASS 38/38 |
| `corepack pnpm --filter @teapremium/admin test:browser` | PASS 87/87, gồm 73 ca hồi quy cũ và 3 ca mới F1/F2 |
| `corepack pnpm --filter @teapremium/admin lint` và `corepack pnpm --filter @teapremium/admin typecheck` | PASS, exit 0 |
| `corepack pnpm --filter @teapremium/admin build` | PASS, exit 0 |
| `corepack pnpm --filter @teapremium/admin exec playwright test --config playwright.products-live.config.ts` | PASS 1/1, 20,2 giây. PG17, Production IAM, HTTPS tin cậy. Kiểm cả round-trip F1 và trạng thái database qua API thật. |
| `docker exec teapremium-p112-live node /tmp/p112-focused-closure-probes.cjs` | PASS 10/10. Bản sao probe gốc cập nhật kỳ vọng để kiểm bản sửa. Đây là lượt kiểm của implementer. Log local `artifacts/p1-12/focused-closure.log`. |
| `git diff --check origin/main`, kiểm staged và whitespace từng tệp untracked | PASS |

Lượt browser đầu phát hiện lệnh định dạng có thể trả focus vào editor sau khi người dùng sang ô tên phụ. Đã bỏ việc chuyển focus bất đồng bộ và chạy lại 87/87. Next build sinh lại `next-env.d.ts`, đã đưa đúng tệp sinh tự động về bản ban đầu. SHA-256 của lockfile giữ nguyên `5623c82755afcfdd6730bf9386a4290f2f45cd09bd029941d8d0375885212639`. Tổng change set hiện tại 32 tệp, 0 staged.

Ảnh trong kiểm chứng F1 dùng URL fixture synthetic cấu hình trong API cô lập. Real R2 **NOT RUN**. Giữ bằng chứng độc lập trước đó về publish thành công với PNG qua `FakeMediaStorage`. Full Backend regression và nhóm Backend 46 ca dùng bằng chứng trước đó do không có delta backend. Stack P1-12 đã dừng, worktree P1-06 tách biệt.

**READY_FOR_FOCUSED_G4: YES** · Chờ reviewer độc lập kiểm cùng worktree.
