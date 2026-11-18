# P0 Closure #1 — hồ sơ G3 local

**Phạm vi owner giao:** remediation **P0-02/P0-04**, dùng/cập nhật P0-09/P0-10 tests; A01 D11/§7. Branch `feature/p0-closure-01-db-roles-grants`. Base = HEAD = `3a06ffb7b4f4f53a519c5032802be77ff5f04690`; thay đổi **uncommitted**, không có SHA publish mới. `git fetch origin --prune` exit 0; HEAD trùng origin/main, gồm merge PR #20 `024ea998…` và PR #21 `3a06ffb7…`. Không commit/push/PR/merge/deploy; chỉ PostgreSQL 17 dùng một lần, dữ liệu giả.

## G1/G2 và traceability

Tái sử dụng full G1 `cadce575597738bf40ad7c45689482de5a5fbc3d`, G2 conformance/readiness, targeted A01 đã được owner chấp nhận qua PR #20; hồ sơ G4/CI amendment tại `docs/reviews/a01-amendment-evidence.md` (PR #21). `git diff 47f2c7b491ab124c91e827fda6e010171abeaba7 HEAD -- docs/spec` rỗng. Lần này scoped readiness theo các nguồn dưới; không full audit mới, không sửa authoritative specs/workflow. Owner giao G3 riêng đã gỡ HOLD triển khai Closure #1, không gỡ gate P1-01/Closure #2.

| Nguồn hiện hành | Phạm vi kiểm và acceptance |
|---|---|
| `07-roadmap.md:18,20,25,26,33–40` | P0-02/04/09/10; AC-P0-01…05 giữ nguyên; tests IAM/CSRF/lockout và settings audit hồi quy, không nhận nghiệm thu thủ công. |
| `03-database.md:107–133`, `schema.sql:1210–1287` | DEC-30, roles/DML/sequence/history/functions, audit append-only, ledger F-02 SELECT/INSERT; backup tách investigation. |
| `02-architecture.md:27–66`, `06-infrastructure.md:191–199` và §3 | Một PG17/modular monolith; init hook + existing path, ownership preflight trước toàn chain, runtime tp_app, migration riêng. |
| FR-PLT-01; FR-IAM-01/02/03; BR-IAM-01/02/06; NFR-04/05/07/08/09; DEC-01/14/15/30 | Audit/IAM/Data Protection giữ hành vi dưới quyền hạn chế; secrets không log/argv; backup/HTTPS vận hành chưa nghiệm thu. |
| A01 D11 §3.5/§7; `task-readiness.md` A01; `p0-closure-acceptance.md` | Đúng một nhóm closure được owner giao. Không P1-01 DDL, recovery/coverage D8 hay audit refactor D12. |

Không phát hiện mâu thuẫn contract mới trong scope. Existing DB owner lạ là failure case phải dừng, không phải quyền tự chuyển ownership; các gap ngoài scope giữ trạng thái/mốc cũ.

## Thay đổi và thiết kế

Trước đây Compose cấp bootstrap account cho API; fixture tạo role và P009 harness tự GRANT, nên tests chưa chứng minh artifact triển khai. Nay provisioning chung tạo LOGIN NOINHERIT tối thiểu nếu thiếu, không ALTER password/role, không drop/recreate DB/role, không REASSIGN OWNED. Maintenance DB postgres tách TP_DATABASE do tp_owner sở hữu. Init hook và existing wrapper gọi cùng psql SQL; password đọc bằng getenv, không nhận secret qua argv. Sau finding G4 F1, `protect-credentials.sql` tắt và kiểm effective settings của statement/duration/sampling/error/parameter/debug logging trong từng provisioning session, gọi lại ngay sau mỗi reconnect; không ALTER SYSTEM/DATABASE/ROLE logging policy. Lỗi SET hoặc validation dừng trước credential SQL; wrapper tắt xtrace trước đọc secret. Đánh đổi: session provisioning không ghi SQL nhạy cảm vào server log; client giữ lỗi terse, operator nhận exit code và thông báo an toàn.

