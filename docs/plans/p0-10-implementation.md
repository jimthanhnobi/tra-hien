# P0-10 — ArchitectureTests và coverage migration còn thiếu

**G3: READY_FOR_G4; G4 chưa chạy độc lập.** Base `2740377d7e9f0860007e7b931a85427150266631` (latest `origin/main` lúc fetch), nhánh `feature/p0-10-architecture-tests`. Working tree sạch trước khi tạo nhánh; thực hiện trong main Codex thread, không spawn subagents. Owner đã giao commit/push/mở PR khi local checks đạt; không merge hoặc tự duyệt G4.

## G0/G1/G2 và traceability

- Dùng `AGENTS.md`/`docs/WORKFLOW.md` cập nhật tại base trên. G1 full baseline có inventory/gaps/traceability tại `docs/analysis/`, SHA `cadce575597738bf40ad7c45689482de5a5fbc3d`; G2 tại `docs/architecture/conformance-review.md` và `docs/plans/task-readiness.md`.
- Đã đối chiếu diff spec từ baseline: chỉ bốn file `02-architecture.md`, `03-database.md`, `07-roadmap.md`, `schema.sql` đổi trong amendment G26/G01/F-01 đã được owner chấp nhận, commit `0bfc98e`. `git diff --quiet 0bfc98e -- docs/spec` exit 0: không có spec drift tiếp theo. Targeted audit đọc §3/§5.1/§10 kiến trúc, database conventions/migration và platform DDL hiện hành; không cần lặp full audit.
- Task gốc **P0-10** tại `docs/spec/07-roadmap.md:26`, dependency **P0-04** đã merge. **AC-P0-05** tại dòng 38 yêu cầu test fail khi Application phụ thuộc EF Core hoặc đọc DateTime.Now. `02-architecture.md:68–77` quy định graph/BCL-only Domain/Application abstractions; dòng 100–103 quy định IClock; §10 dùng NetArchTest và PostgreSQL thật cho migration. **NFR-09** tại `01-requirements.md:206`; **DEC-01** tại `00-README.md:85`.
- `03-database.md:7–18` và `schema.sql:36–140` là schema platform được kiểm. Amendment không thay luật dependency/direct clock; không phát hiện blocker mới cho scoped G2. Q1–Q8, IAM/settings APIs, business modules, CI P0-11 không phải dependency của task này.

## Audit nền có sẵn và phần thực sự thiếu

| Phần hiện có | Quyết định P0-10 |
|---|---|
| `ProjectGraphTests`: 4 direct csproj graph cases, Domain package rule/Application package allowlist; clock chỉ tìm hai chuỗi nguồn | Giữ graph/package checks; chỉnh allowlist khớp `Microsoft.Extensions.*.Abstractions` của §3; thay string scan bằng compiled IL rule. |
| `PlatformDatabaseFixture`: PostgreSqlBuilder("postgres:17"), migrate DB rỗng, CreateContext/ScalarAsync/ExecuteAsync, dispose container | Tái sử dụng nguyên fixture, không tạo fixture khác. Test class mới dùng cùng IClassFixture pattern hiện hành. |
| `PlatformDatabaseTests`: 9 bảng, 19 constraint/18 index, tên PK/FK/UNIQUE/CHECK, index predicates/order, FK delete action, serial, extensions/function smoke; idempotent script chạy hai lần và Down về 0 | Không viết lại migration-from-empty, script idempotency hoặc rollback tests. Bổ sung parity những định nghĩa chưa được so đầy đủ. |
| P0-04 UoW success/failure/exception/cancellation/multiple saves; P0-05 audit identity/redaction/atomicity; P0-06 dispatcher retry/dead/concurrency và job ownership | Giữ nguyên regression files; chạy lại toàn bộ backend. Không mở rộng hoặc sửa runtime khi một test mới fail; báo blocker nếu có. |

## Thiết kế và failure cases

### Compiled architecture và clock

NetArchTest.Rules 1.3.2 chỉ thêm vào ArchitectureTests; Mono.Cecil 0.11.3 là transitive test dependency. Rule kiểm namespace dependencies trên type thật, kết hợp Assembly.GetReferencedAssemblies để không bỏ lọt global type như Program. Domain/Application còn có compiled assembly allowlist; direct csproj graph và Domain không PackageReference được giữ. API có thể sử dụng Domain qua dependency transitive để map DomainException hiện có; không thêm direct project reference hoặc sửa runtime vì điều này.

`NoDirectClockRule` kiểm MethodReference trong IL của từng type và nested types (kể cả async state machine/lambda). Cấm DateTime.Now/UtcNow/Today, DateTimeOffset.Now/UtcNow và direct TimeProvider system-clock access. Chỉ giữ ba boundary có sẵn: SystemClock.GetUtcNow và TimeProvider.System registration ở PersistenceRegistration/Program, ràng buộc đúng assembly/type/member. IClock access được phép; comment/string chứa DateTime.Now không gây false positive.

Test-only compiled samples chứng minh cùng rule từ chối dependency EF/Npgsql/upward layers và clock qua alias/static import/async/lambda/TimeProvider; đồng thời chấp nhận injected clock/BCL value. Negative tests đòi tập mẫu được chọn có đúng một type, result fail và đúng failing type; không giả tạo TestResult PASS. Không thêm sample lỗi vào production assemblies.

