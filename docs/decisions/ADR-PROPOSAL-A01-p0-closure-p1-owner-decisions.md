# ADR-PROPOSAL-A01 — Quyết định owner cho đóng P0 và P1 (18/11/2026)

- **Trạng thái:** ACCEPTED — OWNER APPROVED ngày 18/11/2026 cho D1–D14/finalize §8 và hai bổ sung dưới đây. **Amendment: READY_FOR_LOCAL_G4**, G4 NOT RUN. Approval chỉ cho sửa specs/workflow/tài liệu liên quan; không cho source code, migration, commit/push/PR/merge.
- **Baseline:** `main` @ `288ca8b55cab2017c9ee42bd35036e2d2e5bd87c`.
- **Nguồn:** `docs/spec/00-README.md` phiên bản 2.0; G1 `docs/analysis/spec-inventory.md` @ `cadce575597738bf40ad7c45689482de5a5fbc3d`; G2 `docs/architecture/conformance-review.md` và `docs/plans/task-readiness.md`. Đã đối chiếu diff spec từ G1 tới baseline: chỉ 02/03/07/schema đổi (amendment audit, platform, địa giới); rà lại các đoạn này và các hợp đồng P0/P1 bị tác động. Đây là **TARGETED G1/G2**, không phải full audit mới hay G4 độc lập.
- **Nguồn review/prompt:** đã đọc `docs/reviews/P0-closure-and-P1-readiness-review.md` (R1–R14, G34–G38, §6 C1–C3) và `C:/Users/ADMIN/Downloads/PROMPTS-p0-closure.md` (bước 0–5). Báo cáo là review chỉ đọc tại cùng baseline; kết quả frontend là evidence do báo cáo ghi nhận, backend/Compose NOT RUN, CI OWNER-REPORTED — không chuyển thành PASS của phiên này. Prompt là tài liệu giao việc **chờ duyệt**: câu mẫu "Owner đã DUYỆT" không phải approval thực tế. §7 ghi các sửa đổi cần áp vào prompt trong lần giao việc sau; không sửa file Downloads.
- **Quy tắc áp dụng hiện hành:** owner đã cho phép amendment authoritative specs và workflow. Base/audited input `288ca8b55cab2017c9ee42bd35036e2d2e5bd87c`; revision sau amendment **UNCOMMITTED**, không có SHA commit mới vì chưa được phép commit. G1/G2 scoped/evidence ở docs/reviews/a01-amendment-evidence.md; review độc lập G4 và publish permission còn riêng. P1-01/closure implementation HOLD tới khi owner giao G3. Phần §6 giữ nhận định tại baseline, không phải gap hiện hành đã triển khai.
- **Hai bổ sung owner-approved:** tp_readonly chỉ backup có kiểm soát, không query tay tùy ý; điều tra dùng identity/quyền read riêng tối thiểu, mặc định không đọc IAM credentials/tokens/stamps và Data Protection xml (03 §4). Runbook phải tạo/kiểm owner thứ hai qua Admin API staff.manage + cookie/CSRF và onboarding riêng; không giả định Staff UI hoàn chỉnh.

## 1. Các quyết định owner đã phê duyệt

| # | Chủ đề | Quyết định | Câu hỏi |
|---|---|---|---|
| D1 | Chia DDL P1-01 | `content.redirects` tạo ở P1-01; `inventory.reservations` → P2-05; `catalog.product_reviews` + `catalog.review_media` → P5-07 | Câu 1 |
| D2 | Import sản phẩm | Làm ngay sau P1-05; cột thiếu vẫn import được, sản phẩm vào **nháp** | Câu 2 |
| D3 | SKU | Sinh một lần `{LOẠI}-{CHẤT LIỆU}-{SỐ}` bằng sequence toàn catalog + UNIQUE; cấp hậu tố có khóa/retry; không tự đổi SKU đã cấp | Câu 3, 5 |
| D4 | Mô tả 150 từ | **Không chặn publish**; thiếu thì tự `noindex` + cảnh báo SEO Audit | Câu 4 |
| D5 | Giá trống | Khi publish, mọi variant active phải có giá; inactive được NULL nhưng không bán, kích hoạt phải validate giá | Câu 6 + finalize |
| D6 | Lưu ảnh | Dùng R2 (owner đã có tài khoản); dev bucket riêng, **token giới hạn theo bucket**, không dùng key toàn quyền trong ứng dụng | Câu 7 |
| D7 | Bằng chứng đổi trả | Ảnh ≤ 15 MiB/ảnh, video mp4/mov ≤ 50 MiB/video chỉ cho `purpose=evidence`, bucket private | Câu 8 |
| D8 | Khôi phục TOTP | ≥ 2 owner có TOTP trên thiết bị riêng trước dữ liệu thật; CLI khẩn cấp chỉ cho người vận hành được owner ủy quyền, audit nguyên tử + thu hồi phiên | Câu 9 |
| D9 | Đóng P0 | Hoàn tất closure #1, #2 và nghiệm thu **trước** P1-01 | Câu 10 + finalize |
| D10 | Mức review | Phân tầng A/B, vẫn G4 độc lập + owner cho publish + CI đúng published SHA; amendment workflow được giao riêng | Câu 11 + finalize |
| D11 | Quyền DB | `infra/postgres/init-roles.sql` idempotent cho volume mới **và DB hiện hữu**; helper grants dùng chung + migration bổ sung, không xóa dữ liệu | R1 |
| D12 | Audit | Phân loại field audit **theo từng bảng**, mặc định vẫn che | R2/G37 |
| D13 | `rowVersion` | Chuỗi mờ (opaque string) trên API, nguồn từ `xmin` (hoặc `ConcurrencyStamp` của Identity) | G34 |
| D14 | Cảnh báo kho P1-10 | P1 xử lý nội bộ an toàn/idempotent, retry có giới hạn; P2-13 thêm Telegram theo trạng thái hiện tại, không phát lại cảnh báo cũ | G35 + finalize |

## 2. Lý do ngắn

- **D1:** các bảng hoãn có FK tới bảng đơn/khách chưa tồn tại; redirect cần ngay từ P1-04 (BR-CAT-08).
- **D2/D5:** dữ liệu Excel của shop không đầy đủ; bắt đủ mới import thì việc nhập catalog — việc tốn thời gian nhất — bị chặn.
- **D3:** mã có nghĩa giúp nhân viên tìm hàng; sequence cấp số nguyên tử, UNIQUE là lớp bảo vệ cuối (kể cả SKU nhập tay). Đổi loại/chất liệu không tái sinh mã; không cam kết số liền mạch vì rollback có thể để khoảng trống.
- **D4:** loại trang mô tả mỏng khỏi tập trang được phép index mà không chặn việc bán; noindex là chỉ dẫn kỹ thuật, không cam kết Google xử lý ngay.
- **D7:** video điện thoại thường > 15 MB; iPhone quay `.mov`.
- **D8:** hiện chưa có luồng khôi phục được hỗ trợ khi owner duy nhất mất TOTP; tránh phải sửa DB thủ công.
- **D11/D12/D13:** xem báo cáo rà soát R1, R2, R7.

## 3. Nội dung sửa cụ thể

### 3.1 `schema.sql`

**(a) D5 — giá trống khi nháp hoặc variant inactive** (`catalog.variants`):

```sql
-- cũ
  price_vnd      bigint NOT NULL CHECK (price_vnd > 0 AND price_vnd <= 10000000000),
-- mới
  price_vnd      bigint CONSTRAINT ck_variants_price CHECK (price_vnd IS NULL OR (price_vnd > 0 AND price_vnd <= 10000000000)),  -- luật active/publish/kích hoạt do app kiểm (BR-CAT-04.3, BR-CAT-15)
```

**(b) D3 — mã SKU tự sinh:**

```sql
-- catalog.product_types: thêm
  sku_prefix     text CONSTRAINT ck_product_types_sku_prefix CHECK (sku_prefix ~ '^[A-Z]{2,4}$'),   -- AM (ấm), CH (chén), KH (khay)...
-- catalog.materials: thêm
  sku_code       text CONSTRAINT ux_materials_sku_code UNIQUE CONSTRAINT ck_materials_sku_code CHECK (sku_code ~ '^[A-Z]{2,3}$' AND sku_code <> 'XX'),  -- XX dành cho chưa có material
-- catalog.products: thêm
  sku_base       text NOT NULL CONSTRAINT ux_products_sku_base UNIQUE CONSTRAINT ck_products_sku_base CHECK (sku_base ~ '^[A-Z]{2,4}-[A-Z]{2,3}-[0-9]{4,}$' AND length(sku_base) <= 40),
-- mới, sau catalog.products
CREATE SEQUENCE catalog.sku_number_seq START 1;
```

