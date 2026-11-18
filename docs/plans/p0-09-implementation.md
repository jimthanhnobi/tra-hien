# P0-09 — Settings và dữ liệu hành chính (G3 local)

**Hiện hành — READY_FOR_FOCUSED_G4 (G3 remediation, 18/11/2026).** Branch `feature/p0-09-recovery`, base/HEAD `23bf4b2d495a4f915e323747afd78ceb4dc83d2e`; code chưa commit. Independent G4 đã BLOCK với một P1/hai P2; owner giao sửa cả ba findings. Kết quả sửa, RED/GREEN và inventory hiện hành ở mục **G3 remediation sau independent G4** cuối tài liệu. Chờ focused G4 trong context mới; không tự nhận G4 PASS hoặc cho phép publish.

**Lịch sử WIP:** các mục từ Readiness đến Checkpoint byte-safety bên dưới là hồ sơ checkpoint ban đầu, không thay thế kết quả kiểm chứng mới. Checkpoint `feature/p0-09-settings-admin-data` tại `8d3ec3c7e93626d31ceadae373cefb9f38290c54`, base `72cdf11c9e6bb4905df6566feb0ee69455912b05`. Owner đã cho phép xuất bản riêng checkpoint trong phiên trước; quyền đó không áp dụng cho recovery này.

## Readiness và phạm vi

- Fetch origin thành công, working tree ban đầu sạch; spec alignment F-02 đã có trên origin/main tại base trên. Reuse independent G4 PASS trong phiên trước cho contract và spec alignment; không tự chấm G4 implementation.
- G1 full baseline `cadce575597738bf40ad7c45689482de5a5fbc3d`, amendment G26/G01/F-01 `0bfc98e`, F-02 spec alignment `72cdf11`. Scoped G1/G2 đối chiếu `03` §3.6/§4/§5/§6/§8, `schema.sql` F-02, `02` §3/§5.2/§5.4/§7, `05` §4.7, `07` P0-09/AC-P0-04 và F-02 readiness. Không audit lại toàn dự án.
- Traceability: P0-09, AC-P0-04, FR-PLT-01, FR-SHP-01, FR-CNT-06, BR-SHP-01, BR-PRC-06, BR-RET-01, BR-MKT-01/05/07/08, DEC-01/10/11/19/20, NFR-04/05/07/08/09.
- Hiện có Settings/Province/Ward EF shapes, audit interceptor, UoW, IAM/CSRF và fixture PostgreSQL 17; chưa có Settings API, default seed, import ledger/CLI. Chỉ triển khai task này; không sửa docs/spec, không CRM/checkout/storefront feature, không auto-import/auto-migrate.
- Dự kiến touched: Application Settings/administrative-data contracts/handlers/validators, Infrastructure adapters/ledger mapping/migration, API endpoints/CLI/DI, tests/fixture, generated OpenAPI/client, tài liệu này. Transaction/audit/Identity hiện có được giữ và kiểm hồi quy.

## Acceptance và rủi ro

**Owner decision 18/11/2026:** chấp nhận recovery target chỉ là **verified full snapshot**; từ chối target delta vì payload delta không tái dựng được catalog cũ từ ledger metadata hiện có. `re_advance` delta vẫn được khi identity/payload và current base khớp tiền đề delta gốc. Quyết định được owner trả lời trực tiếp trong phiên, không tự thêm bảng lưu snapshot hoặc sửa authoritative specs.

Settings phải kiểm GET/PUT hợp lệ, đúng permission GET site exception, anonymous/CSRF, JSON/key/type/invariant âm, atomic group update, optimistic conflict, unchanged NO-OP, audit actor/correlation/redaction và rollback. Defaults chỉ giá trị spec, không giả bank/contact/analytics.

Importer phải kiểm pinned source/CSV, UTF-8/TEXT/FullName/province-only, manifest trust/approval, full/delta, busy/stale/concurrent/NO-OP, FK/history, SQL rollback và recovery/re-advance với high-water. Runtime tests đăng nhập thật tp_app, không SET ROLE từ superuser. Chỉ disposable PostgreSQL 17; không chạy import lên DB local được cấu hình của người dùng hoặc Production.

Migration thêm đúng ledger đã duyệt và defaults (không overwrite settings hiện có); P0-04/StaffIam lịch sử không sửa. Down ledger có dữ liệu phải từ chối; rollback vận hành dùng backup/controlled recovery, không DELETE history hoặc đơn/địa chỉ. Application model/snapshot và catalog parity phải được chứng minh khi hoàn tất.

## Settings API và seed

- `GET/PUT /api/v1/admin/settings/{group}` dùng AdminCookie/StaffReady, CSRF hiện có. PUT luôn `settings.write`; GET site dùng `content.write`; các GET khác `settings.write`. Không thêm group ngoài `site,shipping,payment,orders,referral,notify,retention,marketing`; không mở Settings ra public.
- Response `{group,version,values}`; `values` là object keyed bằng full setting keys. PUT `{version,values}` là cập nhật các keys gửi lên, giữ keys khác. `version` là hash canonical group snapshot, không thêm cột/schema concurrency. Adapter lấy table lock settings trong UoW trước đọc; stale version → 409, invalid group/key/JSON/types/shape/invariant → 400 ProblemDetails. No-op giữ timestamps và không thêm audit.
- FluentValidation/handlers ở Application, ports không lộ EF. Audit `[Audited]`/interceptor cũ cùng transaction, actor/correlation từ context xác thực, numeric before/after đúng và bank/PII bị che; audit SQL failure rollback settings.
- Migration seed **24 keys**: shipping 2, orders 5, returns 1, marketing 1, referral 5, payment.methods_enabled, SEO sold_unique/handling/transit 3, retention 6. Values đúng `03` §5, chỉ `ON CONFLICT DO NOTHING`; không overwrite operator changes. Không seed bank/contact/notify/site/analytics hoặc các SEO policy chưa chốt.
- Tên kỹ thuật cho `site.*` tương ứng các mục spec: `name,hotline,zalo_url,facebook_url,address,opening_hours,logo_asset_id,favicon_asset_id,default_og_asset_id`. Asset IDs là UUID; URL http(s) không userinfo; bank object dùng đúng bốn field spec; phương thức thanh toán chỉ cod/bank_transfer; percent 0..100, tiền/số ngày/quantity kiểm integer và dấu phù hợp. Không triển khai business workflows sử dụng settings của pha sau.

## Importer, manifest và trust boundary

