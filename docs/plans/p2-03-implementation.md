# P2-03: Import khách cũ dry-run (BR-CRM-03)

> **Immutable Implementation Record (`WORKFLOW.md` §2.2):** Tài liệu này ghi nhận phạm vi, truy vết đặc tả, quyết định thiết kế đã phê duyệt, danh sách tài sản thay đổi và ma trận kiểm thử trước thời điểm đóng dấu `candidate.manifest`. Các trạng thái động (**Dynamic Task Status**: `manifest_sha256`, đường dẫn `verify.log` gắn manifest, verdict Independent G4, published commit SHA, PR URL, kết quả 4 job GitHub Actions và trạng thái merge) được lưu tại báo cáo bàn giao, PR body và `artifacts/`.

- **Tier:** `A` — nhập hàng loạt PII khách hàng (tối đa `2.000` dòng/lần: họ tên, SĐT, Zalo, nhãn, ghi chú tương tác), phân quyền Admin (`customers.write` + `AdminSecurity.Ready` + `X-CSRF-TOKEN`, chặn `content` `403` theo `AC-P2-09`), cấp phát sequence `crm.customer_code_seq` & ghi nguyên tử nhiều bảng dưới transaction/lock/savepoint, bật `[Audited]` + `AuditValuePolicy` cho `crm.interactions` không rò PII thô, và đóng carry-over bắt buộc `[P2]` từ G4 `P2-02` bằng rule kiểm biên module mức IL.
- **Approved BASE & Context:** `origin/main` SHA **`82889b8ee88f88c4a8b47f74d73bc0b95e0b8831`** (đã chứa Merge PR `#45` `P2-02`, PR `#46` `P2-05`, PR `#47` `P2-13`, PR `#48` Knowledge Recovery `ADR-P2-01`, PR `#49` PR-2 Verification Tooling Hardening và PR `#50` Workflow V2 Governance), nhánh `feature/p2-03-customer-import`, worktree `D:\tra-hien-p2-03`.
- **Readiness & Decision References:**
  - **Full G1 Audited Spec SHA:** `cadce575597738bf40ad7c45689482de5a5fbc3d` ([spec-inventory.md](../analysis/spec-inventory.md), [spec-gaps.md](../analysis/spec-gaps.md)), kèm các mốc targeted `A01`, `B01` (`dadc4435e8f115faaeb9e8511e56e09d5d517a8a`), `P1-04` (`f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7`), `P1-07` (`ed49d11d945ec27033489d4b834dd49b8c9bb6ed`) và commit chạm `docs/spec/` gần nhất `57cb13fb4b03e28749e06ed8a136b6b5e6fa1748` (`P2-01`). Kiểm tra `git diff 57cb13f..82889b8 -- docs/spec/`: `0` thay đổi; `BR-CRM-03` (`04-business-rules.md:222`), `05-api.md:214`, `schema.sql:556-673` và `AC-P2-08` (`07-roadmap.md:113`) không đổi.
  - **G2 / Dependencies:** `docs/architecture/conformance-review.md`, [task-readiness.md](task-readiness.md), [ADR-P2-01-phase2-contracts.md](../decisions/ADR-P2-01-phase2-contracts.md); phụ thuộc `P2-01` (`85218d1`) và `P2-02` (`97735e7`) đã merge trên `origin/main`.
  - **Trạng thái phê duyệt quyết định (`O4`):** **Owner phê duyệt tường minh ngày `2026-11-26`** (`OWNER_APPROVED`, `Clarification only` — không sửa `docs/spec/`):
    - **`O4.1 = B`:** `POST /api/v1/admin/customers/import?dryRun=false` trả HTTP `200` kèm `summary.committed: bool` (`!dryRun && errorCount == 0`). Khi `errorCount > 0`: dừng sớm trước `nextval('crm.customer_code_seq')`, ghi `0` customer/tag/interaction/audit và tiêu hao `0` sequence (`committed = false`, dòng hợp lệ ở trạng thái `would_import`). Khi `errorCount == 0` (kể cả có `duplicate_in_file` / `existing_phone_skipped` theo `AC-P2-08` hoặc 100% `skipped`): `committed = true`.
    - **`summary.newTags[]`:** liệt kê các tag mới chưa có trong `crm.tags` (khử trùng không phân biệt hoa thường, giữ thứ tự xác định) ở cả `dryRun=true` và `dryRun=false` (`newTags.Count == createdTagsCount` khi `committed = true`).
    - **`O4.2 = A`:** cột `note` khác rỗng tạo 1 `crm.interactions(kind='note', channel='other')`, `last_contacted_at = null`, `note_summary = null`, gắn `[Audited]` + `AuditValuePolicy` che cứng `content`/`follow_up_result`.
    - **`O4.3 = A`:** cột `tags` tách theo `,`/`;`, tự tạo tag mới (`color = null`) khi chưa tồn tại; `.xlsx` dùng chế độ `SingleOrNamed("KhachHang")`.
    - **`possible_duplicate_zalo_customer`:** giữ `zalo_only_reimport_risk` cho mọi dòng Zalo-only hợp lệ; gắn thêm cảnh báo `possible_duplicate_zalo_customer` khi cặp chuẩn hóa `(full_name.Trim().ToLowerInvariant(), zalo_name.Trim().ToLowerInvariant())` khớp với khách active Zalo-only trong DB (`merged_into_customer_id IS NULL AND phone_e164 IS NULL AND zalo_name IS NOT NULL`) hoặc dòng Zalo-only hợp lệ trước đó trong cùng tệp; **không** tự gộp/bỏ qua theo `zalo_name`, không thêm `UNIQUE` hay migration.
  - **Chính sách review (Workflow V2):** Áp dụng chính sách độc lập mô hình của `AGENTS.md` / `docs/WORKFLOW.md` — tính độc lập của G4 dựa trên ngữ cảnh hội thoại tách biệt, quyền read-only, bằng chứng kiểm chứng được và danh tính ứng viên chính xác (`candidate.manifest`), không phụ thuộc nhà cung cấp hay tên mô hình.
- **Migration / Recovery:** `none` — toàn bộ bảng `crm.customers`, `crm.tags`, `crm.customer_tags`, `crm.interactions`, sequence `crm.customer_code_seq`, chỉ mục `ux_customers_phone` / `ux_tags_name` / `ix_customers_search` và grants cho `tp_app` (`INSERT`/`SELECT`/`UPDATE` các bảng CRM, `USAGE` trên `crm.customer_code_seq`, `EXECUTE` trên `public.f_unaccent(text)` và `pg_catalog.pg_advisory_xact_lock(bigint)`) đã có từ `P0-04`/`P2-01`. `P2-03` chỉ gắn `[Audited]` mức C# lên `Interaction`, không đổi EF model snapshot hay DDL. Rollback bằng cách revert PR; import không bao giờ ghi đè hồ sơ có sẵn.

