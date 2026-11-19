# P1-01 — Local G3 evidence, 19/11/2026

**Lịch sử G3 ban đầu:** Owner giao local implementation bằng prompt đính kèm ngày 19/11/2026. Một main Codex thread, không subagent. Base và HEAD: `8791e8ac61f5de42815a84edbc0a0602ff177c26`; branch `feature/p1-01-catalog-inventory-db`. Worktree ban đầu sạch; `git fetch origin --prune` xác nhận đúng expected origin/main. Khi bàn giao G3 chưa chạy G4/CI. G4 độc lập sau đó **CONDITIONAL**, remediation hiện hành ở mục dưới; chưa commit/push/PR/merge/deploy.

## Focused remediation sau G4 — 19/11/2026

Owner cho sửa đúng P2 A8 và hoàn tất regression còn thiếu, vẫn thuộc **P1-01 / NFR-08 / 03 §8 / task A8–A9**, không task/kiến trúc mới. Reuse G1/G2 như bên dưới: fetch xác nhận base không đổi, spec diff rỗng. Hash 15 file lúc vào remediation khớp `g4/source-after.json`; bảo toàn worktree đã review. G4 trước sửa xác nhận A1–A7 và idempotency, nhưng A8 collation FAIL; full suite aborted với 497 PASS + 3 SKIP, 30 integration case chưa hoàn tất. Giữ nguyên toàn bộ `artifacts/p1-01/g4/`, không hồi tố G4 PASS.

Chỉ sửa migration P1-01 **Down**, `CatalogInventoryRollbackTests.cs` và hai task/evidence Markdown hiện có. Migration Up, grants, EF model/snapshot/contracts, bốn migration lịch sử, scripts và specs không đổi trong remediation. `DatabaseRolesTests.cs` không sửa thêm so với G3.