- Application sở hữu strict CSV/parser/normalizer, DTOs, planner và version/recovery state machine. Infrastructure sở hữu PostgreSQL reads/locks/mutations và ledger mapping. CLI chỉ chạy khi gọi `import-provinces`, không hosted-service/startup auto-import; API không auto-migrate.
- `AdministrativeCsv.NormalizePinnedJson` dùng `FullName`, NFC, sort ordinal, province declaration rồi ward rows; UTF-8 không BOM/LF cuối. Input hỗ trợ một BOM đầu, CRLF và CSV quoted fields; giữ TEXT/zero đầu, reject malformed/duplicate/orphan/control character; lỗi CSV có số dòng, không echo dữ liệu input. Pinned fixture có 34/3321/3355 và CSV SHA-256 đúng F-02; không gắn counts này vào release khác.
- `ImportJson` serialize schema `vn-admin-manifest/1`/`vn-admin-plan/1`, snake_case record property order, UTF-8 không BOM + LF cuối. Strict required constructor fields, unknown/duplicate members bị reject. CSV/source sizes/hashes kiểm trên bytes thực; không tải mạng ở runtime. Bundle files dưới `<manifest-dir>/source/<source.path>`, LICENSE được giữ và băm. Các bản source fixture gzip trong tests là raw bytes tại commit đã ghim, có MIT notice/attribution.
- Không tin manifest tự khai SHA: cấu hình deployment/OS-ACL phải allowlist **exact manifest SHA-256**. Apply cần allowlist **exact approval bytes SHA-256** và operator đăng nhập qua IAM hiện có (password/TOTP nhập ẩn, owner/settings.write, session revalidated). CLI không coi tên nhập tay/`--approve` là authorization; không có `--force`.
- Trusted config keys (không đưa credential thật vào repo): `AdministrativeImport:AllowedTargets:<n>`; `TrustedManifestSha256:<n>`; `TrustedApprovalSha256:<n>`. Target string chính xác là JSON array `[host,port,database,environmentName]` từ connection thực. LocalTest chỉ Development/Test, host loopback/host.docker.internal, DB `teapremium_dev*`/`teapremium_test*` và exact allowlist. Production không thể chọn LocalTest qua flag/manifest.
- Production còn yêu cầu `ProductionTarget`, `OfficialReconciliationManifestSha256`, `RuntimeRoleVerified=true`, `RestoreDrillVerified=true`, `VerifiedBackupSha256` ở trusted configuration; manifest phải có official issuer/URL/as_of/artifacts/legal references/verification, approval có backup reference/hash khớp. Các cờ config là hồ sơ đã được operator xác minh ngoài CLI, **không** tự chứng minh pháp lý/restore. Chưa có hồ sơ thật nên production APPLY vẫn HOLD.
- Plan chứa source/input/migration identities, current base/active/high-water, before/after catalog hashes, reference digest/counts và changes theo action. Diff liệt kê insert/rename/reparent/reactivate/unchanged/missing/deactivate; approval phải chấp nhận chính xác danh sách deactivate/reparent/reactivate. Reference digest chỉ băm tuples PK/code của rows chịu tác động, không đưa customer IDs/PII vào plan/log. Không sửa customer_addresses hoặc order snapshots.
- Apply theo existing command/UoW; advisory xact lock **hai int32 `(8300,9)` trước mutable reads**, cùng connection/transaction với catalog và ledger append. Busy trả `import_busy`; đọc lại tail/base/diff/migrations, khác plan → stale_plan; lock provinces→wards→reference tables, không lock ledger. DML thất bại/ledger constraint/cancel rollback toàn bộ. Nếu COMMIT outcome unknown, chỉ retry exact committed plan sau kiểm ledger; không auto-retry.
- Full snapshot chỉ deactivate missing sau approval; delta absent giữ nguyên và chỉ deactivate tường minh. Mã lịch sử/FK không DELETE. `IAdministrativeReferenceData` cung cấp readiness và active province/ward parent validation cho task CRM/fulfillment dùng sau này; không sửa global health hoặc thêm CRM/checkout.

## CLI usage (chỉ ví dụ, chưa chạy lên dữ liệu vận hành)

```powershell
dotnet run --project backend/src/TeaPremium.Api -- import-provinces ./bundle/provinces.csv --manifest ./bundle/provinces.manifest.json --dry-run ./bundle/plan.json
dotnet run --project backend/src/TeaPremium.Api -- import-provinces ./bundle/provinces.csv --manifest ./bundle/provinces.manifest.json --plan ./bundle/plan.json --approval ./bundle/approval.json
```

Dry-run chỉ đọc trong repeatable-read/read-only transaction; ghi plan ra path operator chỉ định. Invalid input ghi error report an toàn (code/CSV row), không tạo apply-able plan. Apply đọc plan/approval canonical bytes, xác thực private terminal và yêu cầu trust hashes ở configuration đã bảo vệ. Approval record gồm `plan_sha256,profile,target,operator_id,approved_at,deactivate_provinces,deactivate_wards,reparent_wards,reactivate_codes,backup_reference,backup_sha256,recovery_event_id,re_advance_of,path_position`; không nhập bank/contact/customer payload. Fresh approval cho event mới trong vòng 24h, không future timestamp; exact current-tail retry dùng approval đã ghi trong receipt, không tạo event mới.

## Recovery / re-advance

`A1 → B2 → recovery A1 → re_advance B2`: current active A/B/A/B, historical high-water 1/2/2/2. Recovery target phải verified full snapshot theo owner decision phía trên; path chỉ gồm original normal-import IDs tăng ordinal/effective date. Re-advance chỉ bước kế tiếp của path chưa đóng, cùng immutable release tuple, current base/dry-run/approval mới; delta phải đúng original base. Normal import mới/recovery khác đóng path cũ. Khác hash cùng identity, old/stale plan, arbitrary downgrade/replay đều reject. Historical acceptance không tự là NO-OP; chỉ exact current tail + catalog digest đúng mới NO-OP. Recovery/re-advance append events, không rewrite history.

## Migration / rollback

