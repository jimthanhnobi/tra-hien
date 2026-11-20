# P1-05 — final blocker remediation F4/P1, F5/P1

Owner authorization 20/11/2026: chỉ sửa F4 multiword search và F5 archive target,
thêm focused regression và scoped contract amendment. Independent focused G4 của
snapshot đầu vào BLOCK; F1/F2/F3 đã independently CLOSED theo owner. Không refactor
hay thay source/contracts/tests của ba finding đó. Một main implementer, không agents.

Worktree `D:\tra-hien-p1-05`, branch `feature/p1-05-products-variants`.
Base/HEAD/origin/main sau fetch: `bb9a901044d41a0783d226fcd678e3865390fa30`.
**FOCUSED_FIX: PASS; F4/P1: CLOSED; F5/P1: CLOSED về local remediation.**
**READY_FOR_FOCUSED_G4: YES; READY_FOR_OWNER_PUBLISH: NO.** Full mới1055/1055,
focused74/74, zero failed/skipped. F1/F2/F3 regression50/50 PASS, source không đổi.
Independent focused G4 còn chờ reviewer; đây không là independent closure F4/F5.
Không commit/stage/push/PR/merge/production apply; không tự cấp independent G4 PASS.

## Snapshot và targeted conformance

Trước sửa source, kiểm manifest reviewer
`artifacts/p1-05-focused-g4-review/snapshot.sha256`: SHA-256 chính xác
`12ae338cb4f6c559774216f22afdf47e550e95bcada8eaee4d546de87b68101d`.
**42/42 file hashes khớp; 12 tracked modified +30 untracked; index rỗng**; không extra
paths. Sao lưu bản đã review tại `artifacts/p1-05-final-fix/reviewed-source/` và
`reviewed-snapshot.sha256`; giữ nguyên manifest/evidence cũ. Main worktree ngoài scope.

Đã đọc AGENTS/WORKFLOW/CODEX_USAGE, task/evidence P1-05 và focused G4 probe.log.
Probe PG17.11 owner-provided chứng minh whole-phrase multiword trả0 và archive
missing/hidden/draft/encoded vẫn200. Đây là evidence reviewer, không giả là rerun.
Tái sử dụng G1/G2 đã duyệt, kiểm focused spec diff thay vì audit cả dự án. Amendment
chỉ làm rõ hai contract owner vừa chốt, không đổi architecture/DDL/dependencies.

Trace: P1-05/P1-14, FR-CAT-04/07/11, BR-CAT-08/10, AC-P1-07,
BR-SEO-01/03/22, DEC-04/21/25/26, NFR-07/09. 03 §3.7/05 §4.2/07 AC-P1-07/09
BR-SEO-22 là source hiện hành; 00 §10 ghi owner-approved clarification và gate local.

## F4 — product token AND search

`EfProductStore.ListAsync` gọi `public.f_unaccent(q)` như cũ; split normalized query
theo Unicode whitespace (`Split(null, RemoveEmptyEntries)`), distinct nonempty tokens;
mỗi token thêm literal Contains predicate vào cùng IQueryable (AND trên cùng row).
Npgsql parameterize/escape LIKE metacharacters; không OR/cross-product token pooling,
không thay BR-CAT-10 composition/SKU/rebuild, không thêm fuzzy/ranking/dependency/index.
Empty/whitespace không thêm search predicate. Filters/order/pagination/total code giữ nguyên.