`sku_prefix`/`sku_code` có thể NULL khi nhập dữ liệu tham chiếu chưa hoàn tất; khi cấp mã phải kiểm như BR-CAT-14. `variants.sku` vẫn NOT NULL, UNIQUE và dài 2–40 ký tự theo DDL hiện hành. Không dùng `MAX+1`, COUNT hoặc bộ đếm trong process; không reset sequence khi import/retry. Với DB đã có product, amendment triển khai phải thêm nullable → backfill có kiểm va chạm/không đổi SKU hiện có → NOT NULL; không tự suy mã từ loại/chất liệu hiện tại cho lịch sử chưa rõ nguồn.

**(c) D7 — video bằng chứng** (`media.assets`, `media.upload_sessions`):

```sql
-- media.assets.mime_type: thêm 'video/quicktime'
  mime_type      text NOT NULL CHECK (mime_type IN ('image/jpeg','image/png','image/webp','image/avif','video/mp4','video/quicktime')),
-- media.upload_sessions: thay CHECK expected_bytes, giới hạn cả purpose/MIME
  expected_bytes bigint NOT NULL,
  ...
  CONSTRAINT ck_upload_size_by_kind CHECK (
    purpose IN ('catalog','content','evidence','branding') AND (
    (mime_type IN ('image/jpeg','image/png','image/webp','image/avif')
       AND expected_bytes > 0 AND expected_bytes <= 15728640) OR
    (purpose = 'evidence' AND mime_type IN ('video/mp4','video/quicktime')
       AND expected_bytes > 0 AND expected_bytes <= 52428800)))
```

`media.assets` phải có CHECK tương đương trên `byte_size`, không chỉ MIME/purpose. P1-02 kiểm lại object thực khi complete (size, MIME/nội dung khớp intent), không tin đuôi `.mov`/Content-Type do client gửi; intent hoặc complete sai không tạo asset công khai. Giới hạn chính xác: ảnh 15 MiB = 15 728 640 bytes; video 50 MiB = 52 428 800 bytes. Đây là giới hạn đề xuất cho D7, không đổi lý do/điều kiện đổi trả (G27 vẫn mở).

**(d) D4 — số từ tính sẵn** (`catalog.products`): thêm

```sql
  description_word_count int NOT NULL DEFAULT 0 CONSTRAINT ck_products_word_count CHECK (description_word_count >= 0),  -- app cập nhật khi lưu
```

Giữ `robots_noindex` là **ý định thủ công** hiện hữu (BR-SEO-23); không dùng trường này để lưu trạng thái thin-description. Lý do tự động `thin_description` suy ra từ count, không cần thêm một cờ DB khác. Count được cập nhật phía server cùng transaction lưu mô tả; import và mọi đường sửa mô tả dùng cùng phép đếm. Nếu có dữ liệu cũ phải backfill trước khi dùng count để quyết định index.

### 3.2 `04-business-rules.md`

- **BR-CAT-02** thêm câu: "SKU do hệ thống sinh theo BR-CAT-14; owner được sửa tay khi variant chưa có biến động kho/dòng đơn."
- **BR-CAT-04** sửa:
  - Điểm 1: giữ điều kiện `name`, `short_description`, `description_html` không rỗng và name dài 2–200; không mở rộng việc nới validation từ quyết định ngưỡng 150 từ. Mô tả thiếu số từ chỉ gây thin-description noindex; lỗi không rỗng hay lỗi publish khác vẫn xử lý riêng. Giữ điểm 2/4/5/6 về danh mục, ảnh ALT, thuộc tính và trục biến thể.
  - Điểm 3: "Có ≥ 1 variant `active` và **mọi** variant `active` có giá trong khoảng 1…10 000 000 000 VND."
  - Điểm 7: **giữ validation metadata hiện hành**: `seo_title` ≤ 70 ký tự, `seo_description` 50–160 ký tự và cơ chế nút tự điền theo spec; không chuyển lỗi thành warning, không bổ sung fallback hoặc tự lưu metadata để vượt validation. BR-SEO-31/32 vẫn theo contract hiện có, không nới thêm trong A01.
  - Điểm 8: chỉ bỏ chặn **ngưỡng 150 từ** và áp BR-SEO-34 mới; giữ bảng thông số crawl được, quy tắc nội dung thật và các yêu cầu khác không bị amendment thay đổi.
- **BR-CAT-13** (import) viết lại:
  > Mỗi dòng là 1 variant, nhóm theo `product_slug` (trống thì sinh từ tên theo BR-CAT-01, không tự gộp hai sản phẩm khác chỉ vì trùng tên). **Bắt buộc**: `name`, `product_type_code`. **Tùy chọn**: giá bán, danh mục, chất liệu, nghệ nhân, thuộc tính, mô tả, kích thước, cân nặng, SKU (trống thì sinh theo BR-CAT-14). Giá trị có mặt nhưng sai định dạng/không tồn tại → lỗi dòng đó. Chạy thật tạo product `draft` + variant (giá có thể NULL), không tạo tồn kho. Dry-run không gọi nextval, không ghi DB/R2; chạy thật validate lại. **Không tự upsert variant nếu thiếu khóa nhận diện ổn định**; product_slug/tên/thứ tự dòng không đủ nhận diện variant. Không cam kết cập nhật draft theo slug hoặc idempotency. Khóa variant, hành vi import lại, ô trống, nhóm mâu thuẫn type/material và tính nguyên tử theo nhóm là **OPEN cho P1-07**, không triển khai ngầm trong A01/P1-01.
- **BR-CAT-14 (mới)** Sinh SKU:
  > `sku_base` = `{product_types.sku_prefix}-{materials.sku_code hoặc "XX"}-{số}`; `XX` chỉ khi **chưa chọn material**. Thiếu prefix → `sku_prefix_missing`; material đã chọn thiếu mã → `material_sku_code_missing`. Lấy số bằng `nextval('catalog.sku_number_seq')`, đệm tối thiểu 4 chữ số, không cắt số > 9999. Sinh một lần và lưu; sửa type/material/mã tham chiếu không đổi `sku_base` hay SKU đã cấp. Chuyển type vẫn chỉ khi draft (BR-CAT-03).
  > Variant đầu được cấp SKU trống ưu tiên `sku_base`; các variant sau dùng hậu tố trục chuẩn hóa `[A-Z0-9-]` hoặc `V2`, `V3`…; thêm variant thứ hai **không đổi mã variant đầu**. Sửa tên/trục, xóa nháp hợp lệ hoặc inactive variant không tái sinh/đánh lại mã các variant còn lại; inactive vẫn giữ SKU trong UNIQUE. Cấp hậu tố trong transaction có khóa theo product (serialize cả create/import cùng product); UNIQUE `variants.sku` bảo vệ toàn catalog, kể cả va chạm SKU nhập tay. Khi đụng UNIQUE của mã tự sinh (kể cả ứng viên sku_base cho variant đầu), rollback về savepoint hoặc retry transaction với ứng viên hậu tố kế tiếp, có giới hạn và lỗi rõ khi hết lượt; không retry mọi lỗi DB. Rút ngắn phần nhãn hậu tố để toàn SKU ≤ 40 ký tự, luôn giữ nguyên số sequence và phần chống trùng; kiểm regex cuối trước insert. SKU nhập tay trùng trả lỗi, không tự sửa mã người dùng. Owner được sửa tay trước movement/order theo BR-CAT-02; sau đó cấm. `sku_base` không phải trường sửa tay.
  > P1-05/P1-07 phải test nhiều request cùng type/material và cùng product, va chạm mã tay, rollback có khoảng trống, mốc 9999→10000, thêm variant thứ hai, sửa type/material/trục/prefix, SKU dài tối đa và cấm sửa sau giao dịch. P1-01 chỉ tạo sequence/UNIQUE/CHECK; chưa chứng minh generator đúng.