- `202611180001_SettingsAdministrativeData`: chép exact approved F-02 table+PK/FK/UNIQUE NULLS NOT DISTINCT/CHECK/grants; không đổi provinces/wards/Settings schema hoặc hai migration cũ. Ledger SQL-owned (`ExcludeFromMigrations`) vì EF nullable alternate-key không diễn đạt đúng nullable predecessor constraint; entity columns có snapshot, PostgreSQL catalog parity test kiểm đủ constraint/index thật thay vì tin mock/model.
- Thêm SELECT migration history cho tp_app để bind applied migration IDs vào plan; không cấp runtime DDL/history mutation. Các role tp_app/tp_readonly phải được provision trước migration theo spec, runtime không dùng tp_owner. Test login thật kiểm current_user=session_user=tp_app, rolsuper=false, không member tp_owner, ledger SELECT/INSERT và deny UPDATE/DELETE/TRUNCATE/MAINTAIN.
- Test migrate từ DB rỗng và existing platform/IAM, idempotent script hai lần, model pending=false, catalog parity và migration Down rỗng. Down populated ledger bị từ chối; giữ settings seeds khi Down vì operator có thể đã sửa. Go-live dùng roll forward/backup/controlled recovery, không restore production hoặc sửa audit/ledger history trong task này.
- SQL artifact local ignored: `artifacts/p0-09/idempotent.sql`; không chứa actual connection/password/customer dataset. Scope mới đúng một table; implicit PK/unique indexes, không tạo index thừa.

## Verification checkpoint (lịch sử, không phải kết quả recovery)

Windows Application Control chặn runtime assembly loading (`0x800711C7`) trong EF/test/OpenAPI tooling; **không sửa policy**. Runtime verification chạy trong isolated Linux SDK container `teapremium-p009-build`, source mount read-only và copied workspace `/work`; PostgreSQL 17 Testcontainers riêng, không dùng connection DB của người dùng. SDK image `mcr.microsoft.com/dotnet/sdk:10.0.403`, Node22.22.2 tải official binary và kiểm SHA256 theo SHASUMS trước dùng. Host vẫn build/format/frontend được. Lỗi ban đầu về fixture duplicate settings, thiếu role/migration-history privilege, snapshot/copy tooling, stdin CRLF và analyzer đã được phân biệt với các lượt PASS sau sửa.

| Lệnh | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS trên Windows và Linux. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warnings/errors (Windows và Linux). |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore` | Linux PostgreSQL 17: PASS **327/327** — Unit45, Architecture34, Integration248, zero skipped; TRX đã copy vào ignored `artifacts/p0-09/verified-results`. Sau lượt đầy đủ chỉ thêm assertion nâng cấp existing platform/IAM bảo toàn operator settings/staff: focused test `DefaultsAreExactRepeatableAndDoNotInventBusinessData` PASS **1/1**, TRX `/work/upgrade-verified`. Windows runtime BLOCKED bởi Application Control, không dùng exit0/0-tests làm PASS. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS ở lượt chốt trước checkpoint; không tính command format sửa file là verify PASS. |
| `dotnet tool run dotnet-ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | Linux PASS: `No changes have been made to the model since the last migration.` Design-time dummy connection port1, không truy cập DB thật. |
| `dotnet tool run dotnet-ef migrations script --idempotent --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --output /work/p009-idempotent.sql` | Linux PASS; script đã đọc, up/down/idempotency thực thi trên disposable PG17 trong tests. |
| `corepack pnpm generate:api`; `corepack pnpm check:api` | Linux PASS, actual OpenAPI tool + type generator; hai generated files được copy về workspace. Windows generate bị Application Control BLOCKED, không đổi pipeline để giả PASS. |
| `corepack pnpm lint`; `corepack pnpm typecheck`; `corepack pnpm build` | Windows PASS cho api-client/store/admin. |
| `corepack pnpm test` | Windows PASS: store2 + admin unit8 + Playwright16 = **26**, zero skipped. Next-generated next-env change đã hoàn nguyên riêng về baseline sau tests/build; không sửa frontend feature. |
| `git diff --check` | PASS trước handoff; kiểm lại cả file mới bên dưới. |

Coverage: Settings defaults/GET-PUT/permission/CSRF/validation/atomicity/concurrency/audit/redaction/NO-OP/rollback; importer pin/UTF8/CSV/parent/province-only/full-delta/history/approval/trust/base/order/NO-OP/busy/concurrency/constraint/runtime-role/recovery/re-advance/failure/production denial; regression IAM/UoW/audit/outbox/idempotency/schema/architecture. Tests có synthetic future-phase reference tables để chứng minh address/order snapshots không bị đổi, không triển khai modules pha sau.

## Handoff / residual risks của checkpoint (lịch sử)

Production APPLY **HOLD**: chưa official full reconciliation/source mapping, production role/backup/restore evidence; không auto-import/auto-migrate hoặc execute production SQL. Backup/restore drill thực, live CLI interactive owner terminal, production cutover và GitHub Actions **NOT RUN**. Public storefront settings/address endpoints/CRM address UX thuộc pha/task tương ứng, không triển khai ngoài scope admin/API+CLI này.

G3 chưa tự nhận independent review. Owner đã yêu cầu commit/push **WIP checkpoint** trong phiên 18/11/2026, chưa yêu cầu PR/merge. Việc push không thay G4 và không cấp production APPLY.

## Hướng dẫn tiếp tục checkpoint trước recovery (lịch sử)

1. Checkout `feature/p0-09-settings-admin-data`, kiểm remote/head và đọc task record này; không bắt đầu lại G1/G2. Không cần viết lại Settings/importer từ đầu.
2. Rà chốt implementation so spec và quyết định recovery-only-full ngày 18/11; ưu tiên trust/approval/CLI, manifest/plan fixed format, SQL-owned ledger mapping/privileges và readiness port. Cần kiểm CLI private-terminal end-to-end (chưa chạy tương tác thật); giữ production gate HOLD.
3. Dùng `$review-gate` trong **fresh context** đọc cùng worktree, full diff so `origin/main` và mọi local delta; reviewer độc lập phải xác minh code/tests, không chỉ task record. Chưa có G4 PASS cho implementation này.
4. Sửa finding nếu có, chạy focused regression tương ứng rồi review lại. Chỉ sau PASS và owner authorization mới mở PR/merge; chưa triển khai P1.

Checkpoint inventory: **40 file** gồm 11 tracked modified + 29 new/untracked trước commit. Phạm vi: Application Settings/AdministrativeData (8), API endpoints/CLI/Program (3), Infrastructure adapters/models/DI/migration/snapshot (10), tests/fixtures/project (15), generated OpenAPI/client (2), task record (1), .gitattributes binary rule cho compressed fixture (1). `docs/spec/**`, `apps/**`, `infra/**` và hai migration lịch sử giữ nguyên; không đính kèm artifacts, credential hoặc DB dump.

Checkpoint byte-safety: .gitattributes khai báo fixture *.gz là binary để Git không chuẩn hóa CRLF làm hỏng compressed source bytes; staged blobs được so byte-for-byte với fixture đã kiểm chứng.

## G3 recovery closure — 18/11/2026

### Git recovery và G1/G2