PG17 tests đọc qua Admin API và search_text thật sinh bởi save/rebuild: có/không dấu,
case, đảo thứ tự và cách xa qua SKU/material/type, Unicode/repeated whitespace, SKU,
missing token/cross-product false match, literal `%`/`_`/`\` và SQL-looking string;
empty/absent q, 200/201/NUL boundaries, status/type/category/material/artisan/stock,
page1/2/3/total/stable ordering. P1-14 nhận cùng semantics/fixture qua Store API.

## F5 — public canonical archive destination

Chỉ năm prefix: active product, visible category/collection, published material/artisan.
Mọi input/hop/final leaf dùng `TaxonomyRules.ValidSlug` ASCII canonical hiện có;
không URL-decode. Encoded slash/backslash/control/double encoding/encoded letters,
query/fragment, external/protocol-relative, unsupported route, uppercase/Unicode
slug/trailing slash/extra segment đều không hợp lệ. Self/cycle409 redirect_conflict;
missing/nonpublic/noncanonical leaf400 validation_failed/errors.redirectTo.

Save trước mọi writes:

1. Giữ P1-03 SHARE reference tables → routing namespace khi cần → product parent
   NO KEY UPDATE/variant UPDATE → reference rows như implementation đã review.
2. Đọc discovery graph không khóa để resolve canonical leaf. Bảo vệ destination
   row bằng FOR SHARE NOWAIT trước graph; material đã có SHARE table lock từ đầu.
   NOWAIT tránh cycle với aggregate/reference holder; contention409 concurrency_conflict,
   không global catalog lock. Map đúng Postgres55P03 trực tiếp hoặc wrapper EF
   InvalidOperationException/InnerException55P03; không nuốt lỗi DB khác.
3. Lấy shared graph `(8104,100)` qua routing port hiện có; resolve lại theo fresh
   snapshot. Nếu leaf thay đổi thì409 concurrency_conflict, rollback, client retry;
   không lấy row/namespace đích mới sau graph. Re-read final state không khóa thêm,
   state/slug đã được bảo vệ đến commit bởi row/table lock.
4. Ghi direct final target qua graph protocol cũ; business/audit/outbox cùng UoW.
   Không HTTP dưới lock. Failure không persist product/history/rowVersion/target/
   graph/audit/outbox. Không reset sequence hay xóa dữ liệu để rollback.

Fallback cũ giữ published artisan → published material → visible primary category;
đã từng public và không eligible target thì400, never-public archive giữ no redirect/
no invented history (public route404 khi P3 thực hiện). Không mở rộng thành arbitrary
content route resolver hay sửa target lifecycle ngoài archive transaction.

## Tests, lock evidence và lịch sử trung thực

`ProductFinalBlockerTests`/`ProductFinalBlockerPathTests`: Admin search matrix; direct
và chain tới từng loại eligible leaf, từng nonpublic/missing leaf, unsafe paths,
self/cycle, fallback priority/none, never-public; rejection fingerprint gồm aggregate/
typed values/publication history/versions/all redirects/taxonomies/audit/all outbox.

`ProductArchiveConcurrencyTests`: 20 deterministic cases =5 destination kinds ×
state/slug ×2 transaction orders; DbCommandInterceptor barriers ở actual graph/row
acquisition, pg_stat_activity + pg_blocking_pids quan sát cùng tp_app, không grant mới.
Archive-first giữ graph và protected target; writer chờ lock; sau archive commit
pause writer trước đổi state/slug, đọc eligible destination thật ở commit. Writer-first
product/material chặn archive ở namespace/reference table; category/collection/artisan
giữ row làm archive trả409 NOWAIT; retry sau commit phải resolve slug mới hoặc từ chối
nonpublic state. Product đã archive có graph tới valid fallback được resolve theo graph.
Failure fingerprint kiểm cả committed state sau writer. Hai discovery cases: reclaim
historical alias sau leaf protection và đổi slug sau discovery nhưng trước protection
→409 sau fresh graph check, giữ nguyên fingerprint, không khóa leaf mới. Leaf lookup
không thấy row vẫn kiểm fresh graph trước khi kết luận400, để discovery đổi trả409.

Archive-first rồi target chuyển hidden/archive là lifecycle diễn ra **sau** archive
commit; tests không claim target luôn public vĩnh viễn. Invariant được chứng minh tại
archive commit; downstream target lifecycle và SSR301→200 vẫn là P3 acceptance.

Lịch sử trước final green:

- Lần compile test đầu dùng sai tên Total thay TotalItems; sửa test helper.
- Red9/9 FAIL: tám nonpublic target cases tái hiện F5. Search case lúc đó dùng chung
  product_type name chứa "Ấm tử sa" nên negative fixture không độc lập; không dùng
  failure này làm red proof F4. Đã đổi negative product sang type khác; owner probe
  là baseline F4. Không thay composition để chiều test.
- First build chặn shadowed local, EF1002 và CA1859; sửa tên biến, dùng closed
  table whitelist + parameterized slug và concrete dictionary, không tắt analyzer.
- Basic lượt đầu40PASS/6FAIL: fixture cycle self-edge bị DB CHECK chặn; active
  fixture thiếu published_at; legacy seeded target chain assertion yêu cầu sửa mọi
  unrelated alias sai scope. Sửa fixture hợp DDL, kiểm archive edge đi trực tiếp
  final leaf; giữ unchanged legacy graph ngoài mutation.
- Concurrency lượt đầu15PASS/6FAIL: sáu NOWAIT cases500 do Npgsql EF wrapper của
  55P03; sửa mapping hẹp như trên. Không gọi các lần fail là PASS.
- Combined lượt đầu69PASS/4FAIL do PostgreSQL53300 connection slots ở cuối class
  có52 disposable databases/pools. Tách21 unsafe-path cases thành class fixture riêng,
  không đổi shared fixture, grants, production config hay tăng max_connections.
- Focused sau split73/73 PASS. Self-check bổ sung cửa sổ discovery-before-protection,
  sửa mapping missing leaf do slug race thành409 khi fresh graph thay đổi. Full run
  `artifacts/p1-05-final-fix/full/` đã chủ động cancel/superseded (unit174/architecture34
  đã PASS, không claim integration/full total). Final run dùng thư mục `full-final/`.
- Lượt `full-final/` bị mất tool session khi client cập nhật tools; Docker exec vẫn
  chạy nhưng không hoàn tất integration/TRX sau hơn20phút. Quan sát PG không có lock
  wait; stack report không xác định được business frame, chưa kết luận nguyên nhân.
  Không claim PASS/exit0 cho lượt này. Đã cancel/restart riêng runner, giữ source
  nguyên hash và chạy lại `full-logged/`, console verbosity normal +TRX, stdout/stderr
  và exit code ghi trong container vào ignored artifacts, độc lập kênh tool.
  Diagnostic dotnet-stack cài tạm `/tmp/p105-final-diag`, không package/project dependency;
  external createdump không có ptrace permission, không đổi quyền. Sau đó dùng
  diagnostic-client dotnet-dump trên testhost giả để xem async stack, file ignored.
- Lượt full-logged cũng chờ ở baseline DatabaseRolesTests.RunToolAsync sau khi child
  script đã xong: async report `async-waiting.txt` chứng minh PipeStream.ReadAsyncCore
  →StreamReader.ReadToEndAsyncInternal→RunToolAsync→DevMigrateRestoresCleanCheckout.
  SDK MSBuild/C# compiler server còn giữ output pipe. Lệnh chuẩn
  `docker exec --env DOTNET_PROCESSOR_COUNT=4 teapremium-p105-g3 dotnet build-server shutdown`
  giải phóng pipe và test tiếp tục PASS (không skip/assertion/source change).
  Cleanup log ở `build-server-cleanup.log`. Baseline harness await output không có
  deadline riêng tại DatabaseRolesTests.cs:660; ghi nhận limitation ngoài F4/F5,
  không mở rộng scope sửa. Roles diagnostic standalone103/103 exit0, không cộng vào
  full total. Không quy kết hang cho tool reset sau khi đã có async stack evidence.
- Dump managed tạm `testhost-managed.dmp` khoảng2GB chứa fixture test tổng hợp,
  được ignored. Automatic approval review từ chối xóa cả computed/literal path với
  lý do “blocked by policy”; giữ file, không commit/publish, không bypass policy.

Final focused **74/74 PASS**, exit0, zero skip (52 search/eligibility/path cases +22
concurrency/discovery cases); `focused-final74.trx`. Không cộng reruns vào full total.
Artifacts riêng `artifacts/p1-05-final-fix/`.
Runner Linux `teapremium-p105-g3`, bind đúng worktree, SDK10.0.403/Node22.22.2;
PostgreSQL17 Testcontainers/runtime tp_app, real HTTP cookie+CSRF, disposable data.

Exact final commands, prefix `docker exec --env DOTNET_PROCESSOR_COUNT=4 teapremium-p105-g3`
trừ EF/client-only commands ghi riêng:

| Command | Kết quả mới |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | exit0 PASS |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | exit0 PASS, zero warnings/errors |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | exit0 PASS |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~ProductArchiveConcurrencyTests\|FullyQualifiedName~ProductFinalBlocker' --logger 'trx;LogFileName=focused-final74.trx' --results-directory /workspace/artifacts/p1-05-final-fix` | exit0 PASS74/74 |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --logger 'console;verbosity=normal' --results-directory /workspace/artifacts/p1-05-final-fix/full-logged` | exit0 PASS1055/1055 =174 unit +34 architecture +847 integration PG17; SDK server cleanup như trên |
| `corepack pnpm check:api` | exit0 PASS, OpenAPI/client current |

Trong bảng Markdown, `\|` ở filter được escape để render; actual shell argument dùng
literal `|` trong một quoted argument, đúng transcript. Client-only command
`docker exec teapremium-p105-g3 corepack pnpm --filter @teapremium/api-client typecheck`
exit0 PASS, gồm F3 negative/positive TS assertions không đổi.

```text
docker exec --env 'ConnectionStrings__Default=Host=127.0.0.1;Port=1;Database=ci_design_only;Username=ci_dummy;Password=ci_dummy' teapremium-p105-g3 dotnet ef migrations has-pending-model-changes --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --no-build
docker exec --env 'ConnectionStrings__Default=Host=127.0.0.1;Port=1;Database=ci_design_only;Username=ci_dummy;Password=ci_dummy' teapremium-p105-g3 dotnet ef migrations script --idempotent --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --no-build --output artifacts/p1-05-final-fix/migrations.sql
```

Hai EF commands exit0 PASS; no model drift, idempotent SQL sinh trên design-only
loopback port1, không connect DB thật. Generated JSON/TS có cùng SHA-256 với reviewer
snapshot; API shape/required/nullability không đổi. Không rerun frontend lint/tests/build
toàn workspace trong pass này vì không đổi UI/client/contracts/dependencies; không
claim frontend26/26 lịch sử là rerun hiện hành. Giữ shared fixtures/migrations/grants/apps.

Full command chạy từ `artifacts/p1-05-final-fix/run-full-logged.sh` qua Docker exec
detached; `full-logged.log` chứa console, `full-logged.exit` là actual dotnet exit
code (không dùng exit0 của detached-launch làm exit code test). Source-under-test
manifest41backend/client paths dùng đối chiếu sau full suite.

Final TRX verifier `scripts/check-test-results.ps1 -Directory artifacts/p1-05-final-fix/full-logged`
hoàn tất exit0: đủ3TRX, zero failed/skipped/empty. Integration847/847 trong21.0280
phút, gồm723 original regressions +50 F1/F2/F3 +74 F4/F5; không cộng focused/roles
reruns vào1055. `integration-groups.json` và `trx-summary.json` ghi counters thực.
Lượt full này có SDK build-server cleanup sau stall, không gọi là chạy unattended
không can thiệp, không lấy runtime21phút làm benchmark. Limitation unbounded output
wait ở baseline harness vẫn chưa sửa, owner/reviewer cần biết khi tái chạy local/CI.

Final hash check41backend/client paths PASS sau suite; main vẫn sạch, fetch giữ
origin/main=HEAD/base bb9a901044d41a0783d226fcd678e3865390fa30. UTF-8 strict51paths,
Markdown task/evidence links/fences, git diff --check/cached --check PASS. Không
model/migration/grant/apps/package changes ngoài reviewed snapshot; generated
contracts và F1/F2/F3 source/tests giữ SHA-256. Không thêm business finding mới;
test harness limitation và ignored diagnostic dump đã ghi rõ, không sửa ngoài scope.
Runner riêng `teapremium-p105-g3` đã dừng sau kiểm thử; inspect xác nhận `exited`.

## Ownership, migration, rollback, handoff

Một implementer sở hữu store search/save + new archive helper, hai new integration
test files, năm scoped spec files, task/current evidence/traceability. Exact final
delta/inventory/hash sẽ lưu artifacts; không sửa F1/F2/F3 source/generated contracts.

Delta14paths:10 existing modified +4 new. So reviewer snapshot42:5 paths đổi,
37 paths nguyên SHA-256; thêm9 affected paths (5 spec trước sạch +4 new files).
Full change set51paths =17 tracked modified +34 untracked, index0; reviewer phải
đọc cả untracked. Danh sách delta (N=new file):

```text
backend/src/TeaPremium.Infrastructure/Catalog/EfProductStore.cs
backend/src/TeaPremium.Infrastructure/Catalog/EfProductStore.Save.cs
backend/src/TeaPremium.Infrastructure/Catalog/EfProductStore.Archive.cs (N)
backend/tests/TeaPremium.IntegrationTests/ProductFinalBlockerTests.cs (N)
backend/tests/TeaPremium.IntegrationTests/ProductArchiveConcurrencyTests.cs (N)
docs/spec/00-README.md
docs/spec/03-database.md
docs/spec/05-api.md
docs/spec/07-roadmap.md
docs/spec/09-seo.md
docs/analysis/traceability.md
docs/plans/p1-05-implementation.md
docs/plans/p1-05-g4-fix-evidence.md
docs/plans/p1-05-final-blocker-evidence.md (N)
```

Current full source/status/hash, scoped delta và test summary ở
`artifacts/p1-05-final-fix/final-status.txt`, `final-source-sha256.json`,
`remediation-delta.json`, `trx-summary.json`. Rollback source chỉ delta14paths:
42-path reviewer source có bản sao; năm spec affected lấy nội dung base HEAD,
bốn new files đưa khỏi change set sau khi lưu evidence. Không destructive reset/clean.

Migration **NO**: chỉ query/validation/locking và scoped contract text, reuse schema,
indexes/grants/ports/UoW/graph/audit/outbox đã có. Rollback source về reviewer snapshot
đã lưu, không DB Down hay sửa business data; chưa apply production.

NOT RUN: independent focused G4, published feature CI, Store API P1-14, SSR HTTP301→200/
target future lifecycle P3, live cache/R2/production. Backend tests không chứng minh
production readiness hoặc future lifecycle. Sau local PASS giao reviewer độc lập
cùng worktree, toàn tracked/staged/untracked change set; owner vẫn giữ publish/merge.