- **BR-CAT-15 (mới)** Variant `price_vnd IS NULL` không hiện ở storefront, không thêm giỏ, không đưa vào feed, không chọn được trong đơn Admin (trả `variant_not_sellable`).
  > Khi publish, có ≥ 1 variant active và **mọi variant active** có giá 1…10 000 000 000 VND; thiếu giá trả `variant_price_missing` trong toàn bộ lỗi publish. Variant inactive được NULL kể cả khi product đã active, không chặn publish chỉ vì giá của inactive. Draft vẫn được lưu thiếu giá. **Chuyển inactive → active phải validate giá trước khi đổi trạng thái**; active của product đã publish không được sửa giá về NULL. Inactive hoặc giá NULL luôn không bán/không hiện storefront, không cart/order/feed/JSON-LD Offer; không đổi NULL thành 0 hoặc tạo KM dựa trên NULL. Kiểm cả thêm/sửa variant, kích hoạt, publish và import; khóa parent cùng transaction để publish/kích hoạt không race với việc ghi NULL. DB CHECK một cột không bảo vệ luật liên bảng. BR-CAT-11 và BR-PRC-01/02 dùng cùng tiêu chí bán được; test ở P1-05/06/07/14 và P2-05/06. Archive không làm variant thiếu giá thành bán được; không thêm điều kiện buộc inactive phải có giá khi archive.
- **BR-IAM-07 (mới)** Lệnh khẩn cấp:
  > CLI `reset-staff-mfa --email <email>` không mở HTTP, không khởi động web listener/background jobs. **Chỉ người vận hành được owner ủy quyền** qua tài khoản OS/SSH định danh và quyền chạy command hạn chế; không đặt quyền này vào role sales/content, không cấp shell/credential vận hành cho tiến trình API. Runbook ghi căn cứ xác minh yêu cầu, mã sự cố và người thực hiện; không coi việc biết email hoặc vào được container là đủ quyền. Danh tính operator/allowlist cụ thể được owner cấu hình trong runbook trước vận hành; operator authorization đã là yêu cầu, không phải tùy chọn.
  > Nhập tương tác lý do ≥ 10 ký tự sau trim (theo prompt bước 3), gõ lại email; tùy chọn `--reset-password` nhập ẩn mật khẩu tạm đạt BR-IAM-01. Email không ghi log/telemetry/history; ưu tiên nhập tương tác, nếu dùng `--email` phải kiểm soát process list và shell history. Không truyền password/secret qua argv/env/log. Từ chối tài khoản không tồn tại/inactive và command không được ủy quyền; không tạo tài khoản, tự cấp owner hoặc xóa trạng thái lockout/cửa sổ sai mật khẩu để unlock tài khoản. Credential CLI chỉ cần quyền IAM/audit cần thiết; quyền OS chạy CLI không đồng nghĩa tự cấp `tp_owner`/superuser như gợi ý R4.
  > Khóa staff row và chạy **một transaction**: tắt TOTP; xóa authenticator secret, pending key/time, last-used step và recovery tokens nếu có; đổi `security_stamp` lẫn concurrency stamp; đặt `must_change_password=true` **dù có hay không `--reset-password`**. Nếu có cờ thì reset mật khẩu trong cùng transaction. Phải bắt cài lại TOTP trước khi có quyền nghiệp vụ, kể cả staff không phải owner; cần trạng thái recovery bền vững/luồng auth được đặc tả trong P0-08, không chỉ dựa vào TwoFactorEnabled=false (có thể vô tình cho staff bỏ qua MFA).
  > Mọi cookie/phiên cũ bị từ chối ở request kế tiếp sau commit; setup/pending token cũ không dùng lại được. Onboarding chỉ cho auth endpoints tối thiểu để đổi password/cài và verify TOTP; CSRF/rate limit vẫn áp dụng. Không mở toàn bộ `auth/*` để gọi chức năng quản lý staff. Giữ hạn chế recovery cho tới khi cả đổi password và verify TOTP thành công.
  > Audit thành công đúng một bản ghi `platform.audit_logs`: `actor_type=system`, `actor_id=NULL`, `entity_type=iam.staff_users` (tên bảng đã kiểm tại migration StaffIam), `entity_id` UUID đích, `action=state_change`, correlation/operation ID. `changes` chỉ có MFA trước/sau thực tế, `password_reset`, `reason_length` và tham chiếu sự cố/operator do runbook tin cậy cấp, được phân loại audit cụ thể; không ghi lý do hay tên operator tự do nguyên văn. Lý do đầy đủ giữ trong hồ sơ vận hành có kiểm quyền, nối bằng mã sự cố. Không lưu email/password/TOTP/secret/stamp/token trước/sau. Audit lỗi hoặc cancellation → rollback toàn bộ, exit khác 0; không báo thành công. Lỗi trước transaction ghi sự kiện vận hành đã che dữ liệu, không tạo audit thành công giả. Hai reset đồng thời không làm mất audit/cập nhật. Ghi nhận: người nắm quyền DB/OS đặc quyền có thể bỏ qua CLI; runbook/ủy quyền là ranh giới vận hành cần owner quản lý.
- **BR-IAM-08 (mới)** Trước **lần nhập dữ liệu thật đầu tiên**, kể cả API P1-05, import P1-07, ảnh P1-02 hoặc UI P1-12, và trước go-live P3-12: ≥ 2 tài khoản owner active, hoàn tất đổi password/TOTP trên hai thiết bị riêng, không dùng chung TOTP secret. Kiểm bằng checklist vận hành (không suy ra hai thiết bị từ count DB). CLI đóng P0 trước P1-01 theo D9; enrollment hai owner là gate trước dữ liệu thật, không cấm test bằng dữ liệu giả. Cảnh báo Admin thuộc P0-12 trong closure P0-08/P0-12 được owner duyệt.
  > Theo prompt bước 3, đề nghị `ownerMfaCoverageOk` ở `admin/auth/me` và banner **chỉ owner**; true khi ≥2 owner active, đã đổi mật khẩu, bật TOTP và không còn recovery pending. Đây chỉ là độ phủ tài khoản, không chứng minh thiết bị độc lập. Kiểm 0/1/2 owner, reset/khóa/hạ role làm cờ đổi đúng, không lộ danh sách/secret cho non-owner; cập nhật OpenAPI/client trong closure #2. Việc hai owner enrollment hoàn tất là bước 4 trước tuyên bố đóng P0, theo prompt; không phải API feature đã có.
- **BR-INV-11/13** thêm: "Ở P1, InventoryAlert có consumer nội bộ an toàn/idempotent và hiển thị cảnh báo; retry có giới hạn theo outbox hiện hành, lỗi quá hạn có trạng thái kết thúc và được quan sát. Không treo message thiếu handler chờ Telegram. P2-13 chỉ gửi cảnh báo hiện tại/digest, không phát lại cảnh báo P1 cũ."
- **BR-RET-01** thêm: "Bằng chứng: ảnh (≤ 15 MiB/ảnh) hoặc video `mp4`/`mov` (≤ 50 MiB/video), lưu bucket private; giới hạn bytes tại §3.1(c)."

### 3.3 `09-seo.md`

- **BR-SEO-34** sửa phần "Bắt buộc":
  > Mô tả riêng ≥ 150 từ **là một điều kiện để index, không phải điều kiện publish.** Đếm plain text từ `description_html` đã sanitize: bỏ script/style, giải mã HTML entities, ngăn cách các block, gộp whitespace Unicode (kể cả NBSP), đếm token không rỗng; không tính boilerplate tự chèn/bảng thông số. Không chỉ regex xóa tag làm dính hai từ. Cùng thuật toán dùng cho lưu/import/audit.
  > `thin_description = description_word_count < 150`. `effective_noindex` là OR của noindex thủ công `robots_noindex`, thin-description và mọi điều kiện cấm index khác tại §2/§3; `SITE_INDEXABLE=false` vẫn thắng và giữ `noindex,nofollow`. Không ghi `robots_noindex=true` khi mô tả mỏng; đủ 150 từ **chỉ gỡ lý do thin-description**, không bao giờ tự đặt cờ thủ công về false. Chỉ trả `index,follow` và đưa lại vào sitemap/feed khi toàn bộ điều kiện tương ứng đều đạt; draft vẫn 404, archived vẫn 301/404, canonical/query và vòng đời hàng đã bán giữ theo luật hiện hành. Noindex do mô tả mỏng dùng `noindex,follow`, không Disallow để crawler đọc được meta.
  > Thay đổi count qua ngưỡng theo **cả hai chiều** hoặc thay đổi cờ thủ công phải phát `StorefrontInvalidate` cùng transaction, làm mới tag product/catalog/sitemap/feed theo §5.8 kiến trúc. Sitemap/feed/SSR dùng cùng kết quả dẫn xuất; feed vẫn kiểm thêm BR-SEO-80/81, không đưa variant NULL giá vào. P1-05 lưu count/warning/outbox, P1-14 trả dữ liệu phục vụ; HTML/robots/canonical/sitemap/feed nghiệm thu tại P3-09/P3-15/P3-17, không tuyên bố đã có storefront trong P1-01.
