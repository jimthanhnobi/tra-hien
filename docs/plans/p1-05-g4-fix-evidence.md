# P1-05 — focused remediation F1/P1, F2/P2, F3/P2

**Historical record:** owner xác nhận F1/F2/F3 đã independently CLOSED ở focused G4.
Hai gap triage đã thành F4/P1, F5/P1 và được giao remediation riêng;
xem [current final blocker evidence](p1-05-final-blocker-evidence.md). Các trạng thái
OPEN/owner decision bên dưới là tại thời điểm handoff ba finding ban đầu.

Ngày 20/11/2026. Owner cung cấp independent G4 **BLOCK**, cho sửa đúng F1/F2/F3,
regression và triage hai gap search/redirect, không mở rộng implementation scope.
Worktree `D:\tra-hien-p1-05`, branch `feature/p1-05-products-variants`;
base/HEAD/origin/main sau fetch `bb9a901044d41a0783d226fcd678e3865390fa30`.
Không commit/stage/push/PR/merge/production apply. Kết quả dưới là local remediation,
không thay thế independent focused G4.

**FOCUSED_FIX: PASS. F1/P1, F2/P2, F3/P2: CLOSED về local remediation;
READY_FOR_FOCUSED_G4: YES.** Independent G4 hiện vẫn BLOCK theo kết quả owner cung
cấp, chờ reviewer đóng findings. Hai extra gaps search/redirect vẫn OPEN như triage
bên dưới; không cấp publish/không claim toàn AC-P1-07/archive eligibility đã đạt.

Full regression mới **981/981 =174 unit +34 architecture +773 integration PG17**,
zero failed/skipped; focused50/50; frontend26/26. Không tái dùng931/931 lịch sử hoặc
cộng focused reruns vào tổng.

## Baseline và scope

Đọc AGENTS/WORKFLOW, task/evidence P1-05; tái sử dụng G1 TARGETED/G2 owner-approved.
Đã kiểm SHA-256 toàn bộ **39/39 paths** với manifest G3 cuối:
`artifacts/p1-05/final-source-sha256.json`, **0 missing/changed/unrelated**, staged
rỗng. Main worktree sạch. Giữ bản sao snapshot được review (39 files) và status/hash
ở `artifacts/p1-05-g4-fix/reviewed-source/`, `reviewed-source-sha256.json`,
`reviewed-status.txt`; không overwrite manifest G3.

Sources: `04-business-rules.md:37–45` BR-CAT-04.5/6, `:70–71` BR-CAT-15;
`07-roadmap.md:54,69,77` P1-05/AC-P1-02/09; `05-api.md:7–17,109–115` API/
rowVersion/errors; `02-architecture.md` §5.2/5.4/5.5 transaction/audit/outbox;
FR-CAT-04/05/07, DEC-04/25/26, NFR-07/09. Không sửa authoritative specs.

## F1 — final active aggregate axis invariant

Trước sửa, chỉ publish chạy kiểm trục; PATCH245→180 sau publish vẫn HTTP200.
Tách nguyên rule BR-CAT-04.6 thành `ProductRules.VariantAxisErrors`, dùng chung
trong `PublishErrors` và mọi supported mutation có final Product.status=active.
Kiểm mọi active combination theo đầy đủ ordered axes, không từng axis riêng lẻ;
chỉ yêu cầu axes khi >1 active variant. Inactive không tham gia. Draft vẫn được
thiếu axis/price; publish gom toàn bộ lỗi như trước. Không chạy lại toàn bộ content/
SEO publish gates khi sửa metadata của active product.

Workflow load parent dưới `FOR NO KEY UPDATE` trước đọc variants; validate final
state trước `SaveAsync`, giữ đúng transaction/parent lock đến commit/rollback.
Không dựa riêng variant rowVersion và không thay lock order P1-03/P1-04.
Lỗi final axis trả422 `variant_requirements_not_met`, errors gồm
`variant_axis_missing`/`variant_axis_duplicate`; publish giữ422
`publish_requirements_not_met`. Failed mutations không gọi persistence/audit/outbox.