`preflight.sql` dùng trong provisioning/dev-migrate và embedded vào GrantsSql; migration lưu bản SQL cố định tại thời điểm tạo, không gọi helper lúc replay. Kiểm roles/membership, DB/schema/relation/function ownership, CREATE/TEMP, column/default ACL, privilege nguy hiểm/grant-option; missing role hoặc drift → dừng trước toàn chain/EF history. Public schema pg_database_owner và member objects của trusted extensions được xử lý theo PostgreSQL; extension member functions có thể thuộc bootstrap superuser theo [CREATE EXTENSION PostgreSQL 17](https://www.postgresql.org/docs/17/sql-createextension.html). Admin chỉ bootstrap extensions/delegate grant-option đúng unaccent/advisory, không role membership.

Migration mới `202611180710_ReconcileRuntimeGrants`: platform/iam DML explicit, app sequence USAGE, backup SELECT tables/sequences, history chỉ SELECT; f_unaccent/unaccent và bốn hàm advisory runtime cần được cấp explicit. Không PUBLIC application grants. Audit/ledger chỉ SELECT/INSERT; không UPDATE/DELETE/TRUNCATE/MAINTAIN. Default privileges gắn tp_owner, không blanket default DML; future migration vẫn phải cấp explicit và revoke ngoại lệ. Không sửa migration cũ/model/schema.

`dev-migrate.ps1` dùng direct TCP login tp_owner, preflight trước sinh SQL offline rồi kiểm lại cùng session ngay trước toàn idempotent chain. F2 thêm locked project restore trước build, `-PreflightOnly` vẫn không restore/build/migrate. F3 kiểm schema ACL thực qua nspacl/aclexplode/is_grantable cho app/backup, không sửa drift; mọi role membership bất thường vẫn dừng. Bản frozen preflight của migration closure chưa publish được đồng bộ, không sửa migration lịch sử trên main. API Compose chỉ có password tp_app; owner/backup credentials ở DB container/operator. Không auto-migrate Production startup. Dockerfile copy embedded preflight để build từ context thật.

Tests dùng provisioning/migrations thật; IAM host/P009 host chạy tp_app, DDL chạy riêng owner. Quan sát pg_stat_activity cho cancellation chỉ dùng admin trong fixture instrumentation; không cấp pg_read_all_stats cho runtime/owner. Các bảng CRM/sales giả trong tests F-02 giữ fixture-only grants, không chứng minh quyền module chưa triển khai. Case identity investigation chỉ có cột audit an toàn, không kế thừa backup/owner; IAM credentials/tokens/stamps, DP xml và audit changes bị deny. Đây không phải tính năng investigation được ship.

## Kiểm chứng G3 trước G4 (lịch sử)

Môi trường: Windows .NET SDK 10.0.403, Docker Desktop Linux engine 29.4.1; tests PostgreSQL **17.12**, Testcontainers; lượt toàn bộ cuối chạy Linux SDK 10.0.403/pwsh với Docker socket và host override host.docker.internal. Input là bản sao worktree cùng thay đổi uncommitted; không phải CI/published revision. Snapshot loại .git/bin/obj/node_modules/.env/secret/artifacts; mount repo chỉ đọc, build/test trong filesystem container riêng.

| Lệnh thực chạy | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS trên Windows và Linux. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warning/error. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS. |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~DatabaseRolesTests --logger trx --results-directory artifacts/closure-01-final-focused` | PASS 51/51 trước bốn ca hardening TEMP/sequence/grant-option/extension owner cuối; lượt full cuối bên dưới bao gồm tất cả. |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/test-results` | PASS **415/415**: 45 Unit + 34 Architecture + **336 Integration**, 0 fail/skip; TRX có **55** ca DatabaseRolesTests. |
| `pwsh -NoProfile -File scripts/check-test-results.ps1 -Directory /evidence/test-results` | PASS đúng 3 TRX, executed=passed=total từng project, zero skipped. |
| `dotnet ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS: No changes have been made to the model since the last migration. Connection design-only loopback port 1. |
| `pwsh -NoProfile -File scripts/migrations.ps1` | PASS, idempotent SQL offline **36.598 bytes**, exit 0. |
| `docker compose --env-file infra/.env.example -f infra/compose.dev.yml config --quiet` | PASS exit 0; parsed JSON kiểm API Username=tp_app và POSTGRES_DB=postgres, không in config/secret. |
| `docker build -f infra/Dockerfile.api -t tra-hien-closure01-api:local .` | PASS rebuild preflight cuối từ context thật, exit 0. |
| `git diff --check` | PASS. |

Runner local: `artifacts/closure01-runner/Dockerfile` (SDK + Docker CLI) và `run.sh`; build `docker build -t tra-hien-closure01-tests:local artifacts/closure01-runner`, chạy `docker run --rm --name tra-hien-closure01-verification -v /var/run/docker.sock:/var/run/docker.sock -v C:/Users/ADMIN/source/tra-hien:/input:ro -v C:/Users/ADMIN/source/tra-hien/artifacts/closure-01-verification:/evidence -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal -e DOTNET_NOLOGO=true -e DOTNET_CLI_TELEMETRY_OPTOUT=true tra-hien-closure01-tests:local sh /input/artifacts/closure01-runner/run.sh`. Không đưa credentials vào runner arguments.

Evidence local ignored: `artifacts/closure-01-verification/{restore,build,format,tests,model,migrations}.log`, `test-results/*.trx`, `migrations.sql`, `artifacts/closure-01-docker-verification.log`. Các lượt đầu bắt lỗi alias record SQL, fingerprint serialization, mount init hook và harness dùng runtime cho DDL/owner để quan sát app; đã sửa. Windows full trước lượt bổ sung cuối PASS 45 unit + 34 architecture + 320 integration, **3 nhóm CLI Linux-only SKIP**, không gọi đó là full zero-skip PASS. Linux chạy các ca PTY/source-link thực; không đổi skip để lấy kết quả xanh.

SHA256 source trước G4 (lịch sử, không phải bản fixes hiện hành): migration `F115FC0876E56478789AE1ACB8364A2CE79A667C0ED5DB1CFE6BCD80FAA84253`; init-roles.sql `F5ED6804E9906D1C7BDF76E0034A8DDC667BB2BD6B1B06591B8EBCFD25FD1AAE`. Fetch của lượt G3 trước exit 0, HEAD/origin/main đúng base nêu trên.

## Inventory, rollback và handoff

Changed paths (không staged; bao gồm untracked):

- `infra/postgres/init-roles.sql`, `init-roles.sh`, `preflight.sql`, `protect-credentials.sql`; `infra/compose.dev.yml`, `.env.example`, `README.md`, `Dockerfile.api`.
- `backend/src/TeaPremium.Infrastructure/Persistence/GrantsSql.cs`, `Migrations/202611180710_ReconcileRuntimeGrants.cs`, `.Designer.cs`; `TeaPremium.Infrastructure.csproj`.
- `backend/tests/TeaPremium.IntegrationTests/DatabaseProvisioning.cs`, `DatabaseRolesTests.cs`, `PlatformDatabaseTests.cs`, `P009Harness.cs`, `P009ImportTests.cs`, `StaffIamTests.cs`, `TeaPremium.IntegrationTests.csproj`.
- `docs/plans/p0-closure-acceptance.md`, hồ sơ này. Không đổi snapshot/model, lịch sử migration, specs/workflow hoặc code recovery/P1.
- `scripts/dev-migrate.ps1` (F4 bổ sung file bị thiếu trong inventory G3 trước).

Inventory sau fixes: **22 paths = 11 tracked modified + 11 untracked**, không staged; 8 infra + 4 Infrastructure source/project + 7 integration-test/project + 2 plans + 1 script. Đối chiếu bằng `git diff --name-only origin/main`, `git diff --cached --name-only`, `git ls-files --others --exclude-standard`.

Migration chỉ đổi ACL/default ACL, không data/index/table shape. Apply order: backup theo operator → provisioning/preflight → migrate owner → runtime app. Khi lỗi dừng API, kiểm history/ownership, forward fix hoặc rollback app; Down grants giữ ACL an toàn, không tự trả về quyền rộng/thiếu quyền. Provisioning có thể tạo một phần role/DB trước lỗi ngoài transaction; rerun an toàn, không dọn bằng drop/reset. Existing volume owner sai cần owner duyệt từng object trước tiếp tục.

**NOT RUN / giới hạn ở lần sửa findings:** focused independent G4 tiếp theo; published CI/merge; toàn stack Compose/manual Caddy trust/HTTPS và owner A/B trên môi trường operator; backup mã hóa/restore drill/OS-network credential controls; Closure #2 MFA recovery; không rerun frontend/OpenAPI trong fixes vì không đổi frontend/API contract (G4 trước đã kiểm PASS). Các integration tests HTTPS dùng in-process host không thay operator acceptance/Caddy. Không nhận Closure #1/P0 DONE. Không tác động DB/volume sẵn có của operator hoặc dữ liệu thật; existing DB tests chỉ dùng container disposable tạo trong phiên.

Handoff G3 ban đầu **READY_FOR_LOCAL_G4** đã được reviewer trả BLOCK F1–F4. Owner giao đúng bốn fixes local; G4 PASS/publish vẫn chưa được cấp. Kết quả xác minh fixes và handoff hiện hành ghi bên dưới.

## Fixes F1–F4 sau independent G4 BLOCK

Owner chỉ cho sửa local đúng bốn findings. Branch/base/head giữ nguyên như đầu hồ sơ; không commit/push/PR/merge/deploy. Source chuẩn và migration lịch sử trên main không đổi; chỉ frozen SQL của migration closure chưa publish được cập nhật cùng embedded resource. Traceability/dependency/rollback ở các mục trên vẫn áp dụng; không Closure #2/P1-01.

| Finding | File sửa trong lượt này | Fix và regression proof | Kết quả local |
|---|---|---|---|
| F1 P0 | init-roles.sql, init-roles.sh, protect-credentials.sql, DatabaseRolesTests.cs; infra README | Effective settings guard mỗi connect; session-only, fail closed; 5 ca aggressive logging với 3 fake password riêng, xtrace, thiếu quyền thiết lập, settings bị fault-inject, credential SQL lỗi read-only; kiểm log thực, login và password fingerprint sau retry/repeat, policy của connection khác giữ nguyên | PASS |
| F2 P1 | scripts/dev-migrate.ps1, DatabaseRolesTests.cs; infra README | Locked project restore trước build sau preflight; 2 ca checkout tạm không bin/obj/assets: PreflightOnly không restore; first/second run PASS; lock mismatch dừng trước EF history | PASS |
| F3 P2 | preflight.sql, migration closure 202611180710_ReconcileRuntimeGrants.cs, DatabaseRolesTests.cs | Schema ACL grant option dừng, membership tiếp tục bị từ chối; 6 ca existing DB cho cả hai roles × public/platform/iam giữ drift/data/history; 5 ca mới trước DDL (có membership), 2 ca runtime không delegate USAGE cho PUBLIC; helper/frozen SQL khớp | PASS |
| F4 P3 | hồ sơ này; acceptance runbook | Bổ sung scripts/dev-migrate.ps1 và protect-credentials.sql; inventory thực tế 22 paths (11 modified + 11 untracked), không staged | PASS |

Lượt cuối chạy Linux SDK 10.0.403/Testcontainers PostgreSQL **17.12**, snapshot source từ cùng worktree mount `/input:ro`, build/test trong `/workspace` riêng của container `tra-hien-closure01-g4fix-final`. Focused chạy trước full; không lấy log implementer cũ làm kết quả lần này.

| Exact command | Result/exit |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS 0; Windows và Linux |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS 0, zero warnings/errors; Windows/Linux |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS 0; Windows/Linux |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~DatabaseRolesTests --logger trx --results-directory /evidence/focused` | PASS 0, **75/75**, zero skip; lượt trước bổ sung xtrace 74/74 là lịch sử |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/test-results` | PASS 0, **435/435 = 45 Unit + 34 Architecture + 356 Integration**, zero fail/skip |
| `pwsh -NoProfile -File scripts/check-test-results.ps1 -Directory /evidence/test-results` | PASS 0, đúng 3 TRX, toàn bộ executed/passed |
| `dotnet ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS 0, no model changes; design-only loopback port 1 |
| `pwsh -NoProfile -File scripts/migrations.ps1` | PASS 0, idempotent SQL **37.003 bytes** |
| `docker compose --env-file infra/.env.example -f infra/compose.dev.yml config --quiet` | PASS 0 |
| `docker build -f infra/Dockerfile.api -t tra-hien-closure01-g4fix-api:local .` | PASS 0, backend restore/build layers chạy lại từ context thật |
| `git diff --check` | PASS 0 |

Độc lập với test runner, container `tra-hien-closure01-g4fix-repro` chạy `sh /input/artifacts/closure-01-g4fix-verification/reproduce.sh` qua cùng image `tra-hien-closure01-tests:local`, mount repo read-only và Docker socket. Script/evidence đều ignored, không ship. Chỉ PostgreSQL 17 disposable, không mở host port; passwords giả sinh trong runner, Docker nhận env bằng tên biến, không đưa value qua argv/report.

- F1: cấu hình đồng thời log_statement=all, log_duration=on, duration/sample=0, cả sample rates=1 và error logging; fresh/repeat wrapper exit 0, không credential marker hoặc credential SQL trong actual server logs.
- F2: snapshot `/clean` không bin/obj/assets, chỉ tái dùng NuGet downloads; PreflightOnly exit 0 không sinh assets; `pwsh -NoProfile -File scripts/dev-migrate.ps1 -Container tra-hien-closure01-g4fix-repro-db` first/second đều exit 0.
- F3: sau migrate, áp đúng schema USAGE WITH GRANT OPTION của reviewer lên tp_app trong DB giả; provisioning và migration exit khác 0, đúng schema-grant-option diagnostic. History giữ 4 dòng; ACL drift giữ nguyên để owner xử lý. Test runtime delegation âm chạy trên DB hợp lệ riêng.

Safe evidence: `artifacts/closure-01-g4fix-verification/{restore,build,format,focused,tests,model,migrations,runner,reproduction}.log`, `focused/*.trx`, `test-results/*.trx`, `repro-*.log`, `migrations.sql`, `reviewed-files.sha256`. Không lưu actual server logs chứa password; assertion chỉ báo pass/fail/marker absent, không xuất marker/value/credential SQL.

**READY_FOR_FOCUSED_G4** — bốn findings đã được sửa và kiểm chứng local; đây không phải independent G4 PASS. Reviewer mới đọc cùng worktree và toàn diff/untracked. NOT RUN/giới hạn vận hành ở trên giữ nguyên; owner chưa cấp publish permission.

## Focused correction P2 — table/sequence GRANT OPTION

**Nguồn giao việc:** sau focused independent G4, F1/F2/F3-schema/F4 được reviewer kết luận CLOSED; phát hiện P2 mới: tp_readonly SELECT WITH GRANT OPTION trên bảng không bị từ chối. Verdict của reviewer là **CONDITIONAL**, READY_FOR_OWNER_PUBLISH_APPROVAL **NO**. Owner chỉ cho sửa P2 local. Các verdict/evidence trước giữ nguyên theo mốc lịch sử; phần này là G3 correction, không phải independent G4 hay publish permission.

**Readiness/traceability:** remediation P0-02/P0-04, dùng P0-09/P0-10 tests; DEC-30, A01 D11 §3.5/§7, FR-PLT-01, NFR-07/08/09; hồi quy AC-P0-01…05 theo scope cũ. G1 baseline `cadce575597738bf40ad7c45689482de5a5fbc3d` và targeted A01 revision `47f2c7b491ab124c91e827fda6e010171abeaba7` giữ nguyên; `git fetch origin --prune` exit 0, HEAD/origin/main đúng base `3a06ffb7b4f4f53a519c5032802be77ff5f04690`, diff specs từ revision A01 tới HEAD rỗng. Nguồn: `03-database.md:107–133`, `06-infrastructure.md:191–199`, `schema.sql:1210–1287`, `07-roadmap.md:18–26,33–40`; SQL ACL tại `infra/postgres/preflight.sql`, frozen SQL migration closure và regression tests. Scoped G2 **GO**: chỉ siết guard theo ma trận quyền đã duyệt, không thay kiến trúc/dependency/schema. Owner giao correction cụ thể; Closure #2/P1-01 vẫn ngoài scope.

### Tái hiện trước sửa và thay đổi tối thiểu

Chạy snapshot code trước sửa trên Linux .NET SDK 10.0.403, PostgreSQL **17.12** disposable, network none/tmpfs, không host port/volume nghiệp vụ. Provision/migrate bằng artifact hiện hành, sau đó administrator chạy:

```sql
GRANT SELECT ON platform.data_protection_keys TO tp_readonly WITH GRANT OPTION;
SELECT r.rolname,a.privilege_type,a.is_grantable
FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
JOIN pg_roles r ON r.oid=a.grantee
WHERE c.oid='platform.data_protection_keys'::regclass AND r.rolname='tp_readonly';
```

Kết quả **`tp_readonly|SELECT|t`**, trong khi `pwsh -NoProfile -File scripts/dev-migrate.ps1 -Container tra-hien-p2-baseline-disposable -PreflightOnly` vẫn exit **0**. Container được xóa sau probe; chỉ dữ liệu/password giả, không in secret. Regression RED đã xác nhận 20 cases: **8 FAIL / 12 PASS**, 0 skip; tám ca tp_readonly (bảng platform/iam/history và sequence, mỗi ca có/không pending closure) fail đúng `Assert.Throws() Failure: No exception was thrown`. Draft test đầu có lỗi tên cột EF history, đã sửa dùng `"MigrationId"` trước lượt RED xác nhận; không lấy lỗi draft làm proof regression.

Fix chỉ thêm `OR a.is_grantable` vào nhánh tp_readonly của ACL relation trong **hai** bản SQL: common preflight và frozen SQL của **migration closure mới, chưa commit**. `pg_class.relacl/aclexplode` bao phủ cả tables/sequences trong platform/iam/public, không nhầm SELECT hợp lệ với SELECT WITH GRANT OPTION. Nhánh tp_app đã kiểm mọi `is_grantable`, giữ nguyên. Guard database/schema/function/default/column ACL, ownership và mọi membership bất thường giữ nguyên. GrantsSql đọc đúng common embedded resource nên không cần thêm bản sao logic hoặc sửa helper. Frozen-migration parity test tiếp tục so khớp toàn bộ output helper.

Tests bổ sung:

- `ObjectGrantOptionDriftStopsBeforeMigrationWithoutRepair`: 10 ma trận relation/role/privilege × 2 trạng thái (closure đã áp dụng hoặc còn pending) = **20 cases**. Bao gồm tp_readonly SELECT trên Data Protection/IAM/history và sequence; tp_app SELECT/UPDATE/DELETE settings, INSERT audit/F-02 và USAGE sequence. Đối chiếu `aclexplode.is_grantable` với quyền hiệu lực `... WITH GRANT OPTION`; quyền thường vẫn hợp lệ trước mutation trên DB hoàn tất.
- `InheritedObjectDelegationIsRejectedBeforeMigration`: 2 roles × 2 trạng thái = **4 cases**. Delegate có SELECT grant option và membership `WITH INHERIT TRUE`: role đích không có grant option trực tiếp trong ACL nhưng có quyền hiệu lực; guard membership phải dừng.
- Mỗi drift case kiểm common preflight, provisioning wrapper, CLI PreflightOnly, CLI full chain và frozen migration; so nguyên history/ACL/membership/data fingerprint, không sửa drift, không sinh PUBLIC relation grants. Pending case giữ đúng 3 migrations, completed case giữ 4.
- Thêm **4 cases** runtime login thật không delegate table/sequence cho PUBLIC, xác nhận catalog không nhận grant. Các ca schema grant option, positive DML/sequence/function, append-only audit/F-02, credentials logging và clean checkout trước đó được giữ lại.

**Migration/rollback:** chỉ đổi guard, không DDL/data/grants mới, không tự REVOKE/repair, không sửa migration lịch sử/model/Down. Apply vẫn provisioning/preflight → migrate tp_owner → runtime tp_app; drift phải dừng để owner duyệt remediation riêng. Không hạ guard để cho drift đi qua; ưu tiên forward correction. Không đổi password/ownership/sequence hoặc xóa dữ liệu.

### Inventory đầy đủ sau correction

Scope lượt correction chỉ **4 files**: common preflight, migration closure mới, DatabaseRolesTests và hồ sơ này. Tổng local delta đối chiếu origin/main vẫn **22 files = 11 tracked modified + 11 untracked**, không staged:

| Trạng thái | Path |
|---|---|
| M | backend/src/TeaPremium.Infrastructure/TeaPremium.Infrastructure.csproj |
| M | backend/tests/TeaPremium.IntegrationTests/P009Harness.cs |
| M | backend/tests/TeaPremium.IntegrationTests/P009ImportTests.cs |
| M | backend/tests/TeaPremium.IntegrationTests/PlatformDatabaseTests.cs |
| M | backend/tests/TeaPremium.IntegrationTests/StaffIamTests.cs |
| M | backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj |
| M | docs/plans/p0-closure-acceptance.md |
| M | infra/.env.example |
| M | infra/Dockerfile.api |
| M | infra/README.md |
| M | infra/compose.dev.yml |
| ?? | backend/src/TeaPremium.Infrastructure/Persistence/GrantsSql.cs |
| ?? | backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611180710_ReconcileRuntimeGrants.Designer.cs |
| ?? | backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611180710_ReconcileRuntimeGrants.cs |
| ?? | backend/tests/TeaPremium.IntegrationTests/DatabaseProvisioning.cs |
| ?? | backend/tests/TeaPremium.IntegrationTests/DatabaseRolesTests.cs |
| ?? | docs/plans/p0-closure-01-implementation.md |
| ?? | infra/postgres/init-roles.sh |
| ?? | infra/postgres/init-roles.sql |
| ?? | infra/postgres/preflight.sql |
| ?? | infra/postgres/protect-credentials.sql |
| ?? | scripts/dev-migrate.ps1 |

### Verification correction P2

Runner `tra-hien-p2-fix-runner`, image `tra-hien-closure01-tests:local`, mount cùng source `/input:ro`, Docker socket và evidence ngoài repository tại `C:/Users/ADMIN/AppData/Local/Temp/tra-hien-p2-table-grant-fix-20261118`. Snapshot `/workspace` loại .git/artifacts/bin/obj/node_modules/.env/certificate; build/test trong container, không dùng bin/obj host. SHA256 của ba source/code files correction khớp giữa snapshot đã test và worktree. Các kết quả dưới là G3 local, không chuyển thành independent G4 PASS.

| Exact command | Result/exit |
|---|---|
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~ObjectGrantOptionDriftStopsBeforeMigrationWithoutRepair --logger trx --results-directory /evidence/red-confirmed` trước fix SQL | RED exit 1: 20 total, 8 FAIL đúng missing rejection của tp_readonly, 12 PASS tp_app; 0 skip. TRX red-confirmed là evidence chuẩn, không dùng console log của draft chạy chồng. |
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS 0 |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS 0, zero warning/error |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS 0 |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~DatabaseRolesTests --logger trx --results-directory /evidence/focused` | PASS 0, **103/103**, zero fail/skip = 75 cũ + 28 mới |
| `docker compose --env-file infra/.env.example -f infra/compose.dev.yml config --quiet` | PASS 0 |
| `docker build -f infra/Dockerfile.api -t tra-hien-p2-table-grant-api:local .` | PASS 0 |
| `git diff --check` | PASS 0 |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/full` | PASS 0, **463/463 = 45 Unit + 34 Architecture + 384 Integration**, zero fail/skip |
| `pwsh -NoProfile -File scripts/check-test-results.ps1 -Directory /evidence/full` | PASS 0, đúng 3 TRX, executed=passed=total |
| `dotnet ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS 0, no model changes; connection design-only loopback port 1 |
| `pwsh -NoProfile -File scripts/migrations.ps1` | PASS 0; SQL idempotent **37.023 bytes**, lưu riêng trong evidence |

F1–F4 được giữ nguyên về hành vi và kiểm lại trong focused/full trên: F1 aggressive logging/fail-closed/reconnect/xtrace 5/5 cùng punctuation, F2 clean checkout/lock mismatch 2/2 cùng entry point first/rerun, F3 schema drift 6/6 và membership/preflight âm, F4 vẫn đủ 22 paths. Positive permissions/defaults và append-only audit/F-02 PASS; không claim G4 mới từ regression. Green suite không còn FAIL. Lượt full cuối có 384 Integration (tăng 28), không lấy count 356/435 lịch sử làm kết quả hiện hành.

Evidence: `baseline-reproduction.log` và baseline logs; `red-confirmed/*.trx`, `red-summary.txt`; `restore.log`, `build.log`, `format.log`, `focused.log`, `focused/*.trx`, `full.log`, `full/*.trx`, `check-results.log`, `model.log`, `migrations.log`, `migrations.sql`, `docker-build.log`, `correction-paths.txt`, `reviewed-files.sha256` trong thư mục Temp nêu trên. Password giả không được in vào report/argv; không lưu actual server logs chứa credential. Hashes code khớp snapshot; docs cập nhật sau test không ảnh hưởng code đã chạy.

**NOT RUN:** independent focused G4 cho correction này; CI tại published HEAD (chưa publish), frontend/OpenAPI (không đổi API/frontend), toàn stack HTTPS/owner A/B, backup/restore và credential/OS/network controls vận hành. Không DB nghiệp vụ/Production, không spec/workflow/Closure #2/P1-01. Không commit/push/PR/merge/deploy. Giới hạn và rollback cũ vẫn giữ nguyên. Independent verdict hiện có vẫn **CONDITIONAL**; publish permission **NO** cho tới khi reviewer mới xác minh correction và owner cho phép riêng.

**READY_FOR_FOCUSED_G4** — G3 correction P2 local đã kiểm chứng đầy đủ trong scope được giao; reviewer tiếp theo phải dùng fresh context/session trên cùng worktree, không dùng phiên implement này làm independent approval. Dừng local.