- **§2 Ma trận index**: sửa hai dòng sản phẩm active/độc bản đã bán: chỉ index và vào sitemap nếu không có lý do noindex; thêm dòng mô tả < 150 từ → noindex,follow, không sitemap/feed (trừ ưu tiên SITE_INDEXABLE=false).
- **BR-SEO-60**: mô tả < 150 từ **đã nằm trong Cảnh báo** tại baseline; bổ sung hệ quả noindex và chỉ rõ đây là noindex dự kiến, không tự báo thêm lỗi "trang chính có noindex". Noindex ngoài ý muốn vẫn là lỗi; noindex thủ công vẫn có lý do/audit theo BR-SEO-23.

### 3.4 `02-architecture.md`

- **§5.4 Audit** thêm:
  > Giá trị trước/sau được lọc theo **bảng phân loại field/path cho từng `entity_type`** (`Safe`/`Pii`/`Secret`). Field/path chưa khai báo → `[redacted]`, áp dụng đệ quy JSON/object/array, không để tên key không tin cậy chứa PII lọt log. Mỗi task đánh dấu `[Audited]` khai báo phân loại và test. Các ứng viên Safe như slug/SKU/code, tên sản phẩm, tiền/số lượng/trạng thái/thời điểm phải được xét **cụ thể từng entity và kiểu giá trị**, không whitelist toàn cục bằng tên/hậu tố. Tên nghệ nhân là tên người → Pii mặc định; muốn audit nguyên văn phải có quyết định riêng. Tên/SĐT/email/địa chỉ người, free-text note, token/secret luôn che; không tự coi tên material/product là Safe nếu có nội dung nhạy cảm. Giữ nguyên phân loại settings/bank/credential hiện có, audit cùng transaction và identity UUID/TEXT G26. D8 dùng cùng chính sách, không tự ghi raw reason/operator vào changes.
- **§5.10 Upload** sửa MIME: ảnh ≤ 15 MiB; `purpose=evidence` thêm `video/mp4`, `video/quicktime` ≤ 50 MiB. Upload intent nhận thêm `slugHint` (BR-SEO-82; xử lý G14); P1-02 validate/chuẩn hóa slugHint, không dùng tên/email trong evidence key, có fallback không chứa PII. Evidence đi bucket private, kiểm quyền purpose/đối tượng trước khi ký GET hết hạn ≤ 10 phút; kiểm complete theo §3.1(c).

### 3.5 `03-database.md`

- **§4** sửa tiêu đề và nội dung:
  > **Một đường dẫn duy nhất:** `infra/postgres/init-roles.sql` (đúng 02 §2, 03 §4, 06 §5). Script chưa có ở baseline; sẽ được tạo trong closure P0-02/P0-04. Role là đối tượng cấp cluster; provisioning idempotent: tạo nếu thiếu, kiểm thuộc tính/quyền nguy hiểm nếu có; không tự đổi password đã có, không tự drop/recreate role/DB hoặc dùng `REASSIGN OWNED` hàng loạt. Credential truyền qua kênh bí mật được kiểm soát, không ghi repo/log. Password rotation là thao tác riêng được owner ủy quyền.
  > **Volume mới:** init hook chỉ là một cách gọi script; tạo DB nếu chưa có, đúng owner. Đề nghị wrapper `infra/postgres/init-roles.sh` truyền biến bí mật sang psql theo cách đã kiểm và gọi cùng `init-roles.sql`; mount SQL có `:'owner_pw'` đơn thuần không tự nhận biến môi trường Docker. **Volume/DB hiện hữu:** chạy thủ công **cùng SQL** bằng tài khoản provisioning được ủy quyền, có bước preflight/backup, ngoài init hook (hook không tự chạy lại). Không `docker compose down -v`, không xóa volume, không tạo lại DB. Nếu DB/object có owner sai, dừng và báo danh sách; owner duyệt remediation theo object riêng, không tự đổi ownership/dữ liệu. Đổi connection string API sang `tp_app` sau khi kiểm quyền, giữ credential migration `tp_owner` riêng; không dùng role đặc quyền để né lỗi quyền.
  > **Quyền trên object do migration cấp** qua helper đề xuất `backend/src/TeaPremium.Infrastructure/Persistence/GrantsSql.cs`, API helper cố định allowlist schema/object (không ghép input người dùng). Helper sinh SQL cố định lúc tạo migration; migration giữ SQL đó, không gọi helper có thể thay đổi trong tương lai làm đổi lịch sử. Thêm migration closure sau head P0 để sửa quyền **platform/iam đã tồn tại**; không sửa migration cũ. P1-01 dùng cùng helper cho đúng 5 schema mới. Role script không chứa bản sao grant bảng. tp_readonly là tài khoản backup được kiểm soát; không dùng điều tra, điều tra cần identity/quyền read riêng đã che dữ liệu và loại trừ credentials/DP keys mặc định theo 03 §4. Test dùng chính script/helper/migration, không tự tạo role/GRANT thay thế; fixture-only tables có thể có grants riêng, phải ghi rõ là fixture, không là bằng chứng quyền ứng dụng.
  > Helper giữ ma trận 03 §4: USAGE schema cho app/readonly; DML bảng nghiệp vụ cho app, SELECT cho readonly; USAGE sequence cho app (kể cả SKU); quyền đọc history migration phục vụ readiness; public.f_unaccent gọi được; runtime không CREATE schema/object, không là owner/superuser/CREATEROLE/CREATEDB/BYPASSRLS, không thừa kế role đặc quyền hoặc quyền từ PUBLIC. REVOKE append-only audit/stock_movements và ledger F-02, giữ EXECUTE advisory lock như DDL; readonly không ghi. Default privileges nếu dùng phải gắn **đúng role tạo object `tp_owner`**, không thay thế grants/revoke explicit từng migration và không áp nhầm cho bảng ngoại lệ.
  > Missing role → entry point `scripts/dev-migrate.ps1` preflight **trước cả chuỗi migration**, nêu đúng SQL cần chạy; helper cũng kiểm trước grant/DDL của migration mới. Không hứa helper ở migration mới chạy được trước GRANT trong P0-09 cũ: chạy EF trực tiếp bỏ qua preflight vẫn có thể lỗi tại migration cũ, phải ghi rõ trong runbook. Chạy lại provisioning và idempotent migration script không đổi dữ liệu/quyền, không reset password/sequence. Integration test đăng nhập thật `tp_app`/`tp_readonly`, kiểm âm DDL/append-only và so fingerprint dữ liệu/IAM/ledger trước-sau trên DB P0 có dữ liệu giả. Kiểm cả baseline P0 trước khi có closure migration lẫn checkout mới từ rỗng.
- **§1** dòng Concurrency: thêm "API trả `rowVersion` dạng chuỗi mờ".
- **§3.2** thêm đoạn SKU (BR-CAT-14) và giá NULL (BR-CAT-15).
- **§6 Seed tham chiếu**: thêm cột `sku_prefix` cho từng product type (`teapot`→`AM`, `cup`→`CH`, `pitcher`→`TO`, `tea_tray`→`KH`, `tea_caddy`→`HU`, `vase`→`BB`, `incense`→`XT`, `wine_set`→`RU`, `accessory`→`PK`) và `sku_code` cho material (tử nê `TN`, lục nê `LN`, đoạn nê `DN`, chu nê `CN`, hồng nê `HN`, hắc nê `HE`, gang `GA`, thủy tinh `TT`, gốm `GM`, sứ `SU`, đồng hợp kim `DO`, gỗ `GO`, đá `DA`). Owner sửa được trong Admin.

