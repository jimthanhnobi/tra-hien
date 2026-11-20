# P1-04 — amendment local, targeted G1/G2 và evidence

**Cập nhật G3:** owner đã xác nhận amendment G4 PASS, duyệt amendment và giao
implementation local. Hành vi, tests, trạng thái bàn giao hiện hành nằm tại
[P1-04 G3 evidence](p1-04-g3-evidence.md). Nội dung bên dưới giữ lịch sử các lượt
amendment/ finding fix trước phê duyệt G3, không phải gate đang chặn implementation.

Ngày 19/11/2026. **READY_FOR_LOCAL_G4: NO cho implementation P1-04.**
Owner báo amendment G4 BLOCK (P1 fallback, P2 warning); bản sửa đang chuẩn bị cho
focused G4, chưa tạo EF migration/API/use cases. Các quyết định owner đã chốt
không cần duyệt lại; scope lượt này chỉ sửa hai finding, không lập kế hoạch lại.

## G0 và phạm vi được giao

- Worktree duy nhất: `D:\tra-hien-p1-04`; branch `feature/p1-04-catalog-taxonomy`.
- Base/HEAD/origin/main sau fetch: `f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7`.
- Đầu vào sạch. Không thao tác trong worktree P1-03 hoặc CI reliability.
- Owner giao G3 P1-04 local; sau discovery yêu cầu chuẩn bị amendment schema/publish
  và tuân gate review. Handoff này dừng ở **amendment trước migration**.
- Không commit/push/PR/merge/production apply. G4 amendment trước đó BLOCK theo
  owner; focused G4 bản sửa và CI delta chưa chạy.

## Targeted G1/G2

Tái sử dụng full G1 `cadce575597738bf40ad7c45689482de5a5fbc3d`, A01
`024ea998c43baf0afde30b069ed6b4876cb96f4a`, B01 và
[G2 conformance](../architecture/conformance-review.md),
[task readiness](task-readiness.md), [P1-02](p1-02-implementation.md).
Đầu lượt kiểm `git diff --exit-code 8791e8ac61f5de42815a84edbc0a0602ff177c26 HEAD -- docs/spec`
exit 0, rỗng. Spec đầu vào không đổi; sau câu trả lời owner mới sửa sáu file spec
local dưới đây. Coverage **TARGETED**, không nhận là full audit mới.

Đã đọc/đối chiếu 00-README, 02-architecture và phần liên quan của 01/03/04/05/07/09
cùng schema.sql, G1/G2/A01/B01. Targeted delta audit sáu file spec xác nhận chỉ đổi
publication-history/publish contract của P1-04; không thay layers, module, roadmap,
product SKU hoặc P1-03. G02 tạo bảng redirect đã CONTRACT_ALIGNED bởi A01/P1-01.

| Nguồn tại audited HEAD, trước amendment | Requirement / phạm vi |
|---|---|
| `07-roadmap.md:53` | P1-04; dependency P1-01 đã ở base; P1-03 không là dependency |
| `01-requirements.md:66–72` | FR-CAT-02/03/08 hierarchy, material/artisan, collection thủ công |
| `04-business-rules.md:34,51–60,65–67` | BR-CAT-01/06/08/09/10/14 slug, IDs, history, tree, search, SKU bất biến |
| `schema.sql:226–279,392–413,1137–1144` | Bốn bảng, media/product FK, collection_products, redirects |
| `05-api.md:5–27,107–108` | Admin routes, permission, CSRF, rowVersion, pagination/ProblemDetails |
| `09-seo.md:36,76,81–104` | BR-SEO-03/23/31/32/35/36/37 redirect, manual noindex, fallback, content |
| `04-business-rules.md:255` | BR-CNT-02 server Tiptap renderer/sanitizer whitelist |
| `02-architecture.md` §3–7,10 | Layers, UoW, audit/outbox, AdminCookie, real PG tests |

DEC-01/04/14/15/16/21/26/30; NFR-04/07/08/09. AC-P1-03 là product redirect
acceptance ở P1-05; P1-04 test cùng invariant cho bốn entity, không nhận PASS AC
product. AC-P1-04 là giá vốn/kho, không là acceptance riêng task P1-04.

