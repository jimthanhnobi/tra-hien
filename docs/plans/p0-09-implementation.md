# P0-09 — Settings và dữ liệu hành chính (G3 local)

**WIP CHECKPOINT — chưa independent G4 implementation, chưa được merge.** Ngày 18/11/2026, branch `feature/p0-09-settings-admin-data`, base `72cdf11c9e6bb4905df6566feb0ee69455912b05`. Owner yêu cầu lưu tiến độ và push riêng feature branch để tiếp tục ngày mai; chỉ xuất bản checkpoint, không mở PR/merge/deploy hoặc công nhận implementation đã được review.

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

## Verification (G3, không phải independent G4)

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

## Handoff / residual risks

Production APPLY **HOLD**: chưa official full reconciliation/source mapping, production role/backup/restore evidence; không auto-import/auto-migrate hoặc execute production SQL. Backup/restore drill thực, live CLI interactive owner terminal, production cutover và GitHub Actions **NOT RUN**. Public storefront settings/address endpoints/CRM address UX thuộc pha/task tương ứng, không triển khai ngoài scope admin/API+CLI này.

G3 chưa tự nhận independent review. Owner đã yêu cầu commit/push **WIP checkpoint** trong phiên 18/11/2026, chưa yêu cầu PR/merge. Việc push không thay G4 và không cấp production APPLY.

## Tiếp tục phiên sau

1. Checkout `feature/p0-09-settings-admin-data`, kiểm remote/head và đọc task record này; không bắt đầu lại G1/G2. Không cần viết lại Settings/importer từ đầu.
2. Rà chốt implementation so spec và quyết định recovery-only-full ngày 18/11; ưu tiên trust/approval/CLI, manifest/plan fixed format, SQL-owned ledger mapping/privileges và readiness port. Cần kiểm CLI private-terminal end-to-end (chưa chạy tương tác thật); giữ production gate HOLD.
3. Dùng `$review-gate` trong **fresh context** đọc cùng worktree, full diff so `origin/main` và mọi local delta; reviewer độc lập phải xác minh code/tests, không chỉ task record. Chưa có G4 PASS cho implementation này.
4. Sửa finding nếu có, chạy focused regression tương ứng rồi review lại. Chỉ sau PASS và owner authorization mới mở PR/merge; chưa triển khai P1.

Checkpoint inventory: **40 file** gồm 11 tracked modified + 29 new/untracked trước commit. Phạm vi: Application Settings/AdministrativeData (8), API endpoints/CLI/Program (3), Infrastructure adapters/models/DI/migration/snapshot (10), tests/fixtures/project (15), generated OpenAPI/client (2), task record (1), .gitattributes binary rule cho compressed fixture (1). `docs/spec/**`, `apps/**`, `infra/**` và hai migration lịch sử giữ nguyên; không đính kèm artifacts, credential hoặc DB dump.

Checkpoint byte-safety: .gitattributes khai báo fixture *.gz là binary để Git không chuẩn hóa CRLF làm hỏng compressed source bytes; staged blobs được so byte-for-byte với fixture đã kiểm chứng.