**D11 cũng cần amendment `06-infrastructure.md` §3/§5 và `02-architecture.md` §2:** đồng bộ path trên, tách tạo DB (chỉ nếu thiếu, ngoài transaction) khỏi reconcile roles; bổ sung runbook DB hiện hữu, preflight/backup, secret injection, đổi runtime connection và kiểm quyền. Không chỉ sửa 03 rồi để ví dụ 06 vẫn CREATE ROLE/CREATE DATABASE vô điều kiện. Chưa thực thi các amendment này.

### 3.6 `05-api.md`

- **§1**: `rowVersion` là **chuỗi** (opaque); client chỉ gửi lại, không phân tích. Catalog dùng nguồn `xmin` uint ở DB; IAM giữ `ConcurrencyStamp` hiện có, **không lộ SecurityStamp**. Chốt serialization/validation và đồng bộ OpenAPI/client/test, không tự chấp nhận lẫn số/chuỗi không có contract. Stale → 409; malformed → 400. Contract uint → opaque string đã được owner phê duyệt; spec local đã áp, code/client chưa đổi.
- **§4.2** `admin/media/upload-intents` body thêm `slugHint?`.
- **§4.2** `POST admin/products/{id}/publish` ghi chú: trả `200` kèm `warnings[]` (vd. `thin_description_noindex`) khi publish thành công nhưng chưa đủ điều kiện index.
- **§4.1** ghi chú: không có endpoint HTTP reset MFA; dùng CLI BR-IAM-07.
- **§4.3** (G13, theo chỉ dẫn finalize): người đọc kho hạn chế (`inventory.read` nhưng không `inventory.write`) chỉ nhận `availability` (`in_stock`/`out_of_stock`), không `low`, `onHand`, `reserved`, `available`, giá vốn hoặc movements (403). Người có inventory.write được số lượng/movements, giá vốn vẫn cần `costs.read`. Test JSON theo permission (không hardcode tên role), cả response lồng nhau/export; giá vốn không lọt vào response đọc hạn chế.

### 3.7 `07-roadmap.md`

- **P1-01** sửa mô tả:
  > Migration schema `media`, `catalog`, `inventory`, `pricing` (chỉ `sale_prices`), `content` (chỉ `redirects`) theo `schema.sql`, **trừ** `inventory.reservations` (→ P2-05) và `catalog.product_reviews`, `catalog.review_media` (→ P5-07); grants theo 03 §4; mapping bigint/numeric theo cột thực trong scope, không tạo cột smallint giả (rating thuộc P5-07); quy ước `rowVersion`. Sau **toàn bộ P0 closure** D9 (§7), không chỉ closure grants.
- **P0-02/P0-04/P0-09/P0-10**: closure #1 D11 (script/compose/runtime grants/migration bổ sung/test chung/runbook); **P0-08/P0-12**: closure #2 D8 (recovery CLI, trạng thái onboarding bền vững, migration Identity bổ sung nếu cần, test và cảnh báo). **D12/P0-05 remediation** hoàn tất trước P1-04 theo báo cáo R2/I-04, không tự đưa thành blocker của P1-01. Giữ ID task cũ, các nhãn closure §7 chỉ là nhóm sửa, không phải roadmap mới.
- **P1-05** phụ thuộc: `P1-02, P1-03, P1-04` (G32).
- **P1-07**: ưu tiên thực hiện ngay sau P1-05, trước P1-06; giữ ID và dependency P1-05, không gộp import vào P1-01/05.
- **P1-10**: outbox + dữ liệu cảnh báo kho; Telegram gắn P2-13. P1 chỉ đối soát movements/on_hand; đối soát reservations/reserved bắt đầu khi P2-05 đã tạo bảng (không SELECT bảng chưa tồn tại/không báo đã đối soát phần này). P1-13 hiển thị cảnh báo kho tối thiểu; dashboard tổng hợp vẫn P2-16. Consumer nội bộ InventoryAlert phải có ngay P1, xử lý an toàn/idempotent, hoàn tất message khi thành công; lỗi retry trong giới hạn outbox hiện hành, quá giới hạn có trạng thái kết thúc/quan sát, không retry vô hạn hoặc treo vì thiếu Telegram handler. P2-13 gửi theo trạng thái hiện tại/digest, không phát lại cảnh báo P1 đã lỗi thời. Test P1-10 gồm giao message trùng, lỗi consumer có giới hạn retry và không cần Telegram; P2-13 kiểm không replay message cũ và không gửi cảnh báo đã hết hiệu lực.
- **P2-01**: ghi rõ không áp `fk_reservations_line` tại đây vì bảng reservations chưa có; vẫn tạo sales.order_lines. G03 marketing FK là gap riêng cần xử lý trước P2-01.
- **P2-05**: thêm migration `inventory.reservations`, index và `fk_reservations_line` tới sales.order_lines; hoàn tất đối soát reservation của BR-INV-13. Không chỉ chuyển CREATE TABLE mà để ALTER FK trong migration P2-01.
- **P5-07**: thêm "+ migration `catalog.product_reviews`, `catalog.review_media`".
- **AC-P1-09 (mới, P1-07)**: import 3 dòng khác product: (1) đủ, (2) tên + type có prefix, (3) type không tồn tại → 1–2 draft, dòng 2 giá NULL + SKU đúng mẫu và không bán được, dòng 3 lỗi. Dry-run không ghi/cấp sequence; import lại thiếu khóa variant ổn định không tự cập nhật variant có sẵn. Fixture publish đủ mọi điều kiện khác, có 1 active giá hợp lệ + 1 inactive giá NULL → thành công; kích hoạt inactive giá NULL → 422 `variant_price_missing`, không đổi trạng thái. Fixture draft có active giá NULL → publish 422 `publish_requirements_not_met`, toàn bộ lỗi có `variant_price_missing`; điền giá hợp lệ mới kích hoạt/publish được. Test giá và kích hoạt thuộc P1-05, import/key thuộc P1-07; giữ bộ test cạnh tranh/bất biến SKU BR-CAT-14, không giả định trạng thái import khi hợp đồng P1-07 chưa chốt.
- **AC-P1-10 (mới, nghiệm thu chia pha)**: P1-05/14 kiểm count/warning/dữ liệu index/outbox; P3-09/15/17 kiểm SSR/robots/canonical/sitemap/feed/audit. Product đủ điều kiện khác, cờ thủ công false, site indexable: publish mô tả 149 từ thành công + noindex/không sitemap/feed, 150/151 → chỉ bỏ lý do thin và đủ điều kiện index. 151→149 loại lại. Với manual noindex=true hoặc SITE_INDEXABLE=false, 149→150 **vẫn không index** và cờ thủ công không đổi; draft/archived giữ vòng đời. Metadata sai validation hiện hành vẫn chặn publish dù mô tả có 149 hay 150 từ; không tự fallback. Kiểm whitespace/HTML/entities, tag invalidation cả hai chiều và audit manual/thin.
- **AC-P0-06 (mới, closure P0-08)**: operator được ủy quyền reset owner A → request kế tiếp với cookie cũ 401; lần login mới chỉ onboarding cho tới **đổi password và verify TOTP** trong cả hai mode có/không `--reset-password`; đúng một audit system/state_change không chứa secret/PII. Test staff không phải owner cũng không bỏ qua reenrollment.
  > Bộ test bắt buộc: operator không quyền; target thiếu/inactive; lý do 9/10 ký tự hoặc email xác nhận sai; password tạm không đạt policy; EOF/cancel; DB/audit failure rollback; reset đồng thời; pending secret/mã/recovery token cũ vô hiệu; không HTTP endpoint/listener/background jobs; sales/content không có đường gọi reset; log/stdout/process args không lộ secret; phiên của staff khác vẫn dùng được; quyền nghiệp vụ/CSRF, lockout và last-owner guard vẫn hoạt động; cờ ownerMfaCoverageOk đúng với 0/1/2 owner và sau reset. Chạy với PostgreSQL 17 dùng một lần và tài khoản giả. Audit lỗi phải giữ nguyên MFA/password/stamp/recovery state; kiểm bằng fingerprint, không chỉ exit code.