**G2: GO chuẩn bị amendment; HOLD migration/application tại gate review amendment.**
Schema chỉ approved in principle; business contract owner-approved. Chi tiết nguồn
và ranh giới tại [owner decision](../decisions/ADR-P1-04-publication-history-and-publish.md).
Không dùng Q1–Q8/import key/pha sau làm blocker. Không tự coi amendment đã G4 PASS.

## Quyết định và authoritative delta

- `schema.sql`: thêm đúng bốn `first_published_at timestamptz NULL`, không default;
  PowerShell so với HEAD xác nhận toàn bộ DDL còn lại giống hệt baseline.
- `03-database.md` §3.10 và BR-CAT-08: set một lần ở public transition thành công,
  giữ nguyên sau hide/archive/republish/rename; cùng transaction với trạng thái.
- **Không backfill bằng current status/created_at/updated_at/thời điểm migration.**
  Đề xuất timestamp migration trước câu trả lời owner đã rút bỏ. Target rỗng không
  cần backfill; target populated phải có evidence history hoặc HOLD đến phương án
  được duyệt. Không coi NULL trên row cũ chưa rõ lịch sử là never-public.
- `05-api.md` §4.2: GET catalog.read; ghi catalog.write; material/artisan public
  chỉ qua /publish + catalog.publish; POST/PATCH không bypass. Schema/name/slug/
  kind validation, server sanitize, media tùy chọn phải public-use image hợp lệ.
- BR-SEO-35: dưới 400 từ sanitized plain text **chỉ warning**, không hard blocker,
  không tự noindex; SEO fallback BR-SEO-31/32, không áp product BR-CAT-04.
- Category/collection hidden/visible qua catalog.write, không thêm /publish hoặc
  archive redirect rule. `00-README.md` ghi amendment local pending G4.

## Hiện trạng implementation đã kiểm

P1-01 đã có entities/mappings/migration/grants của bốn bảng và quan hệ, media và
redirects. Chưa có taxonomy API/use cases/slug service/redirect writer/sanitizer,
chưa có taxonomy xmin mapping/[Audited]/FirstPublishedAt. Chỉ product có mốc đó.
Có Result/Error, FluentValidation/transaction decorators, IUnitOfWork/EfUnitOfWork,
IClock, AdminCookie/CSRF, AuditSaveChangesInterceptor/AuditValuePolicy deny-by-default,
EfOutboxWriter và OutboxBatchProcessor. Chưa có RebuildProductSearchText handler.

BR-CAT-10 phải enqueue cùng rename transaction và có consumer tối thiểu rebuild
từ current product/variants/type/material/artisan bằng f_unaccent, idempotent
redelivery; không silently ignore hoặc enqueue để handler_unavailable. P1-04
không thêm product CRUD hoặc sửa SKU. Đây là kế hoạch, chưa có code.

## Endpoint inventory dự kiến sau gate

Tất cả dưới `/api/v1/admin`; **0 endpoint đã thêm**, 20 endpoint dự kiến:

| Endpoint | Quyền |
|---|---|
| GET `categories`, `categories/{id}` | catalog.read |
| POST `categories`, PATCH `categories/{id}` | catalog.write |
| GET `collections`, `collections/{id}` | catalog.read |
| POST `collections`, PATCH `collections/{id}` | catalog.write |
| GET `collections/{id}/products` | catalog.read |
| PUT `collections/{id}/products` | catalog.write |
| GET `materials`, `materials/{id}` | catalog.read |
| POST `materials`, PATCH `materials/{id}` | catalog.write |
| POST `materials/{id}/publish` | catalog.publish |
| GET `artisans`, `artisans/{id}` | catalog.read |
| POST `artisans`, PATCH `artisans/{id}` | catalog.write |
| POST `artisans/{id}/publish` | catalog.publish |

Không DELETE/public routes, seed, products/variants CRUD, SKU generator, media
infrastructure, frontend UI, store API hoặc CI optimization.

## File scope và bất biến dự kiến cho G3 tiếp theo

Domain/Catalog rules; Application/Catalog DTOs/validators/handlers/persistence ports;
Infrastructure/Catalog persistence/redirects/sanitizer/search outbox handler;
taxonomy entity/model/audit policy và migration/snapshot mới; API/Catalog +
Program/service registration; generated client; unit/PG17 integration tests.
Những file code này **chưa bị sửa** ở handoff amendment.