- Preflight: `git fetch origin --prune` PASS; worktree sạch, HEAD ban đầu `40c2fdf210a49c5ecf8247350ee7573f6e5117c7` trên `fix/revert-p0-09-wip`. `git branch -avv` xác nhận local/remote checkpoint đều tại WIP; local `main` cũ chưa đồng bộ, nên không dùng làm base.
- `origin/main` đúng **`23bf4b2d495a4f915e323747afd78ceb4dc83d2e`**; tree **`67168ec7b4e72b32870036a77c4c6e41f6b689d5`** bằng F-02 `72cdf11c9e6bb4905df6566feb0ee69455912b05`. `git diff --exit-code 72cdf11 origin/main` PASS. Đây là bằng chứng Git local sau fetch; không tự nhận đã kiểm ruleset GitHub.
- Thực hiện đúng `git switch -c feature/p0-09-recovery origin/main` rồi `git cherry-pick --no-commit 8d3ec3c7e93626d31ceadae373cefb9f38290c54`. Ngay sau cherry-pick, `git write-tree` = original WIP tree **`15a730e89b115fc00e0c1459cc806a3b316e4e9a`**. Kiểm lại equality trước unstage cũng PASS. Không làm lại implementation từ đầu.
- Sau xác minh dùng `git restore --staged -- .` để bỏ staging, giữ file. HEAD vẫn bằng base; checkpoint branch không đổi. Không stash/reset/clean, commit/push/PR/merge hay sửa lịch sử.
- G1 full baseline `cadce575597738bf40ad7c45689482de5a5fbc3d`, G26/G01/F-01 `0bfc98e`, F-02 `72cdf11` được tái dùng. `git diff --exit-code 72cdf11 HEAD -- docs/spec` PASS; specs không đổi. Scoped G1/G2 kiểm lại `00` terminology/DEC, `01` IAM/PLT/NFR, `02` §3/§5.2/§5.4/§7, `03` §3.6/§4/§5/§6/§8, `04` BR liên quan, `05` §4.7, `07` P0-09/AC-P0-04, DDL F-02 và owner-approved readiness. Không có thay đổi kiến trúc hoặc spec mới; không cần audit toàn dự án lần nữa.
- Traceability giữ danh sách ban đầu; bổ sung phạm vi xác thực CLI: **FR-IAM-01/03, BR-IAM-01/02, AC-P0-02**. Nguồn cụ thể: `01-requirements.md:54,56,190,201`; `02-architecture.md:101,122`; `03-database.md:80,122,157`; `04-business-rules.md:10,11,183,191,221`; `05-api.md:199`; `07-roadmap.md:25,34,36`; `schema.sql:1228`. Dependency P0-04/05/08/10 đã có tại base. Quyết định recovery chỉ verified full snapshot giữ nguyên; Production APPLY vẫn HOLD.

### Audit checkpoint và sửa lỗi trong scope

Đã rà 40 path checkpoint: handwritten Application/API/Infrastructure/tests; migration Up/Down/grants so F-02; generated model/designer qua snapshot/parity; OpenAPI/client qua generator thật; fixture binary qua giải nén/hash/pinned normalization test; LICENSE/attribution và `.gitattributes`. File mới thứ 41 là `P009CliTests.cs`. Không sửa `docs/spec/**`, `apps/**`, `infra/**`, lockfiles hoặc migration lịch sử.

| Finding G3 | Sửa và kiểm chứng |
|---|---|
| **P1 — session vừa login chưa được bind khi apply** | `ImportExecution` mang `OperatorStamp`; adapter so với `IStaffIdentity.SessionAsync` trong transaction, cùng active/password/owner/permission guards. Test thiếu stamp, revoked, inactive, non-owner đều reject và zero catalog/ledger writes. Production còn yêu cầu owner đã bật TOTP. |
| **P1 — CLI từ chối owner LocalTest chưa bật TOTP** | `FactorComplete` biểu thị factor đã xác thực, không biểu thị password login thất bại. CLI áp đúng FR-IAM-03/BR-IAM-02: LocalTest chưa bật TOTP được dùng; đã bật phải xác thực; profile Production vẫn yêu cầu. PTY chứng minh apply không TOTP ở LocalTest và TOTP missing/invalid/valid/replay. Không sửa IAM implementation. |
| **P2 — manifest có null source entry làm CLI crash** | Guard source entry/path trước dereference; process test `null-source` trả lỗi an toàn, không tạo plan áp dụng hay ghi DB. Có thêm process tests unknown JSON field, invalid UTF-8, checksum và orphan CSV. |
| **P2 — thiếu metadata hiển thị trong plan/receipt** | Plan ghi trực tiếp `recovery_event_id/re_advance_of/path_position`; receipt ghi `incident_record`. Recovery/re-advance test kiểm các field này. Plan/approval tạo từ WIP cũ cần dry-run và duyệt lại; đây chưa phải contract đã phát hành. |
| **Test harness — key repository khác giữa HTTP test host và CLI process** | P009 fixture cấu hình rõ cùng DB Data Protection repository, application name và certificate dùng một lần; dispose certificate khi đóng harness. PTY TOTP sau sửa kiểm thật giữa hai process, không giả FactorComplete. |

Bổ sung test quyền ledger so với DDL cho cả `tp_app` và `tp_readonly` (gồm MAINTAIN), lỗi SQL ward sau province SaveChanges, reference-impact đổi sau dry-run, cancellation ngay trong ward SQL sau province SaveChanges và advisory lock được giải phóng. Giữ các test Settings concurrency/CSRF/permission/validation/redaction/no-op/audit rollback, full/delta/pin/history/high-water/recovery/re-advance của checkpoint.

### Evidence mới trên recovered worktree

Evidence local ignored: **`artifacts/p0-09-recovery/`**. Linux SDK container `teapremium-p009-recovery-build`, image `mcr.microsoft.com/dotnet/sdk:10.0.403` (digest `sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e`), source mount read-only `/source`, workspace copy `/work`, Docker socket chỉ cho Testcontainers. PostgreSQL image của fixture là `postgres:17`; không dùng DB local/shared/production của owner. `diff -qr --exclude=bin --exclude=obj /source/backend /work/backend` PASS sau lượt cuối. Node `22.22.2` binary được kiểm SHA-256 với SHASUMS256 trước dùng; pnpm giữ phiên bản repo `12.16.1`.

Các lệnh .NET/API dưới đây chạy tại `/work` trong container, không lấy PASS từ hồ sơ checkpoint:

| Lệnh thực tế | Kết quả mới / evidence |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS, `linux/closure-restore.log`. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, **0 warnings, 0 errors**, `linux/closure-build.log`. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS, exit 0, `linux/closure-format.log` (rỗng vì không diagnostic). |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/ready-tests` | PASS **341/341**: Unit **45**, Architecture **34**, Integration **262**; **0 skipped/failed**, `linux/ready-tests.log` và 3 TRX trong `linux/ready-tests/`. |
| `./scripts/check-test-results.ps1 -Directory artifacts/p0-09-recovery/linux/ready-tests` (host) | PASS; kiểm đủ ba TRX, executed=total=passed>0. Tên TRX mang UTC `2026-11-18_02_19_41`, tương ứng 09:19:41 giờ Việt Nam. |
| `dotnet tool run dotnet-ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS, `No changes have been made to the model since the last migration.`, `linux/closure-ef-pending.log`. Design-time connection cố ý `127.0.0.1:1`, không DB thật. |
| `dotnet tool run dotnet-ef migrations script --idempotent --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --output /evidence/closure-idempotent.sql` | PASS; SQL **27936 bytes**, `linux/closure-idempotent.sql`. Tests thực thi idempotent script hai lần trên PG17, migrate từ rỗng và nâng cấp existing platform/IAM; giữ staff/operator settings. Down rỗng PASS, Down có history bị từ chối. |
| `corepack pnpm generate:api`; `corepack pnpm check:api` | PASS, `linux/closure-api-generate.log`, `linux/closure-api-check.log`. So output mới với **hai file trong worktree host** sau chuẩn hóa CRLF: equality PASS (`actual-openapi.json`, `actual-schema.ts`); không chỉ check output vừa tự ghi. |
| `corepack pnpm install --frozen-lockfile`; `corepack pnpm lint`; `corepack pnpm typecheck` (host) | PASS; lockfile không đổi, không nâng dependency. |
| `corepack pnpm --filter @teapremium/admin exec playwright install chromium`; `corepack pnpm test` (host) | PASS **26 tests** = store2 + admin unit8 + browser16, zero skipped, `frontend-tests.log`; browser setup tại `playwright-install.log`. |
| `corepack pnpm build` (host) | PASS exit 0, api-client/store/admin, `frontend-build.log`. Next-generated `apps/admin/next-env.d.ts` được hoàn nguyên riêng về HEAD sau build, ngoài scope sản phẩm. |
| `docker build -f infra/Dockerfile.api -t tra-hien-api:p009-recovery .` | PASS với context thật; lượt cuối gồm các sửa runtime, `docker-api-final.log`. Dockerfile dùng SDK10.0.404 như repo; không sửa Dockerfile. |
| `docker build -f infra/Dockerfile.web --build-arg APP=store -t tra-hien-store:p009-recovery .`; tương tự `APP=admin`/tag admin | PASS cả hai, `docker-store.log`, `docker-admin.log`. Chỉ build, không chạy app/compose/deploy hoặc kết nối DB owner. |
| `git diff --check`; `git diff --cached --check` | PASS; kiểm whitespace riêng nội dung tất cả untracked text, compressed fixture được hash/test. |

Lượt baseline mới ngay sau recovery cũng PASS 327/327; lượt focused P009 sau sửa đầu PASS 39/39. **Chỉ 341/341 ở ready-tests là kết quả full backend cuối**, không cộng các lượt lặp vào coverage/count cuối.

CLI tương tác thực: helper local `artifacts/p0-09-recovery/cli-e2e/Driver.cs{,proj}` được copy vào `/cli-e2e`; build `dotnet build /cli-e2e/Driver.csproj --no-restore -warnaserror`, rồi chạy `dotnet /cli-e2e/bin/Debug/net10.0/Driver.dll` với `ASPNETCORE_TEST_CONTENTROOT_TEAPREMIUM_API=/work/backend/src/TeaPremium.Api`. Helper tự tạo/dispose PG17 Testcontainer và owner/staff giả, gọi **entry point thật** qua `script -q -e -E never -c ... /dev/null`; password/TOTP chỉ nhập qua stdin của PTY, không truyền qua args, không echo/ghi transcript credential. Không dùng account của owner.

**PTY PASS 18 cases**, `linux/cli-pty.log`: dry-run thật bốn lần/zero writes; bad password; non-owner; untrusted approval; apply LocalTest chưa bật TOTP; exact retry no-op; tiến release khác đã duyệt; stale base; ledger insert failure rollback catalog/history; invalid manifest; default Production-profile HOLD; enabled TOTP missing/invalid/valid/replay. Receipt cuối đúng ba successful events; các lần reject/failure không append. Đây là local executable verification helper, không phải test case trong tổng 341 hoặc suite CI đa nền tảng.

Các lỗi môi trường/lượt chưa đạt được giữ đúng bản chất: lần frontend đầu thiếu Chromium nên 16 browser launch fail, sau cài chạy lại 16/16 PASS; tar.xz thiếu xz nên chuyển archive tar.gz và xác minh checksum; vài lượt build test mới bị compiler/analyzer hoặc shell filter quoting, đã sửa và chạy lại. Lần PTY đầu chưa tìm content root, rồi phát hiện fixture key repository lệch; cấu hình fixture đã sửa, lượt PTY cuối PASS. Không bỏ test hoặc coi exit 0/0 tests là PASS; không sửa Windows Application Control.

### NOT RUN, residual risks và rollback

- **NOT RUN:** independent G4 implementation; GitHub Actions cho recovery (không publish); Windows .NET runtime suite (chọn Linux cô lập, không thay đổi policy); actual production/shared DB migration/import; encrypted production backup/restore drill; full official-catalog/source mapping/legal reconciliation. Các bằng chứng production còn thiếu giữ **Production APPLY HOLD**, không giả xác minh từ fixture/community hashes.
- **P0/P1:** không còn finding implementation đã biết sau G3 checks; đây không phải kết luận G4 PASS. **P2:** chưa fault-inject mất COMMIT acknowledgement/network partition hay timeout riêng (đã chứng minh exact retry/ledger no-op, SQL/ledger failure và cancellation rollback); ACL của trusted config, runtime permissions/backup/restore trên production vẫn cần operator kiểm độc lập. Không dùng test grants hoặc config flags làm bằng chứng hạ tầng thật.
- Rollback code local: giữ nguyên checkpoint/base để owner khôi phục có chủ đích; không tự xóa file. Migration giữ Up additive đúng F-02, seeds `ON CONFLICT DO NOTHING`; Down chỉ DB dùng một lần và ledger rỗng. Ledger đã có history dùng roll forward/controlled recovery đến verified full snapshot, không DELETE history/customer address/order snapshot. Không có migration nào chạy lên DB owner trong phiên này.