`ProductG4ConcurrencyTests` dùng hai barriers sau actual parent-lock acquisition,
quan sát pg_stat_activity + pg_blocking_pids của **cùng tp_app**, không grant thêm
quyền DB. Pause T1 đang giữ parent, bắt T2 chờ; sau commit T1 pause T2 trước
validation, fingerprint committed product/variant/typed values/audit/all outbox;
release T2, fingerprint phải y nguyên nếu bị từ chối.

| Thứ tự | HTTP phải kiểm |
|---|---|
| publish trước → PATCH245→180 | 200 /422, axis duplicate; active giữ180/245 |
| PATCH245→180 trước → publish | 200 /409 với parent token cũ; retry token mới422 full publish duplicate, draft không publish |
| activate245 thứ nhất → activate245 thứ hai | 200 /422 với hai variant token khác nhau; không hai active245 |
| create245 thứ nhất → create245 thứ hai | 200 /409 token parent cũ; retry token mới422; không variant/ledger/event thừa |

Không gọi409 do stale token là bằng chứng axis validation: các retry token mới
bắt buộc422. Thêm non-concurrent regression cho creation/activation/axis removal,
inactive NULL, unrelated edit, full multi-axis combinations, single-active optional
axis; mọi rejection kiểm persisted fingerprint không đổi.

## F2 — explicit control-field types

Published helper contracts: noindexReason là optional string|null;
confirmTypeChange là optional boolean|null. Omission/null không là xác nhận; type
change thật cần confirmation=true như trước. Giữ optional null semantics đã công bố.

`ProductMutationValidator` kiểm `ProductRules.ValidControlFields` trước transaction
decorator/DB mutation. Object/array/number/boolean thay string, string/object/array/
number thay boolean đều400 `validation_failed`; không phụ thuộc control có được
dùng trong command đó hay không. Không chỉ parse reason khi robotsNoindex đổi.
Kiểm cùng lớp lỗi ở rowVersion (present phải string/null; outer mutations vẫn
bắt buộc nonnull hợp lệ qua workflow) và defaultVariant (object, recurse controls).
Embedded defaultVariant.rowVersion optional string|null không tạo quyền bypass;
runtime contract cũ giữ nguyên. Unknown/duplicate-key/NUL guards không bị nới.

Regression: invalid controls trên Product PATCH và nested create; positive omitted/
explicit-null/valid false/true/string; invalid controls không đổi product/variant
versions/values/audit hoặc bất kỳ outbox message nào. Optional controls đúng kiểu
vẫn tuân noindex-reason/type-confirmation business rule.

## F3 — requiredness khác nullability

`ProductOpenApi` clone từng property schema bằng public CreateShallowCopy rồi bỏ
JsonSchemaType.Null cho fields thực tế không nhận null; giữ constraints/format và
không mutate shared components của P1-03/P1-04. Required arrays giữ đúng fields cũ.

- ProductCreate.name/productTypeId, ProductPatch.rowVersion,
  VariantCreate/VariantPatch.rowVersion: required + nonnullable.
- ProductMediaCreate.assetId/altText/rowVersion: required + nonnullable.
- Optional nhưng không nhận null: Product name/type/slug/flags/arrays/defaultVariant;
  Variant SKU/position/status/flags/threshold/attributes; media assetId/ALT/sortOrder.
  Omission vẫn hợp lệ, không thêm required fields.
- Optional nullable text/FKs/price/weight/dimensions và hai nullable controls giữ null.

Schema assertion phân biệt required/type-null, backend HTTP null rejection, TS
negative assertions cho bốn field gốc và optional nonnullable representatives;
positive omitted/nullable price/description/control assertions. Regenerate bằng
generator hiện có, không sửa tay generated output. Existing reference/taxonomy
paths và common schemas ngoài Product phải giữ nguyên.

