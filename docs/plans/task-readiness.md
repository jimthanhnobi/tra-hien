# Mức sẵn sàng task P0–P6

**Cập nhật hiện hành A01 (18/11/2026):** amendment MERGED qua PR #20; G4 tầng A PASS trước publish/merge theo bằng chứng reviewer do owner cung cấp; CI tại PR HEAD và merge SHA đã xác minh riêng PASS. Xem mục A01 cuối file. Các trạng thái/số dòng baseline bên dưới là lịch sử; closure/runtime/P0 acceptance chưa hoàn tất.

**Trạng thái:** PROPOSED. Bám `docs/spec/07-roadmap.md`; giữ nguyên mọi ID task và AC, không sửa dependency trong spec. Đối chiếu `docs/analysis/spec-inventory.md`, `spec-gaps.md`, `traceability.md` và [rà soát kiến trúc](../architecture/conformance-review.md). “GO” ở đây là đủ rõ để giao task theo gate G2/G3 của `AGENTS.md`, không phải xác nhận code đã chạy.

Riêng **F-02 contract/DDL đã OWNER APPROVED sau independent focused G4 PASS**, tài liệu được gộp tại `cf833bb149771b4380bce82d82769166a73ac145`. Dòng F-02 dưới đây và §9 của readiness doc ghi gate hiện hành; các hồ sơ P0-01/P0-04 bên dưới giữ mốc lịch sử của chúng.

## P0-01 — đề xuất task record chính xác

| Trường | Nội dung |
|---|---|
| Mục tiêu | Dựng foundation có thể restore, build, lint, typecheck từ checkout sạch, đúng cấu trúc đã chốt. |
| Nguồn/scope | `docs/spec/07-roadmap.md:17` (P0-01); `02-architecture.md:27–77,235–248` (§2/§3/§9/§10); DEC-01/15/16 tại `00-README.md:85,99–100`; `06-infrastructure.md:226–230` (chuẩn CI về sau). |
| Phụ thuộc gốc | `—` trong roadmap. Cần có toolchain .NET SDK 10, Node và pnpm qua Corepack; máy hiện có .NET 10/Node/Corepack, chưa có `pnpm` global. |
| Phạm vi | Một solution .NET 10 gồm 4 source project, 3 test project; `apps/store`, `apps/admin`, `packages/api-client`; pnpm workspace, `global.json`, central package management, `.editorconfig`, analyzers warnings-as-errors; lockfile cho restore tái lập. |
| Ngoài phạm vi | Compose P0-02; cross-cutting P0-03; DbContext/migration P0-04; IAM P0-08; architecture behavior tests/Testcontainers P0-10; CI/OpenAPI generation P0-11. Không tạo DTO API giả hoặc tính năng nghiệp vụ. |
| Nghiệm thu | Đúng layout, project references đúng `02 §3`, restore/build sạch với warning là lỗi, 2 app Next.js TypeScript strict/Tailwind và workspace build/lint/typecheck được; `api-client` là package scaffold có thể tham chiếu, generation thật khi có OpenAPI. Đây là tiêu chí **đề xuất cho P0-01**, không gán mã AC mới. `AC-P0-01` tại `07-roadmap.md:33` là khóa tài khoản của P0-08, không dùng làm AC scaffold. |
| Rollback/vận hành | Task chỉ tạo file nền; revert branch/PR nếu hỏng. Không có migration, dữ liệu thật, secret hay thay đổi runtime production. Ghi toolchain và cách chạy vào README của task khi implement. |

### Layout cần tạo ở P0-01

```text
backend/
  TeaPremium.sln                   # tên/path solution đề xuất để lệnh kiểm tra cố định
  Directory.Packages.props
  src/TeaPremium.Domain/
  src/TeaPremium.Application/
  src/TeaPremium.Infrastructure/
  src/TeaPremium.Api/
  tests/TeaPremium.UnitTests/
  tests/TeaPremium.IntegrationTests/
  tests/TeaPremium.ArchitectureTests/
apps/store/
apps/admin/
packages/api-client/
global.json
package.json
pnpm-workspace.yaml
pnpm-lock.yaml
.editorconfig
```