- **Before:** test mới `ExternalCollationBlocksDownBeforeAnyDropAndEmptyDownUpStillWorks` FAIL đúng vì không có exception (exit 1, `remediation/a8-red/`). Trên container mới image `postgres:17.12`, `CREATE COLLATION catalog.g4_outside FROM pg_catalog."C";` rồi áp Down cũ: psql exit 0, 25→0 bảng P1, sequence bị xóa, collation còn. `a8-before-state.json`, `a8-before.log` ghi trạng thái và server **17.12**.
- **Correction:** thêm một truy vấn pre-DROP `pg_depend → pg_namespace`, fail-closed với object ngoài các catalog đã được whitelist/kiểm tra; chỉ cho default ACL per-schema table/sequence của `tp_owner` do Up tạo. Bắt collation, conversion, operator/opclass/opfamily, text-search objects, extended statistics, extension và loại namespace dependency khác. Không quản lý/xóa các object này. Catalog thực tế đã khảo sát trong `namespace-catalog-inspection.log`; cơ sở kỹ thuật: [PostgreSQL 17 pg_depend](https://www.postgresql.org/docs/17/catalog-pg-depend.html), [pg_default_acl](https://www.postgresql.org/docs/17/catalog-pg-default-acl.html).
- **After, local VERIFIED PASS A8:** cùng SQL trên DB 17.12 riêng bị từ chối **P0001**, psql exit 3 trước DROP. JSON trước/sau bằng nhau: đủ 25 bảng, sequence hiện hữu/last_value=9876, collation giữ OID, P0 fixture/history/roles/ACL/default ACL nguyên vẹn. Xóa collation rồi empty Down→Up PASS. Xem `a8-after-state.json`, `a8-after.log`, `a8-probe.py`; script kiểm chứng exit 0 vì xác nhận đúng lỗi mong đợi.
- **Coverage:** thêm 7 case: collation, conversion, operator, operator family, text-search configuration/dictionary, extended statistics. Operator family do fixture administrator tạo/xóa vì PostgreSQL yêu cầu superuser; không nới role ứng dụng/migration. Tất cả case dependency/data cũ vẫn chạy. Lượt đầu sau sửa có một lỗi setup operator-family bằng owner (25/26); đã sửa fixture cho đúng quyền, không bỏ/skip case. Kết quả cuối nhóm P1 **78/78 PASS, exit 0, khoảng 19.8 giây**.

**Timeout root cause: UNCONFIRMED.** Đã chạy riêng cả hai case `DevMigrateRestoresCleanCheckoutAndStopsOnLockedRestoreFailure` bằng timeout 2m + diagnostic logger: **2/2 PASS, exit 0, 20.8 giây** (`brokenLock: True` khoảng 3s, False khoảng 11s). G4 log dừng tại Testcontainers `ExecAsync(init-roles.sh)` trong provisioning trước copy checkout/CLI/restore; không có evidence kết luận restore latency, deadlock hoặc leaked subprocess. Đã đọc `RunToolAsync`: stdout/stderr drain đồng thời, `WaitForExitAsync()` không token/timeout riêng; runner blame là giới hạn chẩn đoán cho lượt này. Không sửa P0 helper/script chỉ từ suy đoán. Log/process tree của lượt full mới nằm trong `remediation/`.

Các lệnh dưới đây đều ở root repo; evidence tương đối dưới `artifacts/p1-01/remediation/`:

| Lệnh | Kết quả | Evidence |
|---|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | exit 0 | `restore.log` |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | exit 0, 0 warning/error | `build.log` |
| `dotnet tool restore` | exit 0 | `tool-restore.log` |
| `dotnet tool run dotnet-ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | exit 0, no changes; offline loopback:1 | `pending-model.log` |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~DevMigrateRestoresCleanCheckoutAndStopsOnLockedRestoreFailure' --logger 'console;verbosity=normal' --logger trx --results-directory artifacts/p1-01/remediation/timeout-before --blame-hang-timeout 2m --blame-hang-dump-type none` | exit 0, 2 PASS | `timeout-before.log`, `timeout-before-result.json`, TRX |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~CatalogInventory' --logger 'console;verbosity=normal' --logger trx --results-directory artifacts/p1-01/remediation/p1-focused --blame-hang-timeout 2m --blame-hang-dump-type none` | exit 0, 78 PASS / 0 FAIL / 0 SKIP | `p1-focused.log`, `p1-focused-result.json`, TRX |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger 'console;verbosity=normal' --logger trx --results-directory artifacts/p1-01/remediation/full --blame-hang-timeout 3m --blame-hang-dump-type none` | exit 0, **534 PASS / 0 FAIL / 3 SKIP**, 185.8s | `full.log`, `full-result.json`, `full-summary.json`, TRX |
| `dotnet tool run dotnet-ef migrations script 202611190201_P1_01_CatalogInventoryFoundation 202611180710_ReconcileRuntimeGrants --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --output artifacts/p1-01/remediation/down-after.sql` | exit 0 | `down-script.log`, `down-after.sql` |
| `python artifacts/p1-01/remediation/a8-probe.py before` / `after` | exit 0 cả hai; ghi nhận before defect và after rejection | `a8-{before,after}*` |
| `git diff --check` | exit 0, kiểm lại khi chốt docs | `diff-check.log` |

**Đối chiếu regression:** unit 45/45, architecture 34/34, integration 455 PASS + 3 SKIP / 458, không case chưa hoàn tất. Tổng 537 mục: **534 PASS**, đúng 527 G3 + 7 regression case mới. P1-01 **78/78 PASS**, trong đó rollback **26/26**; không cộng focused vào tổng full vì trùng test. Cả **30/30 case G4 từng chưa hoàn tất nay đều PASS theo exact test name**, lưu tại `g4-uncompleted-reconciled.json`. Hai case clean-checkout cũng PASS trong full. Ba mục CLI Linux của P0 vẫn NOT RUN trên Windows như lịch sử bên dưới; không đổi skip attribute. Full mới không hang; nguyên nhân lần treo G4 vẫn **UNCONFIRMED**, không suy thành defect restore/CLI/source khi chưa có bằng chứng.

**A1–A7 và A9 local không regression** theo focused/full hiện hành; Up/Down/idempotency, quyền/SQLSTATE/concurrency/search/parity đều chạy trên PG17 disposable. A8 local VERIFIED PASS sau correction, chờ reviewer độc lập xác nhận closure. **READY_FOR_FOCUSED_G4: YES**. Không có actionable finding mới trong self-check; không còn blocker regression, giữ giới hạn Linux skips/CI published HEAD NOT RUN. Đây không phải independent G4 PASS và chưa có quyền publish. Các kết quả G3 ban đầu dưới đây giữ lịch sử; kết quả remediation trên có ưu tiên cho trạng thái hiện hành.

## G1/G2 và traceability

Tái sử dụng full G1 `cadce575597738bf40ad7c45689482de5a5fbc3d`, A01 `024ea998c43baf0afde30b069ed6b4876cb96f4a`, G2 conformance-review/task-readiness và targeted PASS trong PR #26. Đã kiểm `git diff dadc4435e8f115faaeb9e8511e56e09d5d517a8a origin/main -- docs/spec`: chỉ `07-roadmap.md:51` sửa dependency theo B01; 03/schema không đổi từ A01. Scoped G1/G2 vẫn phù hợp, không có gap DDL mới hay thay kiến trúc. Không audit toàn bộ lại, không xác nhận độc lập các G4 lịch sử owner cung cấp.

Nguồn: `schema.sql:144–493`, `:512–525`, `:1137–1143`; `03-database.md` §1/§3.1–3.3/§3.7–3.8/§4/§8; `02-architecture.md` §3/§5.2/§5.4; `04-business-rules.md` CAT/INV/PRC; `07-roadmap.md:51`. FR-CAT-01…09/11, FR-INV-01…06; BR-CAT-02/04/08/10/13/14/15, BR-INV-01/02/07/10, BR-PRC-01/02; NFR-04/08/09; DEC-01/04/05/06/20/21/22/25–30. Nền cho AC-P1-01…10 ở các task sau, chưa nghiệm thu nghiệp vụ/API. Dependency C1–C3 và owner authorization đủ; import P1-07, MFA recovery và gate dữ liệu thật không mở rộng scope này.

## Thay đổi và inventory

Các path `Persistence/` dưới đây thuộc `backend/src/TeaPremium.Infrastructure/`; các test thuộc `backend/tests/TeaPremium.IntegrationTests/`. Toàn bộ thay đổi local: 5 file tracked sửa nhưng chưa staged, 10 file untracked; staged rỗng.

- `Persistence/CatalogInventoryEntities.cs`: 25 POCO, persistence tối thiểu.
- `Persistence/CatalogInventoryModel.cs`, `AppDbContext.cs`: mapping explicit, bigint/numeric(12,2), system xmin; bỏ FK index convention, giữ mọi index P0 explicit.
- `Persistence/GrantsSql.cs`: thêm grants năm schema/SKU, DML chỉ đúng 25 bảng; giữ quyền âm IAM/audit/ledger/stock_movements. Test kiểm helper re-run không cấp INSERT cho bảng ngoài scope.
- Migration mới duy nhất `202611190201_P1_01_CatalogInventoryFoundation.cs`, `.Designer.cs`, `AppDbContextModelSnapshot.cs`. Bốn migration lịch sử không đổi. Preflight/grants/Down SQL cố định, không gọi helper lúc replay.
- Tests mới: `CatalogInventoryFixture.cs`, `CatalogInventoryDatabaseTests.cs`, `CatalogInventoryParityTests.cs`, `CatalogInventoryRollbackTests.cs`. `DatabaseRolesTests.cs` chỉ đổi năm assertion số migration hiện hành 4→5; nhánh closure pending vẫn là 3.
- Tài liệu: `docs/plans/p1-01-implementation.md` (tracked), `docs/plans/p1-01-local-evidence.md` và [p1-01-schema-inventory.md](p1-01-schema-inventory.md) (untracked). Xem bảng inventory cho từng entity/table và toàn bộ 124 ngoại lệ tên implicit. Hai file persistence mới, hai file migration mới và bốn file test mới cũng untracked; AppDbContext/GrantsSql/snapshot/DatabaseRolesTests là tracked.

Tổng: **25 bảng, 250 cột người dùng, 1 SKU sequence; 25 PK, 36 FK, 17 UNIQUE, 63 CHECK, 1 EXCLUDE, 18 index explicit** (cộng 43 backing index PK/UNIQUE/EXCLUDE). Không có bảng deferred, API, seed CLI, generator, upload hay logic kho/publish.

## Acceptance A1–A9

| Tiêu chí | Trạng thái | Evidence tự động |
|---|---|---|
| A1 | PASS | EmptyAndExistingP0UpgradePreserveDataAndHistory: PG17, 25 bảng/sequence; nâng từ P0 có fixture staff/settings/DP key/audit/ledger, fingerprint tất cả bảng P0 và history giữ nguyên |
| A2 | PASS | EveryColumnConstraintIndexForeignKeyAndSequenceMatchesApprovedDdl: DB tham chiếu riêng chạy DDL authoritative; cột/type/null/default/collation, constraint/FK/delete/deferrability/validation, expression/index/opclass/partial, sequence; 7 mutation detector tests |
| A3 | PASS | `pending-model.log`: no model changes, exit 0; snapshot assertion trong regression |
| A4 | PASS | EavRequiresExactlyOneTypedValue, BigintPriceBoundsAndNullablePrice, RequiredChecksReturnExactSqlState, BothMediaTablesEnforceMimePurposeAndSize, UniqueCoverSkuAndExclusionBoundaries; CHECK 23514, UNIQUE 23505, EXCLUDE 23P01; NULL/1/10^10/0/âm/+1, MIME/purpose/bytes cả hai bảng |
| A5 | PASS | RealLoginsEnforceLeastPrivilegeAndReexecutionPreservesIt + P0 DatabaseRolesTests: đăng nhập thật app/backup, DML/nextval/unaccent, deny 42501 DDL/DML/append-only, role attrs/membership, điều tra tách credential/không đọc cột nhạy cảm mặc định, grants re-run |
| A6 | PASS | GINUnaccentFixtureAndSequenceBoundaryWithoutGenerator: lowercase/bỏ dấu kể cả Đ, fixture query và GIN gin_trgm_ops; không claim planner/benchmark |
| A7 | PASS | XminRejectsStaleEfUpdateAndMappingsUseExactTypes + PersistenceAllowsFalseDefaultsNullableUniqueAndEditingUniqueFields: bigint/numeric, 4 system xmin, stale EF update bị DbUpdateConcurrencyException; false/NULL/mutable slug/code |
| A8 | PASS local sau remediation | 26 rollback case PASS; thêm collation/conversion/operator/opfamily/text-search/statistics ngoài scope; chặn trước DROP, giữ đủ 25 bảng/sequence/collation/P0; empty Down/Up PASS. P2 chờ focused G4 closure |
| A9 | PASS local sau remediation | IdempotentScriptExecutesTwiceAndKeepsRowsSequenceAndHistory + full regression exit 0; 30 case G4 chưa hoàn tất đã PASS, history/data/sequence/quyền giữ nguyên. CI NOT RUN tới publish permission |

## Lệnh và kết quả G3 ban đầu (trước independent G4)

Log/TRX local ở `artifacts/p1-01/` (gitignored, reviewer cùng worktree đọc trực tiếp). Các lần chạy trước khi sửa lỗi test giữ ở `regression.log`, `regression/`, `final-regression.log`, `final-regression/`, `verified-regression.log`, `verified-regression/`, `debug.log`, `persistence.log`, `focused.log`; không dùng làm kết quả cuối. Lỗi đã sửa: số history P0 cố định, fingerprint XML, exception TOAST nội bộ trong Down, chuẩn hóa CRLF khi so frozen SQL, và assertion nhận nhầm operation xmin ảo của Npgsql là cột vật lý. Source cuối được chuẩn hóa LF theo `.editorconfig`; frozen SQL P0/P1 focused recheck 2/2 PASS trước lượt toàn suite cuối.

| Lệnh | Exit / kết quả cuối | Evidence |
|---|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | 0 / PASS | `restore.log` |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | 0 / PASS, 0 warning/error | `build.log` |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory artifacts/p1-01/handoff-regression` | 0 / PASS: unit 45/45, architecture 34/34, integration 448 PASS + 3 SKIP / 451; 0 FAIL | `handoff-regression.log`, TRX từng project; P1-01 71/71 PASS |
| `dotnet tool restore` | 0 / PASS | `tool-restore.log` |
| `dotnet tool run dotnet-ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | 0 / PASS | `pending-model.log`; connection offline loopback:1 |
| `./scripts/migrations.ps1` | 0 / PASS | `migrations.log`; `artifacts/migrations.sql`, 85456 bytes |
| `corepack pnpm run check:api` | 0 / PASS | `check-api.log` |
| `git diff --check` | 0 / PASS | `diff-check.log`, không lỗi whitespace |

Tổng 527 PASS, 3 SKIP, 0 FAIL (530 mục do runner báo). Ba mục P0 CLI Linux bị SKIP theo attribute có sẵn trên Windows: `SourceLinksOutsideTheBundleAreRejected`, `AuthorizedLocalTestApplyAndExactRetryStillUseTheRealPrivateTerminal`, `ImportFileArgumentsCannotConfigureTheHost`. Các mục này **NOT RUN trên Linux trong G3 này**, không coi là PASS; không đổi attribute/độ bao phủ để che lỗi. CI sau publish vẫn bắt buộc. `verification-summary.json` và TRX/log giữ số test thực tế; không có performance benchmark hoặc nghiệm thu dữ liệu thật.

## Migration/recovery, rủi ro và bàn giao

Up chỉ trên PG17 disposable được tạo mới bởi fixture với dữ liệu giả. Không truy cập production/staging/dev volume hiện hữu, không xóa volume có sẵn. Provision role từ SQL/wrapper chung; test không viết GRANT thay migration (trừ các probe drift P0 đã có). Down lấy ACCESS EXCLUSIVE lock trên cả 25 bảng, kiểm dữ liệu và whitelist object/dependency trước DROP; cho phép TOAST nội bộ, chặn dependency PostgreSQL theo dõi kể cả function dùng composite/array type. Không dùng CASCADE. Down bỏ bảng/sequence và P1 default grants; giữ namespace rỗng và quyền nền P0/IAM, Up lại được. Không hứa phát hiện dependency chỉ nằm trong chuỗi dynamic SQL mà PostgreSQL không ghi catalog.

Tiền/stock/DDL/grants là review tầng A. Mapping unique/expression/exclusion có phần SQL tường minh được ghi trong inventory; migration sau chạm những object đó phải review đúng constraint/expression. Từ dữ liệu thật đầu tiên chỉ roll forward, backup/recovery theo owner; revert source không rollback DB. Entity mới không có `[Audited]`, chưa thêm use case; task sau phải định nghĩa audit theo B01/D12. Dữ liệu free text/UUID/tên người vẫn có thể là PII; chỉ dùng fixture giả.

**Handoff G3 ban đầu (lịch sử): READY_FOR_LOCAL_G4: YES.** Independent G4 sau đó CONDITIONAL; trạng thái remediation/READY_FOR_FOCUSED_G4 hiện hành ở đầu file. CI published HEAD và nghiệm thu vận hành dữ liệu thật vẫn NOT RUN. Không tự cấp G4 PASS hoặc quyền publish.