## Hai gap G4 bổ sung — TRIAGE ONLY, OPEN

### AC-P1-07 multi-word search

Owner G4 repro `am tu sa chu ban`→0 là **OWNER-PROVIDED runtime evidence**;
source-confirmed tại `EfProductStore.cs` ListAsync: normalize toàn query rồi
`SearchText.Contains(normalized)`. Canonical BR-CAT-10 ghép name/subtitle/SKUs/
variant/material/artisan/type, nên các từ name và artisan không nhất thiết liền
nhau. Text đầy đủ không chứng minh full AC-P1-07. Không chạy lại reproduction này
hoặc sửa search trong remediation F1/F2/F3.

Trace: FR-CAT-11 (`01:75`), BR-CAT-10 (`04:61`), AC-P1-07 (`07:74`), Admin q
(`05:109`), P1-05 search_text (`07:54`), P1-14 Store search (`07:63`).
`03:94–96` còn mô tả query whole-string LIKE/fuzzy; contract query recipe cần
hòa giải với measurable AC, không âm thầm chọn cách tìm khác.

**Direct P1-05 acceptance gap:** Admin q đã có trong P1-05 và G3 chỉ chứng minh
search_text/single phrase, không đạt toàn AC-P1-07. Không tự coi là closed hoặc
chuyển toàn trách nhiệm sang P1-14. Đề nghị owner giao focused correction trong
P1-05: deterministic token matching phù hợp AC, escape literal %/_/backslash,
giữ filter/paging/permission/GIN; không cần search engine mới. Owner implementation
của P1-05 chịu Admin q + data/rebuild, reviewer focused G4 kiểm source/runtime;
P1-14 implementer tiếp nhận và chứng minh cùng fixture qua Store API, không lộ
cost/exact stock. Acceptance: name=`Ấm tử sa`, artisan=`Chu Bân`, có SKU/variant
chen giữa → query không dấu nhiều từ trả đúng product; negative/missing-token và
wildcard literal không trả sai; rename/outbox/replay vẫn tìm đúng current names.
**OPEN, cần xử lý/owner quyết định tường minh trước claim P1-05 hoàn tất acceptance.**

### Archive redirectTo public resolution

Owner G4 báo target đúng cú pháp nhưng không public vẫn được nhận; source-confirmed
`ProductWorkflow.cs` archive/ValidPath chỉ kiểm local-path syntax, store graph kiểm
chain/cycle. Default ArchiveTargetAsync đã lọc trạng thái public/history của
artisan/material/category; explicit override chưa resolve entity/status. Không
sửa resolver/Store routes trong remediation này.

Trace: BR-SEO-22 (`09:75`) archive từng-public bắt buộc301 và chọn target, BR-SEO-03
(`09:36`) graph, API archive (`05:113`), P1-05 archive (`07:54`); P3-13/AC-P3-13
(`07:132,156`) thực thi lifecycle public. **Direct correctness risk của dữ liệu
archive P1-05:** một 301 target không tồn tại/draft/hidden sẽ thành redirect tới404
khi Store receiver/routes được nối. Không đóng risk chỉ vì P3 chưa render.

Đề nghị owner authorize focused P1-05 semantic target validation, chốt target
classes hợp lệ theo catalog contracts hiện có; dùng resolver/port đọc entity/state
trong UoW trước graph, không gọi HTTP dưới lock hoặc đoán content tables/routes
pha sau. P1-05 implementer sở hữu validation/persisted graph tests; P1-14 cung cấp
public catalog eligibility; P3-13 implementer và independent reviewer sở hữu HTTP
end-to-end: old URL301→target200 một bước, không loop/chain/soft404; missing/draft/
hidden/archived/cross-prefix target không hợp lệ bị từ chối không đổi product/
redirect/audit/outbox, fallback đúng artisan→material→category và never-public404.
**OPEN, cần quyết định/fix trước nhận archive contract an toàn; live-route nghiệm
thu vẫn P3.** Không tự thêm policy cho arbitrary content targets ngoài approved scope.