### Changed files và handoff local

**41 files = 11 tracked modified + 30 untracked files; staged rỗng; committed delta so origin/main rỗng.** Các path untracked phải được reviewer đọc trực tiếp, vì `git diff origin/main` không hiển thị chúng. Danh sách đầy đủ (`M`/`??` là trạng thái cuối):

```text
 M .gitattributes
 M backend/src/TeaPremium.Api/Program.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/AppDbContext.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/PersistenceRegistration.cs
 M backend/tests/TeaPremium.IntegrationTests/AuditInterceptorTests.cs
 M backend/tests/TeaPremium.IntegrationTests/PlatformDatabaseTests.cs
 M backend/tests/TeaPremium.IntegrationTests/PlatformSchemaParityTests.cs
 M backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj
 M packages/api-client/openapi.json
 M packages/api-client/src/generated/schema.ts
?? backend/src/TeaPremium.Api/AdministrativeData/ImportProvincesConsole.cs
?? backend/src/TeaPremium.Api/Settings/SettingsEndpoints.cs
?? backend/src/TeaPremium.Application/AdministrativeData/AdministrativeCsv.cs
?? backend/src/TeaPremium.Application/AdministrativeData/IAdministrativeReferenceData.cs
?? backend/src/TeaPremium.Application/AdministrativeData/ImportContracts.cs
?? backend/src/TeaPremium.Application/AdministrativeData/ImportPlanner.cs
?? backend/src/TeaPremium.Application/AdministrativeData/ImportUseCases.cs
?? backend/src/TeaPremium.Application/AdministrativeData/ImportValidation.cs
?? backend/src/TeaPremium.Application/Settings/SettingsCatalog.cs
?? backend/src/TeaPremium.Application/Settings/SettingsContracts.cs
?? backend/src/TeaPremium.Infrastructure/AdministrativeData/AdministrativeDataImport.cs
?? backend/src/TeaPremium.Infrastructure/AdministrativeData/EfAdministrativeImport.cs
?? backend/src/TeaPremium.Infrastructure/AdministrativeData/EfAdministrativeReferenceData.cs
?? backend/src/TeaPremium.Infrastructure/AdministrativeData/ImportEnvironment.cs
?? backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611180001_SettingsAdministrativeData.Designer.cs
?? backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611180001_SettingsAdministrativeData.cs
?? backend/src/TeaPremium.Infrastructure/Settings/EfSettingsStore.cs
?? backend/tests/TeaPremium.IntegrationTests/Fixtures/v5.2.0/ATTRIBUTION.md
?? backend/tests/TeaPremium.IntegrationTests/Fixtures/v5.2.0/LICENSE
?? backend/tests/TeaPremium.IntegrationTests/Fixtures/v5.2.0/LICENSE.gz
?? backend/tests/TeaPremium.IntegrationTests/Fixtures/v5.2.0/README.md.gz
?? backend/tests/TeaPremium.IntegrationTests/Fixtures/v5.2.0/json__full_json_generated_data_vn_units.json.gz
?? backend/tests/TeaPremium.IntegrationTests/Fixtures/v5.2.0/json__vn_provinces_metadata.json.gz
?? backend/tests/TeaPremium.IntegrationTests/P009CliTests.cs
?? backend/tests/TeaPremium.IntegrationTests/P009Harness.cs
?? backend/tests/TeaPremium.IntegrationTests/P009ImportTests.cs
?? backend/tests/TeaPremium.IntegrationTests/P009SettingsTests.cs
?? backend/tests/TeaPremium.UnitTests/AdministrativeDataTests.cs
?? backend/tests/TeaPremium.UnitTests/SettingsValidationTests.cs
?? docs/plans/p0-09-implementation.md
```

Git status/inventory và SHA-256 của toàn bộ changed paths được lưu trong `artifacts/p0-09-recovery/final-status.txt`, `changed-files-sha256.csv`; evidence hashes tại `evidence-sha256.csv`. Specs/apps/infra/migrations lịch sử không đổi. Handoff **READY_FOR_INDEPENDENT_G4** (tương đương workflow `READY_FOR_LOCAL_G4`), fresh reviewer phải đọc **cùng worktree** `C:\Users\ADMIN\source\tra-hien`, diff so `origin/main`, staged/unstaged và nội dung 30 untracked files. Dừng tại G3; chưa commit/push/PR/merge.

## G3 remediation sau independent G4 — 18/11/2026

**READY_FOR_FOCUSED_G4** (G3 local handoff, tương đương READY_FOR_LOCAL_G4). Branch `feature/p0-09-recovery`; base = HEAD = `23bf4b2d495a4f915e323747afd78ceb4dc83d2e`; staged rỗng. Owner giao đúng ba findings trong báo cáo `C:/Users/ADMIN/AppData/Local/Temp/tra-hien-p009-g4-20261118/P0-09-G4-review.md`. Đây là remediation implementation, không phải independent approval.

### Scoped readiness và corrections

Reuse G1/G2/F-02 nêu trên; `git diff 72cdf11c9e6bb4905df6566feb0ee69455912b05 HEAD -- docs/spec` rỗng; specs không có local delta. Scope vẫn P0-09/AC-P0-04, FR-PLT-01/FR-CNT-06, NFR-04/07/08/09, DEC-01/19 và hợp đồng F-02 tại 03 §3.6, API 05 §4.7. Dependency IAM/UoW/platform đã có. Không thêm module, DDL hay quyết định F-02. Các migration, snapshot, validators nghiệp vụ, ImportEnvironment và IAM giữ nguyên bytes so G4 handoff.

