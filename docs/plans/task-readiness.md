# Mức sẵn sàng task P0–P6

**Cập nhật B01 (19/11/2026):** PR #22–#25 đã merge; CI tại từng PR HEAD/merge SHA PASS, baseline `origin/main` = `dadc4435e8f115faaeb9e8511e56e09d5d517a8a`. G4 #24/#25 PASS tại đúng HEAD theo **OWNER-PROVIDED REVIEWER EVIDENCE**, các finding CLOSED; không phải GitHub VERIFIED REVIEW. C1/C2/C3 PASS về readiness; owner chưa giao P1-01 G3. Targeted G1/G2 và bằng chứng ở mục hậu kiểm cuối file; các ghi chú GO/HOLD cũ giữ làm lịch sử.

**Trạng thái:** PROPOSED. Bám `docs/spec/07-roadmap.md`; giữ nguyên mọi ID task và AC. Lần hậu kiểm 19/11 chỉ sửa dependency P1-01 còn sót theo B01, được owner cho phép riêng. Đối chiếu `docs/analysis/spec-inventory.md`, `spec-gaps.md`, `traceability.md` và [rà soát kiến trúc](../architecture/conformance-review.md). “GO” ở đây là đủ rõ để giao task theo gate G2/G3 của `AGENTS.md`, không phải xác nhận code đã chạy.

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

## B01 — lịch sử trạng thái sau PR #23 (18/11/2026)