Hai mục này không thuộc lệnh sửa đúng ba finding; vẫn ghi là residual acceptance
blockers để focused reviewer/owner quyết định, không tự cấp publish hoặc G4 PASS.

## Verification và lịch sử

Prefix Linux `docker exec --env DOTNET_PROCESSOR_COUNT=4 teapremium-p105-g3`;
SDK10.0.403, Node22.22.2, PG17 Testcontainers/tp_app/real cookie+CSRF; runner reuse
đúng worktree, không real customer data/R2/production. Artifacts ignored riêng
`artifacts/p1-05-g4-fix/`.

- Red before-fix: integration filter FullyQualifiedName~ProductG4FindingTests,
  `red-findings.trx`: exit1 **0PASS/6FAIL**, tái hiện F1/F2/bốn F3 null schemas.
- Basic green: cùng filter `green-findings.trx`: exit0 **31/31** tại revision đó.
- Concurrency first instrumentation: `green-concurrency.trx` exit1 **4FAIL** vì
  tp_owner không thấy pg_stat_activity của tp_app; sửa observer sang runtime role,
  không đổi production SQL/locks/grants. Không gọi các test timeout là PASS.
- First build dùng copy constructor OpenApiSchema không public →CS1729; chuyển
  sang public CreateShallowCopy theo package hiện có, không đổi dependencies.
- Expanded focused lượt đầu49PASS/1FAIL: schema test chưa đọc oneOf(null,$ref)
  của JsonElement, đồng thời phát hiện defaultVariant nullable reference chưa bỏ
  null branch. Sửa schema helper xử lý type flag và oneOf/anyOf null branches,
  assertions kiểm cả union; không biến assertion yếu đi để PASS.
- **Final focused50/50 PASS**, exit0 `focused-final.trx`, gồm46 finding cases và
  bốn deterministic concurrency scenarios; zero skip. TS api-client typecheck
  exit0 trên regenerated output, gồm negative null/defaultVariant assertions.

Exact commands trong runner (trừ full command có env riêng bên dưới):