| G4 finding | Root cause và correction | Paths remediation |
|---|---|---|
| 1 / P1 | Generic host đọc raw import file arguments như configuration trước khi CLI kiểm file. Nhận diện `import-provinces` trước CreateBuilder, chỉ truyền args rỗng cho host của import command; giữ nguyên args cho startup API/bootstrap-owner. Trusted configuration/profile vẫn từ deployment sources; không đổi IAM, TOTP, session, permission hoặc production checks. | `backend/src/TeaPremium.Api/Program.cs`; real-process regressions trong `backend/tests/TeaPremium.IntegrationTests/P009CliTests.cs`, API startup regression trong `OpenApiTests.cs`. |
| 2 / P2 | Chỉ kiểm reparse point của file cuối. CLI giờ reject rooted/drive/ADS/backslash/control/dot/empty/ambiguous path components, kiểm mọi ancestor từ volume root, source root, thư mục trung gian và final file; recheck sau đọc. Giữ hash bytes, tăng bound khi copy để file đang lớn lên cũng không được ghi quá MaxBytes vào buffer. | `backend/src/TeaPremium.Api/AdministrativeData/ImportProvincesConsole.cs`; source path/link regressions trong `P009CliTests.cs`. |
| 3 / P2 | Exporter để JsonElement dictionary thành object không có value shape. API document transformer khai báo union JSON đệ quy (null/boolean/number/string/array/object), dùng cùng map cho GET response và PUT values. Generator hoist union đệ quy từ AST đã sinh thành named alias để tránh TS2502; không viết tay body JSON model và không đổi runtime DTO/serialization. Regenerate cả OpenAPI/client bằng pipeline chuẩn. | `backend/src/TeaPremium.Api/Settings/SettingsOpenApi.cs`, `Program.cs`, `packages/api-client/generate.mjs`, `openapi.json`, `src/generated/schema.ts`; schema assertions trong `OpenApiTests.cs`, compile-time fixture `packages/api-client/src/settings-contract.test-d.ts`. |

**Filesystem precondition bắt buộc:** operator phải đặt toàn bộ bundle và các thư mục cha trong cây được kiểm soát quyền ghi bằng OS ACL, giữ **immutable trong toàn bộ validation/read/plan/apply**. Không đọc trực tiếp từ thư mục upload/shared writable. Managed filesystem API đang dùng không cung cấp atomic open-beneath/no-follow xuyên mọi ancestor trên mọi OS; pre/post checks không loại trừ được transient replacement rồi đổi lại. Không tuyên bố bảo đảm TOCTOU tuyệt đối hoặc đã xác minh ACL thật. Source hashes/trusted manifest vẫn bắt buộc. Native atomic filesystem adapter không được thêm trong slice này; focused reviewer cần đánh giá giới hạn theo deployment precondition được yêu cầu trong remediation.

### RED/GREEN và commands đã chạy

Evidence mới tại ignored `artifacts/p0-09-remediation/`; source mount host read-only, code/test snapshot `/rem-work` trong Linux SDK `10.0.403`, Node `22.22.2`/pnpm `12.16.1`; PG17 Testcontainers tự tạo/dispose, không DB owner. `before-source.csv` giữ inventory SHA-256 trước sửa; RED dùng code trước correction, GREEN dùng code hiện hành.

- RED: real CLI suite **9 fail / 12 pass / 21 executed**, trong đó cả ba option-shaped filename cases manifest/approval/profile fail vì được chấp nhận (`linux/red-tests.log`, TRX `linux/red-tests/`). Sau hoàn thiện fixture LICENSE cho intermediate link, chạy riêng original source: **2 fail / 1 pass / 3** — source-root và intermediate links được chấp nhận; final-file link đã bị chặn trước sửa (`linux/red-links.log`, TRX `linux/red-links/`). Không gọi final-file link là lỗi mới.
- RED: `corepack pnpm --filter @teapremium/api-client typecheck` từ chối valid response/PUT compatibility bằng TS2344 (`linux/red-client.log`).
- GREEN focused: `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~P009 --logger trx --results-directory /rem-evidence/focused` → **59/59 PASS**, zero skipped, `linux/focused.log` + TRX. Suite chứa 23 CLI cases: untrusted manifest/approval, nonlocal profile, real PTY authorized apply/exact retry, valid regular/nested source, root/intermediate/file links, traversal/malformed paths và existing validation cases. Linux-specific facts/theories khai rõ skip reason trên OS khác; trên Linux đã chạy thật, không skip.
- Compile-time fixture kiểm numeric/null, boolean, array, nested object ở các group thực; response values và PUT values tương thích hai chiều; function/undefined/nested function/bigint bị từ chối. Không dùng unsafe cast. API schema test và normal API process startup/options regression nằm trong full backend run dưới đây.

| Exact command | Kết quả / evidence |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS, `linux/restore.log`. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS 0 warnings/errors, `linux/build.log`. |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /rem-evidence/full-tests` | **360/360 PASS** = Unit45 + Architecture34 + Integration281, total=executed=passed, zero skipped; `linux/full-tests.log`, ba TRX trong `linux/full-tests/`, `test-summary.json`. Bao gồm migrations từ rỗng/existing IAM, idempotent SQL, F-02 schema/grant parity, guarded Down, IAM/UoW/audit/outbox và runtime ledger privileges. Không cộng focused/RED/helper vào tổng này. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS, `linux/format.log`; source chỉ được format có chủ đích ở những file sửa trước verify. |
| `dotnet tool restore`; `dotnet tool run dotnet-ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS/no pending changes, `linux/tool-restore.log`, `linux/ef-pending.log`; dummy connection 127.0.0.1:1. |
| `dotnet tool run dotnet-ef migrations script --idempotent --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --output /rem-evidence/idempotent.sql` | PASS, `linux/ef-script.log`, `linux/idempotent.sql`; no real DB tooling connection. |
| `dotnet build /rem-pty/Driver.csproj --no-restore -warnaserror`; `dotnet /rem-pty/bin/Debug/net10.0/Driver.dll` | **18/18 PTY cases PASS**, 3 expected events, `linux/pty-build.log`, `linux/cli-pty.log`; cùng helper G3, chỉ đổi project reference/contentroot sang snapshot mới. Password/TOTP synthetic, không echo. |
| `dotnet build /rem-probe/Driver.csproj --no-restore -warnaserror`; `dotnet /rem-probe/bin/Debug/net10.0/Driver.dll` | Re-run reviewer helper: cả ba configuration cases và source-root link đều rejected, ledger=0. Actual tp_app CREATE schema/table, ALTER settings, DELETE migration history bị từ chối 42501; `linux/probe.log`. Assertions bền vững nằm trong automated tests; helper chỉ bổ sung evidence. Post-commit branch của helper này không chạy vì approval nay bị reject đúng. |
| `corepack pnpm install --frozen-lockfile` | PASS, `linux/red-frontend-install.log`; lockfiles không đổi. |
| `corepack pnpm generate:api`; `corepack pnpm check:api` | PASS, `linux/api-generate.log`, `linux/api-check.log`; output pipeline đã copy về đúng hai file generated host, không sửa tay. |
| `corepack pnpm lint`; `corepack pnpm typecheck` | PASS ba packages, `linux/frontend-lint.log`, `linux/frontend-typecheck.log`; bao gồm new contract type fixture. |
| `corepack pnpm test`; `corepack pnpm build` | **26/26 PASS** (store2 + admin unit8 + browser16), build api-client/store/admin PASS; `linux/frontend-tests.log`, `linux/frontend-build.log`. Chỉ chạy frontend trong snapshot, không sửa apps host. |
| `docker build -f infra/Dockerfile.api -t tra-hien-api:p009-remediation .`; `docker build -f infra/Dockerfile.web --build-arg APP=store -t tra-hien-store:p009-remediation .`; `docker build -f infra/Dockerfile.web --build-arg APP=admin -t tra-hien-admin:p009-remediation .` | Cả ba PASS exit0, host context thật; `docker-api.log`, `docker-store.log`, `docker-admin.log`. Chỉ build. |
| `git diff --check`; `git diff --cached --check` | PASS; staging vẫn rỗng. Final inventory/hashes tại `final-status.txt`, `after-source.csv`, `changed-files-sha256.csv`, `remediation-integrity.json`. |