### 3.8 `00-README.md`

Thêm vào §6 (DEC):

| Mã | Quyết định |
|---|---|
| DEC-25 | Import/nhập liệu cho phép thiếu dữ liệu ở nháp; giá bắt buộc khi bán (BR-CAT-13/15) |
| DEC-26 | SKU tự sinh `{LOẠI}-{CHẤT LIỆU}-{SỐ}` (BR-CAT-14) |
| DEC-27 | Mô tả < 150 từ không chặn bán, chỉ `noindex` (BR-SEO-34) |
| DEC-28 | Bằng chứng đổi trả: ảnh ≤ 15 MiB, video ≤ 50 MiB chỉ purpose=evidence, bucket private |
| DEC-29 | ≥ 2 owner có TOTP + CLI khẩn cấp có audit (BR-IAM-07/08) |
| DEC-30 | Quyền DB: script role + migration grants dùng chung cho mọi môi trường và test |

### 3.9 Ngoài `docs/spec/` — D10 phân tầng review

Sửa `docs/WORKFLOW.md` §5 và `AGENTS.md` mục G4 **đã được owner cho phép trong amendment này**, G4 độc lập còn NOT RUN:

| Tầng | Áp dụng khi task chạm | Review |
|---|---|---|
| **A — đầy đủ** | Migration/DDL/grants; tiền (giá, KM, giá vốn, thanh toán, hoàn tiền, báo cáo); tồn kho; auth/quyền/CSRF/PII; checkout công khai; index/canonical/sitemap | G4 độc lập như hiện tại |
| **B — rút gọn** | UI Admin dùng API đã qua tầng A; CRUD không có tiền/kho/PII; tài liệu; refactor không đổi hành vi | Session mới đọc diff + chạy test + checklist ≤ 1 trang; không cần hồ sơ dài |

Task chạm cả hai tầng → A. Mục tiêu hồ sơ 1–2 trang khi đủ chứng cứ, **không cắt acceptance/security/test evidence để đạt giới hạn trang**. Cả A/B vẫn reviewer độc lập trong session mới/cùng worktree, đọc committed/staged/unstaged/untracked, G4 PASS rồi mới owner cho publish, CI đúng published SHA, owner merge. Tầng B chỉ rút gọn cách ghi hồ sơ, không bỏ gate. Nội dung đã áp vào AGENTS.md/WORKFLOW.md trong amendment được cho phép; không thay gate G4/publish/CI/merge.

## 4. Việc owner cần làm ngoài code

1. Tạo **owner thứ hai** (người tin cậy hoặc tài khoản owner thứ hai của bố trên thiết bị khác) trong bước nghiệm thu 4 của prompt, trước xác nhận đóng P0 và trước dùng dữ liệu thật. Tạo qua POST admin/staff bằng phiên owner ready có staff.manage/CSRF; B dùng session riêng đổi password/TOTP, kiểm qua auth/me và staff/{idB} theo runbook, không chờ Staff UI.
2. R2: tên bucket `trahien-media-dev`, `trahien-private-dev` là đề xuất (và bản `-prod` khi tới P3). Token tách môi trường, giới hạn bucket/quyền object tối thiểu; bucket evidence không public/domain công khai, GET có kiểm quyền rồi ký URL ≤ 10 phút. Xác minh cấu hình thật ở P1-02; việc owner có tài khoản không chứng minh token/quyền đã sẵn sàng.
3. Chỉ định operator được phép recovery, thiết bị owner dự phòng và nơi giữ runbook/credential. Không đưa secret hoặc dữ liệu thật vào ADR/evidence.

## 5. Kiểm chứng amendment và kế hoạch runtime