### Model snapshot và PostgreSQL catalog parity

- `Database.HasPendingModelChanges()` chạy như automated test. Negative case rebuild snapshot thật vào builder riêng bằng protected BuildModel reflection, thêm đúng một shadow column, rồi đòi EF model differ phát hiện AddColumnOperation. Snapshot production internal nên test không thay visibility hoặc sửa generated code; không mutate runtime model.
- Đọc bản copy `schema.sql` từ test output. Chỉ lấy đoạn chín bảng platform và function f_unaccent; không chạy schema nghiệp vụ tương lai. Tạo schema reference `p010_reference` trong transaction trên chính PostgreSQL 17 fixture đã migrate. DDL/negative mutations chỉ nằm trong reference transaction và rollback khi dispose.
- So catalog giữa platform đã migrate và reference DDL: toàn bộ column names/types/nullability/default/identity/generated/collation; PK/FK/UNIQUE/CHECK definitions, deferrability/validation; toàn bộ index definitions (keys/order/uniqueness/predicate); sequence metadata (không so last_value thay đổi theo dữ liệu); function definition gồm volatility/strict/parallel options. Chuẩn hóa schema qualifiers và whitespace, không so physical column ordinal vì không ảnh hưởng logical schema.
- Negative cases giữ nguyên tên nhưng đổi NOT NULL/default/type, FK deferrability, sequence increment, hoặc index predicate. Mỗi case trước hết chứng minh reference khớp platform, sau mutation phải mất đúng catalog row cũ. Vì vậy chỉ đếm tên/index không đủ để test xanh.

## File thay đổi

| File | Vai trò |
|---|---|
| `backend/Directory.Packages.props` | Pin NetArchTest.Rules phục vụ test; không thêm runtime PackageReference. |
| `backend/tests/TeaPremium.ArchitectureTests/TeaPremium.ArchitectureTests.csproj`, `packages.lock.json` | Test dependency và locked graph. |
| `backend/tests/TeaPremium.ArchitectureTests/ProjectGraphTests.cs` | Giữ graph/package tests, bỏ clock string scan đã được thay bằng IL gate. |
| `backend/tests/TeaPremium.ArchitectureTests/CompiledArchitectureTests.cs` | Production dependency/clock gates và negative/control cases. |
| `backend/tests/TeaPremium.ArchitectureTests/NoDirectClockRule.cs` | NetArchTest custom IL rule. |
| `backend/tests/TeaPremium.ArchitectureTests/ArchitectureRuleSamples.cs` | Test-only compiled violations/positive controls. |
| `backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj` | Copy authoritative schema.sql vào output để test đọc; không sửa spec. |
| `backend/tests/TeaPremium.IntegrationTests/PlatformSchemaParityTests.cs` | Model/snapshot gate và reference catalog parity; tái sử dụng fixture. |
| `docs/plans/p0-10-implementation.md` | Hồ sơ targeted audit, thiết kế và chứng cứ. |

## Chứng cứ thực chạy

| Lệnh | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode -v:q` | PASS, exit 0. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror -v:q` | PASS, exit 0; 0 warnings/errors. |
| `dotnet test backend/TeaPremium.sln --no-build -v:q` | PASS **183/183**: 34 architecture, 11 unit, 138 integration; 0 failed/skipped. PostgreSQL 17 Testcontainers thực; gồm mọi P0-04/05/06 regression cũ. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS, exit 0, toàn solution. |
| `git diff --check` | PASS; CRLF warning của generated lockfile không phải lỗi diff. |

Trước full suite đã chạy focused architecture và schema tests thật. Các lỗi trong giai đoạn viết test đã được xử lý: CA1822 trên positive fixture, negative dependency dùng global Program không khớp namespace rule, snapshot internal, nullable/analyzer assertion, và PostgreSQL `text || char` cần cast rõ ràng. Negative snapshot sau sửa và mọi catalog negative cases đều PASS. Không thay production code để làm test xanh.

NOT RUN: frontend/Compose/CI/G4 độc lập, vì task chỉ thêm backend test coverage; không coi local PASS là CI hay G4 approval. Migration/rollback dữ liệu production: NOT APPLICABLE, không có model/DDL/migration delta. Rollback task bằng revert test/dependency/doc changes, không thao tác DB thật.

## Giới hạn và G4 handoff

- Static IL/dependency checks bắt direct compiled calls/references; không tuyên bố phát hiện reflection, dynamic loading, hoặc helper nằm ngoài các production assemblies được kiểm. Khi thêm boundary clock mới phải review allowlist cụ thể.
- Module cross-table access/endpoint nghiệp vụ của pha sau chưa có type tương ứng để kiểm; §3 module conventions vẫn cần G4 ở task liên quan. Không dựng module giả hoặc phân tích nghiệp vụ tương lai trong P0-10.
- Parity chỉ phần platform đã được duyệt, không áp cả 78 bảng của schema đích. Thay ranh giới DDL hoặc internal EF snapshot API cần review test helper; không tự cập nhật expected values để bỏ qua drift.
- NetArchTest custom rules theo API upstream: https://github.com/BenMorris/NetArchTest#custom-rules. Dependency chỉ phục vụ test.
- PR chờ fresh G4 reviewer đối chiếu P0-10/AC-P0-05/NFR-09/DEC-01 và chạy checks độc lập. Không self-approve hoặc merge.