| Lệnh sau `docker exec teapremium-p105-g3` | Exit/kết quả mới |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | 0 PASS |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | 0 PASS, zero warnings/errors |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | 0 PASS |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~ProductG4 --logger 'trx;LogFileName=focused-final.trx' --results-directory /workspace/artifacts/p1-05-g4-fix` | 0 PASS50/50 |
| `corepack pnpm generate:api` | 0 PASS |
| `corepack pnpm check:api` | 0 PASS |
| `corepack pnpm --filter @teapremium/api-client typecheck` | 0 PASS |
| `corepack pnpm lint` | 0 PASS |
| `corepack pnpm typecheck` | 0 PASS |
| `corepack pnpm test` | 0 PASS26/26 mới =10 Node +16 browser |
| `corepack pnpm build` | 0 PASS api-client/Admin/Store production build |

Các dotnet restore/build/format/focused và generate/check/typecheck toàn workspace
được gọi với `--env DOTNET_PROCESSOR_COUNT=4` khi ghi trong transcript; lint và
api-client-only typecheck không override CPU. Commands/exit không dựa vào CI cũ.

```text
docker exec --env DOTNET_PROCESSOR_COUNT=4 teapremium-p105-g3 dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /workspace/artifacts/p1-05-g4-fix/full
docker exec --env 'ConnectionStrings__Default=Host=127.0.0.1;Port=1;Database=ci_design_only;Username=ci_dummy;Password=ci_dummy' teapremium-p105-g3 dotnet ef migrations has-pending-model-changes --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --no-build
docker exec --env 'ConnectionStrings__Default=Host=127.0.0.1;Port=1;Database=ci_design_only;Username=ci_dummy;Password=ci_dummy' teapremium-p105-g3 dotnet ef migrations script --idempotent --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --no-build --output artifacts/p1-05-g4-fix/migrations.sql
```

Full backend **exit0 PASS981/981**, gồm integration773/773 trong9m15s, unit174/174
và architecture34/34; zero fail/skip. `scripts/check-test-results.ps1 -Directory artifacts/p1-05-g4-fix/full`
exit0 xác nhận đủ ba TRX và zero skipped/empty. Hai EF commands exit0 PASS, model không drift; script chỉ design
loopback port1, không kết nối DB thật. No migration/spec/grant changes.

Semantic diff regenerated OpenAPI so snapshot G4: **0 changed/missing paths**;
chỉ8 own schemas ProductCreate/ProductMediaCreate/ProductMediaRequest/ProductPatch/
ProductWriteRequest/VariantCreate/VariantPatch/VariantWriteRequest đổi nullability.
P1-03/P1-04 existing contracts không đổi; không thêm required fields.

## Ownership, migration, rollback, handoff

Một main implementer. Delta so snapshot39file: ProductRules, ProductWorkflow,
ProductContracts validator, ProductOpenApi, generated OpenAPI/schema, product TS
assertions; mới ProductG4FindingTests và ProductG4ConcurrencyTests; task/evidence/
traceability docs. Exact final inventory/hash nằm ở
`artifacts/p1-05-g4-fix/final-status.txt`, `final-source-sha256.json`,
`remediation-delta.json`, `trx-summary.json`; giữ nguyên manifest/snapshot G3 gốc.

Delta có13paths so snapshot G4 (10 modified existing +3 new):

```text
backend/src/TeaPremium.Api/Catalog/ProductOpenApi.cs
backend/src/TeaPremium.Application/Catalog/ProductContracts.cs
backend/src/TeaPremium.Application/Catalog/ProductRules.cs
backend/src/TeaPremium.Application/Catalog/ProductWorkflow.cs
backend/tests/TeaPremium.IntegrationTests/ProductG4ConcurrencyTests.cs
backend/tests/TeaPremium.IntegrationTests/ProductG4FindingTests.cs
docs/analysis/traceability.md
docs/plans/p1-05-g3-evidence.md
docs/plans/p1-05-implementation.md
docs/plans/p1-05-g4-fix-evidence.md
packages/api-client/openapi.json
packages/api-client/src/generated/schema.ts
packages/api-client/src/products-contract.test-d.ts
```

Toàn change set review so origin/main:39original+3new=42paths; full original
inventory trong G3 evidence cùng ba paths mới của remediation. G4 phải đọc cả
30untracked files, không chỉ tracked diff; staged tiếp tục rỗng.

Final local checks: UTF-8 strict42paths PASS, Markdown fences/local links ba task/
evidence docs PASS; git diff --check/cached --check exit0. Apps/spec/migrations/
grants diff ngoài snapshot G4 rỗng; main worktree sạch; không commit mới hoặc stage.
Không có finding mới ngoài3finding đã sửa và2gap owner đã nêu. Nhánh/base không đổi.
Runner riêng `teapremium-p105-g3` đã dừng sau kiểm thử, inspect xác nhận `exited`.

**NOT RUN:** independent focused G4/published-feature CI, live R2/cache receiver/
Sales order-table integration/production và2extra-gap runtime reruns không thuộc
remediation. Test thật mới kiểm các areas nền gồm SKU/price/reference locks,
redirect graph/media/audit/invalidation thông qua full regression, không nhận
benchmark hay phê duyệt độc lập từ các tests.

Migration **NO**, không DDL/grants/snapshot/spec/UI/source refactor ngoài findings.
Rollback code về snapshot39file đã lưu; không xóa business data, audit/outbox hoặc
reset sequence. Không production apply. Original areas SKU/price/reference locks/
redirect graph/media/audit/invalidation phải tiếp tục PASS trong full regression.
G4 do reviewer độc lập trong context mới, cùng worktree/full change set gồm untracked.
Local CLOSED chỉ là remediation evidence, không độc lập phê duyệt G4.
