# Mức sẵn sàng task P0–P6

## Current Project State Pointer (Snapshot `2026-11-25 18:46 +07:00`)

> **Điểm vào nhanh cho Technical Lead mới:** Mọi mục lịch sử P0/P1/B01 bên dưới được bảo toàn nguyên vẹn để truy vết. Trạng thái dự án mới nhất tại `origin/main = 591f872d7ba7377e7c5254360a59058576d5c04a` (đã merge tới PR `#47` `P2-13`), phân tách mốc `Latest Verified Spec SHA`, bảng `P1-10..P2-16`, snapshot các worktree in-flight, danh sách HOLD (`P2-07` publish, `P2-06` G3), follow-up `PR-2`/`PR-3` và checklist bàn giao nằm tại mục **[Phase 2 — Trạng thái dự án, Snapshot Worktrees & Bàn giao Technical Lead](#phase-2--trạng-thái-dự-án-snapshot-worktrees--bàn-giao-technical-lead-snapshot-2026-11-25-1846-0700)** ở cuối file. Nguồn quyết định nghiệp vụ & hợp đồng dùng chung: [ADR-P2-01](../decisions/ADR-P2-01-phase2-contracts.md) và [spec-gaps.md](../analysis/spec-gaps.md). Đây là snapshot tại thời điểm kiểm chứng, không phải trạng thái động tự cập nhật.

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

---

## Phase 2 — Trạng thái dự án, Snapshot Worktrees & Bàn giao Technical Lead (Snapshot `2026-11-25 18:46 +07:00`)

> **Lưu ý về tính thời điểm:** Toàn bộ trạng thái Git, worktree, manifest và log kiểm chứng trong mục này được kiểm chứng trực tiếp (read-only) tại mốc **`2026-11-25 18:46 +07:00`** trên `origin/main = 591f872d7ba7377e7c5254360a59058576d5c04a`. Đây **không phải** trạng thái động luôn đúng; mọi phiên Technical Lead hoặc G3/G4 sau này bắt buộc tự chạy lại `git fetch origin --prune`, `git log`, `git worktree list` và `./scripts/candidate-manifest.ps1 -Verify`.

### 1. Mốc kiểm định đặc tả (`Latest Verified Spec SHA`)

Phân biệt rạch ròi giữa Full G1 audit, Targeted G1 audit đã commit, audit ở phiên riêng và commit chạm `docs/spec/` gần nhất (chi tiết bằng chứng tại [ADR-P2-01](../decisions/ADR-P2-01-phase2-contracts.md) §2.2 và [spec-gaps.md](../analysis/spec-gaps.md)):

| Loại mốc | Commit SHA | Phạm vi & Ghi chú | Mức kiểm chứng |
|---|---|---|---|
| **Full G1 Audited Spec SHA** | `cadce575597738bf40ad7c45689482de5a5fbc3d` (`16/11/2026`) | Đọc đủ 10 Markdown + `schema.sql` ([spec-inventory.md](../analysis/spec-inventory.md), [spec-gaps.md](../analysis/spec-gaps.md)) | `VERIFIED_IN_CURRENT_SESSION` |
| **Targeted G1 A01** | Base `288ca8b` → merge `024ea99` (`PR #20`, `18/11/2026`) | Amendment P0/P1 ([a01-amendment-evidence.md](../reviews/a01-amendment-evidence.md)) | `VERIFIED_IN_CURRENT_SESSION` |
| **Targeted G1/G2 B01** | `dadc4435e8f115faaeb9e8511e56e09d5d517a8a` (`19/11/2026`) | Thu gọn vận hành P0 & readiness `P1-01` ([ADR-B01](../decisions/ADR-B01-right-sized-operations.md)) | `VERIFIED_IN_CURRENT_SESSION` |
| **Targeted G1 gần nhất đã commit trong `spec-gaps.md`** | `f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7` (`P1-04`, `19/11/2026`) + closure `P1-07` tại `ed49d11` (`20/11/2026`) | Lưu tại [spec-gaps.md](../analysis/spec-gaps.md) dòng `91–111` | `VERIFIED_IN_CURRENT_SESSION` |
| **Phase 2 Readiness Audit (phiên riêng)** | Phiên đọc chỉ đọc trên `82710b3` (`22/11/2026`) | Chưa có biên bản riêng commit trong `docs/analysis/`; các quyết định Owner đã được xác nhận tường minh tại `STEP 462` | `REPORTED_BY_OTHER_AGENT` |
| **Commit gần nhất thay đổi `docs/spec/`** | `57cb13fb4b03e28749e06ed8a136b6b5e6fa1748` (`P2-01`, PR `#44` merge `85218d1`, `23/11/2026`) | `0` thay đổi trong `docs/spec/` từ `57cb13f` tới `591f872`. Chỉ đi qua task-scoped G4 của `P2-01`, **không phải Full G1 audit mới** | `VERIFIED_IN_CURRENT_SESSION` |

### 2. Trạng thái các task từ `P1-10` đến `P2-16` (xác minh từ `git log --merges origin/main` & worktrees)

*(Lưu ý bằng chứng CI: phiên PR-1 kiểm chứng trực tiếp lịch sử merge trên `origin/main`, không truy vấn trực tiếp GitHub Actions API cho PR `#34`–`#47`; do đó không ghi `CI PASS` cho các PR chưa xác minh API trong phiên này.)*

| Task | Trạng thái (`2026-11-25 18:46`) | PR / Merge SHA trên `origin/main` | Phụ thuộc chính | Ghi chú / Lý do HOLD |
|---|---|---|---|---|
| `P1-10` | `MERGED` | PR `#42` · `e63e90b99b596ba4e1d218d2a920733cbd79edbb` (`2026-11-23`) | `P0-06`, `P1-08` (đã merge) | Quyết định `DEC-P110-01..03` đã `OWNER_APPROVED` (`STEP 438`); CI status trong phiên này: `UNKNOWN`. |
| `P1-11` | `MERGED` | PR `#34` · `c88069d58241fd51f5f6a76da2276c41f7a3830a` (`2026-11-20`) | `P1-03`, `P1-04` (đã merge) | Đã merge trước `P1-10`; CI status trong phiên này: `UNKNOWN`. |
| `P1-12` | `MERGED` | PR `#36` · `b45558fd054e9b7d3ba0a28d93caf4ef100f0eed` (`2026-11-21`) | `P1-05` (đã merge) | Đã merge trước `P1-10`; CI status trong phiên này: `UNKNOWN`. |
| `P1-13` | `MERGED` | PR `#43` · `be3f7b6e50cd75dde392feac4c3c57f2c886f078` (`2026-11-23`) | `P1-09`, `P1-10` (đã merge) | Hoàn tất mã nguồn Phase 1 (`14/14` task đã merge; gate dữ liệu thật/go-live theo `B01` vẫn mở). `GAP-P1-13-01..03`/`ASSUMPTION-01..02` ở trạng thái `IMPLEMENTED_BUT_NOT_CONFIRMED` (`O5`). |
| `P1-14` | `MERGED` | PR `#39` · `b5d0852cdccee96404f9dca7411b079f345ebeb0` (`2026-11-22`) | `P1-05`, `P1-08` (đã merge) | Đã merge; nền Store Catalog cho `BR-SEO-21`. |
| `P2-01` | `MERGED` | PR `#44` · `85218d119b36d5a1b8977a75c9fb71abf454ddde` (`2026-11-23`) | P1 DB foundation (đã merge) | 26 bảng + `crm.customer_code_seq`; đã áp `G03`, `G21` (`order_lines` DELETE), `G17a` (`OWNER_APPROVED`, `STEP 462`). |
| `P2-02` | `MERGED` | PR `#45` · `97735e75f48911babd5af016867d17537ef4afab` (`2026-11-25`) | `P2-01` (đã merge) | Worktree `D:\tra-hien-p2-02` sạch, chưa dọn. `GAP-P202-01..03` / `ASSUMPTION-P202-01..03` / `R-P201-04` là `IMPLEMENTED_BUT_NOT_CONFIRMED` (`O5`). |
| `P2-03` | `IN_REVIEW` (G4 chưa chốt verdict) | Chưa merge (`D:\tra-hien-p2-03` @ `591f872`) | `P2-02` (đã merge) | Manifest `MATCH`; `ASSUMPTION-P203-01..03` là `IMPLEMENTED_BUT_NOT_CONFIRMED`; cần Owner/G4 xem xét rủi ro re-import dòng zalo-only (`O4`) trước publish. |
| `P2-04` | `PLAN_READY` (chưa G3) | Chưa merge (`D:\tra-hien-p2-04` @ `97735e7`) | `P2-02` (đã merge) | Chỉ có 1 file plan untracked; cần rebase lên `origin/main` mới, đồng bộ `[Audited] Interaction` với `P2-03` nếu `P2-03` merge trước, và chốt `GAP-P204-01`/`ASSUMPTION-P204-01..03` (`O7`). |
| `P2-05` | `MERGED` | PR `#46` · `862fbf05fc8b9f9c34fc0441e559bb13e24604a3` (`2026-11-25`) | `P2-01`, `P1-08` (đã merge) | Worktree `D:\tra-hien-p2-05` sạch, chưa dọn. Sở hữu `CompositeOrderOutboxHandler`, `ICatalogInventoryRead`/`IProductSoldOutSync`, `G21` reservations, `G12` core. `GAP-P205-01`/`ASSUMPTION-P205-01..02` là `IMPLEMENTED_BUT_NOT_CONFIRMED` (`O5`). |
| `P2-06` | `PLAN_READY` · **`HOLD G3`** | Chưa merge (`D:\tra-hien-p2-06` @ `862fbf0`) | `P2-05` (đã merge); **phối hợp `P2-07`** | **HOLD G3** do: (1) xung đột `CONF-P206-P207-01` (`OrderPaymentStatusCalculator`, `O1`/`O2`/`O3`); (2) câu hỏi scope `PROP-P206-SCOPE` (`O6a` giữ `ExpireUnpaidOrders`/`extend-payment` ở `P3-03`); (3) `ASSUMPTION-P206-01,03,04,05` (`O7`). `G11=A` & `G12=A` đã `OWNER_APPROVED`. |
| `P2-07` | `IN_REVIEW` · **`HOLD PUBLISH`** | Chưa merge (`D:\tra-hien-p2-07` @ `591f872`) | `P2-05` (đã merge) | Manifest `MATCH`; chưa có verdict G4 cuối; **HOLD publish** chờ chốt `CONF-P206-P207-01` và `O1`–`O3` (`GAP-P207-01`, `ASSUMPTION-P207-01..04` đang `IMPLEMENTED_BUT_NOT_CONFIRMED`). |
| `P2-08` | `PLAN_READY` · **HOLD phần phụ thuộc `O3`/`P2-07`** | Chưa merge (`D:\tra-hien-p2-08` @ `862fbf0`) | `P2-05` (đã merge); cần `OrderPaymentStatusCalculator` & chốt `O3` | Chỉ có 1 file plan untracked; cần chốt `O3` (`Order.MarkShipping` `Order.cs:569`), đồng bộ `P2-07` cho `cod_amount_vnd`, chốt `GAP-P208-01..02`/`ASSUMPTION-P208-01` (`O7`) và rebase baseline mới trước G3. |
| `P2-09` | `WAITING_DEPENDENCIES` | Chưa tạo worktree | `P2-07`, `P2-08` (chưa merge) | Chờ `P2-07` và `P2-08` merge. |
| `P2-10` | `WAITING_DEPENDENCIES` | Chưa tạo worktree | `P2-07`, `P2-08` (chưa merge) | Chờ `P2-07`, `P2-08`; áp `G04+G29`, `G27` (cần duyệt diff spec), `G30`, follow-up `O2` (`flag=needs_refund`) và `O3` (phí ship). |
| `P2-11` | `WAITING_DEPENDENCIES` | Chưa tạo worktree | `P2-08` (chưa merge) | Chờ `P2-08`; đăng ký `IOrderOutboxStep` trên `CompositeOrderOutboxHandler` của `P2-05`. |
| `P2-12` | `PLAN_READY` (chưa G3) | Chưa merge (`D:\tra-hien-p2-12` @ `97735e7`) | `P2-02` (đã merge) | Chỉ có 1 file plan untracked; cần chốt `GAP-P212-01` (`O6b`, không chạy retention dữ liệu thật) & `ASSUMPTION-P212-01..03` (`O7`), rebase baseline mới trước G3. |
| `P2-13` | `MERGED` | PR `#47` · `591f872d7ba7377e7c5254360a59058576d5c04a` (`2026-11-25`) | `P0-06`, `P1-10` (đã merge) | Worktree `D:\tra-hien-p2-13` sạch, chưa dọn. `GAP-01..03`/`ASSUMPTION-01..02` là `IMPLEMENTED_BUT_NOT_CONFIRMED` (`O5`). |
| `P2-14` | `WAITING_DEPENDENCIES` | Chưa tạo worktree | `P2-02` (merged), `P2-03`, `P2-04` | Chờ `P2-03` và `P2-04` merge; lưu ý quyền `GET admin/tags` (`GAP-P202-02=B`). |
| `P2-15` | `WAITING_DEPENDENCIES` | Chưa tạo worktree | `P2-05..P2-10` (mới merge `P2-05`) | Chờ toàn bộ API đơn/thanh toán/vận đơn/COD/đổi trả hoàn tất. |
| `P2-16` | `WAITING_DEPENDENCIES` | Chưa tạo worktree | `P2-15` | Chờ `P2-15` (và toàn bộ nguồn dữ liệu đơn/kho/tài chính P2). |

### 3. Snapshot các Worktree hiện hữu (kiểm chứng read-only lúc `2026-11-25 18:46 +07:00`)

*(Không có container Docker nào đang chạy vào thời điểm `docker ps` lúc `18:46:28 +07:00`. `P1-10`, `P1-13`, `P2-01` không còn worktree cục bộ, trạng thái merge xác minh từ lịch sử Git của `origin/main`. Worktree `D:\tra-hien-p2-decisions-recovery` trên nhánh `docs/p2-decisions-and-handoff-recovery` tại base `591f872` là worktree docs-only của chính PR-1 Knowledge Recovery này, không tự nhúng digest manifest của chính nó để tránh vòng lặp băm.)*

| Worktree | Branch | BASE / Merge-Base (`origin/main`) | HEAD | Trạng thái working tree | Candidate Manifest SHA-256 | Kiểm chứng & Review / Merge Status |
|---|---|---|---|---|---|---|
| `D:\tra-hien` | `main` | `be3f7b6` (chậm `8` commit / `4` PR so với `origin/main` `591f872`) | `be3f7b6e50cd75dde392feac4c3c57f2c886f078` | `0` staged, `1` untracked (`202611230721_P2_05_InventoryReservations.Designer.cs`) + `stash@{0}` (`p1-14-remediation-pre-integration-20261121`) | Không áp dụng | Local `main` chưa fast-forward; **giữ nguyên file untracked và `stash@{0}`** cho tới khi Owner xử lý riêng. |
| `D:\tra-hien-p2-02` | `feature/p2-02-customers` | `29581cf` (nằm trong `origin/main` qua PR `#45` `97735e7`) | `29581cfcf4d1969d92d1e3c4e5e4409f2302526f` | Sạch (`0` modified / untracked / staged) | Lịch sử `f1a542d8…` (`29` entries, base `85218d1`) | **MERGED** (PR `#45`). Worktree chưa dọn. |
| `D:\tra-hien-p2-03` | `feature/p2-03-customer-import` | `591f872d7ba7377e7c5254360a59058576d5c04a` | `591f872d7ba7377e7c5254360a59058576d5c04a` | `0` staged, `16` modified, `9` untracked (`25` file) | `d7da23f3c4a8c05831c72dd24b76a73e555d5e017f6a3b7291e5a5adff96539b` (`MATCH`, `25` entries, base `591f872`) | **Chưa merge · G4 verdict: `UNKNOWN`**. Log mới nhất trên manifest `d7da23f3…`: `152211-827-backend` `PASS` (`1118s`, `1690/1690` tests); `155707-329-backend` `FAIL` (`2` timeout song song); `164618-826-frontend` `PASS` (`305s`); `165134-566-backend` `FAIL` (`1690/1690` tests PASS, `migrations.sql` PASS, lỗi cuối ở `check:api` do `ERR_PNPM_META_FETCH_FAIL` DNS offline); `174305-611-api` `PASS` (`3210s`, kết thúc `18:36:37`, contract khớp worktree). Không suy ra G4 PASS từ log verify; lưu ý `O4`. |
| `D:\tra-hien-p2-04` | `feature/p2-04-interactions-followups` | `97735e7` (chậm so với `591f872`) | `97735e75f48911babd5af016867d17537ef4afab` | `0` staged, `1` untracked (`docs/plans/p2-04-implementation.md`) | Chưa có | **`PLAN_READY`** (chưa G3, `NOT_RUN`). Cần rebase + chốt `O7`. |
| `D:\tra-hien-p2-05` | `feature/p2-05-order-core` | `5e6f6b8` (nằm trong `origin/main` qua PR `#46` `862fbf0`) | `5e6f6b8f6d6baf6fd8af0e6011674a65e018cdd1` | Sạch (`0` modified / untracked / staged) | Lịch sử `9f095247…` (`48` entries, base `97735e7`) | **MERGED** (PR `#46`). Worktree chưa dọn. |
| `D:\tra-hien-p2-06` | `feature/p2-06-admin-orders` | `862fbf0` (chậm so với `591f872`) | `862fbf05fc8b9f9c34fc0441e559bb13e24604a3` | `0` staged, `1` untracked (`docs/plans/p2-06-implementation.md`) | Chưa có | **`PLAN_READY` · `HOLD G3`** (`CONF-P206-P207-01`, `O1`–`O3`, `O6a`, `O7`). |
| `D:\tra-hien-p2-07` | `feature/p2-07-payments-refunds` | `591f872d7ba7377e7c5254360a59058576d5c04a` | `591f872d7ba7377e7c5254360a59058576d5c04a` | `0` staged, `15` modified, `14` untracked (`29` file) | `54bdc666e66b133eea0c9be2c9d0fe23a45f0cc413df5165679ff111e3fb577d` (`MATCH`, `29` entries, base `591f872`) | **Chưa merge · G4 verdict: `UNKNOWN` · `HOLD PUBLISH`** (`CONF-P206-P207-01`, `O1`–`O3`). Log mới nhất trên manifest `54bdc666…`: `160130-328-frontend` `PASS` (`515s`) và `161038-060-backend` `PASS` (`339s`, `411` Unit / `34` Arch / `1242` Integration, `0` skip, `OpenAPI/client are current`). Không suy ra G4 PASS từ log verify. |
| `D:\tra-hien-p2-08` | `feature/p2-08-shipments` | `862fbf0` (chậm so với `591f872`) | `862fbf05fc8b9f9c34fc0441e559bb13e24604a3` | `0` staged, `1` untracked (`docs/plans/p2-08-implementation.md`) | Chưa có | **`PLAN_READY`** (chưa G3, `NOT_RUN`). Chờ chốt `O3` (`Order.MarkShipping`), đồng bộ `P2-07` và chốt `O7`. |
| `D:\tra-hien-p2-12` | `feature/p2-12-privacy-merge-erase` | `97735e7` (chậm so với `591f872`) | `97735e75f48911babd5af016867d17537ef4afab` | `0` staged, `1` untracked (`docs/plans/p2-12-implementation.md`) | Chưa có | **`PLAN_READY`** (chưa G3, `NOT_RUN`). Chờ chốt `GAP-P212-01` (`O6b`) và `O7`, rebase baseline mới. |
| `D:\tra-hien-p2-13` | `feature/p2-13-notifications` | `8fec151` (nằm trong `origin/main` qua PR `#47` `591f872`) | `8fec1516e5275d58c8e0087e8240f3270e087e11` | Sạch (`0` modified / untracked / staged) | Lịch sử `d43f4b53…` (`19` entries, base `862fbf0`) | **MERGED** (PR `#47`). Worktree chưa dọn. |

### 4. Shared Contracts, Cross-Task Follow-Ups và Quyết định Owner chưa chốt

- **8 Shared Contracts & Ownership:** Chi tiết đầy đủ tại [ADR-P2-01 §11](../decisions/ADR-P2-01-phase2-contracts.md#11-bảng-shared-contracts--ownership) (`OrderPaymentStatusCalculator` đang xung đột `P2-07` ↔ `P2-06`; `CompositeOrderOutboxHandler`/`ICatalogInventoryRead`/`IProductSoldOutSync` đã merge tại `P2-05`; `[Audited] Interaction`/`TabularImportFile`/`CrmModuleBoundaryRule` đang in-flight ở `P2-03`; `IRefundRecordedHook` đang in-flight ở `P2-07`; `INotificationChannel`/`NotificationTemplateNames` đã merge tại `P2-13`).
- **Quyết định Owner chưa chốt (`O1`–`O7`):** Chi tiết tại [ADR-P2-01 §10](../decisions/ADR-P2-01-phase2-contracts.md#10-tổng-hợp-các-quyết-định-owner-chưa-chốt-o1o7-không-phải-owner_approved) — không chặn PR-1; chỉ giữ HOLD các task chịu tác động trực tiếp (`P2-07` publish, `P2-06` G3, phần phụ thuộc `O3` của `P2-08`, phần retention `O6b` của `P2-12`).
- **Cross-Task Follow-Ups (Phase 2–5):** Chi tiết tại [ADR-P2-01 §12](../decisions/ADR-P2-01-phase2-contracts.md#12-cross-task-dependencies-và-follow-ups-phase-25--workflow-v2).

### 5. Workflow V2 Follow-Ups (RECORD ONLY — CHƯA TRIỂN KHAI TRONG PR-1)

Hai gói cải tiến quy trình sau đã được thống nhất phương hướng và chỉ được ghi nhận để triển khai ở các PR riêng sau khi PR-1 hoàn tất (giữ nguyên khung cổng `G0`–`G4` và phân tầng `A`/`B` hiện hành):

#### PR-2 — Deterministic Verification Tooling Hardening (`NOT_STARTED`, phạm vi dự kiến: `scripts/*`, CI `full`)

1. Ngăn tuyệt đối việc dùng filtered tests làm bằng chứng full verification (chặn kết hợp `-Manifest` với `-Filter`).
2. Phân biệt rõ mức phủ kiểm chứng `FULL` / `FOCUSED` (ví dụ `RESULT=PASS_FOCUSED`) / `API` / `DOCS`.
3. Xem xét bổ sung `-Mode docs` để kiểm chứng cục bộ đúng tập candidate docs-only thực tế (bao gồm cả file modified và untracked).
4. Phân biệt rạch ròi giữa **candidate integrity** (manifest khớp working tree / staged index) và **base freshness** (`-RequireBase`).
5. Kiểm tra đồng thời cả working tree và staged index trước publish.
6. Phân biệt luồng **publish lần đầu** và **republish sau CI remediation** — không áp đặt cứng `HEAD == base` cho mọi trường hợp nếu không phù hợp với topology nhánh đã publish.
7. Bổ sung cơ chế **host-level mutual exclusion** đơn giản cho các lượt heavy PostgreSQL verification, không chỉ dựa vào `docker ps` (vốn có thể xảy ra race condition giữa nhiều agent khởi chạy gần như đồng thời).
8. Viết đầy đủ regression tests cho toàn bộ chốt kiểm chứng mới.

#### PR-3 — Workflow V2 Governance (`NOT_STARTED`, phạm vi dự kiến: `AGENTS.md`, `docs/*`, `.agents/skills/*`, CI `full`)

1. Áp dụng triệt để **chính sách độc lập với mô hình (`model-independent`)**: không hardcode tên mô hình, nhà cung cấp hay mức suy luận thành quy định hoặc điều kiện nghiệm thu.
2. Chuẩn hóa **Blind-First G4** (reviewer tự kiểm đối chiếu độc lập trước khi đối chiếu nhận định implementer) và **một report contract thống nhất** cho các cổng.
3. Tách **Immutable Implementation Record** (hồ sơ từng lát cắt G3 cố định sau merge) khỏi **Dynamic Task Status** (trạng thái sống của dự án).
4. Áp dụng **lập kế hoạch theo mức độ rủi ro**; giữ rõ phân định giữa **Phase-level G1/G2** và **scoped task planning**, hỗ trợ **Design Spike chỉ đọc** khi cần làm rõ rủi ro trước G3.
5. Không tạo worktree sớm chỉ để chứa file plan trước khi sẵn sàng G3.
6. Không đặt giới hạn cứng số dòng cho tài liệu kế hoạch và không dùng tỷ lệ phần trăm số file thay đổi làm hard gate cho re-review.
7. Thực hiện remediation và focused re-review dựa trên ngữ cảnh kỹ thuật, nguyên nhân gốc và bán kính ảnh hưởng (`blast radius`).
8. Chuẩn hóa mô hình **Stateless Lead** và **knowledge checkpoint** định kỳ vào Git.

### 6. Checklist bàn giao cho Technical Lead mới

Trước khi ra quyết định điều phối hoặc phát hành prompt cho bất kỳ task nào, phiên Technical Lead mới phải hoàn tất 4 bước sau:

1. **Xác minh Git state, merged tasks, active worktrees và candidate snapshots:** Chạy `git fetch origin --prune`, kiểm `git log -n 15 --oneline origin/main`, `git worktree list`, `git status --short -uall`, `docker ps`, và chạy `./scripts/candidate-manifest.ps1 -Verify` cho các worktree in-flight (`P2-03`, `P2-07`, hoặc các nhánh mới phát sinh); không tin mặc định vào snapshot cũ của mục §3.
2. **Đọc các quyết định Owner còn hiệu lực và các xung đột chưa giải quyết:** Đọc [ADR-P2-01](../decisions/ADR-P2-01-phase2-contracts.md) và [spec-gaps.md](../analysis/spec-gaps.md) để nắm rõ `15` quyết định `OWNER_APPROVED` đã khôi phục, các mục `IMPLEMENTED_BUT_NOT_CONFIRMED`, xung đột `CONF-P206-P207-01`, và trạng thái chưa chốt của `O1`–`O7`.
3. **Xác minh dependencies và follow-ups của những task tiếp theo:** Đối chiếu thứ tự mở khóa an toàn (`P2-03` chốt G4 & `O4`; `O1`–`O3` mở khóa `P2-07` → `P2-06` / `P2-08` → `P2-09` / `P2-10` / `P2-11`; `O6b` & `O7` trước `P2-04` / `P2-12`), không bỏ qua bước rebase/`base-integration` cho các worktree đang chậm sau `origin/main`.
4. **Xác minh workflow hiện hành, trạng thái `PR-2`/`PR-3` và bước tiếp theo an toàn:** Kiểm tra `AGENTS.md`, `docs/WORKFLOW.md`, trạng thái merge của PR-1, kế hoạch triển khai `PR-2` và `PR-3`, giữ nguyên hiện trạng `D:\tra-hien` local `main` (file designer untracked & `stash@{0}`) cho tới khi có lệnh dọn dẹp riêng của Owner.