- Serialize structural category writes trước khi đọc cây để chống write-skew;
  validate parent/self/cycle/ancestor depth + full subtree height ≤3. Khóa row
  riêng không đủ cho reparent hai nhánh; lock namespace chỉ hierarchy, không toàn
  app. Test barrier với hai transaction độc lập, timeout/conflict hữu hạn.
- Opaque rowVersion: malformed 400/stale 409; collection relation-only PUT cũng
  đổi version parent. Unique/FK/order/no duplicate/atomic replace, giữ product IDs.
- Slug BR-CAT-01 ≤80 gồm suffix; UNIQUE chống race; auto suffix retry có giới hạn/
  savepoint, không sửa slug nhập tay. Rename ever-public/flatten/reuse/old→final
  cùng transaction; draft không tạo public history. Không chain/loop.
- Server Tiptap whitelist BR-CNT-02, validate URL/media public image, no private
  evidence; không tin HTML client. Audit nghiệp vụ quan trọng và noindex reason
  BR-SEO-23 bằng entity/path allowlist; che nội dung cá nhân/HTML/PII.
- First publication/slug/state/redirect/audit/outbox cùng UoW; failure rollback hết.
  Giữ seed metadata/IDs/FKs/SKUs. Không tái sinh SKU khi đổi material code.

## Evidence thực chạy

Các .NET commands chạy baseline trước application delta; không phải test P1-04.

| Lệnh | Exit / kết quả |
|---|---|
| `git fetch origin --prune` | 0 PASS; expected origin/main khớp |
| `git diff --exit-code 8791e8ac61f5de42815a84edbc0a0602ff177c26 HEAD -- docs/spec` | 0 PASS trước amendment |
| `dotnet --version` | 0; 10.0.404 |
| `dotnet restore backend/TeaPremium.sln --locked-mode` | 0 PASS |
| `dotnet build backend/TeaPremium.sln --no-restore` | 0 PASS; 0 warnings/errors |
| `dotnet test backend/tests/TeaPremium.UnitTests/TeaPremium.UnitTests.csproj --no-build --no-restore` | 1 FAIL; 66 passed / 6 failed / 72 total; Domain.dll bị Application Control chặn 0x800711C7 |
| `dotnet test backend/tests/TeaPremium.ArchitectureTests/TeaPremium.ArchitectureTests.csproj --no-build --no-restore` | 1 FAIL; assembly bị Application Control chặn trước discovery |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --no-restore --filter FullyQualifiedName~CatalogInventoryParityTests` | 1 FAIL; assembly bị Application Control chặn trước discovery; chưa chạy PG integration test |
| `docker info --format '{{.ServerVersion}}'` | 0; 29.6.2 |
| `docker image inspect postgres:17 --format '{{.Id}}'` | 0; image sha256:31ba9c94a03ada9988f2bb493a22a75eef3a185884533e5cdbcd498ea835b08f |

Không tắt/sửa Application Control. Full backend/focused P1-04/OpenAPI/client/
frontend/G4/CI delta **NOT RUN**; không claim runtime/permission/security PASS.
Python doc-validator attempt (`python -`, stdin gọi `scripts/ci-fastpath.py`): exit 1, Python alias chưa có
interpreter. Dùng PowerShell UTF-8 strict/whitespace/local-file-link check thay thế.

### DDL trên PostgreSQL 17 riêng

Container `teapremium-p104-amendment-01abebd4`, `--network none`, không publish port,
không shared volume; chỉ dữ liệu tổng hợp. Các lệnh thực:

```powershell
docker run --detach --rm --network none --name teapremium-p104-amendment-01abebd4 --label codex.task=p1-04-amendment --env POSTGRES_HOST_AUTH_METHOD=trust postgres:17
docker exec teapremium-p104-amendment-01abebd4 pg_isready -U postgres
Get-Content -Raw -Encoding UTF8 docs/spec/schema.sql | docker exec --interactive teapremium-p104-amendment-01abebd4 psql -U postgres --set ON_ERROR_STOP=1
```

Run/readiness exit 0. DDL attempt đầu exit 1 ở cuối vì thiếu role tp_app; không
claim PASS cho lượt này. Tạo role tiền đề đúng spec, DB mới và chạy lại:

```powershell
docker exec teapremium-p104-amendment-01abebd4 psql -U postgres --set ON_ERROR_STOP=1 --command 'CREATE ROLE tp_app; CREATE ROLE tp_owner; CREATE ROLE tp_readonly;' --command 'CREATE DATABASE p104_amendment;'
Get-Content -Raw -Encoding UTF8 docs/spec/schema.sql | docker exec --interactive teapremium-p104-amendment-01abebd4 psql -U postgres -d p104_amendment --set ON_ERROR_STOP=1 --quiet
```

Cả hai exit 0, toàn schema DDL PASS trên DB mới. Sau đó pipe SQL sau qua
`docker exec --interactive teapremium-p104-amendment-01abebd4 psql -U postgres -d p104_amendment --set ON_ERROR_STOP=1`: exit 0.

```sql
DO $$
DECLARE matched integer;
BEGIN
  SELECT count(*) INTO matched FROM information_schema.columns
  WHERE table_schema = 'catalog' AND table_name IN ('categories','collections','materials','artisans')
    AND column_name = 'first_published_at' AND data_type = 'timestamp with time zone'
    AND is_nullable = 'YES' AND column_default IS NULL;
  IF matched <> 4 THEN RAISE EXCEPTION 'Expected exactly four nullable timestamptz columns without defaults'; END IF;
