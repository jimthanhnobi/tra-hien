# P0-08 — IAM nhân viên: triển khai và bằng chứng review

Trạng thái hiện tại: **G4 độc lập PASS; focused G4 PASS; P2 đã đóng**. Code đã commit local tại `4f39d48a79141494cb18592c31cf58caadaae6e3` trên `feature/p0-08-staff-iam`. Owner đã cho phép push nhánh để kiểm tra; chưa cho phép mở PR hoặc merge. Các mục G3/remediation bên dưới giữ bằng chứng lịch sử; kết quả review cuối nằm ở mục cuối hồ sơ.

## G0/G1/G2 và traceability

- Ngày 17/11/2026 (Asia/Saigon), working tree ban đầu sạch, nhánh `feature/p0-07-idempotency`, HEAD `50bf7a1b1b69df8fdbd60110c7b11226fa15dc25`. Đã `git fetch origin --prune`, fast-forward local main và tạo `feature/p0-08-staff-iam` từ `72c637ab60f46e6367609c57ec114493b6ad675e` (cũng là HEAD/base của task). Không reset/clean/stash, không có local changes cũ bị ghi đè.
- GitHub connector xác nhận [PR #16](https://github.com/jimthanhnobi/tra-hien/pull/16) `merged=true`, merge SHA đúng base trên. Main dùng được qua build và toàn bộ kiểm thử local; không suy diễn GitHub Actions đã PASS cho thay đổi chưa publish.
- G0: TeaPremium v2.0, 16/11/2026 tại `docs/spec/00-README.md:3`. Phạm vi theo `07-roadmap.md:24`, dependency P0-04 đã có trên main; P0-05/06/07/10/11 hiện hữu được dùng làm nền/hồi quy.
- G1 TARGETED: tái sử dụng full audit SHA `cadce575597738bf40ad7c45689482de5a5fbc3d` trong `docs/analysis/spec-inventory.md`, gaps và traceability; đối chiếu amendment G26/G01/F-01 đã được duyệt tại `0bfc98ecc4074c5e66503c8dbb83b8eb1bd81856`. `git diff 0bfc98e HEAD -- docs/spec` rỗng. Không chạy lại full audit, không sửa spec. Đọc riêng các phần IAM, security, migration, audit/transaction và DEC dưới đây; các gap customer/pha sau không chặn IAM nhân viên.
- G2 scoped: tái sử dụng `docs/architecture/conformance-review.md`, `docs/plans/task-readiness.md` và evidence P0-04…07/10/11. Không thêm DB/context/UoW, service auth, Redis, BFF hay roadmap. Chọn cùng scoped `AppDbContext` và `EfUnitOfWork`, Identity là adapter Infrastructure qua port Application. Endpoint chỉ bind → handler → map response. Không có blocker kiến trúc được xác nhận trước code.
- Đã đọc AGENTS.md, `docs/WORKFLOW.md`, `docs/CODEX_USAGE.md`, local implementation-slice/spec-audit/architecture-plan skills; làm trong main thread, không subagent. G4 phải là session độc lập cùng worktree, sau khi dừng G3.

| Nguồn, ID giữ nguyên | Thực hiện / bằng chứng |
|---|---|
| `07-roadmap.md:24,33–35`, P0-08, AC-P0-01/02/03 | Identity staff, auth, staff management, CLI; lockout, Production TOTP gate, CSRF thật. T01–T27 chỉ là nhãn kế hoạch test local của owner. |
| `01-requirements.md:54–56,204`, FR-IAM-01/02/03, NFR-07 | Email/password, quản lý nhân viên, đổi mật khẩu lần đầu, TOTP, HTTPS/cookies/CSRF/rate limit. |
| `04-business-rules.md:10–11,20`, BR-IAM-01/02/06 | Password ≥12 đủ bốn nhóm; rolling 15 phút/5 lần sai; lock 15 phút; sliding 8h/tối đa 7d; security stamp revocation. |
| `02-architecture.md` §3/§4/§5.2/§5.4/§6/§7/§8, đặc biệt `:186–236` | Application ports/handlers, một transaction, audit, AdminCookie, exact permission seed, Data Protection DB + X.509. |
| `05-api.md` §1 (`:3–26`), §4.1 (`:90–101`) | `/api/v1/admin/auth/*`, `/staff[/{id}]`, ProblemDetails, camelCase, phân trang và permission server-side. |
| `03-database.md:148–150,187–193`, `schema.sql:1214–1218` | Seed bằng migration, bảy bảng IAM, snake_case/constraint tên rõ, migration additive/API không auto-migrate. |
| `00-README.md:98–100`, DEC-14/15/16 | Staff/customer tách bảng/scheme/cookie/host; same-origin Admin, không localStorage/sessionStorage. |
| NFR-04/08/09; AC-P0-04/05 | Hồi quy PII/audit/UoW/outbox/idempotency/architecture/schema. Không nhận đã hoàn tất settings P0-09 hoặc Admin UI P0-12. |

## Audit foundation trước sửa và lựa chọn persistence

- API main chưa có Identity, authentication, permissions, CSRF hay staff endpoint production. Authentication giả và CSRF trong test P0-07 chỉ thuộc test assembly.
- `AppDbContext` main có 9 bảng platform, snake_case, mapping kiểu PostgreSQL và InitialPlatform. `PersistenceRegistration` cấp cùng scoped context cho audit, UoW, outbox; giữ nguyên quyền sở hữu transaction của `EfUnitOfWork` và request owner P0-07.
- `AuditSaveChangesInterceptor` khóa original row, yêu cầu transaction cho update/delete, snapshot trước/sau và redaction allowlist; không ghi secret/PII chưa phân loại. `HttpAuditContext` chỉ nhận identity đã xác thực của đúng scheme. Giữ cơ chế này cho `StaffUser [Audited]`.
- Idempotency là opt-in; authorization và CSRF phải chạy trước filter. Không gắn idempotency vào auth/reset vì response/cookie/secret không được replay. Bộ test idempotency hiện hữu giữ nguyên logic, chỉ chuyển test CSRF thật sang HTTPS.
- ProblemDetails/correlation/log JSON hiện hữu được dùng lại. CI hiện hữu có restore/build/format, Testcontainers PG17, API drift, migration SQL, FE/Docker; không sửa workflow. Client TS vẫn sinh từ API thật.

**Phương án nhỏ nhất đã triển khai:** `AppDbContext : IdentityDbContext<StaffUser, IdentityRole<Guid>, Guid>` và `IDataProtectionKeyContext`. Dùng class DataProtectionKey của framework thay class platform tương đương; mapping/table/sequence platform không đổi. `IStaffIdentity` không lộ EF/Identity type cho Application. Application command decorators mở UoW; UserManager/ProtectedStaffStore dùng đúng context này, mọi SaveChanges cùng transaction. Lỗi mutation rollback; authentication bị từ chối là outcome thành công của command để commit bộ đếm sai/lockout rồi HTTP mới map 401.

Migration `202611170919_StaffIam` chỉ tạo `iam.staff_users`, `roles`, `user_roles`, `role_claims`, `user_claims`, `user_logins`, `user_tokens`; UUID staff sinh ở ứng dụng. `NormalizedEmail` required + unique index; Identity security/concurrency stamps giữ đúng semantics. Staff API dùng `rowVersion` dạng concurrency stamp string (Identity), không ép thành xmin của aggregate catalog/order. Field bổ sung: display_name/is_active/must_change_password/created_at.

Role UUID cố định, seed 3 roles và 45 permission claims (owner 28, sales 11, content 6), theo đúng matrix §7, không wildcard owner. Pin Identity store schema version 1 ở runtime/design time và ignore passkey types: passkey không thuộc bảy bảng đã được giao. Không seed owner/password.

**Lỗi integration được chứng minh khi rollback:** EF parser mặc định nhận ID 14 chữ số nhưng lịch sử InitialPlatform và quy ước repo dùng 12 chữ số. `RepositoryMigrationIds` chỉ thích nghi ID/name parser/generator (vẫn nhận cả 14 chữ số), không sửa migration/history cũ. Có phụ thuộc hẹp vào EF default generator internal API ở version đã pin; phải giữ test up/down khi nâng EF. Test rollback gọi được cả full historical ID; script rollback sinh được bằng tên `StaffIam InitialPlatform`.

## Cookie, xác thực và authorization

- Scheme duy nhất cho Admin là `AdminCookie`; cookie `__Host-tp_admin`, Path `/`, không Domain, HttpOnly, Secure Always, SameSite Strict; session cookie không persistent. Sliding 8 giờ; signed original-start claim không reset khi sliding, hết tuyệt đối tại 7 ngày. `IClock`/TimeProvider điều khiển được trong test.
- Mỗi request kiểm user active/lockout/security stamp từ DB và nạp lại roles/permission claims từ Identity DB. Cookie không chứa email/password/TOTP secret. Role claim đổi được refresh ngay; role assignment/deactivate/lock/reset/password change rotate stamp. Không dùng identity/role/header do client tự khai.
- `StaffOnboarding` chỉ là authenticated AdminCookie cho auth flow. `StaffReady` và mọi permission policy bắt buộc hết password-change gate và đủ factor. Cookie onboarding không có permission claims. Owner Production chưa verified TOTP chỉ vào auth flow; sau enrollment vẫn phải login lại với TOTP mới. Staff đã bật TOTP phải cung cấp mã ở login, kể cả Development.
- Mật khẩu lần đầu bắt buộc đổi với cả bootstrap owner; không cho đổi sang lại chính mật khẩu hiện tại. Đổi/reset rotate stamp, mọi phiên cũ bị từ chối ngay ở request sau; session bị lockout không sống lại khi hết 15 phút. Failed password/TOTP attempts được serialize trên row staff và giữ cửa sổ rolling 15 phút trong user_tokens.
- Quản lý staff cần `staff.manage` qua middleware; adapter kiểm lại actor/stamp/quyền trong transaction trước mutation. Gán owner còn yêu cầu actor có role owner thật. Một advisory transaction lock serialize staff/owner membership, kiểm không bỏ/vô hiệu/khóa owner hoạt động cuối cùng, kể cả request đồng thời. PATCH stale concurrency stamp → 409.
- Staff [Audited] đi qua interceptor/redaction; thay đổi role membership ghi thêm audit `state_change` trên UUID `iam.staff_users` với tên roles cố định, cùng context/UoW. Không tạo encoding khóa ghép mới cho interceptor. Actor/correlation dùng approved audit context. Không có credential/token/secret trong audit response.

## TOTP và giới hạn recovery/rotation

- TOTP tương thích định dạng Identity/RFC 6238: base32, SHA1, 6 số, bước 30 giây. Secret chờ xác minh mã hóa với Data Protection purpose riêng trong user_tokens, hết hạn 10 phút; setup chưa bật TwoFactorEnabled. Setup lại thay pending secret; không thay secret đã verified.
- Verify thành công mới ghi authenticator key qua protected Identity store và bật TwoFactorEnabled; secret lưu DB đã mã hóa. Verified secret không được trả lại từ API. Setup response chứa shared key/provisioning URI chỉ cho chính tài khoản đang onboarding, qua HTTPS/no-store; không log body, mã hoặc URI.
- Framework mặc định không lưu consumed timestep và dùng wall clock. [Nguồn framework](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Identity/Extensions.Core/src/AuthenticatorTokenProvider.cs) được đối chiếu; adapter `StaffTotp` dùng IClock, cửa sổ ±1 bước, lưu high-water mark trong user_tokens. Login/verify cùng row lock + UoW; replay cùng mã/bước, bước cũ và race hai login chỉ có tối đa một success.
- **MISSING_CONTRACT, giới hạn vận hành cần owner chốt trước go-live:** `02-architecture.md` §6, `04-business-rules.md` BR-IAM-02 và `05-api.md` §4.1 yêu cầu setup/verify nhưng không định nghĩa xác minh người mất thiết bị, recovery code hay reset/rotation secret đã verified. P0-08 không mở endpoint bypass/disable/reset TOTP, không cho password reset hay bootstrap tắt MFA. Setup tài khoản đã bật → 409. Mất thiết bị phải dừng tài khoản đó; dùng một owner khác có TOTP độc lập nếu đã có. Trước khi bổ sung recovery phải duyệt cách xác minh operator, approver, audit, revoke phiên và secret cũ; không hướng dẫn SQL để bypass MFA. Đây là quyết định security chưa được duyệt cho chức năng bổ sung, không giả nhận có tự phục hồi.

## CSRF, transport và endpoints

- Antiforgery cookie `__Host-tp_admin_csrf`, Secure/HttpOnly/SameSite Strict; header `X-CSRF-TOKEN`. GET csrf cấp request token gắn identity; sau login/password change/enrollment cần lấy token mới. Login/logout/PATCH/POST đều validate trước binding/handler/idempotency; thiếu/sai/mismatch token → 400 ProblemDetails.
- Middleware order: correlation/problem → forwarded headers (nếu có trusted peers) → HSTS/HTTPS+host gate → authentication → authorization → CSRF → rate limiter → idempotency buffering → endpoint. Các nhánh test tự có pipeline chỉ tồn tại trong test assembly.
- Admin HTTP bị từ chối `https_required`; Production có DB bắt buộc cấu hình `Iam:AdminHost` và certificate. Host Admin không khớp → 403. Không đổi infra deployment, không tự tạo CORS/local token storage.
- Login 10 requests/phút/IP, queue 0. Mặc định không xử lý forwarding headers. Chỉ `Iam:TrustedProxies` (địa chỉ peer rõ ràng) được xử lý X-Forwarded-For/Proto, tối đa 1 hop; không đọc CF-Connecting-IP làm nguồn quyền/IP. Caddy phải sanitize header theo trust chain thực tế; không điền toàn mạng/khách vào trusted peers.

| Endpoint dưới `/api/v1/admin` | Quyền / hợp đồng |
|---|---|
| GET `/auth/csrf` | Anonymous hoặc onboarding; response `{token}`, no-store. |
| POST `/auth/login` | Anonymous + CSRF + rate limit; `{email,password,totpCode?}`; 401 generic `invalid_credentials`, không báo user có tồn tại. Response user + passwordChangeRequired/totpRequired, chỉ permissions có hiệu lực. |
| POST `/auth/logout`; GET `/auth/me` | AdminCookie/onboarding; me không trả permissions của phiên chưa ready. Logout cần CSRF. |
| POST `/auth/change-password` | AdminCookie + CSRF; `{currentPassword,newPassword}`; thành công 204 và sign out. |
| POST `/auth/totp/setup`; POST `/auth/totp/verify` | AdminCookie + CSRF; phải đổi initial password trước. Verify body `{code}`; 204/sign out sau thành công. |
| GET `/staff`, GET `/staff/{id}` | staff.manage + ready; list `page=1&pageSize=20`, max 100. |
| POST `/staff` | staff.manage; `{email,displayName,initialPassword,roles}`; mật khẩu tạm truyền HTTPS, không echo lại. |
| PATCH `/staff/{id}` | staff.manage; `{rowVersion,isActive?,locked?,roles?,newPassword?}`; roles là tập role mới (hỗ trợ nhiều role); reset dùng password mạnh do owner nhập, không sinh plaintext/token response. |

Reset procedure: owner xác minh nhân viên ngoài hệ thống trước khi đặt temporary password, gửi qua kênh riêng phù hợp; không đặt vào URL, ticket, log hoặc audit. Reset giữ nguyên verified TOTP, rotate stamp và bật must_change_password. Nhân viên đăng nhập bằng password tạm (và TOTP nếu đã bật), chỉ đổi password; sau đó login lại. Không triển khai quên mật khẩu public/customer hay SMTP/reset-link chưa có spec.

## Operations/CLI và migration recovery

Development: dùng `dotnet dev-certs https --trust` trên máy operator, sau đó `dotnet run --project backend/src/TeaPremium.Api --launch-profile https`. Gọi `https://localhost:7071/api/v1/admin/...`; profile HTTP/Compose HTTP hiện hữu không dùng cho login staff. Không đổi Secure/SameSite để tiện localhost. WebApplicationFactory tests dùng HTTPS và certificate test tạo tạm ngoài repo.

Production: operator cung cấp `ConnectionStrings__Default`, `Iam__AdminHost` (hostname admin, không scheme/port), `Iam__KeyCertificatePath`, password chứng chỉ qua secret `Iam__KeyCertificatePassword` nếu cần; cấp quyền file tối thiểu, backup certificate/private key cùng key ring đã mã hóa. Data Protection keys lưu bảng platform hiện hữu, application name `teapremium`. Không xoay/xóa certificate cũ nếu còn cần giải mã dữ liệu/backup; quy trình cert rollover phải giữ khả năng decrypt trước khi thay. Cấu hình `Iam__TrustedProxies__0`… đúng peer reverse proxy nếu TLS kết thúc ở đó; không tin header từ internet trực tiếp. Không thay file production deployment trong task này.

Trước API mới: backup DB và certificate/key ring; operator review `artifacts/migrations.sql`, chạy migration bundle bằng tp_owner theo workflow hiện hành; API không tự migrate hoặc EnsureCreated. Grant IAM runtime tối thiểu qua runbook vận hành trước go-live, giữ audit append-only và quyền key ring cần thiết; không suy diễn Testcontainers superuser là bằng chứng quyền production đã kiểm.

Bootstrap chỉ chạy thủ công trong terminal riêng tư (không pipeline/redirect, không email/password arguments):

```powershell
dotnet run --project backend/src/TeaPremium.Api --no-launch-profile -- bootstrap-owner
```

CLI hỏi email, password ẩn và confirm; transaction tạo owner nếu chưa có owner membership. Có owner → no-op; email đã tồn tại khi chưa có owner → conflict, không promote/overwrite/reactivate user. Không chạy hosted services/API listener hoặc migration khi gọi CLI. Sau đó HTTPS login → đổi initial password → login lại → setup/verify TOTP → login lại với mã bước mới. Password và email thật không đưa vào command line, Git, Dockerfile hoặc repo.

Rollback dev rỗng: script `artifacts/p0-08-rollback.sql` sinh bằng `dotnet ef migrations script StaffIam InitialPlatform ...`; chỉ DROP bảy bảng IAM và xóa entry migration mới, giữ platform/history cũ. Đã test up → down về InitialPlatform → up, và idempotent script chạy hai lần trên PostgreSQL 17. **Sau có staff thật/go-live không chạy Down**: dừng writer, giữ IAM data và roll-forward fix; nếu bắt buộc restore phải dùng backup DB + key/certificate tương ứng, đánh giá mất thay đổi sau backup và hủy phiên cũ. Không thực thi script destructive trên dữ liệu thật trong task.

## Test plan local và kiểm chứng

| Nhãn local | Scenario/test coverage |
|---|---|
| T01/T02 | DB mới, đúng 7 bảng, 3 roles/45 permission claims độc lập với production constant, script lặp hai lần, model snapshot, down/up bảo toàn 9 bảng platform. |
| T03/T26 | Cookie host-only/Secure/HttpOnly/Strict; HTTPS/host gate, trusted forwarded proto; idle đúng 8h và sliding không vượt 7 ngày với clock điều khiển. |
| T04/T05 | 5 lần sai → khóa 15m, sát biên 14:59/15:00, failure ngoài rolling window, phiên cũ không sống lại. |
| T06/T07 | Unknown/inactive generic login errors; 5 dạng password yếu; email normalized trùng; reset yếu rollback cả role/stamp. |
| T08/T09 | First-password gate cho employee và bootstrap owner; không đổi sang password cũ; password change vô hiệu nhiều cookie cũ. |
| T10/T11/T12/T13 | Pending không kích hoạt/mã hóa; expiry; mã sai; owner Production restricted; mã đúng; replay/outdated và hai login đồng thời cùng code; không tự rotate verified secret. |
| T14/T15/T16 | Anonymous 401; customer cookie/Bearer không dùng cho Admin; 12 cases sales/content × 6 permissions staff.manage/costs.read/customers.read/catalog.write/inventory.write/orders.read. Probe endpoints chỉ có trong test assembly. |
| T17 | Nhiều role, role đổi revoke cookie cũ; role claim DB đổi được refresh mỗi request. |
| T18/T19 | CSRF thiếu/sai/token từ anonymous identity sau login bị chặn; cookie/session và mutation hợp lệ hoạt động; logout. |
| T20 | Request thứ 11 bị 429; spoof X-Forwarded-For/CF header không đổi bucket khi không trusted; peer trusted mới cho forwarded client IP phân bucket. |
| T21/T22 | Sales/content không list/create/patch/promote owner; last active owner không bị bỏ role/deactivate/lock; stale rowVersion 409; hai owner deactivation đồng thời chỉ một success. |
| T23 | Bootstrap repeat không overwrite/create thêm/promote, owner cũng first-change; entry point reject redirected input; CLI không được chạy như startup service. |
| T24 | Reset/deactivate revoke cookie; audit có staff actor; logs/audit không chứa password/email/displayName/TOTP secret/URI/code. |
| T25 | Toàn bộ tests audit/idempotency/outbox/UoW/schema/architecture hiện hữu. |
| T27 | OpenAPI endpoint/schema thật, generated TS client và frontend regression. |

## Kết quả thực chạy ngày 17/11/2026

| Command | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS, cả 7 projects; không thay package sau lượt locked restore. Lượt đầu thêm dependency dùng `--force-evaluate` để cập nhật lockfile rồi đã chạy locked-mode thật. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warnings/0 errors; build lại sau sửa bootstrap gate cũng PASS. |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger 'trx;LogFilePrefix=handoff' --results-directory artifacts/p0-08-handoff-tests` | PASS **261/261**: Unit 11, Architecture 34, Integration 216, zero skipped. 39 cases IAM; toàn bộ 177 integration cases hiện hữu vẫn pass. Log `artifacts/p0-08-handoff-tests.log`, 3 TRX trong thư mục kết quả. |
| `./scripts/check-test-results.ps1 -Directory artifacts/p0-08-handoff-tests` | PASS, kiểm đủ ba projects, không test rỗng/skipped. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS ở lượt cuối; có dùng `dotnet format ... --no-restore` để sửa format trước khi xác minh. |
| `./scripts/migrations.ps1` | PASS, idempotent SQL `artifacts/migrations.sql` 23.689 bytes. Đã đọc phần IAM: bảy CREATE TABLE + 3 roles/45 claims/index/constraints, không DROP/ALTER platform; không credential seed. |
| `dotnet ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS: `No changes have been made to the model since the last migration.` Connection design-time dummy trên port 1, không nối DB thật. |
| `dotnet ef migrations script StaffIam InitialPlatform --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --output artifacts/p0-08-rollback.sql` | PASS. Đã đọc rollback SQL; thực chạy down/up và lặp script trên PG17 ở T01/T02. |
| `corepack pnpm generate:api` | PASS, cập nhật `packages/api-client/openapi.json` và `src/generated/schema.ts`. |
| `corepack pnpm check:api` | PASS, cả sau lần sửa bootstrap cuối; contract/client current. |
| `corepack pnpm install --frozen-lockfile` | PASS, 4 workspace projects, không đổi pnpm lockfile. |
| `corepack pnpm lint` | PASS cả 3 packages/apps, max-warnings=0. |
| `corepack pnpm typecheck` | PASS cả 3 packages/apps. |
| `corepack pnpm test` | PASS 4 frontend scaffold tests (2 store + 2 admin), không coi đây là IAM UI tests. |
| `corepack pnpm build` | PASS api-client + hai Next apps. Không đổi frontend UI. |
| `docker build -f infra/Dockerfile.api -t teapremium-api:p0-08-local .` | PASS trên code cuối; image manifest `sha256:82c5fec5bfa7fc111295010f2eb41d91a582456dd8abee1272271461543b69f2`, Linux locked restore/build 0 warnings/errors. Log `artifacts/p0-08-docker-build.log`. |
| `dotnet run --project backend/src/TeaPremium.Api --no-launch-profile --no-build -- bootstrap-owner` với stdin redirect của tool | PASS negative check: exit **2** và thông báo yêu cầu terminal riêng tư, không truy cập DB. Positive/repeat flow và first-change test bằng command handler thật với DB17. Tương tác terminal ẩn mật khẩu của operator **NOT RUN** thủ công. |
| `git diff --check` | PASS sau sửa EOF và chuẩn hóa line endings của file task. |

Các lượt red ban đầu (model chưa rebuild, một test HTTP CSRF, biên cookie/SQL-script formatting, và bootstrap first-change) đã được sửa và chạy lại; không dùng log cũ fail làm bằng chứng PASS. Generated migration/snapshot đã được review local, không gọi đó là review độc lập.

**NOT RUN:** G4 độc lập, GitHub Actions cho snapshot local, production migration/deployment, kiểm quyền tp_app/tp_owner trên server thật, reverse proxy/browser flow production, và E2E Admin UI P0-12. Không có thao tác nào trên dữ liệu production. Không khẳng định compliance/pháp lý/performance đã được kiểm.

**P0/P1 blocker đã biết tại handoff: không có finding xác nhận còn mở từ lượt implementation/tests local.** Residual risks: recovery/rotation TOTP chưa có hợp đồng được duyệt (giữ deny, không bypass), certificate/key/proxy/DB permissions cần cấu hình và kiểm khi vận hành, và adapter ID migration dựa EF internal API cần regression khi upgrade. Đây chưa phải G4 PASS. Bước tiếp theo: reviewer session mới đọc toàn bộ local diff và untracked files ở chính worktree này; G3 dừng, không publish.

## Local inventory cuối G3

Branch `feature/p0-08-staff-iam`; HEAD/base và `origin/main` đều `72c637ab60f46e6367609c57ec114493b6ad675e`. **Staged: 0; unstaged tracked: 14; untracked: 16 files**. File generated/lockfile thay đổi đều trong scope backend/contract IAM; `docs/spec/**`, CI và infra production không có diff. Artifacts/logs Docker/TRX/SQL local ở `artifacts/` bị ignore, không đưa vào Git.

`git status --short`:

```text
 M backend/Directory.Packages.props
 M backend/src/TeaPremium.Api/Program.cs
 M backend/src/TeaPremium.Api/packages.lock.json
 M backend/src/TeaPremium.Infrastructure/Persistence/AppDbContext.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs
 M backend/src/TeaPremium.Infrastructure/Persistence/PlatformEntities.cs
 M backend/src/TeaPremium.Infrastructure/TeaPremium.Infrastructure.csproj
 M backend/src/TeaPremium.Infrastructure/packages.lock.json
 M backend/tests/TeaPremium.ArchitectureTests/packages.lock.json
 M backend/tests/TeaPremium.IntegrationTests/IdempotencyTests.cs
 M backend/tests/TeaPremium.IntegrationTests/PlatformDatabaseTests.cs
 M backend/tests/TeaPremium.IntegrationTests/packages.lock.json
 M packages/api-client/openapi.json
 M packages/api-client/src/generated/schema.ts
?? backend/src/TeaPremium.Api/Iam/
?? backend/src/TeaPremium.Application/Iam/
?? backend/src/TeaPremium.Infrastructure/Iam/
?? backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611170919_StaffIam.Designer.cs
?? backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611170919_StaffIam.cs
?? backend/src/TeaPremium.Infrastructure/Persistence/RepositoryMigrationIds.cs
?? backend/tests/TeaPremium.IntegrationTests/StaffIamTests.cs
?? docs/plans/p0-08-implementation.md
```

`git diff --stat origin/main` (chỉ tracked; Git không tính nội dung 16 file untracked trong stat này):

```text
 backend/Directory.Packages.props                   |   2 +
 backend/src/TeaPremium.Api/Program.cs              |  21 +-
 backend/src/TeaPremium.Api/packages.lock.json      |  20 +
 .../Persistence/AppDbContext.cs                    |  42 +-
 .../Migrations/AppDbContextModelSnapshot.cs        | 706 ++++++++++++++++++++-
 .../Persistence/PlatformEntities.cs                |   7 -
 .../TeaPremium.Infrastructure.csproj               |   5 +-
 .../TeaPremium.Infrastructure/packages.lock.json   | 147 +----
 .../packages.lock.json                             | 130 +++-
 .../IdempotencyTests.cs                            |   2 +-
 .../PlatformDatabaseTests.cs                       |   1 +
 .../TeaPremium.IntegrationTests/packages.lock.json |  90 ++-
 packages/api-client/openapi.json                   | 617 +++++++++++++++++-
 packages/api-client/src/generated/schema.ts        | 473 +++++++++++++-
 14 files changed, 2078 insertions(+), 185 deletions(-)
```

`git diff --cached --name-only`: rỗng. Các tracked files trong status trên đều unstaged. `git ls-files --others --exclude-standard` — reviewer phải đọc trực tiếp đủ 16 file này:

```text
backend/src/TeaPremium.Api/Iam/AdminEndpoints.cs
backend/src/TeaPremium.Api/Iam/AdminSecurity.cs
backend/src/TeaPremium.Api/Iam/BootstrapOwnerConsole.cs
backend/src/TeaPremium.Application/Iam/StaffContracts.cs
backend/src/TeaPremium.Application/Iam/StaffPermissions.cs
backend/src/TeaPremium.Infrastructure/Iam/IamModel.cs
backend/src/TeaPremium.Infrastructure/Iam/IamRegistration.cs
backend/src/TeaPremium.Infrastructure/Iam/ProtectedStaffStore.cs
backend/src/TeaPremium.Infrastructure/Iam/StaffIdentity.cs
backend/src/TeaPremium.Infrastructure/Iam/StaffTotp.cs
backend/src/TeaPremium.Infrastructure/Iam/StaffUser.cs
backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611170919_StaffIam.Designer.cs
backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611170919_StaffIam.cs
backend/src/TeaPremium.Infrastructure/Persistence/RepositoryMigrationIds.cs
backend/tests/TeaPremium.IntegrationTests/StaffIamTests.cs
docs/plans/p0-08-implementation.md
```

## Remediation P2 G4 — metadata success 204 (17/11/2026)

G4 độc lập trước đó kết luận PASS, còn một P2: POST `auth/logout`, `auth/change-password`, `auth/totp/verify` thực trả 204 nhưng OpenAPI/client khai 200. Owner giao sửa đúng finding này trên cùng branch local. G0: vẫn `feature/p0-08-staff-iam`, HEAD/base `72c637ab60f46e6367609c57ec114493b6ad675e`, staged rỗng. G1/G2 tái sử dụng baseline phía trên; không có spec drift (`git diff 0bfc98e HEAD -- docs/spec` rỗng), không đổi kiến trúc hoặc migration. Traceability: P0-08, `05-api.md` §1/§4.1, FR-IAM-02/03, BR-IAM-06, NFR-07 và AC-P0-03 cho các đường password/TOTP/CSRF đang được hồi quy.

Chỉ năm file đổi so với snapshot G4:

- `backend/src/TeaPremium.Api/Iam/AdminEndpoints.cs`: thêm `.Produces(StatusCodes.Status204NoContent)` và `.ProducesProblem(400/401/403)` cho đúng ba endpoint. Giữ nguyên handler, `Results.NoContent()`, sign-out, authorization và error mapping.
- `backend/tests/TeaPremium.IntegrationTests/StaffIamTests.cs`: thêm theory T27 với ba cases, dùng Identity/cookie/CSRF thật và PostgreSQL 17. Đối chiếu runtime status 204 với OpenAPI sinh thật, body runtime rỗng, schema 204 không khai content, không còn success 200; anonymous vẫn 401 và authenticated thiếu CSRF vẫn 400 ProblemDetails. Kiểm error metadata 400/401/403 còn đúng media type.
- `packages/api-client/openapi.json` và `packages/api-client/src/generated/schema.ts`: regenerate từ API, không sửa generated output bằng tay. Ba operation có status `204,400,401,403`, 204 không có body.
- Hồ sơ này: ghi remediation và bằng chứng; không tự nhận focused G4 đã PASS.

Fingerprint SHA256 so với snapshot trước sửa xác nhận các file IAM logic, migrations/snapshot, UoW/audit/outbox, dependencies và file khác không đổi. Không thay DDL; rollback chỉ cần hoàn nguyên metadata/tests của finding rồi regenerate contract, không chạy migration hoặc khôi phục DB.

| Command thực chạy | Kết quả |
|---|---|
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-restore --filter FullyQualifiedName~T27AuthNoContentRuntimeMatchesGeneratedContract` | RED đúng mục tiêu trước sửa metadata: 3 FAIL vì contract thiếu runtime status 204; runtime/body và negative checks trước assertion đã đúng. Log `artifacts/p0-08-p2-red.log`. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warnings/errors. |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~StaffIamTests --logger 'trx;LogFilePrefix=p2-focused' --results-directory artifacts/p0-08-p2-focused` | PASS **42/42**, 0 failed/skipped; log `artifacts/p0-08-p2-focused.log`. |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger 'trx;LogFilePrefix=p2-full' --results-directory artifacts/p0-08-p2-full` | PASS **264/264**: Unit 11, Architecture 34, Integration 219; 0 failed/skipped. Log `artifacts/p0-08-p2-full.log`. |
| `./scripts/check-test-results.ps1 -Directory artifacts/p0-08-p2-full` | PASS, đủ 3 projects, không test rỗng/skipped. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS, không cần format sửa source. |
| `corepack pnpm generate:api` | PASS; JSON và TS được cập nhật đúng metadata. |
| `corepack pnpm check:api` | PASS, generated contract/client current. |
| `corepack pnpm --filter @teapremium/api-client typecheck` | PASS. |
| `git diff --check` | PASS. |

Không có P0/P1 mới được xác nhận trong phạm vi sửa. P2 đã được implement và kiểm chứng local, **focused G4 độc lập NOT RUN**, không tự đóng bằng reviewer approval. GitHub Actions/production và Docker/frontend full không chạy lại cho bản metadata này; kết quả G3/G4 lịch sử phía trên chỉ áp dụng snapshot cũ. Inventory vẫn 14 tracked modified + 16 untracked, staged 0; stat Git của contract thay đổi nên số dòng G3 phía trên là lịch sử. **READY_FOR_FOCUSED_G4**, dừng; không commit/push/PR/merge.

## Kết quả G4 độc lập, focused re-review và handoff cho owner

Cập nhật 17/11/2026, sau các mốc G3/remediation lịch sử phía trên. Review bằng context reviewer độc lập với implementer, trên cùng worktree local; không dùng kết luận tự review của implementer. G4 đọc đủ 14 tracked changes và toàn bộ 16 untracked files, đối chiếu FR-IAM-01/02/03, BR-IAM-01/02/06, NFR-07, AC-P0-01/02/03, DEC-14/15/16, API/schema, transaction/audit và permission matrix. Không audit lại G1/G2 vì không có spec drift.

### Findings và remediation

- **G4 toàn P0-08: PASS**, không phát hiện P0/P1 có thể hành động trong phạm vi review. Có một **P2**: logout/change-password/totp/verify trả 204 nhưng OpenAPI/client khai 200; test cũ chỉ kiểm path/schema. Finding này không bị giấu trong kết quả PASS.
- Owner giao sửa P2; thêm metadata 204 + ProblemDetails 400/401/403 cho đúng ba endpoint, regenerate JSON/TS, thêm ba test thực với PostgreSQL 17/cookie/CSRF, kiểm status và body rỗng. Red phase ba test FAIL đúng lỗi contract trước sửa; sau sửa đều PASS.
- **Focused G4: PASS, P2 đã được xác minh đóng**, không có finding mới. Reviewer xác nhận runtime 204/body rỗng, contract chỉ success 204, JSON/TS đồng bộ, lỗi anonymous 401 và thiếu CSRF 400 giữ nguyên. Hoàn nguyên riêng remediation trong bộ nhớ rồi so SHA256 khôi phục đúng endpoint/tests/JSON/TS của snapshot G4 cũ; 25 file còn lại không đổi, bao gồm security logic và migrations.
- Fingerprint 30 file trước/sau focused review không đổi. Trước commit, kiểm lại cả untracked, scope và secrets: chỉ có credential tổng hợp trong tests, không có secret thật/default owner password, .env, certificate/private key hoặc artifact DB được đưa vào commit. Không còn P0/P1 đã xác nhận hoặc P2 chưa remediation trong phạm vi hai lượt review.

### Lệnh reviewer thực chạy và kết quả

| Command | Kết quả độc lập |
|---|---|
| `dotnet test backend/TeaPremium.sln --no-restore --logger 'trx;LogFilePrefix=g4' --results-directory artifacts/p0-08-independent-g4` | G4 ban đầu PASS **261/261**: Unit 11, Architecture 34, Integration 216; 0 failed/skipped. |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-restore --filter FullyQualifiedName~T27AuthNoContentRuntimeMatchesGeneratedContract --logger 'trx;LogFilePrefix=g4focused' --results-directory artifacts/p0-08-independent-g4-focused` | Focused review PASS **3/3**, 0 failed/skipped. |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger 'trx;LogFilePrefix=g4focusedfull' --results-directory artifacts/p0-08-independent-g4-focused-full` | Focused review full regressions PASS **264/264**: Unit 11, Architecture 34, Integration 219; 0 failed/skipped. |
| `./scripts/check-test-results.ps1 -Directory artifacts/p0-08-independent-g4` | PASS đủ ba test projects, không rỗng/skipped. |
| `./scripts/check-test-results.ps1 -Directory artifacts/p0-08-independent-g4-focused-full` | PASS đủ ba test projects, không rỗng/skipped. |
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS trong G4 ban đầu. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS trong cả hai lượt; 0 warnings/errors. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS trong cả hai lượt. |
| `corepack pnpm check:api` | PASS trong cả hai lượt; sau sửa metadata JSON/TS đúng 204, không chỉ đồng bộ một contract sai. |
| `corepack pnpm --filter @teapremium/api-client typecheck` | PASS trong focused review. |
| `dotnet ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS trong G4 ban đầu sau khi cấp connection design-time dummy port 1; không truy cập DB production. |
| `git diff --check` | PASS trong cả hai lượt và trước commit. |

Các integration tests dùng PostgreSQL 17 Testcontainers thật và WebApplicationFactory/Identity/cookie/CSRF thật, gồm migration từ rỗng, idempotent SQL và up/down/up, exact roles/claims, lockout/clock, password/TOTP gates và replay, revocation, permissions, last-owner concurrency, trusted proxy/rate limit, bootstrap, audit/PII và hồi quy idempotency/outbox/UoW. Không dùng mocks làm bằng chứng tính đúng PostgreSQL.

Lượt EF check đầu tiên của G4 thiếu biến cấu hình nên exit 1; reviewer cấp dummy connection rồi chạy lại PASS. Script đối chiếu SHA đầu tiên của focused review giả định CRLF nên dừng; chạy lại với LF/CRLF xác nhận đủ hash khớp. Đây là lỗi setup/check script đã xử lý, không phải test sản phẩm chưa sửa. Các log/TRX/SQL nằm trong artifacts local bị ignore, không commit; bảng trên ghi lại command/count/verdict để owner kiểm tra trên nhánh.

### Chưa chạy và giới hạn bằng chứng

- GitHub Actions cho feature HEAD: **NOT RUN** ở thời điểm ghi hồ sơ; workflow hiện chạy khi PR vào main hoặc push main. Push feature branch riêng không phải bằng chứng CI PASS.
- Production migration/deployment, quyền DB production, browser/reverse proxy production, CLI nhập password ẩn thủ công: **NOT RUN**. Docker build và frontend full đã PASS ở G3 trước remediation; không chạy lại trong focused G4 metadata-only.
- Recovery/rotation TOTP chưa có hợp đồng được owner duyệt; hiện giữ deny, không cung cấp bypass. Certificate/key ring/trusted proxy/DB privileges phải được cấu hình và kiểm khi vận hành.
- SEO, customer accounts và Admin UI P0-12 không thuộc scope P0-08. Review PASS không có nghĩa toàn ứng dụng không còn bug.

### Commit/push được owner cho phép

Code commit: `4f39d48a79141494cb18592c31cf58caadaae6e3` — `feat(iam): implement P0-08 staff authentication and authorization`, parent `72c637ab60f46e6367609c57ec114493b6ad675e`. Commit chứa đúng snapshot đã review: 30 files, +5679/−185; worktree sạch sau commit. Cập nhật hồ sơ review này là commit tài liệu riêng, không thay runtime/migration. Owner đã yêu cầu push `feature/p0-08-staff-iam` để tự kiểm tra; **không mở PR, không merge** trong yêu cầu này.