Các lỗi trung gian đã sửa trước chốt: CA1861 trong test expectation; shell quoting của focused filter; TS2502 ở union recursive output của generator trước bước hoist AST. Không dùng các lượt này làm PASS. Final backend/client/full verification bên trên dùng kết quả sau sửa.

### Inventory, risk, rollback và handoff

**45 changed files = 13 tracked modified + 32 untracked; 245 total source files.** So independent G4 handoff, remediation thay đúng **10 paths** (8 existing, 2 new): `Program.cs`, `ImportProvincesConsole.cs`, `SettingsOpenApi.cs` (new), `P009CliTests.cs`, `OpenApiTests.cs`, `packages/api-client/generate.mjs`, `openapi.json`, `src/generated/schema.ts`, `src/settings-contract.test-d.ts` (new), và task record này. Các paths khác giữ nguyên bytes so `before-source.csv`; original G3 changes được bảo toàn. Full status gồm mọi untracked file ở dưới và trong evidence, không chỉ `git diff`.

Không có P0/P1/P2 mới được xác nhận trong remediation; ba findings đã có correction và GREEN evidence, **chưa** được independent focused G4 chấp nhận. Residual filesystem/ACL precondition nêu rõ ở trên. NOT RUN: focused independent G4, published-head GitHub Actions, Windows native runtime/PTY và Windows-specific reparse tests, wire-level COMMIT acknowledgement-loss fault injection, production migration/import, actual production permissions/config ACL, encrypted backup/restore drill và official reconciliation/legal verification. Production APPLY vẫn **HOLD**; test config flags không chứng minh hạ tầng thật.

Không thêm/sửa migration hoặc chạy data recovery. Rollback remediation chỉ khôi phục có chủ đích 10 paths theo reviewed G3 snapshot và SHA-256 inventory; không reset/clean để xóa original WIP 41 files. Không cần DB rollback vì không có DDL/data apply ngoài disposable tests. Bước tiếp theo: fresh-context focused G4 trên cùng worktree, gồm full current diff và mọi untracked file; chỉ publish sau G4 PASS và owner authorization. G3 dừng tại **READY_FOR_FOCUSED_G4**, không commit/push/PR/merge.

Current Git status:

```text
 M .gitattributes
 M backend/src/TeaPremium.Api/Program.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/AppDbContext.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/PersistenceRegistration.cs
 M backend/tests/TeaPremium.IntegrationTests/AuditInterceptorTests.cs
 M backend/tests/TeaPremium.IntegrationTests/OpenApiTests.cs
 M backend/tests/TeaPremium.IntegrationTests/PlatformDatabaseTests.cs
 M backend/tests/TeaPremium.IntegrationTests/PlatformSchemaParityTests.cs
 M backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj
 M packages/api-client/generate.mjs
 M packages/api-client/openapi.json
 M packages/api-client/src/generated/schema.ts
?? backend/src/TeaPremium.Api/AdministrativeData/ImportProvincesConsole.cs
?? backend/src/TeaPremium.Api/Settings/SettingsEndpoints.cs
?? backend/src/TeaPremium.Api/Settings/SettingsOpenApi.cs
?? backend/src/TeaPremium.Application/AdministrativeData/AdministrativeCsv.cs
?? backend/src/TeaPremium.Application/AdministrativeData/IAdministrativeReferenceData.cs
?? backend/src/TeaPremium.Application/AdministrativeData/ImportContracts.cs
?? backend/src/TeaPremium.Application/AdministrativeData/ImportPlanner.cs
?? backend/src/TeaPremium.Application/AdministrativeData/ImportUseCases.cs
?? backend/src/TeaPremium.Application/AdministrativeData/ImportValidation.cs
?? backend/src/TeaPremium.Application/Settings/SettingsCatalog.cs
?? backend/src/TeaPremium.Application/Settings/SettingsContracts.cs
?? backend/src/TeaPremium.Infrastructure/AdministrativeData/AdministrativeDataImport.cs
?? backend/src/TeaPremium.Infrastructure/AdministrativeData/EfAdministrativeImport.cs
?? backend/src/TeaPremium.Infrastructure/AdministrativeData/EfAdministrativeReferenceData.cs
?? backend/src/TeaPremium.Infrastructure/AdministrativeData/ImportEnvironment.cs
?? backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611180001_SettingsAdministrativeData.Designer.cs
?? backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611180001_SettingsAdministrativeData.cs
?? backend/src/TeaPremium.Infrastructure/Settings/EfSettingsStore.cs
?? backend/tests/TeaPremium.IntegrationTests/Fixtures/v5.2.0/ATTRIBUTION.md
?? backend/tests/TeaPremium.IntegrationTests/Fixtures/v5.2.0/LICENSE
?? backend/tests/TeaPremium.IntegrationTests/Fixtures/v5.2.0/LICENSE.gz
?? backend/tests/TeaPremium.IntegrationTests/Fixtures/v5.2.0/README.md.gz
?? backend/tests/TeaPremium.IntegrationTests/Fixtures/v5.2.0/json__full_json_generated_data_vn_units.json.gz
?? backend/tests/TeaPremium.IntegrationTests/Fixtures/v5.2.0/json__vn_provinces_metadata.json.gz
?? backend/tests/TeaPremium.IntegrationTests/P009CliTests.cs
?? backend/tests/TeaPremium.IntegrationTests/P009Harness.cs
?? backend/tests/TeaPremium.IntegrationTests/P009ImportTests.cs
?? backend/tests/TeaPremium.IntegrationTests/P009SettingsTests.cs
?? backend/tests/TeaPremium.UnitTests/AdministrativeDataTests.cs
?? backend/tests/TeaPremium.UnitTests/SettingsValidationTests.cs
?? docs/plans/p0-09-implementation.md
?? packages/api-client/src/settings-contract.test-d.ts
```