`TeaPremium.sln` và chỗ đặt `Directory.Packages.props` là lựa chọn triển khai đề xuất; source spec chỉ ấn định solution và central package management. Project graph bắt buộc theo `02-architecture.md:68–77`: Domain không có package ngoài BCL; Application → Domain, không EF Core; Infrastructure → Application/Domain; Api → Application/Infrastructure. Test projects tham chiếu đúng subject cần test. Pin SDK .NET 10 trong `global.json`, pin pnpm trong `packageManager`, dùng Corepack trong hướng dẫn. Khóa package NuGet/JS tương ứng nếu chọn restore locked/frozen; không cam kết lock mode khi chưa sinh lockfile hợp lệ.

### Kiểm tra nghiệm thu và lệnh phải chạy khi triển khai

Chạy từ gốc repo trong PR P0-01; lệnh dưới đây là **kế hoạch**, chưa có kết quả chạy. Tạo script `lint`, `typecheck`, `build` thực cho hai app và package phù hợp. Không dùng test 0 case để tuyên bố nghiệp vụ đã được kiểm chứng.

```powershell
dotnet --list-sdks
node --version
corepack pnpm --version
dotnet sln backend/TeaPremium.sln list
dotnet restore backend/TeaPremium.sln --locked-mode
dotnet build backend/TeaPremium.sln --no-restore -warnaserror
dotnet test backend/TeaPremium.sln --no-build
corepack pnpm install --frozen-lockfile
corepack pnpm --dir apps/store run lint
corepack pnpm --dir apps/store run typecheck
corepack pnpm --dir apps/store run build
corepack pnpm --dir apps/admin run lint
corepack pnpm --dir apps/admin run typecheck
corepack pnpm --dir apps/admin run build
corepack pnpm --dir packages/api-client run build
git diff --check
```

`dotnet restore --locked-mode` đòi `packages.lock.json` được tạo/commit trong P0-01. Nếu `api-client` chỉ là package nguồn và không cần build riêng, phải ghi bằng chứng import/typecheck ở cả hai app và điều chỉnh lệnh tương ứng trong task record trước review. Nếu scaffold có test thật, chạy test và báo số case; nếu chưa có, ghi rõ test nghiệp vụ **NOT RUN/chưa áp dụng**, không báo “PASS” vì runner chạy 0 test. P0-10 mới kiểm luật kiến trúc và migration bằng Testcontainers. Kiểm manual project references và workspace imports bên cạnh output command; `dotnet sln list` một mình không chứng minh graph.

## Thứ tự và gate theo roadmap