---

## 1. Định danh đặc tả và ranh giới phạm vi

### 1.1 Nguồn đặc tả và mã định danh (`ID`, `FR`, `BR`, `NFR`, `AC`, `DEC`)

- **Task ID:** `P2-03` — *Import khách cũ dry-run (BR-CRM-03)* ([07-roadmap.md](../spec/07-roadmap.md) dòng `88`, phụ thuộc `P2-02` dòng `87`).
- **Tham chiếu đặc tả:**
  - [00-README.md](../spec/00-README.md)`:40-46` (UTC `timestamptz`, `Guid.CreateVersion7()`, lỗi tiếng Việt), `:76` (`Q5`: mặc định `had_purchase = true` khi import khách cũ), `:85-115` (`DEC-01`, `DEC-03`, `DEC-14`, `DEC-15`, `DEC-16`, `DEC-30`).
  - [01-requirements.md](../spec/01-requirements.md)`:35-43` (vai trò Admin), `:138` (`FR-CRM-01`), `:141` (`FR-CRM-04`), `:142` (`FR-CRM-05` — chỉ nhánh tạo ghi chú `kind=note` từ cột `note` lúc import), `:190` (`FR-PLT-01`), `:196-210` (`NFR-01`, `NFR-04`, `NFR-07`, `NFR-08`, `NFR-09`, `NFR-12`).
  - [02-architecture.md](../spec/02-architecture.md)`:68-77` (§3 luật phụ thuộc & ranh giới module), `:105-108` (§5.2 `IUnitOfWork`), `:122-129` (§5.4 `[Audited]` & `AuditValuePolicy`), `:178-180` (§5.9 ProblemDetails & log che PII), `:189-235` (§6–§7 `AdminCookie`, CSRF, ma trận quyền).
  - [03-database.md](../spec/03-database.md)`:10-18` (§1 mã `KH` + `crm.customer_code_seq`, `xmin`), `:76-80` (§3.5 khách hàng & `had_legacy_purchase`), `:94-99` (§3.7 `f_unaccent` & tìm đuôi SĐT), `:104-107` (§3.9 audit), `:134-158` (§4 quyền `tp_app`).
  - [schema.sql](../spec/schema.sql)`:556-608` (`crm.customer_code_seq`, `crm.customers`, `crm.tags`, `crm.customer_tags`), `:655-673` (`crm.interactions`).
  - [04-business-rules.md](../spec/04-business-rules.md)`:220-222` (`BR-CRM-01`, `BR-CRM-02`, `BR-CRM-03`), `:225-226` (`BR-CRM-06`, `BR-CRM-07`), `:237` (`BR-CRM-14`).
  - [05-api.md](../spec/05-api.md)`:3-29` (§1 quy ước chung, CSRF, `ProblemDetails`), `:130` & `:169` (mẫu import multipart P1), `:214` (`POST admin/customers/import?dryRun=`, quyền `customers.write`).
- **Mã định danh kiểm chứng trực tiếp:**
  - `DEC`: `DEC-01`, `DEC-03`, `DEC-14`, `DEC-15`, `DEC-16`, `DEC-30`.
  - `FR`: `FR-CRM-04`, `FR-CRM-01` (gắn tags/nhóm cho khách import), `FR-PLT-01`.
  - `BR`: **`BR-CRM-03`** (trọng tâm), **`BR-CRM-01`** + `R-P201-04` (chuẩn hóa SĐT di động VN), **`BR-CRM-02`** (không trùng SĐT active chưa gộp), **`BR-CRM-06`** (`occurred_at <= now`), **`BR-CRM-07`** (`kind = 'note'` không đổi `last_contacted_at`), **`BR-CRM-14`** (`search_text = f_unaccent(...)`).
  - `NFR`: `NFR-01`, `NFR-04`, `NFR-07`, `NFR-08`, `NFR-09`, `NFR-12`.
  - `AC`: **`AC-P2-08`** (`07-roadmap.md:113`: 500 dòng với 3 SĐT trùng trong file + 2 SĐT đã có sẵn), **`AC-P2-09`** (`07-roadmap.md:114`: `content` → `403`), **`AC-P0-04`** (audit che PII) và **`AC-P0-05`** (rule kiến trúc mức IL).

### 1.2 Trong phạm vi / Ngoài phạm vi

- **Trong phạm vi (`P2-03`):**
  1. Endpoint `POST /api/v1/admin/customers/import?dryRun=true|false` (`multipart/form-data`, quyền `customers.write` + `AdminSecurity.Ready` + `X-CSRF-TOKEN`).
  2. Tách lõi đọc CSV/XLSX an toàn thuần BCL từ `ProductImportFile` sang `TeaPremium.Application.Import.TabularImportFile` với `ImportTableSchema` tổng quát (giữ 100% hành vi 500 dòng của `P1-07`/`P1-08`, mở `MaxRows = 2000` cho `P2-03` và cho phép unit-test trực tiếp trong `TeaPremium.UnitTests`).
  3. Domain/Application/Infrastructure cho preview (`dryRun=true`) và apply (`dryRun=false`) theo quyết định Owner `O4` (`2026-11-26`): `O4.1 = B` (`HTTP 200` + `summary.committed: bool`, dừng sớm trước sequence khi `errorCount > 0`), `summary.newTags[]`, cảnh báo `zalo_only_reimport_risk` và `possible_duplicate_zalo_customer` (không tự gộp/bỏ qua theo `zalo_name`), `O4.2 = A`, `O4.3 = A`, và xử lý tranh đua `23505` savepoint đơn điệu không thu hẹp `existingPhones`.
  4. Bật `[Audited]` cho `Interaction`, cập nhật `AuditSaveChangesInterceptor` (`or Interaction`) và khai báo allowlist fail-closed cho `crm.interactions` trong `AuditValuePolicy` (che `content` và `follow_up_result`).
  5. Đóng finding `[P2]` của G4 `P2-02`: thêm `CrmModuleBoundaryRule` bằng `Mono.Cecil` trong `TeaPremium.ArchitectureTests` (fail-closed, `0` ngoại lệ sau khi chuyển dòng đăng ký `ICustomerOrderQueries` từ `CrmRegistration` sang `OrdersRegistration`), kèm `7` mẫu vi phạm cố ý biên dịch thật (bao gồm `[return: ...]`), `1` mẫu đối chứng hợp lệ, kiểm tra parity với toàn bộ `AppDbContext.Model` và gỡ kiểm tra reflection nông khỏi `CustomerTagsAddressesAndSecurityTests.cs`.
  6. Tái sinh `packages/api-client/openapi.json` + `packages/api-client/src/generated/schema.ts` qua `./scripts/verify-linux.ps1 -Mode api -WriteBack` và thêm `packages/api-client/src/customers-contract.test-d.ts`.
  7. Đồng bộ hồ sơ bền vững [ADR-P2-01-phase2-contracts.md](../decisions/ADR-P2-01-phase2-contracts.md) và [spec-gaps.md](../analysis/spec-gaps.md) cho các quyết định Owner ngày `2026-11-26` (`O1 = A`, `O2 = A`, `O4` gồm `O4.1 = B`, `O4.2 = A`, `O4.3 = A`), giữ nguyên trạng thái chưa duyệt cho toàn bộ mục còn lại (`O3a`, `O3b`, `ASSUMPTION-P207-01..04`, `O5`, `O6a`, `O6b`, `O7`, `G27` diff, `G24`, `Q1`–`Q8`).