| Phạm vi | Trạng thái / gate |
|---|---|
| A01 amendment | [PR #20](https://github.com/jimthanhnobi/tra-hien/pull/20) và [PR #21](https://github.com/jimthanhnobi/tra-hien/pull/21) MERGED; quyết định lịch sử D8/D9/D12 đã được B01 điều chỉnh có phạm vi. |
| Closure #1 P0-02/04 | [PR #22](https://github.com/jimthanhnobi/tra-hien/pull/22) MERGED, head `86d8b561dc5a2db22a151eb499fb2a8512769cd0`, merge `606fd2e279eb766976db18473cd45e7cbd142108`. Independent focused G4 PASS theo bằng chứng owner cung cấp, 463/463 backend tests do reviewer báo PASS. PR CI run 44270404911 completed/success; merge-push CI run 44271243633 completed/success; cả hai run 3/3 jobs PASS (Backend/API contract, Frontend, Docker). Manual HTTPS/backup/owner còn NOT RUN, không được đánh dấu production-ready. |
| Closure #2 P0-08/12 | **DEFERRED theo owner B01**: không xây `reset-staff-mfa`/owner coverage/banner ở P0; không được nói DONE. Tài khoản và TOTP hiện hữu giữ nguyên. |
| P0 vận hành | Trước dữ liệu thật/go-live phải kiểm HTTPS, một owner đã đổi password + TOTP, CSRF/lockout/logout, settings audit và backup/restore; owner thứ hai là khuyến nghị, không gate. |
| P1-01 | **HOLD / NOT AUTHORIZED G3**. Closure #1 PR #22 MERGED, G4 và CI PASS; B01 PR #23 đã MERGED, CI PR/merge 3/3 PASS nhưng G4 hậu merge CONDITIONAL. Chờ focused independent G4 PASS cho tài liệu correction, scoped C1–C3/role/helper/preflight đối chiếu bản `main` và lệnh G3 của owner. Không chờ Closure #2 MFA recovery hay HTTPS/backup manual trước DDL trên DB giả; bảo đảm nghiệm thu trước dữ liệu thật/go-live. |
| P1-04 | D12: audit entity/path cho các nghiệp vụ quan trọng, che PII mặc định trước khi bật audit entity, không chặn P1-01. |
| P1-07 | Import variant key/match/upsert vẫn OPEN tại task P1-07; không chặn P1-01. |

Không dùng `tp_readonly` để điều tra/query tay. B01 không cấp quyền triển khai production hoặc tự động bỏ G4/CI. Trạng thái lịch sử A01 xem [ADR A01](../decisions/ADR-PROPOSAL-A01-p0-closure-p1-owner-decisions.md); cập nhật mới ở [ADR B01](../decisions/ADR-B01-right-sized-operations.md).

## B01 — hậu kiểm merge và targeted G1/G2 P1-01 (19/11/2026)

**Input/base/HEAD:** `dadc4435e8f115faaeb9e8511e56e09d5d517a8a` = `origin/main` sau `git fetch origin --prune`; worktree ban đầu sạch. Branch local `docs/b01-postmerge-p1-readiness`, delta chưa commit. Owner chỉ cho phép sửa tài liệu và dependency P1-01 còn sót; chưa giao P1-01 G3. Scope đúng ba file: `docs/spec/07-roadmap.md`, tài liệu này và `p1-01-implementation.md`; không application/DB/migration/DDL change.

### Merge, G4 và CI là các loại evidence riêng

GitHub connector GET `/repos/jimthanhnobi/tra-hien/pulls/{22..25}`, Actions runs lọc `head_sha`/event và jobs từng run đã xác minh bảng dưới. Mọi run đều completed/success; cả ba required jobs **Backend and API contract**, **Frontend verification**, **Docker regression builds** PASS. #25 còn job classifier PASS; steps xác nhận chạy full suite ở PR/merge vì có thay đổi workflow/scripts, không phải evidence fast path của delta docs hiện tại.

| PR | Published HEAD → merge SHA (MERGED) | PR CI → push-main CI |
|---|---|---|
| [#22 Closure #1](https://github.com/jimthanhnobi/tra-hien/pull/22) | `86d8b561dc5a2db22a151eb499fb2a8512769cd0` → `606fd2e279eb766976db18473cd45e7cbd142108` | [44270404911](https://github.com/jimthanhnobi/tra-hien/actions/runs/44270404911) → [44271243633](https://github.com/jimthanhnobi/tra-hien/actions/runs/44271243633) |
| [#23 B01](https://github.com/jimthanhnobi/tra-hien/pull/23) | `8a8705efacfffec828509a85b3fcc1d49755a31d` → `9eb5e566dafc3dade3365f3b6a785a53ac116002` | [44273381605](https://github.com/jimthanhnobi/tra-hien/actions/runs/44273381605) → [44274534732](https://github.com/jimthanhnobi/tra-hien/actions/runs/44274534732) |
| [#24 correction G4 B01](https://github.com/jimthanhnobi/tra-hien/pull/24) | `e10f62205d47e87ac5ecd1e28ed1155912d6b54e` → `a98bfecb7a250274054691e2f1fab395c6a7a0cd` | [44276146670](https://github.com/jimthanhnobi/tra-hien/actions/runs/44276146670) → [44277756402](https://github.com/jimthanhnobi/tra-hien/actions/runs/44277756402) |
| [#25 CI fast path](https://github.com/jimthanhnobi/tra-hien/pull/25) | `d4027538dd95c47a801370a2d7e0a24527035253` → `dadc4435e8f115faaeb9e8511e56e09d5d517a8a` | [44282026876](https://github.com/jimthanhnobi/tra-hien/actions/runs/44282026876) → [44432480073](https://github.com/jimthanhnobi/tra-hien/actions/runs/44432480073) |

- **OWNER-PROVIDED REVIEWER EVIDENCE:** owner xác nhận ngày 19/11, reviewer trong các phiên Codex độc lập: #24 focused G4 Tier A **PASS**, P2 **CLOSED**, không actionable finding, reviewed HEAD `e10f62205d47e87ac5ecd1e28ed1155912d6b54e`; #25 independent focused G4 **PASS**, P1/P2 **CLOSED**, READY_FOR_OWNER_MERGE YES, reviewed HEAD cuối `d4027538dd95c47a801370a2d7e0a24527035253`. Reviewer #25 báo 23/23 regression, actionlint và independent probes PASS. Đây là kết quả reviewer do owner cung cấp, **không phải GitHub VERIFIED REVIEW** hoặc checks do phiên readiness chạy lại.
- GitHub Review API không có verdict cho #24/#25; comment BLOCK trước đó là lịch sử, đã được verdict cuối do owner cung cấp thay thế. #23 CONDITIONAL được focused correction #24 đóng; không hồi tố PASS trước merge #23, không mở lại finding đã CLOSED khi chưa có lỗi mới. #22 G4 PASS/463 tests theo evidence owner đã lưu trong mục lịch sử phía trên; CI được xác minh riêng.
- `git show -s --format='%H%n%P%n%s' <merge-SHA>` và `git diff --exit-code <reviewed-HEAD> <merge-SHA>` cho #24/#25: reviewed HEAD đúng parent thứ hai của merge, tree diff rỗng (exit 0). Bản raw reviewer report #24/#25 chưa có artifact riêng trong repo; đoạn này lưu tóm tắt và nguồn owner. Có thể đính kèm report gốc để lưu trữ nếu cần, không thay xác nhận owner bằng approval tự bịa.

### Targeted G1/G2 và nền migration

Tái sử dụng full G1 `cadce575597738bf40ad7c45689482de5a5fbc3d`, G2 [conformance-review](../architecture/conformance-review.md), targeted A01 và [evidence A01](../reviews/a01-amendment-evidence.md). Đã kiểm diff spec từ full baseline và từ A01 merge `024ea998c43baf0afde30b069ed6b4876cb96f4a`: sau A01 chỉ B01 đổi 00/01/02/04/05/06/07 và #25 đổi 06 §7 CI. `03-database.md`/`schema.sql` không đổi từ A01; từ B01 merge #23 tới baseline chỉ 06 §7 đổi. Phạm vi này là **TARGETED**, không phải full audit mới hay đổi kiến trúc.

| Đối chiếu có mục tiêu | Kết quả / deliverable đúng mốc |
|---|---|
| Dependency conflict, `07-roadmap.md` §P1 dòng P1-01 so với §P0 đoạn B01; `06-infrastructure.md` §11; ADR-B01 §Phân định gate | **CONFIRMED_CONFLICT:** dòng cũ yêu cầu “closure #1, #2 và nghiệm thu xong”, các nguồn B01 cho DDL giả không chờ #2/manual HTTPS/owner. Owner cho phép sửa đúng ô dependency; scope/ID/AC giữ nguyên, không thay gate dữ liệu thật. |
| DDL, `schema.sql` MEDIA/CATALOG/INVENTORY/PRICING sale_prices/CONTENT redirects; `03-database.md` §1/§3.1–3.3/§3.7–3.8/§4/§8 | Đúng 25 bảng = 2 media + 15 catalog + 6 inventory + sale_prices + redirects, sequence sku_number_seq; FK trong tập này khép kín. `reserved` có ngay; reservations/FK → P2-05, reviews/media → P5-07, vouchers → P2-01. Giữ UNIQUE/CHECK/EXCLUDE/GIN, money bigint, EAV numeric(12,2), inactive NULL và manual noindex/count theo A01; chưa chạy DDL P1 mới. |
| DB role/SQL/helper, `infra/postgres/{init-roles.sql,init-roles.sh,protect-credentials.sql,preflight.sql}`, `GrantsSql.cs`, `scripts/dev-migrate.ps1` | Có đủ trong base, không đổi từ #22. SQL bảo vệ credential mỗi connect, không reset password/repair drift; preflight trước toàn migration chain; locked restore trước build. Preflight shared được nhúng nguyên với grants cố định trong migration closure; helper hiện chỉ PlatformAndIam. Mở rộng helper cho 5 schema/sequence SKU/append-only stock_movements là P1-01 G3, không gọi helper động từ migration lịch sử. |
| Migration/EF baseline, `Persistence/Migrations`, `AppDbContext.cs`, `03-database.md` §1, `05-api.md` §1 | Đúng 4 migration: `202611161624_InitialPlatform`, `202611170919_StaffIam`, `202611180001_SettingsAdministrativeData`, `202611180710_ReconcileRuntimeGrants`. EF Core/design 10.0.15, Npgsql 10.0.3, snapshot hiện platform/IAM, chưa có P1. Loop snake_case/type mapping chưa đáp ứng long/decimal/xmin P1; §2/§3/A7 task record đã yêu cầu bổ sung đúng cột, bảo vệ system xmin. rowVersion opaque, IAM ConcurrencyStamp riêng; không tạo cột xmin/version thường. |
| Spec/test traceability và feasibility | P1-01; DEC-01/04/05/06/20/21/25–30, FR-CAT/FR-INV đã liệt kê trong task record, BR-CAT-02/04/08/10/14/15, BR-INV-01/02/07/10, BR-PRC-01/02, NFR-04/08/09. A1–A9 của task record là kế hoạch parity/SQLSTATE/permission/concurrency/Down/idempotence, chưa phải AC-P1 nghiệp vụ PASS. G02/G21/G34/G38 đã có contract; import key P1-07 và quyết định pha sau không chặn P1-01. Không cần kiến trúc/roadmap mới. |

**C1 PASS / C2 PASS / C3 PASS về readiness.** Không phát hiện blocker kỹ thuật/contract còn lại cho migration disposable. Chưa triển khai mapping/migration/tests P1 là deliverable G3, không phải precondition. Nghiệm thu HTTPS, owner thật đổi password/TOTP, CSRF/lockout/logout, settings audit, backup mã hóa/restore và runtime-role đúng môi trường vẫn **NOT RUN trước dữ liệu thật/go-live**; không ghi P0 DONE. Closure #2 CLI/coverage DEFERRED, owner thứ hai tùy chọn.

### Kiểm chứng và handoff local

- `dotnet tool run dotnet-ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure`: exit 0, “No changes have been made to the model since the last migration.” Dùng connection design-only loopback port 1, khôi phục env sau lệnh; không truy cập DB. `--no-build` dùng artifact local, không thay full build ở CI baseline đã xác minh.
- `python -` (script inline qua PowerShell here-string UTF-8): exit 0, kiểm tĩnh PASS target 25 bảng/FK khép kín, 4 migration IDs, frozen closure SQL = shared preflight + helper grants; chỉ ô dependency P1-01 đổi trong spec. Không chạy migration, DB hoặc application test trong delta docs này.
- `python scripts/test-ci-fastpath.py`: exit 0, **23/23 PASS** (cả PR/push, fail-closed và link/deletion/reference regression). Cùng script `python -` gọi `is_docs_only`/`validate_changed_docs` trên diff local thực: PASS đúng ba Markdown, đủ điều kiện `docs_only=true`, UTF-8/fences/trailing whitespace/local links hợp lệ. `git diff --check origin/main`; `git diff --cached --check`: exit 0, không diagnostic; staged/untracked rỗng. CI cho delta chưa publish là **NOT RUN**; local classification không phải GitHub Actions fast-path PASS. Khi owner cho publish sau G4, PR chỉ ba Markdown này đủ điều kiện fast path theo 06 §7, vẫn phải kiểm ba required jobs tại published HEAD.
- Migration/rollback của lần sửa này: không có; revert đúng delta ba docs nếu cần. P1-01 sau này thêm migration, không sửa bốn migration cũ; recovery/Down an toàn theo task record §7.

**READY_FOR_FOCUSED_G4** cho delta tài liệu hiện tại; reviewer độc lập đọc cùng worktree, full committed/staged/unstaged/untracked diff so `origin/main`, kiểm dependency và nguồn G4/CI. **READY_FOR_P1_01_G3: YES** là kết luận readiness; **NOT AUTHORIZED G3** cho tới owner giao riêng. Không commit/push/PR/merge và không triển khai P1-01 trong phiên này.