END $$;
SHOW server_version;
SELECT table_name, column_name, data_type, is_nullable, column_default
FROM information_schema.columns WHERE table_schema='catalog'
AND table_name IN ('categories','collections','materials','artisans') AND column_name='first_published_at'
ORDER BY table_name;
```

Output: PostgreSQL **17.11**; đúng bốn nullable timestamptz, không default.
`docker stop teapremium-p104-amendment-01abebd4`: exit 0; --rm cleanup container.
Đây là DDL-target evidence bằng postgres superuser, **không phải EF migration,
runtime grants, historical backfill, lifecycle hoặc concurrency tests PASS**.

## Test matrix còn phải thực hiện sau gate

CRUD; depth3/depth4/direct-indirect cycles/subtree overflow/concurrent reparent;
slug unique/manual/auto/race; draft/public/hidden/archive rename, timestamp giữ
nguyên khi republish, A→B→C/reuse/loop/rollback/concurrent slug-state writes;
collection order/duplicate/FK/atomic replace; tám kinds, nullable/invalid/unique/XX
SKU codes; publish permission/bypass/400-word warning không blocker/noindex;
metadata/media/private asset/sanitize; rename outbox rollback/redelivery/rebuild;
anonymous/missing read-write-publish/missing-invalid CSRF; data/IDs/FKs/seed/SKU
preservation; migration up/down/recovery; media/IAM/API client regression.

Testcontainers phải dùng postgres:17/random port/DB riêng, không shared dev DB.

## Migration, risks và handoff

Chưa có EF migration, application change hoặc DB thật để rollback/assessment.
Mới sửa DDL **target**. G3 sau review phải thêm entity/mapping/snapshot/migration
đồng bộ, không sửa migration cũ; khảo sát đúng target/evidence trước apply; unresolved
historical data HOLD. Dữ liệu thật: backup/roll forward/recovery, không destructive
Down để xóa public history. Không suy ra DB thật rỗng từ test.

P1-03 có thể cùng sửa Program/service registration/client/fixtures/snapshot;
ghi nhận xung đột tích hợp tiềm năng, không copy code chưa xong hoặc sửa worktree
khác. Không thay seed CLI; schema bốn bảng thuộc P1-04. Review depth A.

Delta local chỉ docs/spec + gap/task/decision records, không staged. Reviewer
phải đọc cả hai untracked records. Next step: fresh independent reviewer cùng
worktree review amendment so với origin/main; chưa review implementation P1-04.
**READY_FOR_LOCAL_G4: NO cho implementation; chưa nhận task hoàn tất.**

## Kiểm tra artifact và inventory trước finding fix

PowerShell schema-diff assertion đã chạy exit 0, PASS đúng bốn cột thêm,
không có thay đổi DDL khác:

```powershell
$taskOldSchema = git show HEAD:docs/spec/schema.sql
$taskNewSchema = Get-Content -Encoding UTF8 docs/spec/schema.sql
$taskOldText = ($taskOldSchema -join "`n").TrimEnd()
$taskNewText = (($taskNewSchema | Where-Object { $_ -notmatch '^  first_published_at timestamptz,\s+-- BR-CAT-08:' }) -join "`n").TrimEnd()
if ($taskOldText -cne $taskNewText) { throw 'Unexpected schema delta beyond the four approved columns' }
if (($taskNewSchema | Where-Object { $_ -match '^  first_published_at timestamptz,\s+-- BR-CAT-08:' }).Count -ne 4) { throw 'Expected four approved additions' }
```

UTF-8 strict, không trailing whitespace, cặp code fences và local-file links:
PowerShell kiểm tám Markdown thay đổi/untracked, exit 0 PASS. Không claim đây là
Python validator hoặc CI. `git diff --check origin/main` và
`git diff --cached --check`: exit 0 PASS. Staged rỗng; code/package/client không đổi.

`git status --short`:

```text
 M docs/analysis/spec-gaps.md
 M docs/spec/00-README.md
 M docs/spec/03-database.md
 M docs/spec/04-business-rules.md
 M docs/spec/05-api.md
 M docs/spec/09-seo.md
 M docs/spec/schema.sql