- Chạy schema đích trên PostgreSQL **17** sạch với ba role được chuẩn bị, fail-fast (`ON_ERROR_STOP`); riêng EF migrations kiểm trên DB khác, không vừa chạy toàn schema vừa migrate cùng DB. Ở bước amendment, script `init-roles.sql` chưa được tạo (tới closure #1): dùng fixture tạo ba role chỉ trên DB dùng một lần, ghi rõ không là artifact triển khai; sau closure #1 chạy lại bằng script chung. DDL đích đã áp local và test riêng theo evidence dưới đây; không phải EF migration đã kiểm thử. Các tuyên bố thử PostgreSQL 16/17 trong bản đầu vào không có log trong worktree nên **chưa xác minh**, không giữ như kết quả PASS. Đếm tĩnh baseline hiện có 79 bảng/11 schema; A01 thêm 0 bảng đích, 1 sequence. IAM recovery được EF quản lý, cần đối chiếu migration riêng nếu bổ sung cột.
- Kiểm MIME/purpose/bytes trên **cả** upload_sessions/assets (0, giới hạn, +1; SVG/HTML/MIME giả/video catalog bị từ chối); complete R2 private/permission tại P1-02. Kiểm SKU generation/concurrency ở P1-05/07 và D4 theo AC-P1-10; không lấy test CHECK DDL làm bằng chứng cho hành vi app.
- D11: provisioning rỗng/existing/re-run, migration missing-role trước DDL, login thực app/readonly, quyền âm, default privileges/owner đúng, fingerprint dữ liệu/ledger/password không đổi. D8: toàn bộ AC-P0-06; D12: entity trùng tên field nhưng phân loại khác nhau, nested JSON/path, unknown field và rollback audit.
- BR-CAT-14/15, BR-IAM-07/08, AC-P0-06, AC-P1-09/10, DEC-25…30 đã được định nghĩa đúng một lần ở authoritative file theo approval; checker giữ mọi ID cũ và không thêm task ID. Traceability/gaps chỉ cập nhật sau amendment được duyệt; **không** nhận G02/G05/G13/G14/G21/G32 hoặc G34–G38 implementation-resolved chỉ vì ADR đã ACCEPTED. G34–G38 đã có nguồn ở review §8, cần chuyển/mapping sang gap register khi được phép. Phân biệt giải quyết contract bằng spec với hành vi còn chưa implement/test. G14 vẫn cần validation/fallback slugHint ở P1-02; G13 đã áp contract đọc hạn chế vào spec local; code/test chưa triển khai.
- G3 theo từng task/nhóm closure được giao riêng; G4 độc lập; chỉ publish khi có G4 PASS và owner cho phép. CI phải PASS tại **published head SHA**; hiện chưa publish và chưa chạy CI cho đề xuất này.

DDL amendment PostgreSQL 17.12 đã chạy trên container tmpfs/network none dùng một lần: 79 bảng/11 schema, 43 ca constraints và sequence 9999→10000 PASS; container đã xóa. Không kiểm EF migrations/app/runtime. Kết quả, lệnh và SHA256 file DDL tại docs/reviews/a01-amendment-evidence.md. Các test runtime/permissions/R2/MFA/SEO ở trên vẫn NOT RUN; phần ghi kế hoạch không là PASS implementation.

## 6. Đối chiếu D1–D14 và gap register tại baseline

Số dòng dưới đây thuộc `288ca8b`, không phải số dòng sau amendment. Các thay đổi hợp đồng là đề xuất cần owner duyệt, không phải lỗi đã sửa trong spec.

| D | Nguồn authoritative / bằng chứng | Kết luận và xử lý đề xuất |
|---|---|---|
| D1 | `07-roadmap.md:46` §P1 giao toàn catalog/inventory, `:122` §P3 giao redirects; `schema.sql:479,889` reservations + FK, `:1162–1183` reviews FK sales/CRM, `:1121–1127` redirects; G02/G21 | DESIGN_RISK/MISSING_CONTRACT đã có. Scope 25 bảng + sequence tại task P1-01 không có FK tới bảng deferred; hoãn cả `fk_reservations_line` tới P2-05, không tạo ở P2-01. P5-07 tạo review+media sau sales/CRM. Giữ G03 trước P2-01. |
| D2 | `04-business-rules.md:53` BR-CAT-13; `07-roadmap.md:52` P1-07 sau P1-05; FR-CAT-09 `01-requirements.md:73` | Phù hợp draft/no-stock; ưu tiên import sau P1-05 không đổi ID. Không tự upsert nếu thiếu khóa variant ổn định; chi tiết khóa/import lại/slug trùng OPEN cho P1-07, không chặn P1-01. |
| D3 | `04-business-rules.md:25–26` BR-CAT-02/03; `schema.sql:173–183,214–229,265–301,318–333` chưa có mã tham chiếu/sku_base/sequence, variant SKU UNIQUE 2–40 ký tự | MISSING_CONTRACT generator; DDL và BR-CAT-14 mới cần duyệt. Sequence+UNIQUE+khóa/retry bảo vệ concurrency; lưu mã bất biến, không tái đánh số khi thêm variant; giữ giới hạn sửa tay/đổi type. |
| D4 | `04-business-rules.md:28–36` BR-CAT-04.1/7/8 và `09-seo.md:93–98` BR-SEO-34 bắt nội dung để publish; `:42,51,75` manual/site noindex; `:170` thin đã là warning | CONFIRMED_CONFLICT giữa đề xuất và ngưỡng publish hiện hành; DEC-21 đòi sửa 09 cùng 04 trước code. Chỉ thay ngưỡng 150 từ thành thin-description noindex; giữ manual flag, ưu tiên SEO, validation metadata và điều kiện không rỗng hiện hành. Không đề xuất fallback/nới metadata. |
| D5 | `schema.sql:324` giá NOT NULL >0; `04-business-rules.md:30,51,79–80` publish/card/pricing | CONFIRMED_CONFLICT NULL với DDL hiện hành; cần amendment cùng luật ghi/publish/sale-price/store/order. Finalize xác định inactive được NULL, không bán; kích hoạt phải validate, publish chỉ đòi giá ở active. |
| D6 | DEC-19 `00-README.md:103`, `02-architecture.md` §5.10, `06-infrastructure.md` §1/§8/§9 | Phù hợp R2 hiện có trong kiến trúc; token/bucket thực tế là EXTERNAL_VERIFICATION ở P1-02. D6 không tự đóng G24 về vị trí dữ liệu/pháp lý hoặc Q8 SMTP/backup/VPS. |
| D7 | FR-RET-01 `01-requirements.md:132`, BR-RET-01 `04-business-rules.md:191`, upload ảnh `02-architecture.md:183–185`, `schema.sql:140–169` | G05 CONFIRMED_CONFLICT video và upload ảnh; §3.1/3.4 nới mp4/quicktime evidence 50 MiB, giữ ảnh 15 MiB/private. Không mở video catalog hoặc đổi return policy G27. |
| D8 | BR-IAM-01/02/06 `04-business-rules.md:10–20`; FR-IAM-02/03 `01-requirements.md:55–56`; `02-architecture.md` §5.4/§6; `07-roadmap.md:25,29,34` P0-08/12/AC-P0-02 | MISSING_CONTRACT recovery; finalize xác định operator gate, audit nguyên tử, revoke, bắt password+MFA lại và failure tests. Hai owner/thiết bị và checklist vận hành giữ theo đề xuất; CLI/evidence phải có trong closure #2 trước P1-01, chưa có runtime evidence. |
| D9 | `07-roadmap.md:3,46` hoàn tất P0 trước P1-01; prompt bảng bước 2–5; bản đầu P1-01 §1 nói không phụ thuộc closure MFA | Xung đột nội bộ bản đầu đã hòa giải theo chỉ dẫn finalize: closure #1/#2 + runbook/enrollment + hồ sơ đóng P0 trước P1-01. Mốc này sớm hơn review §6 và do owner chỉ định; D12 vẫn trước P1-04, không thêm gate P1-01. |
| D10 | `AGENTS.md` G4/Publish; `docs/WORKFLOW.md` §5 | Đề xuất quy trình ngoài authoritative spec. A/B vẫn độc lập/cùng worktree/toàn change set, không hạ gate publish. Chưa áp trước owner duyệt riêng; P1-01 theo G4 đầy đủ hiện hành. |
| D11 | `03-database.md:105–120` roles/exceptions/F-02; `06-infrastructure.md:192–208` init hook + CREATE vô điều kiện; `infra/compose.dev.yml:10–12` chỉ mount pgdata | DESIGN_RISK và path mismatch của đề xuất `roles.sql` so với spec `init-roles.sql`. §3.5 thống nhất path, provisioning hai trường hợp và migration sửa quyền nền; test không viết grants thay thế. Không cần xóa dữ liệu hay chỉnh migration cũ. |
| D12 | `02-architecture.md:122–129` §5.4 Safe/PII/secret, UUID/TEXT và transaction; NFR-04 `01-requirements.md:201` | Phù hợp deny-by-default nhưng bản đầu gọi tên nghệ nhân Safe trái chính sách tên người PII. Đã sửa đề xuất theo entity/path, giữ nested redaction và settings hiện có; không tự làm public một field để phục vụ audit. |
| D13 | `05-api.md:10` uint; `03-database.md:15` xmin; `backend/src/TeaPremium.Application/Iam/StaffContracts.cs:8` RowVersion string | CONFIRMED_CONFLICT đề xuất opaque string với contract uint; giữ nguồn xmin cho catalog và ConcurrencyStamp riêng IAM. Contract/client/tests phải đổi có chủ đích sau duyệt, không đổi sang SecurityStamp. |
| D14 | BR-INV-11/13 `04-business-rules.md:73,75`; `03-database.md:64–65` Telegram/reservations; `07-roadmap.md:55,91,94` P1-10/P2-13/P2-16 | Finalize xác định consumer nội bộ P1 an toàn/idempotent, retry có giới hạn/kết thúc, không phát lại cảnh báo cũ khi P2 có Telegram. Giữ phân pha đối soát, UI kho P1-13/dashboard P2-16; cần amendment 03 §3.3 và dependencies sau approval. |

**Path/dependency kiểm trực tiếp:** `AppDbContext`, `PlatformSchemaParityTests`, `PlatformDatabaseTests`, `EfUnitOfWork` tồn tại; các path nằm ở task P1-01 §2. `infra/postgres/init-roles.sql` và `backend/src/TeaPremium.Infrastructure/Persistence/GrantsSql.cs` **chưa tồn tại**, là sản phẩm closure dự kiến. Baseline có **3 migration** (InitialPlatform, StaffIam, SettingsAdministrativeData), không phải 4 như task record ban đầu. `scripts/migrations.ps1` có thật và chỉ sinh SQL, không áp DB. Startup EF design-time dùng project Infrastructure theo script hiện hành; `check:api` là script ở package.json root.

**Phạm vi đã áp đồng bộ local (G4 pending):** ngoài §3.7, D5 bổ sung BR-CAT-11/BR-PRC-01/02 trong 04; D14 bổ sung 03 §3.3; D11 bổ sung 06 §3/§5/02 §2. G34–G38 có trong `docs/reviews/P0-closure-and-P1-readiness-review.md` §8, đã map vào cập nhật A01 trong `docs/analysis/spec-gaps.md`; chỉ CONTRACT_ALIGNED, không nhận implementation resolved. Không coi tài liệu G2 lịch sử PROPOSED là bằng chứng approval mới cho A01/P1-01.

**Hai chi tiết báo cáo cần đọc cùng DDL:** review R1 nói P1 thêm 4 schema, nhưng D1 đưa redirects vào nên scope là **5 schema**; review §5.3 nói P2-05 thêm cột reserved, nhưng `inventory.stock_levels.reserved` đã có trong `schema.sql:404` và thuộc P1-01. P2-05 chỉ thêm bảng reservations/FK/hành vi. Review §6 C1–C3 là điều kiện kiến nghị của reviewer; task record §0 diễn đạt gate hiện hành **đề xuất theo A01**, không gán các gate mới đó cho reviewer gốc.

## 7. Đối chiếu prompt P0 Closure và dependencies

Đã đọc `C:/Users/ADMIN/Downloads/PROMPTS-p0-closure.md`; đây là nguồn ngoài repo, **không chỉnh file đó trong task này**. Các sửa dưới là yêu cầu đề nghị đưa vào prompt khi owner giao việc; không tự thi hành các khối có câu "Owner đã DUYỆT".

| Vị trí prompt (dòng) | Lệch/rủi ro | Nội dung đề nghị đồng bộ |
|---|---|---|
| Bước 1: 21–31 | Câu approval mẫu; `roles.sql`; đánh resolved hàng loạt; script role chỉ tạo ở bước 2 | Chờ approval thực, dùng path thống nhất `infra/postgres/init-roles.sql`. Chuẩn bị role fixture chỉ cho DDL amendment dùng một lần (§5), rồi kiểm lại bằng script sau closure #1. Chỉ ACCEPTED theo quyết định owner, ghi SHA khi có thật; phân biệt contract resolved với implementation còn pending. |
| Bước 2: 56–71 | `roles.sql` khác spec; helper ví dụ `Persistence/Migrations/GrantsSql.cs` chưa thống nhất; chỉ mount init hook không áp existing volume | Đề nghị SQL `infra/postgres/init-roles.sql`, wrapper mới `infra/postgres/init-roles.sh`, helper `backend/src/TeaPremium.Infrastructure/Persistence/GrantsSql.cs`, entry point mới `scripts/dev-migrate.ps1`; thêm nhánh existing DB + backup/ownership preflight; không xóa DB/volume, không tự reset password. Role/SQL/helper paths đều là **dự kiến**, chưa tồn tại. |
| Bước 2: 60–68 | Helper check role ở migration mới không bảo vệ được GRANT ở P0-09 cũ; thiếu test existing/re-run | Preflight trước toàn chuỗi ở dev-migrate; thêm migration mới sửa quyền nền; test thực app/readonly + fingerprint dữ liệu/IAM/ledger/password, re-run không đổi. Không sửa migration cũ hoặc cấp superuser để test PASS. |
| Bước 2: 73–81; bước 4 | Runbook DB sạch chưa có nhánh DB hiện hữu; HTTP Compose không đủ đăng nhập Admin | `docs/plans/p0-closure-acceptance.md` mới ghi **hai** nhánh DB; migrate tp_owner/API tp_app, dùng HTTPS/Caddy thật và luồng bootstrap/password/TOTP/settings/audit/CSRF/lockout/logout. Dùng Caddy local hiện có nếu chưa thêm profile Compose; không coi config --quiet là PASS login. Ghi kết quả thật/test identity giả và giữ dữ liệu hiện hữu. |
| Bước 3: 91–92 | Đường dẫn code rút gọn dễ nhầm | `backend/src/TeaPremium.Api/Program.cs`, `backend/src/TeaPremium.Infrastructure/Iam/StaffIdentity.cs`, `backend/src/TeaPremium.Infrastructure/Iam/StaffTotp.cs`. |
| Bước 3: 95–107 | Thiếu operator gate; xóa failed instants có thể unlock; MustChangePassword=true nhưng test chỉ bắt đổi khi reset; non-owner có thể bỏ MFA sau password change | Theo BR-IAM-07/AC-P0-06: ủy quyền OS/SSH, giữ lockout; đổi password cả hai mode + recovery state bền vững ép verify TOTP; không HTTP/listener/jobs; audit nguyên tử không raw reason/secret, revoke request kế tiếp và failure/concurrency tests. `ownerMfaCoverageOk` chỉ owner, kiểm 0/1/2 và reset. |
| Bước 5: 129–132; prompt chưa có bước publish | DONE/CI có thể ghi quá sớm; quá trình dừng local không tự tạo SHA merge | Chỉ cập nhật trạng thái từ evidence và SHA thật. Mỗi slice dừng READY_FOR_LOCAL_G4; G4 độc lập → owner cho publish → CI đúng published SHA → owner merge. Không suy quyền publish từ approval ADR hoặc câu mẫu. |

Thứ tự đề nghị bám đúng prompt: **owner review → scoped spec/workflow amendment được duyệt → closure #1 (R1/R3: grants + Compose/runtime + runbook) → closure #2 (R4: recovery + coverage warning) → owner nghiệm thu runbook/tạo owner thứ hai → hồ sơ đóng P0 (R14) → P1-01**. Các nhãn #1/#2 là remediation thuộc P0-02/04 và P0-08/12, không cấp task ID mới. Giao một slice được duyệt mỗi lần; việc dùng chung P0-09/10 tests không tạo roadmap khác. D12/R2/I-04 giữ gate trước P1-04; D8 chỉ cần allowlist riêng cho audit recovery, không buộc refactor toàn bộ AuditValuePolicy trước P1-01.

Review §6 chỉ yêu cầu grants trước/cùng P1-01 và MFA trước dữ liệu thật. Owner đã chỉ định trong yêu cầu finalize: **closure #1, #2 và nghiệm thu trước P1-01**. Bản ADR/P1-01 thống nhất mốc đó, không gán cho reviewer gốc và không tự coi toàn ADR đã ACCEPTED. D3/D4/D5/D7 chỉ làm DDL nền ở P1-01; generator/publish/upload/SSR đúng task P1-02/05/07/14/P3. R2 thật chưa cần cho migration dùng dữ liệu giả; trước nhập ảnh/catalog thật phải đạt D6/D8.

## 8. Nội dung finalize và các điểm còn OPEN

Owner đã phê duyệt toàn ADR và chín chỉ dẫn dưới, cộng hai bổ sung backup/điều tra và owner thứ hai qua API. Approval chỉ cho amendment specs/workflow, không tự cấp quyền G3/publish. Hợp đồng importer hoãn P1-07 vẫn OPEN.

| # | Nội dung đưa vào bản phê duyệt | Đối chiếu |
|---|---|---|
| 1 | SKU sequence + UNIQUE + retry có giới hạn; mã đã cấp không tự đổi theo type/material/thêm variant | D3, §3.1(b), BR-CAT-14 |
| 2 | Mô tả dưới 150 từ chỉ tạo thin-description noindex; không ghi đè manual/điều kiện SEO khác; giữ validation metadata và các điều kiện publish khác | D4, BR-CAT-04, §3.3 |
| 3 | Publish đòi giá ở active; inactive được NULL nhưng không bán; kích hoạt phải validate giá | D5, BR-CAT-15, AC-P1-09 |
| 4 | Không tự upsert variant thiếu khóa ổn định; chi tiết giao P1-07 | D2, BR-CAT-13 |
| 5 | MFA CLI có operator authorization, audit cùng transaction, revoke phiên và bắt đổi password/enroll TOTP lại | D8, BR-IAM-07, AC-P0-06 |
| 6 | Hoàn tất closure #1, #2 và nghiệm thu trước P1-01; D12 vẫn trước P1-04 | D9, §7, P1-01 C2 |
| 7 | Review A/B vẫn G4 độc lập, owner cho publish và CI đúng published head; owner giữ merge | D10, §3.9 |
| 8 | Đọc inventory hạn chế chỉ còn/hết, không số lượng/giá vốn | G13, §3.6 |
| 9 | InventoryAlert nội bộ P1 an toàn/idempotent, retry có giới hạn; P2 không replay cảnh báo cũ | D14, §3.7 |

**OPEN được hoãn đúng task:** P1-07 phải chốt khóa nhận diện variant, import lại/ô trống, xử lý nhóm mâu thuẫn và tính nguyên tử theo nhóm trước khi triển khai importer. Quy tắc không upsert khi thiếu khóa đã rõ; chi tiết còn mở không chặn P1-01. Không còn OPEN đối với chín nguyên tắc trên.

**Việc vận hành/evidence chưa hoàn tất, không phải quyết định sản phẩm mới:** owner chỉ định operator/allowlist và thiết bị dự phòng trong runbook; provisioning hiện hữu có ownership bất thường phải có remediation theo object được owner cho phép; R2 token/bucket và các test/G4/CI vẫn cần bằng chứng ở đúng mốc. Các gap ngoài scope như G03/G27 giữ mốc cũ, không thêm blocker cho P1-01.

**Kết luận:** ADR **ACCEPTED / OWNER APPROVED**; amendment **READY_FOR_LOCAL_G4**, không tự nhận G4 PASS. P1-01 và closure implementation HOLD tới khi đủ gate/owner giao G3 riêng. Đã áp specs/workflow và tài liệu hỗ trợ; không source/migration/commit/push/PR/merge. Chỉ chạy DDL trên container PostgreSQL 17 dùng một lần, không DB thật.


Bổ sung ngày owner duyệt: quy trình backup và điều tra tách biệt được áp ở 03 §4/06 §5–6; phương án owner thứ hai API tại docs/plans/p0-closure-acceptance.md, BR-IAM-08 và 05 §4.1. Hợp đồng importer P1-07 chưa chốt, không suy approval thành upsert tự động.