| Pha/task ID hiện có | Dependency gốc và trạng thái | Quyết định/rủi ro phải xử lý tại đúng mốc; bằng chứng |
|---|---|---|
| **P0-01** | Không dependency; **GO về spec/kiến trúc**, chờ duyệt G2 rồi làm một slice G3. | Layout/layer/commands ở trên. Không có blocker xác nhận cho scaffold (`07-roadmap.md:17`; `spec-gaps.md` §Gate trước P0-01). |
| **P0-02, P0-03** | Đều sau P0-01 (`07-roadmap.md:18–19`). | Compose dev và cross-cutting chỉ bắt đầu sau foundation build được; không ép vào PR P0-01. |
| **P0-04** | Sau P0-03 (`07-roadmap.md:20`). G26/G01 amendment đã qua G4 và được owner chấp nhận; **implementation chưa được phép** nếu chưa có giao việc G3 riêng. | Migration platform từ schema đích, kiểm DB rỗng. F-01: owner đã duyệt ma trận 19 constraint tại `docs/plans/p0-04-readiness.md`; DDL đích đã sửa và qua G4 độc lập. EF mapping/migration sau này phải khớp, chưa được phép tạo ở bước này. |
| **P0-05…P0-08, P0-10** | Sau P0-04 (`07-roadmap.md:21–24,26`). | Audit AC-P0-04/FR-PLT-01 ở P0-05; Identity/permission/CSRF P0-08; migration + ArchitectureTests P0-10. |
| **P0-09 — F-02** | Sau P0-04 (`07-roadmap.md:25`); ledger bổ sung thuộc P0-09, không đổi migration cấu trúc P0-04. | [Quyết định F-02](p0-09-f02-readiness.md#9-owner-approval-và-scoped-spec-alignment-17112026): **OWNER APPROVED / focused G4 PASS**; tài liệu đã vào main `cf833bb`. **SPEC ALIGNMENT: READY_FOR_LOCAL_G4**, chỉ đưa đúng ledger/grants đã duyệt vào schema.sql và 03-database.md. **CODING:** approval và fixture/pin/checksum v5.2.0/`62b1abd…` (34 provinces/3321 wards) đã VERIFIED; chờ G4 amendment này và owner giao P0-09 G3 riêng, khi đủ → GO local/test development. **PRODUCTION APPLY:** HOLD đến khi official full-catalog reconciliation/source mapping, production runtime-role permissions, backup/restore drill, dry-run diff/current-base fingerprint/fresh approval và release/version/recovery policy được kiểm đầy đủ. Thiếu production evidence không chặn coding/test local sau coding gate. Chưa triển khai API/CLI/migration. |
| **P0-11, P0-12** | P0-11 sau P0-10; P0-12 sau P0-08 (`07-roadmap.md:27–28`). | CI phải chứng minh restore, migration, generated OpenAPI client và FE checks (`06-infrastructure.md:226–230`); admin skeleton sau IAM. |
| **P1-01…P1-14** | P1-01 sau P0; chuỗi CRUD → kho/UI/store API theo `07-roadmap.md:45–58`. | G21: hoãn `catalog.product_reviews` FK sales/CRM; G02: redirect tối thiểu trước P1-04/P1-05; G32: P1-05 publish cần media P1-02 (`BR-CAT-04`, `07:46,49`). G05 upload evidence video cần chốt trước P1-02. Tồn độc bản P1-08 cần test atomic/unique (G15); UI kho P1-13 sau P1-09, store API P1-14 sau P1-05/P1-08. |
| **P2-01…P2-16** | P2-01 sau P1; order P2-05 cần P1-08; UI đơn P2-15 sau P2-05…10, dashboard P2-16 sau P2-15 (`07-roadmap.md:78–93`). | G03: FK marketing tại P2-01; G04/G27/G29/G30: quyền và chính sách refund/replacement trước P2-10; G17/G28: merge CRM trước P2-12; G22: side effect Delivered phân pha. AC-P2-01 (`07:98`) kiểm 20 yêu cầu đồng thời trên hàng độc bản, không hoãn sang FE. |
| **P3-01…P3-18** | Storefront/go-live theo `07-roadmap.md:114–152`. | G09 guest CSRF trước checkout; G06 consent checkout trước P3-02; G02 redirects nối tới P3-07; G07 P3-09 sitemap/JSON-LD landing/author phụ thuộc nội dung P3-14; brand Merchant trước P3-15; author/landing slug redirects trước P3-14/P3-07. P3-10 đo tài nguyên/backup/restore, P3-11 E2E, P3-12 go-live gate; G24 xác minh ngoài repo. |
| **P4-01…P4-06** | Sau dữ liệu đơn/giao/return P2/P3, theo `07-roadmap.md:160–171`. | Giữ doanh thu khi Delivered theo DEC-07; G16 phân biệt restock vận chuyển thất bại với return sau giao trước P4-02. AC-P4-01…04 là gate fixture/kỳ VN và cảnh báo thiếu chi phí. |
| **P5-01…P5-07** | Sau checkout/CRM đủ nền, theo `07-roadmap.md:179–191`. | Consent gate BR-CRM-09 trước gửi chiến dịch P5-02; P5-05 referral cần dữ liệu đơn Delivered/refund; P5-07 review giữ đến khi sales order lines tồn tại. G20 ẩn danh review; G18 dữ liệu feed cần chốt trước P3-15 dù marketing ở P5. |
| **P6-01…P6-08** | Có điều kiện kích hoạt, không nằm trên đường P0-01 (`07-roadmap.md:199–206`). | Xác minh provider/API hiện hành khi đến P6-01/02/03, kế toán khi P6-05, số liệu vận hành khi P6-07/08. G08 cần quyết định độ hạt Web Vitals trước P6-08. Không dùng P6 làm dependency giả cho scaffold. |

## Việc cần người dùng quyết định

1. Duyệt kết luận kiến trúc và scope P0-01 của hai tài liệu này theo G2; sau đó mới giao `P0-01` qua `implementation-slice` (G3). **Không có blocker kỹ thuật xác nhận cho P0-01.**
2. G26/G01 Option B và bản sửa spec đã được owner chấp nhận sau G4 ngày 16/11/2026. F-01 được owner duyệt và DDL đích đã sửa đúng chín bảng; bản sửa F-01 đã qua G4 độc lập, P0-04 implementation vẫn cần giao việc riêng.
3. Trước các task sau: chốt các hợp đồng tương ứng ở bảng P1–P6, đặc biệt G02/G03 và hai rủi ro SEO; không trì hoãn P0-01 vì chúng.

**Bước tiếp theo duy nhất tại thời điểm lập hồ sơ P0-01:** sau khi duyệt G2, triển khai đúng `P0-01` trên một nhánh/PR nhỏ, chạy và ghi kết quả thật của checklist trên. Trạng thái hiện hành cho P0-04 nằm ở cập nhật bên dưới.

## Cập nhật gate P0-04 tại `origin/main` 9cc5429 (16/11/2026)

P0-01…P0-03 đã được gộp; hướng dẫn P0-01 bên trên là bản ghi tại thời điểm lập kế hoạch. Ngày 16/11/2026, owner duyệt G26 Option B/G01 Option B và chấp nhận bản sửa `docs/spec/` sau G4 GO. Hai ADR trong `docs/decisions/` giữ lịch sử đề xuất. **G26/G01 SPEC AMENDMENT — OWNER ACCEPTED (G4 GO); F-01 DDL AMENDMENT — OWNER APPROVED (G4 GO); P0-04 IMPLEMENTATION — NOT AUTHORIZED.** `docs/plans/p0-04-readiness.md` ghi ma trận F-01 đã duyệt và DDL đã qua G4 độc lập. F-02 là điều kiện riêng của P0-09/CRM; AC-P0-04 chỉ kiểm được khi P0-05/P0-09 triển khai cùng schema đã chốt.

## A01 — trạng thái hiện hành 18/11/2026

Các đoạn PROPOSED/NOT AUTHORIZED lịch sử ở trên giữ mốc của task cũ, không cấp lệnh chạy lại. Owner phê duyệt A01/finalize + hai bổ sung backup/điều tra và owner B API. [PR #20](https://github.com/jimthanhnobi/tra-hien/pull/20): base `288ca8b55cab2017c9ee42bd35036e2d2e5bd87c`, PR HEAD `47f2c7b491ab124c91e827fda6e010171abeaba7`, merge/spec revision `024ea998c43baf0afde30b069ed6b4876cb96f4a`, merged at `2026-11-18T06:28:13Z`; GitHub đã xác minh.

| Phạm vi | Gate hiện hành |
|---|---|
| A01 amendment specs/workflow | OWNER APPROVED / MERGED; independent local G4 tầng A PASS trước publish/merge theo evidence reviewer do owner cung cấp, không finding actionable P0–P3. CI PR HEAD run 44250706587 và merge SHA run 44251560930: completed/success, cả ba jobs PASS (xác minh riêng). Không chứng minh implementation/EF/runtime amendment |
| Closure #1, remediation P0-02/04 | NOT AUTHORIZED G3; G4 amendment đã PASS, cần owner giao riêng; source role/helper/migration chưa tạo; runbook/provisioning/grants thực/backup restore NOT RUN |
| Closure #2, remediation P0-08/12 | NOT AUTHORIZED G3; recovery/coverage/API owner B cần implement/test sau giao việc |
| P1-01 | HOLD / NOT AUTHORIZED G3; C1 amendment hoàn tất (approval/G4/merge/spec SHA/CI). C2 closure #1/#2 và runbook/P0 acceptance chưa đủ; C3 contract đã duyệt nhưng role/helper và evidence triển khai chưa có; owner giao riêng |
| P1-04 | D12 audit entity/path phải hoàn tất trước entity audited; không chặn P1-01 |
| P1-07 | Variant key/import lại/ô trống/nhóm conflict/atomicity OPEN tại task; không auto-upsert thiếu khóa, không chặn P1-01 |

Không dùng tp_readonly query tay điều tra; read identity riêng tối thiểu/no IAM creds/DP keys mặc định. Owner B qua staff.manage/cookie/CSRF và session onboarding riêng, không Staff UI. Runtime, grants thực, MFA recovery và P0 acceptance vẫn NOT RUN. Evidence: [A01](../reviews/a01-amendment-evidence.md); task detail [P1-01](p1-01-implementation.md). Bản cập nhật hồ sơ này cần G4 tầng B riêng; không mở lại G4 tầng A của A01.