?? docs/decisions/ADR-P1-04-publication-history-and-publish.md
?? docs/plans/p1-04-implementation.md
```

`git diff --stat origin/main` (không bao gồm hai untracked files):

```text
 docs/analysis/spec-gaps.md     | 18 ++++++++++++++++++
 docs/spec/00-README.md         | 18 ++++++++++++++++++
 docs/spec/03-database.md       | 26 ++++++++++++++++++++++++++
 docs/spec/04-business-rules.md |  1 +
 docs/spec/05-api.md            |  4 +++-
 docs/spec/09-seo.md            |  2 +-
 docs/spec/schema.sql           |  4 ++++
 7 files changed, 71 insertions(+), 2 deletions(-)
```

`git diff --cached --stat`: rỗng. `git ls-files --others --exclude-standard`:
hai file decision/task có dấu `??` ở trên. Không có commit mới; HEAD/base vẫn
`f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7`.

## Sửa finding amendment G4 — 19/11/2026

Input: owner báo independent amendment G4 BLOCK với P1 SEO fallback và P2
publication warning contract. Đây là thông tin review do owner cung cấp, không
phải tự nhận đã chạy lại review. Chỉ sửa `09-seo.md`, `05-api.md`, decision record,
record này và ba hàng traceability BR-SEO-31/32/35; các phần kế hoạch ở trên giữ
làm lịch sử, không khởi động implementation/planning mới.

Base/HEAD `f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7`, branch
`feature/p1-04-catalog-taxonomy`, worktree `D:\tra-hien-p1-04`.
Targeted G1 coverage: BR-SEO-31/32/35 (DEC-21) ↔ `05-api.md` §1/§4.2 ↔
P1-04/FR-CAT-03 và quyết định owner. Không tạo FR/BR/AC/task ID mới.

P1 đã bổ sung thứ tự fallback, mẫu trung tính dùng tên thực tế, explicit SEO ưu
tiên, brand cấu hình/missing brand, không suy main material, bỏ nguồn rỗng và
word-boundary truncation cho metadata sinh. Fallback không ghi vào explicit SEO.
P2 đã chốt source/procedure/exclusions, NULL=0, code/field/count/response HTTP 200,
không dùng `thin_description_noindex`, không tự noindex hay vượt publish permission.

### Acceptance contract để focused reviewer đối chiếu

Đây là expected behavior của spec; **không phải test runtime đã PASS**:

| Case (áp dụng cả hai entity trừ khi ghi rõ) | Kết quả bắt buộc / nguồn |
|---|---|
| 0 / 399 từ | HTTP 200 published, một content_below_recommended_words; actualWords đúng 0/399, recommendedWords 400 |
| 400 / 401 từ | HTTP 200 published, warnings rỗng |
| NULL/empty/whitespace hoặc HTML sanitize thành rỗng | 0 từ, warning; không thêm publish blocker |
| `<p>Một&nbsp;từ</p><p>hai &amp; ba</p>` | Visible text `Một từ hai & ba`, 5 token; không đếm thẻ, không nối block |
| `<p>một&#160;hai&#x2003;ba</p>` | Normalize NBSP/Unicode whitespace, 3 token |
| script/style có text + `<p>thật</p>` | Chỉ 1 token `thật`, không unwrap/count script/style |
| summary/SEO/lưới sản phẩm/bài liên quan/boilerplate dài, nguồn description/bio rỗng | Vẫn 0 từ; chỉ đếm source của entity |
| Explicit seo_title/seo_description có nội dung | Dùng explicit tương ứng; không overwrite/trim stored SEO bằng fallback; mỗi field chọn độc lập |
| Material không seo_description | Ưu tiên summary; nếu rỗng dùng sanitized description_html text; nếu cũng rỗng dùng mẫu trung tính |
| Material tên `Tử nê`, không summary/description/brand/SEO | Title `Tử nê: đặc điểm, cách chọn và tác phẩm`; description `Thông tin về chất liệu Tử nê.`; 0 từ và warning |
| Artisan tên `Chu Bân`, không main material/bio/brand/SEO | Title `Nghệ nhân Chu Bân`; description `Thông tin về nghệ nhân Chu Bân.`; không suy material từ products; 0 từ và warning |
| Brand thiếu/rỗng | Không brand, không separator treo, không tên brand bịa |
| Metadata sinh dài / ngắn | Rút gọn tại ranh giới từ/Unicode; bỏ brand title trước nếu quá dài; không pad nội dung ngắn. Token đầu vượt mục tiêu giữ nguyên, không thêm blocker |
| Material / artisan dưới 400 | field lần lượt descriptionHtml / bioHtml; code content_below_recommended_words; không dùng mã của product |
| Thiếu quyền publish / CSRF | Từ chối theo API chung, không public transition dù nội dung chỉ cảnh báo |
| Warning hoặc fallback với robots_noindex true/false trước đó | Giữ nguyên cờ; không tự bật/clear noindex; first_published_at lifecycle giữ nguyên |