- **Ngoài phạm vi (tuyệt đối không làm ở `P2-03`):**
  - Chỉnh sửa bất kỳ tệp nào trong `docs/spec/`.
  - CRUD tương tác đầy đủ (`GET/POST/PATCH admin/customers/{id}/interactions[/{iid}]`), hoàn thành hẹn (`complete-follow-up`), danh sách `GET admin/follow-ups` → **`P2-04`**.
  - Giao diện Admin UI khách hàng & màn hình upload/preview import (`apps/admin`) → **`P2-14`**.
  - Gộp khách (`merge`), xóa ẩn danh (`erase`), xuất dữ liệu (`export`), quản lý `consents` → **`P2-12`**.
  - Sở thích khách hàng (`interests`) và job `RecomputeCustomerStats` → **`P2-11`**.

---

## 2. Hợp đồng API multipart, giới hạn tệp, cơ chế preview/apply và mã lý do dòng

### 2.1 Endpoint, bảo mật và cấu trúc multipart

- **Route:** `POST /api/v1/admin/customers/import?dryRun=true|false` (`05-api.md:214`, nhóm tag OpenAPI `"Customers"`).
- **Bảo mật bắt buộc (áp cho cả `dryRun=true` và `dryRun=false`):**
  - HTTPS (`400 https_required`), đúng `AdminHost` nếu cấu hình (`403 forbidden`), cookie `AdminCookie` thỏa `AdminSecurity.Ready` (`401 unauthenticated` / `403 forbidden`), claim permission `customers.write` (`owner`, `sales` được phép; `content` bị chặn `403 forbidden` trước khi đọc body — `AC-P2-09`), header `X-CSRF-TOKEN` khớp cookie antiforgery (`400 csrf_invalid`), response luôn có `Cache-Control: no-store`.
- **Query string:** bắt buộc có đúng 1 tham số `dryRun` mang giá trị `"true"` hoặc `"false"` (thiếu, lặp hoặc giá trị khác → `400 invalid_mode`).
- **Multipart body:** `Content-Type: multipart/form-data; boundary=...` (boundary dài `1..70`), chứa **đúng 1 form section** có `name="file"` và `filename` khác rỗng (tái sử dụng `ProductImportEndpoint.ReadUploadAsync`), cấu hình OpenAPI qua `.AddOpenApiOperationTransformer(...)` để mọi lỗi `415` trả `application/problem+json` có `code` và `traceId`.

### 2.2 Giới hạn tệp, chống tấn công CSV/XLSX và quy tắc cột (`TabularImportFile`)

Tách lõi parser thuần BCL từ `TeaPremium.Api/Catalog/ProductImportFile.cs` sang `TeaPremium.Application/Import/TabularImportFile.cs` với cấu hình `ImportTableSchema` (`ValidateHeader`, `RequiredColumns`, `MaxRows`, `SheetMode`, `PreferredSheetName`). `ProductImportFile` giữ facade nội bộ để không làm đổi hành vi `P1-07`/`P1-08`:

| Hạng mục | Quy tắc kiểm tra | Mã lỗi & HTTP status khi vi phạm |
|---|---|---|
| Định dạng tệp | Chỉ nhận `.csv` (UTF-8 nghiêm ngặt, cho phép đúng 1 BOM đầu `\uFEFF`, từ chối UTF-16/UTF-32/byte lỗi) và `.xlsx` (OOXML chuẩn, không `vbaProject`/`macroEnabled`) | Khác `.csv`/`.xlsx`, không phải `multipart/form-data`, hoặc chứa macro/VBA → `415 unsupported_format`; UTF-16/UTF-8 hỏng → `400 invalid_utf8` |
| Dung lượng upload | Tệp ≤ `5 MiB` (`5.242.880` bytes), tổng multipart request ≤ `5 MiB + 64 KiB` | Vượt ngưỡng → `413 file_limit_exceeded` |
| Chống zip-bomb / XML bomb | Tổng dung lượng giải nén các entry XLSX ≤ `32 MiB`, tối đa `4.096` ZIP entries, `XlsxPartStream` kiểm byte thực đọc không vượt `entry.Length` khai báo, `DtdProcessing.Prohibit`, `XmlResolver = null`, độ sâu XML ≤ `48`, cấm `TargetMode="External"` hoặc path traversal (`..`, `.`, `\`, `:`, `?`, `#`, `%`), tổng ký tự văn bản ô ≤ `32 MiB` | Quá dung lượng/số cột/số phần → `413 file_limit_exceeded`; cấu trúc hỏng/path lạ → `400 malformed_file` / `invalid_part` / `invalid_xml_depth` |
| Chống công thức (Formula) & tương thích SĐT `+84` | XLSX chứa thẻ `<f>` trong ô → từ chối ngay (`400 formula_not_allowed`). CSV **không** từ chối ô bắt đầu bằng `+` vì SĐT E.164 hợp lệ luôn bắt đầu bằng `+84...` | Ô XLSX có `<f>` → `400 formula_not_allowed` |
| Trang tính XLSX (`O4.3 = A`) | `.xlsx` có đúng 1 worksheet (bất kể tên) → đọc trang đó; nếu có nhiều worksheet → tìm duy nhất trang tên `KhachHang` | Không xác định được trang tính duy nhất → `400 missing_sheet` |
| Header (dòng 1) | Đúng tập 7 cột hợp lệ theo `BR-CRM-03`: `full_name`, `phone`, `zalo_name`, `customer_group`, `tags`, `note`, `had_purchase`. Bắt buộc có `full_name`; cột tùy chọn vắng mặt coi như rỗng. Tối đa `100` cột, cấm tên cột lạ, cấm lặp cột, cấm ô ngoài header | Thiếu dòng 1 → `400 missing_headers`; thiếu `full_name` → `400 missing_header`; cột lạ → `400 invalid_header`; lặp cột → `400 duplicate_header`; ô thừa → `400 extra_cells` |
| Số dòng dữ liệu | Bỏ qua dòng hoàn toàn trắng; yêu cầu `1..2.000` dòng dữ liệu không trắng (`04-business-rules.md:222`). Ô boolean XLSX `<c t="b">` tự đổi thành `"true"`/`"false"` | `0` dòng dữ liệu → `400 missing_rows`; `> 2.000` dòng dữ liệu → `413 file_limit_exceeded` |

### 2.3 Cơ chế preview/apply theo quyết định `O4.1 = B` (`2026-11-26`)

- **`dryRun = true` (`PreviewCustomerImport`):** chạy truy vấn `AsNoTracking`, **không** mở transaction ghi, **không** gọi `nextval('crm.customer_code_seq')`, **không** ghi DB/audit. Trả HTTP `200` với `summary.dryRun = true`, `summary.committed = false`, `summary.newTags` liệt kê các tag mới sẽ tạo từ các dòng `would_import`, và kết quả từng dòng kèm `warnings`.
- **`dryRun = false` và `errorCount > 0` (`O4.1 = B`):** bên trong `ApplyCustomerImport` (`EfUnitOfWork.ExecuteAsync` + `pg_advisory_xact_lock`), sau khi validate toàn bộ dòng và đọc tập SĐT / Zalo hiện thời, **dừng sớm trước `nextval('crm.customer_code_seq')`** — ghi `0` customer, `0` tag, `0` interaction, `0` audit log và tiêu hao `0` sequence. Trả HTTP `200` với `summary.dryRun = false`, `summary.committed = false`, `importedCount = 0`, `createdTagsCount = 0`, `createdInteractionsCount = 0`; các dòng hợp lệ giữ `status = "would_import"`, `reasonCode = "would_import"` (`customerId = null`, `customerCode = null`, đã resolve `tags` và `warnings`), và `summary.newTags` vẫn liệt kê danh sách tag mới sẽ được tạo của các dòng hợp lệ đó.
- **`dryRun = false` và `errorCount == 0` (`O4.1 = B` + `AC-P2-08`):** `summary.committed = true` (kể cả khi có `duplicate_in_file` / `existing_phone_skipped` hoặc kể cả khi 100% dòng đều bị `skipped`). Chỉ cấp mã `KH` và ghi DB cho các dòng hợp lệ (`imported`); khi `0` dòng cần import thì không tiêu hao sequence. Khi `committed = true`, `summary.newTags.Count == summary.createdTagsCount`.

### 2.4 Cấu trúc JSON response `CustomerImportResult`, mã lý do dòng và cảnh báo

```json
{
  "summary": {
    "dryRun": false,
    "committed": true,
    "totalRows": 500,
    "readyCount": 495,
    "importedCount": 495,
    "skippedCount": 5,
    "duplicateInFileCount": 3,
    "existingPhoneSkippedCount": 2,
    "errorCount": 0,
    "createdTagsCount": 2,
    "createdInteractionsCount": 120,
    "newTags": ["VIP", "Ấm Tử Sa"]
  },
  "rows": [
    {
      "row": 2,
      "status": "imported",
      "reasonCode": "imported",
      "customerId": "019ab1c0-...",
      "customerCode": "KH001000",
      "normalizedPhone": "+84901234567",
      "existingCustomerId": null,
      "duplicateOfRow": null,
      "createNoteInteraction": true,
      "tags": ["VIP", "Ấm Tử Sa"],
      "warnings": [],
      "errors": []
    }
  ]
}
```

**Danh sách đầy đủ `status` và `reasonCode` từng dòng (khai báo enum chặt trong OpenAPI):**

| `status` | `reasonCode` | Cột lỗi (`errors[].column`) | Ý nghĩa nghiệp vụ |
|---|---|---|---|
| `imported` | `imported` | — | (`dryRun=false`, `committed=true`) Đã tạo mới `crm.customers` (`origin='legacy_import'`, `acquisition_source='legacy'`), gắn `tags` và tạo `interaction(note)` nếu có |
| `would_import` | `would_import` | — | (`dryRun=true`, hoặc `dryRun=false` nhưng `committed=false` do file có dòng lỗi) Dòng hợp lệ, sẵn sàng tạo mới (`customerId = null`, `customerCode = null`, đã resolve `tags` và `warnings`) |
| `skipped` | `duplicate_in_file` | — | SĐT chuẩn hóa trùng với dòng hợp lệ xuất hiện trước đó trong cùng tệp (`duplicateOfRow` chỉ rõ số dòng đầu được giữ lại — `BR-CRM-03`, `AC-P2-08`) |
| `skipped` | `existing_phone_skipped` | — | SĐT chuẩn hóa đã tồn tại ở hồ sơ `crm.customers` chưa gộp (`merged_into_customer_id IS NULL`) trong DB; bỏ qua không ghi đè, trả kèm `existingCustomerId`, không sinh interaction/tag trùng (`BR-CRM-03`, `AC-P2-08`) |
| `error` | `missing_full_name` | `full_name` | Cột `full_name` rỗng hoặc chỉ chứa khoảng trắng |
| `error` | `invalid_full_name` | `full_name` | `full_name` sau `Trim()` dài `> 150` ký tự hoặc chứa `\0` / lone surrogate |
| `error` | `missing_contact` | `phone` | Cả `phone` lẫn `zalo_name` đều rỗng (`BR-CRM-03`, `ck_customer_contact`) |
| `error` | `invalid_phone` | `phone` | `phone` có nhập nhưng không chuẩn hóa được sang di động VN `+84[35789]xxxxxxxx` theo `CustomerRules.TryNormalizePhone` (`BR-CRM-01`, `R-P201-04`) — báo lỗi kể cả khi `zalo_name` có giá trị |
| `error` | `invalid_zalo_name` | `zalo_name` | `zalo_name` sau `Trim()` dài `> 150` ký tự hoặc chứa `\0` / lone surrogate |
| `error` | `invalid_customer_group` | `customer_group` | `customer_group` khác rỗng và không thuộc `retail \| wholesale \| reseller` (mặc định khi rỗng là `retail`) |
| `error` | `invalid_tag` | `tags` | Có phần tử tag sau tách `,`/`;` dài `> 60` ký tự, chứa `\0`/lone surrogate, hoặc vượt quá `50` tag không trùng trên một dòng |
| `error` | `invalid_note` | `note` | `note` sau `Trim()` dài `> 4.000` ký tự hoặc chứa `\0` / lone surrogate |
| `error` | `invalid_had_purchase` | `had_purchase` | `had_purchase` khác rỗng và không thuộc `true \| false \| 1 \| 0` không phân biệt hoa/thường (mặc định khi rỗng là `true` theo `BR-CRM-03` & `Q5`) |

- **Danh sách đầy đủ `warnings` (khai báo enum chặt trong OpenAPI):**
  1. `zalo_only_reimport_risk`: gắn cho mọi dòng Zalo-only hợp lệ (`normalizedPhone == null && zaloName != null && errors.Count == 0`) ở cả `dryRun=true` và `dryRun=false`.
  2. `possible_duplicate_zalo_customer`: gắn bổ sung cho dòng Zalo-only hợp lệ (ở cả `dryRun=true` và `dryRun=false`) nếu cặp chuẩn hóa `(full_name.Trim().ToLowerInvariant(), zalo_name.Trim().ToLowerInvariant())` khớp với hồ sơ `crm.customers` có `merged_into_customer_id IS NULL AND phone_e164 IS NULL AND zalo_name IS NOT NULL` trong DB (tiền lọc bằng `public.f_unaccent(...)` trên PostgreSQL và đối chiếu chính xác `ToLowerInvariant()` trong C# để bất biến với locale DB và không có dương tính giả) hoặc khớp với dòng Zalo-only hợp lệ xuất hiện trước đó trong cùng tệp. **Không** tự gộp, **không** tự bỏ qua theo `zalo_name`, không thêm `UNIQUE` hay migration.
- **Thứ tự ưu tiên xác định:** Dòng có `errors.Count > 0` không chiếm slot trong `seenPhonesInFile` và không chiếm slot trong `seenZaloKeysInFile`, tránh che khuất dòng hợp lệ xuất hiện phía sau.

---

## 3. Đồng thời, sửa lỗi `[BUG P1]` retry savepoint `23505`, cấp mã `KH`, `search_text`, tags, `interaction(note)` và PII/Audit

### 3.1 Xử lý đồng thời & sửa `[BUG P1]` `KeyNotFoundException` (`HTTP 500`) khi retry savepoint `23505`

Bên trong `ApplyCustomerImportHandler` (chạy trong 1 transaction nguyên tử `EfUnitOfWork.ExecuteAsync`):

1. **Tuần tự hóa các lệnh import song song:** gọi ngay đầu transaction `SELECT pg_catalog.pg_advisory_xact_lock({8_203_001L})`. Hai lệnh `import?dryRun=false` đồng thời tuần tự hóa sạch sẽ; lệnh thứ hai thấy đủ SĐT/tag lệnh thứ nhất vừa commit.
2. **Đua đồng thời với thao tác lẻ (`POST admin/customers`, `POST .../correct-identity`, `POST admin/tags`):**
   - Các endpoint đơn lẻ không giữ `CustomerImportLockKey`, nên có thể chèn trùng `ux_customers_phone` / `ux_tags_name` hoặc đổi SĐT khách cũ (`correct-identity`) đúng lúc `ApplyAsync` chuẩn bị `SaveChangesAsync`.
   - Gom chèn `Tag` mới + `Customer` + `CustomerTag` + `Interaction` trong savepoint sạch theo từng vòng thử `$"p203_apply_batch_{attempt}"` (tối đa `MaxSaveAttempts = 3`).
   - **Bất biến đơn điệu không thu hẹp (`[BUG P1]` fix):** Khi bắt `DbUpdateException` (`SqlState == "23505"` trên `ux_tags_name` hoặc `ux_customers_phone`):
     1. Gọi `RollbackToSavepointAsync(savepoint)`, `ReleaseSavepointAsync(savepoint)` và `db.ChangeTracker.Clear()` (dọn sạch mọi entity lẫn `AuditLog` vừa chuẩn bị trong vòng thất bại — bảo đảm `0` trùng `AuditLog`, `0` tag mồ côi).
     2. Nếu `attempt == MaxSaveAttempts`: trả `Result.Failure` `409 concurrency_conflict` để `EfUnitOfWork` rollback toàn bộ transaction.
     3. **Tuyệt đối không ghi đè `existingPhones`** bằng snapshot mới toàn bộ `candidatePhones` (vì nếu khách cũ vừa đổi SĐT qua `correct-identity` thì tập `existingPhones` bị thu hẹp, khiến dòng vốn đã skip ở vòng 1 bật thành `imported` nhưng thiếu khóa trong `allocatedCodes`/`searchTexts` và gây `KeyNotFoundException` / `HTTP 500`). Thay vào đó chỉ truy vấn DB các SĐT đang nằm trong `survivingIndices` hiện tại và gọi `existingPhones.TryAdd(phone, existingId)` trước khi re-plan → đảm bảo tuyệt đối bất biến toán học `survivingIndices ⊆ toImportIndices` ở mọi vòng thử.
   - **Test seam tối thiểu:** `internal interface ICustomerImportSaveHook` trong `TeaPremium.Infrastructure.Crm` (truyền tùy chọn qua constructor `internal` của `EfCustomerImportStore` thông qua `sp.GetService<ICustomerImportSaveHook>()` trong `CrmRegistration.AddCrm()`). Không đăng ký trong production (`null`), không lộ public API, tuân thủ 100% `CompiledArchitectureTests` & `CrmModuleBoundaryRule`, giúp integration test kích hoạt tranh đua PostgreSQL `23505` thật qua các HTTP endpoint thật.

### 3.2 Cấp mã `KH` theo lô và dựng `search_text` theo lô

- Chỉ cấp mã khi `dryRun = false`, `errorCount == 0` và `toImportIndices.Length > 0`:
  - 1 câu SQL `SELECT nextval('crm.customer_code_seq') AS "Value" FROM generate_series(1, {count})`.
  - 1 câu SQL `SELECT public.f_unaccent(src) AS "Value" FROM unnest({sources}) WITH ORDINALITY AS t(src, ord) ORDER BY ord`.
- Giá trị cố định trên `Customer` khi import (`BR-CRM-03`, `Q5`): `Id = Guid.CreateVersion7()`, `Origin = "legacy_import"`, `AcquisitionSource = "legacy"`, `ContactStatus = "active"`, `IsVip = false`, `Email = null`, `NoteSummary = null`, `HadLegacyPurchase = row.HadPurchase` (mặc định `true`), `LastContactedAt = null`, `CreatedBy = actor.Id`, `CreatedAt = nowUtc`, `UpdatedAt = nowUtc`.

### 3.3 Gắn/tạo `crm.tags` (`O4.3 = A`), `crm.interactions(kind='note')` (`O4.2 = A`) và bảo vệ PII / Audit

- **Tags:** tách `,`/`;`, `Trim()`, bỏ rỗng, `1..60` ký tự, tối đa `50` nhãn/dòng, khử trùng `OrdinalIgnoreCase`. Đối chiếu DB ưu tiên `Ordinal` rồi `OrdinalIgnoreCase`; tag mới chưa có trên DB (chỉ tính trên các dòng thực sự `would_import`/`imported`) được trả trong `summary.newTags` theo thứ tự xác định và tạo với `Color = null` khi `committed = true`.
- **Ghi chú tương tác:** khi `note` khác rỗng và dòng được commit, tạo 1 `Interaction` (`Channel = "other"`, `Kind = "note"`, `StaffId = actor.Id`, `OccurredAt = nowUtc`, `CreatedAt = nowUtc`, `FollowUp* = null`), giữ `Customer.LastContactedAt = null` (`BR-CRM-07`) và `Customer.NoteSummary = null`.
- **Audit & PII (`AC-P0-04`, `NFR-04`):** gắn `[Audited]` lên `Interaction`, cập nhật `AuditSaveChangesInterceptor` (`or Interaction`) và nhánh fail-closed `crm.interactions` trong `AuditValuePolicy` che cứng `content` và `follow_up_result` thành `"[redacted]"`. `LoggerMessage` chỉ ghi số đếm tổng hợp (`DryRun`, `Committed`, `TotalRows`, `ImportedCount`, `SkippedCount`, `ErrorCount`, `TraceId`), tuyệt đối `0` PII trong log/audit/ProblemDetails.

---

## 4. Rule kiểm biên module mức IL trong `TeaPremium.ArchitectureTests` (đóng `[P2]` của G4 `P2-02`)

- Chuyển 1 dòng đăng ký `services.AddScoped<ICustomerOrderQueries, EfCustomerOrderQueries>()` từ `CrmRegistration.cs` sang `OrdersRegistration.cs` để `TeaPremium.Infrastructure.Crm` sạch hoàn toàn khỏi `Orders`.
- Thêm `CrmModuleBoundaryRule : ICustomRule` bằng `Mono.Cecil` duyệt đệ quy mọi `NestedTypes` (gồm async state machine `<...>d__N`, lambda `<>c`, closure), `MethodSpecification`/`GenericInstanceMethod`, `MethodReference.ReturnType`, `MethodReturnType.CustomAttributes`, `TypeReference` (`ldtoken`/`isinst`), biến cục bộ và exception handlers; áp allowlist fail-closed `0` ngoại lệ (`AppDbContext`, `IAuditContext`, `AuditLog`, và 10 entity của schema `crm`).
- Bổ sung `7` mẫu vi phạm cố ý biên dịch thật bền vững dưới Release (gồm cả `CrmReturnAttributeSample` kiểm `[return: ...]`) + `1` mẫu đối chứng hợp lệ + kiểm parity tự động với toàn bộ `AppDbContext.Model`; gỡ kiểm tra reflection nông khỏi `CustomerTagsAddressesAndSecurityTests.cs`.

---

## 5. Danh sách tài sản thay đổi (`27` file)

Tổng cộng **`27` file** (**`9` file mới**, **`16` file sửa**, **`2` file tái sinh**; không tạo migration, không sửa `docs/spec/`, không chạm `apps/`):

1. `backend/src/TeaPremium.Domain/Crm/CustomerRules.cs` *(sửa)* — thêm `InteractionChannels`, `InteractionKinds`, `MaxImportRows = 2000`, `MaxInteractionContentLength = 4000`, `TryParseImportHadPurchase`, `TryParseImportTags`.
2. `backend/src/TeaPremium.Application/Import/TabularImportFile.cs` *(mới)* — lõi parser CSV UTF-8 & XLSX an toàn thuần BCL.
3. `backend/src/TeaPremium.Application/Crm/CustomerImport.cs` *(mới)* — DTO (`CustomerImportSummary` gồm `Committed` & `NewTags`, `CustomerImportRow` với `WarningCodes`, `ZaloIdentityKey`), `CustomerImportPlanner`, ports, commands/queries, validators và handlers.
4. `backend/src/TeaPremium.Infrastructure/Persistence/CrmSalesFinanceEntities.cs` *(sửa)* — gắn `[Audited]` lên `Interaction`.
5. `backend/src/TeaPremium.Infrastructure/Persistence/AuditSaveChangesInterceptor.cs` *(sửa)* — thêm `or Interaction` vào nhánh `ValueGenerated.OnAdd`.
6. `backend/src/TeaPremium.Infrastructure/Persistence/AuditValuePolicy.cs` *(sửa)* — thêm nhánh fail-closed `crm.interactions` (che `content`, `follow_up_result`).
7. `backend/src/TeaPremium.Infrastructure/Crm/EfCustomerImportStore.cs` *(mới)* — hiện thực `ICustomerImportStore` với `O4.1 = B`, `newTags[]`, `possible_duplicate_zalo_customer`, advisory lock, batch sequence/unaccent, savepoint `p203_apply_batch_{attempt}` với `existingPhones` đơn điệu không thu hẹp, và test seam nội bộ `ICustomerImportSaveHook`.
8. `backend/src/TeaPremium.Infrastructure/Crm/CrmRegistration.cs` *(sửa)* — đăng ký `ICustomerImportStore` (inject `ICustomerImportSaveHook` tùy chọn), chuyển `ICustomerOrderQueries` sang `OrdersRegistration`.
9. `backend/src/TeaPremium.Infrastructure/Orders/OrdersRegistration.cs` *(sửa)* — tiếp nhận đăng ký `ICustomerOrderQueries`.
10. `backend/src/TeaPremium.Api/Catalog/ProductImportFile.cs` *(sửa)* — giữ facade nội bộ gọi `TabularImportFile`.
11. `backend/src/TeaPremium.Api/Crm/CustomerImportEndpoint.cs` *(mới)* — `POST /api/v1/admin/customers/import?dryRun=`, bảo mật CSRF/`customers.write`, log tổng hợp có `Committed` và `0` PII.
12. `backend/src/TeaPremium.Api/Crm/CustomerEndpoints.cs` *(sửa)* — gọi `customers.MapCustomerImport()` và mở `internal static CustomerActor Actor(HttpContext)`.
13. `backend/src/TeaPremium.Api/Crm/CustomerOpenApi.cs` *(sửa)* — cấu hình enum `status`, `reasonCode` và mảng `warnings` (`zalo_only_reimport_risk`, `possible_duplicate_zalo_customer`).
14. `packages/api-client/openapi.json` *(tái sinh qua `./scripts/verify-linux.ps1 -Mode api -WriteBack`)*.
15. `packages/api-client/src/generated/schema.ts` *(tái sinh qua `./scripts/verify-linux.ps1 -Mode api -WriteBack`)*.
16. `packages/api-client/src/customers-contract.test-d.ts` *(mới)* — kiểm kiểu tĩnh TypeScript cho `dryRun`, `file`, `summary.committed: boolean`, `summary.newTags: string[]`, enum `status`/`reasonCode`/`warnings` và DTO CRM.
17. `backend/tests/TeaPremium.ArchitectureTests/CrmModuleBoundaryRule.cs` *(mới)*.
18. `backend/tests/TeaPremium.ArchitectureTests/ArchitectureRuleSamples.cs` *(sửa)*.
19. `backend/tests/TeaPremium.ArchitectureTests/CompiledArchitectureTests.cs` *(sửa)*.
20. `backend/tests/TeaPremium.UnitTests/CustomerImportRulesAndParserTests.cs` *(mới)*.
21. `backend/tests/TeaPremium.IntegrationTests/AuditValuePolicyTests.cs` *(sửa)*.
22. `backend/tests/TeaPremium.IntegrationTests/CustomerTagsAddressesAndSecurityTests.cs` *(sửa)*.
23. `backend/tests/TeaPremium.IntegrationTests/OpenApiTests.cs` *(sửa)*.
24. `backend/tests/TeaPremium.IntegrationTests/CustomerImportTests.cs` *(mới)*.
25. `docs/plans/p2-03-implementation.md` *(mới — hồ sơ bất biến này)*.
26. `docs/decisions/ADR-P2-01-phase2-contracts.md` *(sửa — cập nhật quyết định Owner `2026-11-26`: `O1 = A`, `O2 = A`, `O4` và trạng thái `CONF-P206-P207-01`)*.
27. `docs/analysis/spec-gaps.md` *(sửa — đồng bộ trạng thái `O1`, `O2`, `O4` và `CONF-P206-P207-01` theo `ADR-P2-01`)*.

---

## 6. Ma trận kiểm thử (`0` skipped)

### 6.1 Unit tests (`TeaPremium.UnitTests/CustomerImportRulesAndParserTests.cs` — `14` methods)
1. `BrCrm03HadPurchaseParserDefaultsToTrueWhenBlankAndParsesBooleanTokensCaseInsensitively`
2. `BrCrm03TagsParserSplitsCommaAndSemicolonTrimsDeduplicatesCaseInsensitivelyAndEnforcesBounds`
3. `BrCrm03PlannerNormalizesPhonesAppliesDefaultsAndFlagsZaloOnlyWarning`
4. `O4PlannerAddsPossibleDuplicateZaloCustomerWarningForInFileAndDbMatchesWithoutAutoSkippingOrShadowingInvalidRows`
5. `O4BuildSummaryComputesCommittedInvariantAndCarriesNewTagsDeterministically`
6. `BrCrm03PlannerMissingBothPhoneAndZaloReportsMissingContact`
7. `BrCrm01BrCrm03PlannerInvalidPhoneFormatsIncludingLandlineAndMissingLeadingZeroReportInvalidPhoneEvenWhenZaloPresent`
8. `BrCrm03PlannerDuplicatePhonesInFileKeepFirstValidRowAndMarkLaterRowsWithDuplicateOfRow`
9. `BrCrm03PlannerExistingActiveDbPhoneMarksFirstValidRowExistingPhoneSkippedAndLaterDuplicateRowDuplicateInFile`
10. `BrCrm03PlannerValidatesFullNameZaloCustomerGroupAndNoteLengthBounds`
11. `TabularImportFileReadsValidCsvWithBomPlus84PhonesMultilineNoteAndSingleOrNamedXlsxUpTo2000Rows`
12. `TabularImportFileRejects2001RowsUnknownHeadersFormulasMacrosZipBombDishonestEntryAndUtf16`
13. `TabularImportFilePreservesP1ProductAndOpeningImport500RowAndSheetLimits`
14. `TabularImportFileRejectsDuplicateNamedSheetsEvenWhenFirstSheetOmitsRelationshipId`

### 6.2 Architecture tests (`TeaPremium.ArchitectureTests/CompiledArchitectureTests.cs` — `3` methods)
1. `CrmInfrastructureDoesNotTouchForeignModulePersistenceEntitiesOrInfraNamespaces`
2. `CrmModuleBoundaryRuleRejectsDeliberateCompiledViolations` (`7` mẫu cố ý, gồm `CrmReturnAttributeSample`)
3. `CrmModuleBoundaryRuleAcceptsOwnCrmEntitiesAndAuditLog`

### 6.3 Integration & Contract tests trên PG17 (`TeaPremium.IntegrationTests` — `21` methods mới/cập nhật)
- `AuditValuePolicyTests.cs` (`2` methods mới): `CrmInteractionsAllowlistSafeMetadataAndRedactContentAndFollowUpResult`, `CrmInteractionsRejectSpoofedChannelOrKindStrings`.
- `CustomerImportTests.cs` (`17` methods):
  1. `AcP208Import500RowsWith3InFileDuplicatesAnd2ExistingCustomersReportsExactReasonsAndLeavesExistingProfilesUntouched` (`csv` & `xlsx`, kiểm `committed`, `newTags[]`, giữ nguyên hồ sơ cũ & `xmin`).
  2. `BrCrm03BrCrm07BrCrm14ApplyImportSetsLegacyOriginAllocatesKhCodesBuildsSearchTextCreatesTagsAndNoteInteractionWithoutUpdatingLastContactedAt`.
  3. `O4MixedRowsWithErrorsOnDryRunFalseReturn200CommittedFalseWithZeroSideEffectsAndCleanFileCommits` (`dryRun=false` có lỗi → `HTTP 200`, `committed=false`, `0` DB/sequence/audit writes, dòng hợp lệ `would_import` + `newTags[]`; sửa lỗi xong commit sạch).
  4. `O4AllSkippedRowsWithDryRunFalseReturnsCommittedTrueAndConsumesZeroSequence` (100% skipped → `committed=true`, `0` sequence).
  5. `O4PossibleDuplicateZaloCustomerWarningDetectsInFileAndDbMatchesAndReimportNeverDuplicatesPhoneInteractionsOrTags` (in-file + DB active Zalo-only, bỏ qua phone+zalo & merged, re-import lần 2 báo `possible_duplicate_zalo_customer` ở cả `dryRun=true|false` và `0` trùng note/tag trên khách có SĐT).
  6. `O4PossibleDuplicateZaloCustomerWarningMatchesUnicodeWhitespaceU000BU00A0AndVietnameseCaseInBothDryRunAndApply` (đồng bộ bộ ký tự cắt trắng Unicode `.NET char.IsWhiteSpace` với `btrim` PG17 trên `U+000B`, `U+00A0`, `U+3000` và tiếng Việt hoa/thường có dấu).
  7. `BrCrm03Boundary2000RowsSucceedsAnd2001RowsReturns413WithZeroDbSideEffects`.
  8. `ImportConcurrencyTwoParallelImportsWithOverlappingPhonesAndNewTagsSerializeCleanlyWithoutDuplicatesOr500`.
  9. `ImportRevalidationCustomerCreatedBetweenDryRunAndApplyIsSkippedOnApplyWithoutOverwriting`.
  10. `ImportMultipartSecurityRejectsMissingDryRunWrongFieldFormulaMacroOversizedAndMalformedFiles`.
  11. `AcP209ImportSecurityRejectsContentRoleUnauthenticatedUnreadyStaffPlainHttpAndMissingCsrfForBothDryRunModes` (`true` & `false`).
  12. `ImportAuditAndLogsRedactAllCustomerTagAndInteractionContentWithZeroRawPiiLeak` (cả nhánh `committed=false` có lỗi lẫn nhánh `committed=true`).
  13. `P1FixSavepoint23505OnCustomerPhoneWithConcurrentCorrectIdentityShrinkingSnapshotRetriesCleanlyWithZeroOrphanTagsAndZeroDuplicateAudit` (`[BUG P1]` + `23505 ux_customers_phone` + `correct-identity`, `0` tag mồ côi, `0` trùng `AuditLog`).
  14. `Savepoint23505OnTagNameRaceWithConcurrentTagCreationRetriesCleanlyAndReusesTagWithZeroDuplicateAudit` (`23505 ux_tags_name`, `0` trùng `AuditLog`).
  15. `Savepoint23505ExceedingMaxSaveAttemptsReturns409ConcurrencyConflictWithZeroCommittedRowsAndZeroOrphanAudit` (`409 concurrency_conflict`, `0` ghi dở, `0` `AuditLog` sót).
  16. `RealTpAppRoleExecutesCustomerImportWithAdvisoryLockSequenceAndInteractionInsert`.
  17. `ImportTransactionFailureRollsBackCustomersTagsInteractionsAndAuditAtomically`.
- `CustomerTagsAddressesAndSecurityTests.cs`: gỡ kiểm tra reflection nông và mở rộng ma trận `AC_P2_09` (`15 → 17` endpoints).
- `OpenApiTests.cs`: kiểm `111` operations, `dryRun` boolean bắt buộc, `multipart/form-data` binary `file`, enum `status`/`reasonCode`/`warnings`, `committed: boolean` và `newTags: string[]` bắt buộc không nullable.

### 6.4 Frontend & Full PR-2 Verification
- Vì ứng viên thay đổi cả backend và `packages/api-client` (`openapi.json`, `schema.ts`, `customers-contract.test-d.ts`), kiểm chứng cuối bắt buộc chạy `./scripts/verify-linux.ps1 -Mode full -Manifest artifacts/p2-03/candidate.manifest` (`coverage=FULL_ALL`) sau khi đóng dấu manifest; chi tiết log, `manifest_sha256`, số lượng test và các hạng mục `NOT_RUN` (GitHub Actions CI, Docker regression builds) nằm tại báo cáo bàn giao theo đúng quy tắc tách biệt Dynamic Task Status.

---

## 7. Quyết định đã chốt, rủi ro còn lại và bàn giao

### 7.1 Bảng quyết định `O4` (`OWNER_APPROVED`, `2026-11-26`, `Clarification only`)

| Mã | Nguồn (`file:dòng`) | Nội dung đã được Owner phê duyệt tường minh ngày `2026-11-26` |
|---|---|---|
| `O4.1 = B` (thay đề xuất cũ `ASSUMPTION-P203-01`) | [04-business-rules.md](../spec/04-business-rules.md)`:222`, [05-api.md](../spec/05-api.md)`:214`, [07-roadmap.md](../spec/07-roadmap.md)`:113` | `dryRun=false` trả HTTP `200` + `summary.committed: bool` (`!dryRun && errorCount == 0`) và `summary.newTags[]`; khi `errorCount > 0` dừng sớm trước sequence (`committed=false`, `0` side effects, dòng hợp lệ giữ `would_import`); khi `errorCount == 0` (kể cả có `duplicate_in_file`/`existing_phone_skipped` hay 100% skipped) thì `committed=true`. Bổ sung cảnh báo `possible_duplicate_zalo_customer` cho dòng Zalo-only hợp lệ khớp cặp chuẩn hóa `(full_name, zalo_name)` với DB active Zalo-only hoặc dòng trước trong file; không tự gộp/bỏ qua theo `zalo_name`, không thêm `UNIQUE`/migration. |
| `O4.2 = A` (`ASSUMPTION-P203-02`) | [04-business-rules.md](../spec/04-business-rules.md)`:222,225-226`, [schema.sql](../spec/schema.sql)`:573,658-662` | Cột `note` tạo 1 `crm.interactions(kind='note', channel='other', staff_id=actor.Id, occurred_at=nowUtc)`, giữ `last_contacted_at = null` và `note_summary = null`, gắn `[Audited]` + redaction `content`/`follow_up_result`. |
| `O4.3 = A` (`ASSUMPTION-P203-03`) | [04-business-rules.md](../spec/04-business-rules.md)`:222`, [schema.sql](../spec/schema.sql)`:598-608` | Cột `tags` tách `,`/`;`, `1..60` ký tự, tối đa `50` nhãn/dòng, tự tạo tag mới (`color = null`) khi chưa có trong `crm.tags`; `.xlsx` đọc 1 sheet hoặc duy nhất sheet `KhachHang`. |

### 7.2 Rủi ro còn lại và bàn giao liên task

1. **Re-import tệp hợp lệ (`errorCount == 0`) chứa khách Zalo-only:** Nếu nhân viên chủ động bấm import lại một tệp hoàn toàn hợp lệ lần thứ 2, các dòng có `phone` sẽ bị bỏ qua an toàn (`existing_phone_skipped`, `0` interaction/tag trùng), còn các dòng Zalo-only sẽ được tạo mới kèm cảnh báo rõ `["zalo_only_reimport_risk", "possible_duplicate_zalo_customer"]` để nhân viên rà soát trên UI preview (`P2-14`) và gộp thủ công (`P2-12`) khi cần — đúng chủ đích `O4` không tự gộp/bỏ qua theo tên Zalo.
2. **Tiêu hao lỗ hổng nhỏ trên `crm.customer_code_seq` khi xảy ra rollback sau bước cấp mã:** Khi `dryRun=false` có `errorCount > 0` hoặc 100% dòng bị skip ngay ban đầu, hệ thống tiêu hao `0` sequence. Chỉ trong trường hợp hiếm vượt quá 3 vòng đua `23505` (`409 concurrency_conflict`) hoặc lỗi DB bất ngờ sau bước cấp mã, sequence PostgreSQL không rollback (hành vi chuẩn của PostgreSQL sequence).
3. **Bàn giao cho các task kế tiếp:**
   - **`P2-04`:** kế thừa `[Audited]` trên `Interaction` và nhánh `crm.interactions` trong `AuditValuePolicy` (đã che `content` và `follow_up_result`).
   - **`P2-14`:** tiêu thụ contract `POST /api/v1/admin/customers/import?dryRun=` từ `@teapremium/api-client`, hiển thị rõ `summary.committed`, `summary.newTags[]` và 2 cảnh báo `zalo_only_reimport_risk` / `possible_duplicate_zalo_customer` trên màn hình preview/apply.
   - **`P2-12`:** giữ `G17a`/`G17b+G28` cho hợp nhất khách trùng Zalo thủ công và ẩn danh dữ liệu.