Không chạy EF/API/frontend/runtime tests trong lượt docs-only này. Evidence .NET
và PG17 ở các mục trên là lịch sử trước finding fix, không nhận là đã chạy lại.
Next step: focused G4 độc lập cùng worktree trên hai finding; không có independent
PASS ở lượt sửa này. Không commit/push/PR/merge/production apply.

### Validation thực chạy cho finding fix

PowerShell kiểm tài liệu/JSON/IDs dưới đây: **exit 0 PASS** — 9 Markdown files
(UTF-8 strict/whitespace/fences/local files và heading anchors), cả hai JSON
examples hợp lệ và khớp shape, giữ nguyên 58 BR-SEO IDs. Đây là kiểm artifact/
contract, không là test HTML sanitizer hay publish runtime:

```powershell
$ErrorActionPreference = 'Stop'
$taskDocs = @(git diff --name-only origin/main -- '*.md') + @(git ls-files --others --exclude-standard -- '*.md')
$taskUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
foreach ($taskDoc in $taskDocs) {
  $taskFile = (Resolve-Path -LiteralPath $taskDoc).Path
  $taskText = $taskUtf8.GetString([IO.File]::ReadAllBytes($taskFile))
  if ($taskText -match '(?m)[ \t]+\r?$') { throw "Trailing whitespace: $taskDoc" }
  $taskFence = ''
  foreach ($taskLine in ($taskText -split '\r?\n')) {
    if ($taskLine -match '^ {0,3}(`{3,}|~{3,})') {
      if (!$taskFence) { $taskFence = $Matches[1] }
      elseif ($Matches[1][0] -eq $taskFence[0] -and $Matches[1].Length -ge $taskFence.Length) { $taskFence = '' }
      continue
    }
    if ($taskFence) { continue }
    foreach ($taskLinkMatch in [regex]::Matches($taskLine, '\]\(([^)]+)\)')) {
      $taskLink = $taskLinkMatch.Groups[1].Value.Trim('<','>')
      if ($taskLink -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') { continue }
      $taskParts = $taskLink -split '#', 2
      $taskTarget = if ($taskParts[0]) { Join-Path (Split-Path -Parent $taskFile) ([Uri]::UnescapeDataString($taskParts[0])) } else { $taskFile }
      if (!(Test-Path -LiteralPath $taskTarget -PathType Leaf)) { throw "Missing target: $taskDoc -> $taskLink" }
      if ($taskParts.Count -eq 2 -and $taskParts[1]) {
        $taskAnchors = foreach ($taskHeading in (Get-Content -LiteralPath $taskTarget -Encoding UTF8)) {
          if ($taskHeading -match '^#{1,6}\s+(.+?)\s*#*\s*$') {
            $taskSlug = [regex]::Replace($Matches[1].ToLowerInvariant(), '[^\p{L}\p{M}\p{N}_\-\s]', '')
            $taskSlug -replace ' ', '-'
          }
        }
        if ([Uri]::UnescapeDataString($taskParts[1]) -notin $taskAnchors) { throw "Missing heading: $taskDoc -> $taskLink" }
      }
    }
  }
  if ($taskFence) { throw "Unclosed fence: $taskDoc" }
}
Write-Output "PASS: $($taskDocs.Count) Markdown files; strict UTF-8, whitespace, fences, local links/anchors"
$taskApi = Get-Content -Raw -Encoding UTF8 docs/spec/05-api.md
$taskSection = [regex]::Match($taskApi, '(?s)\*\*P1-04 — response publish material/artisan\.\*\*.*?(?=\r?\nUpload/complete)').Value
$taskBlocks = [regex]::Matches($taskSection, '(?ms)^```json\r?\n(.*?)^```[ \t]*\r?$')
if ($taskBlocks.Count -ne 2) { throw 'Expected two P1-04 JSON examples' }
$taskSuccess = $taskBlocks[0].Groups[1].Value | ConvertFrom-Json
$taskWarning = $taskBlocks[1].Groups[1].Value | ConvertFrom-Json
if ($taskSuccess.id -ne '<entity-id>' -or $taskSuccess.status -ne 'published' -or $taskSuccess.warnings.Count -ne 0) { throw 'Invalid success example' }
if ($taskWarning.code -ne 'content_below_recommended_words' -or $taskWarning.field -ne 'bioHtml' -or $taskWarning.actualWords -ne 399 -or $taskWarning.recommendedWords -ne 400 -or $taskWarning.actualWords -is [string]) { throw 'Invalid warning example' }
Write-Output 'PASS: both P1-04 JSON examples parse and match the response/warning contract'
$taskSeo = Get-Content -Raw -Encoding UTF8 docs/spec/09-seo.md
$taskBaselineSeo = (git show HEAD:docs/spec/09-seo.md) -join [Environment]::NewLine
$taskIdPattern = '(?m)^- \*\*(BR-SEO-[0-9]+)\*\*'
$taskOldIds = @([regex]::Matches($taskBaselineSeo, $taskIdPattern) | ForEach-Object { $_.Groups[1].Value })
$taskNewIds = @([regex]::Matches($taskSeo, $taskIdPattern) | ForEach-Object { $_.Groups[1].Value })
if (Compare-Object $taskOldIds $taskNewIds) { throw 'SEO rule IDs changed' }
Write-Output "PASS: $($taskNewIds.Count) existing BR-SEO IDs preserved"
```

Các kiểm tra khác của lượt sửa (exit code/kết quả thực):

| Lệnh / đối chiếu | Kết quả |
|---|---|
| `git diff --check origin/main` | 0 PASS |
| `git diff --cached --check` | 0 PASS; staged rỗng |
| `Get-FileHash -Algorithm SHA256 docs/spec/schema.sql` | 0; SHA-256 **4FEBFB27BDE109BA00AB048D88B46DC65DA5CAD917F57C6B0D02C885573EF5A2**, giống byte-for-byte đầu lượt finding fix |
| PowerShell schema-diff assertion ở mục evidence trước finding fix | 0 PASS khi chạy lại; vẫn đúng bốn nullable columns đã duyệt, DDL còn lại giống HEAD |
| SHA-256 snapshot 48 docs/files trước/sau lượt sửa | PASS: chỉ 5 file đã nêu đổi; 43 file còn lại giữ nguyên, gồm schema.sql/00/03/04/spec-gaps |
| Đối chiếu BR-SEO-31/32/35 ↔ 05 §1/§4.2 ↔ decision/acceptance matrix | Đã đọc diff; cùng source/priority/empty behavior/warning threshold/payload; giữ quyền/no-auto-noindex/ever-public |

`git diff --name-only -- backend apps packages` rỗng; không tạo EF migration/API/
client code. .NET/PG17 runtime tests và focused G4 **NOT RUN trong lượt sửa này**.
Validation DDL cũ không được ghi thành đã chạy lại PostgreSQL. Không có commit mới.

**AMENDMENT_READY_FOR_FOCUSED_G4: YES** — sẵn sàng focused review artifact, chưa
phải G4 PASS hay READY_FOR_LOCAL_G4 của implementation P1-04.
