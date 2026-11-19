# P1-02 — Local G3 evidence, 19/11/2026

**G3 recommendation: P2-1 CLOSED; P2-2 giữ CLOSED; READY_FOR_FOCUSED_G4: YES.**
Final actual R2 + HTTPS browser/API/PostgreSQL acceptance: **199 PASS, 0 FAIL**, exit **0**.
Ba mục NOT RUN được tách rõ: independent domain-settings read, lifecycle read và live
fault injection (controlled fake/fault tests PASS riêng). Đây là đề xuất G3, không G4 approval.
Không có quyền publish; xem phần final acceptance cuối file (thay trạng thái blocker cũ).

**G3 ban đầu: READY_FOR_LOCAL_G4: YES.** G4 sau đó **CONDITIONAL** theo bằng chứng
reviewer do owner cung cấp: 631/631 PASS, không actionable P0/P1, còn P2-1/P2-2.
Đây là **OWNER-PROVIDED REVIEWER EVIDENCE**, không phải phiên này tự chạy G4.
Review depth **A** (auth/PII); remediation hiện hành ở phần cuối.
Không commit, push, PR, merge hay deploy. Một main thread, không subagent.

- Repository: `jimthanhnobi/tra-hien`.
- Base = HEAD = latest fetched `origin/main`: `a9f0bba0df6c42610d88481724374cc12402adf6`.
- Branch: `feature/p1-02-media-r2`; toàn bộ delta chưa commit, staged rỗng.
- Targeted G1/G2 **GO local/test**, dependency P1-01 đã merge; không có blocker local.
  Coverage/references/authorization matrix/rollback: [task record](p1-02-implementation.md).
- Baseline [merge CI](https://github.com/jimthanhnobi/tra-hien/actions/runs/44463204951)
  completed/success đúng SHA, ba required jobs PASS. CI delta local **NOT RUN**.
- `docs/spec/**`, P1-01 entities/model/migrations/grants giữ nguyên; không migration mới.

## Verification thực tế

**Full Linux backend: 631/631 PASS, 0 failed, 0 skipped** = 72 unit + 34 architecture
+ 525 integration. Trong đó 90 test media mới (27 unit/63 integration). Không cộng
focused vào full. Windows focused filter `Media` gồm cả test baseline có tên media.

Linux runner dùng .NET SDK **10.0.404**, Ubuntu/FFmpeg **6.1.1-3ubuntu5**, PostgreSQL 17
Testcontainers, DB một lần, runtime đăng nhập thật `tp_app`. Toàn bộ source backend,
tests, scripts và spec trong snapshot Linux khớp worktree khi chuẩn hóa LF; có manifest
SHA-256 ở `artifacts/p1-02/linux-source-sha256.json`. Không copy bin/obj/secrets vào snapshot.

| Lệnh | Exit / kết quả | Evidence local |
|---|---|---|
| `dotnet restore backend/TeaPremium.sln --force-evaluate` | 0; cập nhật lock cho AWSSDK.S3 4.1.2, không đổi package cũ | lockfiles diff |
| `dotnet restore backend/TeaPremium.sln --locked-mode` | 0 cả Windows/Linux | `linux/restore.log` |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | 0; zero warnings/errors cả hai OS | `build.log`, `linux/build.log` |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | 0 cả hai OS | `format.log`, `linux/format.log` |
| `dotnet test backend/tests/TeaPremium.UnitTests --no-restore --filter 'FullyQualifiedName~Media' --logger 'trx;LogFilePrefix=p1-02-unit' --results-directory artifacts/test-results` | 0; 27/27 | `artifacts/test-results/p1-02-unit_*.trx` |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-restore --filter 'FullyQualifiedName~Media' --logger 'trx;LogFilePrefix=p1-02-focused' --results-directory artifacts/p1-02/focused` | 0; 80/80, không skip | `focused/*.trx` |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/test-results` (Linux runner) | 0; **631/631**, không skip | `linux/tests.log`, `linux/test-results/*.trx` |
| `pwsh -NoProfile -File scripts/check-test-results.ps1 -Directory /evidence/test-results` | 0; từng assembly zero skipped | `linux/test-gate.log` |
| `dotnet list backend/TeaPremium.sln package --vulnerable --include-transitive` | 0 cả hai OS; feed không báo vulnerable packages | `packages-windows.log`, `linux/packages.log` |
| `corepack pnpm install --frozen-lockfile` | 0 | `pnpm-install.log` |
| `corepack pnpm generate:api`, `corepack pnpm check:api` | 0; thêm đúng ba endpoint, output current | `api-check.log`, generated diff |
| `corepack pnpm --filter @teapremium/api-client typecheck`, `corepack pnpm --filter @teapremium/api-client lint` | 0 cả hai | `client-types.log`, `client-lint.log` |
| `corepack pnpm typecheck` | 0 cho api-client, admin, store | `typecheck-all.log` |
| `python scripts/test-ci-fastpath.py` | 0; 23/23 | `ci-classifier.log` |
| `artifacts/actionlint/actionlint.exe` | 0 | `actionlint.log` |
| `docker build -f infra/Dockerfile.api -t tra-hien-api:p1-02-local .` | 0 tại source cuối; apt FFmpeg + locked restore/build | `docker-build-final.log` |
| `git diff --check origin/main`, `git diff --cached --check` | 0 | kiểm lại khi bàn giao |

Paths evidence không có prefix ghi trong bảng nằm dưới `artifacts/p1-02/`, được gitignore.
Windows focused dùng executable local đã tải trong artifacts, không commit binary tool:

```powershell
$env:MediaStorage__FfmpegPath=(Resolve-Path 'artifacts/media-tools/ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe').Path
$env:MediaStorage__FfprobePath=(Resolve-Path 'artifacts/media-tools/ffmpeg-9.0.2-essentials_build/bin/ffprobe.exe').Path
```

Full regression chạy bằng `sh /run.sh` trong `tra-hien-p1-02-tests:local`, image bổ sung
Docker CLI từ local runner đã có vào API image; script/snapshot/Dockerfile được giữ ở
`artifacts/p1-02/`. Lệnh orchestration thực tế, exit 0:

```powershell
docker build -f artifacts/p1-02/Dockerfile.runner -t tra-hien-p1-02-tests:local .
$mediaTaskRoot=(Resolve-Path artifacts/p1-02).Path
docker run --rm --name tra-hien-p1-02-regression --mount "type=bind,source=$mediaTaskRoot\linux-source.tar,target=/source.tar,readonly" --mount "type=bind,source=$mediaTaskRoot\linux-run.sh,target=/run.sh,readonly" --mount "type=bind,source=$mediaTaskRoot\linux,target=/evidence" -v /var/run/docker.sock:/var/run/docker.sock -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal tra-hien-p1-02-tests:local sh /run.sh
```

## Phân loại evidence — sửa P2-2

| Phạm vi | Kết quả |
|---|---|
| Fake storage + real API/IAM/CSRF/PG17/full decoder | PASS: sáu format, limits, missing/wrong key/metadata/size/spoof/truncated, expired, repeat/concurrent, creator/purpose/revocation, private list/GET, failure/recovery/orphans/logs |
| AWS SDK presign offline | PASS: tạo URL PUT/private GET, expiry 600s, signed Content-Type, endpoint/bucket lựa chọn. Không gửi HTTP; không chứng minh R2 chấp nhận chữ ký |
| SDK method override/stub | PASS: nhánh adapter, arguments If-Match/If-None-Match, kiểm byte stream/checksum và xử lý exception. `StubS3` override `GetObjectMetadataAsync`, `GetObjectAsync`, `PutObjectAsync` trong R2MediaStorageTests; **không** kiểm HTTP serialization, signing, socket/transport hoặc semantics thực của R2 |
| Real R2 HTTP transport | G3 ban đầu NOT RUN. Smoke sau G4 do owner báo ở dưới; không coi SDK method overrides là transport verification |
| Browser CORS + Media API end-to-end dùng R2 | G3 ban đầu NOT RUN; OPTIONS-only không chứng minh browser PUT hoặc intent → complete |

Không coi config checks, fake hoặc URL ký offline là provider PASS. Có checklist và cấu
hình dev/prod/secret riêng tại [runbook R2](../runbooks/media-r2.md). Live acceptance
bắt buộc trước ảnh/dữ liệu thật. Không kiểm load/performance, production HTTPS/owner/
backup/restore hay pháp lý từ task này; không claim production-ready.

## Finding đã sửa và giới hạn còn lại

- Red tests ban đầu phát hiện slug regex chấp nhận newline cuối chuỗi; đổi anchor sang
  `\A...\z`, case đó PASS. Không bỏ test.
- Repeat response từng lệch fractional ticks sau PostgreSQL lưu microseconds; reload
  asset trước response đầu, first/retry/concurrent/recovery cùng metadata PASS.
- Failure injection DB phát hiện EF Update logger chứa exception text của provider;
  chặn Command/Update/Query categories, giữ safe middleware/status/trace logs. Test bắt
  log dùng marker tổng hợp và signed fake URL PASS; không giảm assertion để né leak.
- Không còn failure/blocker local đã biết. Live provider, independent G4 và CI delta
  chưa publish vẫn NOT RUN. Creator-scoped evidence là ranh giới hiện tại; tích hợp
  return/order authorization và chia sẻ qua đối tượng thuộc slice đổi trả sau này.

## Inventory đầy đủ (42 file sau remediation)

17 tracked modified + 25 untracked, không staged. Reviewer cần đọc cả untracked;
`git diff origin/main` riêng lẻ không bao gồm chúng. Các file generated vẫn được giữ.

```text
.dockerignore
.github/workflows/ci.yml
backend/Directory.Packages.props
backend/src/TeaPremium.Api/Iam/AdminSecurity.cs
backend/src/TeaPremium.Api/Media/MediaEndpoints.cs
backend/src/TeaPremium.Api/Middleware/ProblemExceptionMiddleware.cs
backend/src/TeaPremium.Api/Program.cs
backend/src/TeaPremium.Api/appsettings.Development.json
backend/src/TeaPremium.Api/appsettings.json
backend/src/TeaPremium.Api/packages.lock.json
backend/src/TeaPremium.Application/Media/IMediaStorage.cs
backend/src/TeaPremium.Application/Media/MediaContracts.cs
backend/src/TeaPremium.Infrastructure/Media/EfMediaWorkflow.cs
backend/src/TeaPremium.Infrastructure/Media/ExpireUploadSessions.cs
backend/src/TeaPremium.Infrastructure/Media/FakeMediaStorage.cs
backend/src/TeaPremium.Infrastructure/Media/FfmpegContentValidator.cs
backend/src/TeaPremium.Infrastructure/Media/MediaRegistration.cs
backend/src/TeaPremium.Infrastructure/Media/MediaStorageOptions.cs
backend/src/TeaPremium.Infrastructure/Media/R2MediaStorage.cs
backend/src/TeaPremium.Infrastructure/TeaPremium.Infrastructure.csproj
backend/src/TeaPremium.Infrastructure/packages.lock.json
backend/tests/TeaPremium.ArchitectureTests/packages.lock.json
backend/tests/TeaPremium.IntegrationTests/Fixtures/media/README.md
backend/tests/TeaPremium.IntegrationTests/Fixtures/media/valid.avif
backend/tests/TeaPremium.IntegrationTests/Fixtures/media/valid.jpg
backend/tests/TeaPremium.IntegrationTests/Fixtures/media/valid.mov
backend/tests/TeaPremium.IntegrationTests/Fixtures/media/valid.mp4
backend/tests/TeaPremium.IntegrationTests/Fixtures/media/valid.png
backend/tests/TeaPremium.IntegrationTests/Fixtures/media/valid.webp
backend/tests/TeaPremium.IntegrationTests/MediaApiTests.cs
backend/tests/TeaPremium.IntegrationTests/MediaContentTests.cs
backend/tests/TeaPremium.IntegrationTests/P009Harness.cs
backend/tests/TeaPremium.IntegrationTests/R2MediaStorageTests.cs
backend/tests/TeaPremium.IntegrationTests/packages.lock.json
backend/tests/TeaPremium.UnitTests/MediaValidationTests.cs
docs/plans/p1-02-implementation.md
docs/plans/p1-02-local-evidence.md
docs/runbooks/media-r2.md
infra/Dockerfile.api
packages/api-client/openapi.json
packages/api-client/src/generated/schema.ts
packages/api-client/src/media-contract.test-d.ts
```

Rollback: không có DDL mới; xem task record, giữ dữ liệu/objects và deploy lại binary/config
base khi owner thực sự cho triển khai. Next safe step: independent `$review-gate` tầng A
trong session mới, **cùng worktree**, full committed/staged/unstaged/untracked so origin/main.
Dừng local G3 tại đây; chưa có publish permission.

## Remediation P2-1/P2-2 — 19/11/2026

Owner cho focused remediation và real R2 trên bucket development đã cấu hình rõ,
synthetic fixtures + disposable PG17; không đổi bucket policies/public access/CORS/
lifecycle/permissions, không commit/push/PR/merge/deploy. Fetch vẫn xác nhận expected
main `a9f0bba0df6c42610d88481724374cc12402adf6`; đủ 41 file G3, staged rỗng.
Manifest đầu phiên: `artifacts/p1-02/remediation/source-before.json`.

**P2-2: CLOSED về correction tài liệu, chờ focused independent G4 xác nhận.** Đã đối
chiếu trực tiếp `R2MediaStorageTests.cs`; thay thuật ngữ sai bằng năm loại evidence
trong bảng phía trên. Không sửa/giảm tests để nhận transport PASS.

**Owner-reported real smoke** (không phải phiên này thực thi): SDK PUT/HEAD/GET và
presigned PUT/GET PASS; OPTIONS allowed origin 204/denied origin 403; expired signed
URLs và synthetic cleanup PASS; một anonymous probe trả 401. Owner báo **17 checks,
0 FAIL**, không suy rằng mọi mục đều PASS hoặc mọi public domain đã được kiểm.
Script được owner chỉ ra là `r2_dev_smoke.py`, dùng boto3/requests; chứng cứ này không
thay thế end-to-end với adapter AWSSDK.S3 của ứng dụng hoặc browser thật.

**P2-1: OPEN. READY_FOR_FOCUSED_G4: NO** cho closure toàn bộ hai finding.
P2-2 đã sửa nhưng live acceptance còn thiếu các điều kiện dưới đây; chưa self-approve G4.

### Cấu hình owner và blockers đã xác minh

Owner chỉ định `teapremium-media-dev` tạm dùng cho staging/evidence dev, xác nhận đã
tắt Public Development URL và đặt CORS; Admin origin `http://localhost:3001`. Đây là
owner-attested configuration, không tự coi là policy/domain verification.

- `appsettings.Development.json` thêm **chỉ** Environment/Endpoint/Private:Bucket không
  bí mật. Bỏ PublicBaseUrl/r2.dev cũ và không dùng cùng bucket làm public. Provider kế
  thừa Disabled. Không copy credentials từ chat vào file, tool command, Git hoặc evidence.
- `MediaStorageOptions.Validate()` + `MediaRegistration` bắt đầy đủ cấu hình public và
  private, bucket/Access Key ID phải khác nhau; evidence-only vẫn gọi cùng validation.
  **Một private bucket hiện tại không đủ chạy Media API qua DI thật**. Không tạo bucket,
  sửa policy/permissions/CORS/lifecycle hoặc bypass validation; không dùng public config
  hay credential giả để đánh dấu end-to-end PASS.
- HTTP GET `/`, `/api/v1/admin/auth/csrf`, `/api/v1/admin/media` trên origin owner chỉ
  định đều không kết nối được (HTTP status không có, WebException); kiểm TCP không thấy
  listener 3001, Docker không có Admin port 3001 đang publish. Không đụng API/DB local
  khác đang chạy vì chưa chứng minh disposable.
- HEAD root domain r2.dev owner từng cung cấp trả **401**, không gửi credentials. Đây
  chỉ là anonymous root probe; chưa kiểm existing evidence key, mọi Custom Domain hoặc
  toàn bộ policy/private routes. Không kết luận private PASS từ 401 ở root.
- Secret-input process chưa cung cấp cấu hình/credentials hợp lệ để chạy acceptance.
  Không có real R2 write/delete, không có test object remote cần dọn trong phiên này.

### Ma trận acceptance hiện hành

| Case | Local fake / method stub | Live HTTP / browser phiên này |
|---|---|---|
| Admin thật ở origin owner chỉ định | Không áp dụng | **FAIL environment readiness**: chưa có listener 3001 |
| Permitted Admin login + lấy CSRF | PASS trong focused integration | **NOT RUN**, chưa start API R2/disposable network host |
| Upload-intent HTTP + session persisted | PASS | **NOT RUN**, split configuration chưa đủ |
| PUT URL do Media API cấp → R2; HEAD đúng bucket/object | PASS fake/args stub, không transport | **NOT RUN** |
| Complete → asset persisted, session completed, metadata | PASS | **NOT RUN** |
| Repeated/concurrent complete, không duplicate | PASS | **NOT RUN** |
| GET library có permitted metadata | PASS | **NOT RUN** |
| Missing/invalid CSRF | PASS | **NOT RUN** trên real-R2 API |
| Unauthorized purpose | PASS | **NOT RUN** trên real-R2 API |
| Expired session | PASS | **NOT RUN** trên real-R2 API |
| Wrong MIME/content; byte-size mismatch | PASS | **NOT RUN** trên real-R2 API |
| Missing storage object | PASS | **NOT RUN** trên real-R2 API |
| Permission revoked; wrong user complete | PASS | **NOT RUN** trên real-R2 API |
| Storage failure/retry, DB rollback/recovery (controlled injection) | PASS | **NOT RUN** trên real-R2 API |
| Cleanup/orphan handling | PASS | **NOT RUN** trên real R2; không chạy cleanup bucket rộng |
| Browser allowed origin actual signed PUT/headers/method | Không áp dụng | **NOT RUN**; OPTIONS smoke không thay browser PUT |
| Browser disallowed origin | Không áp dụng | **NOT RUN** |
| Browser progress/error không lộ credentials; browser complete workflow | Không áp dụng | **NOT RUN** |
| Anonymous existing private evidence/staging object | Không áp dụng | **NOT RUN**; root probe 401 là kiểm tra hẹp riêng |
| Authorized signed GET + expired signed GET | Offline presign PASS | **NOT RUN phiên này**; owner báo smoke PASS riêng |
| r2.dev disabled + không Custom Domain public cho private bucket | Không áp dụng | **NOT VERIFIED** độc lập; owner attests r2.dev đã tắt, Custom Domains chưa đọc |
| Negative cross-bucket token scope | Config mismatch tests PASS, không scope thực | **NOT RUN**; chưa có bucket thứ hai cụ thể được cấu hình/duyệt |
| Current lifecycle / abort multipart 7 ngày / expiration scope | Không áp dụng | **NOT VERIFIED**; chưa đọc actual bucket lifecycle |

Lifecycle: tài liệu Cloudflare nói có default multipart-abort rule 7 ngày, nhưng owner
đã xóa rule nên không suy trạng thái hiện tại. Owner cần read-only evidence Enabled/
AbortIncompleteMultipartUpload/filter/Expiration; không tạo expiration toàn bucket.
Xem [runbook lifecycle](../runbooks/media-r2.md#lifecycle--kiểm-read-only-không-khôi-phục-rule-tự-động).

### Delta remediation và kiểm tra

So với manifest đầu phiên, chỉ ba file đổi: `appsettings.Development.json`, evidence
này và `docs/runbooks/media-r2.md`. Không đổi C# implementation/tests, API contract,
DDL/migration/grants/spec; giữ nguyên 41 file G3 và thêm một file config vào delta main.
Helpers/evidence bỏ qua Git trong `artifacts/p1-02/remediation/` không chứa credentials.

Focused mới: locked restore exit 0, build warnings-as-errors exit 0 (zero warnings),
unit Media **27/27 PASS** và integration filter Media **80/80 PASS**, không skip.
Exact commands:

```text
dotnet restore backend/TeaPremium.sln --locked-mode
dotnet build backend/TeaPremium.sln --no-restore -warnaserror
dotnet test backend/tests/TeaPremium.UnitTests --no-build --no-restore --filter FullyQualifiedName~Media --logger trx --results-directory artifacts/p1-02/remediation/unit
dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~Media --logger trx --results-directory artifacts/p1-02/remediation/integration
```

Logs tương ứng `remediation/{restore,build,unit,integration}.log`, TRX ở unit/integration.
**Full regression sau config: exit 0, 631/631 PASS, zero failed/skipped** = 72 unit,
34 architecture, 525 integration. Runner Linux SDK 10.0.404, FFmpeg 6.1.1-3ubuntu5,
PostgreSQL 17 disposable; runtime tp_app trong integration tests, không DB production.
Snapshot backend/scripts/spec khớp worktree khi chuẩn hóa LF. Restore locked, build
warnings-as-errors, format verify, zero-skipped gate và NuGet vulnerable scan trong
runner đều exit 0. Feed không báo vulnerable packages. Logs/TRX mới ở
`artifacts/p1-02/remediation/linux/`; không dùng kết quả G3 cũ thay lần chạy này.

Orchestration thực tế (exit 0):

```powershell
$mediaTaskRoot=(Resolve-Path artifacts/p1-02/remediation).Path
$runnerScript=(Resolve-Path artifacts/p1-02/linux-run.sh).Path
docker run --rm --name tra-hien-p1-02-remediation-regression --mount "type=bind,source=$mediaTaskRoot\linux-source.tar,target=/source.tar,readonly" --mount "type=bind,source=$runnerScript,target=/run.sh,readonly" --mount "type=bind,source=$mediaTaskRoot\linux,target=/evidence" -v /var/run/docker.sock:/var/run/docker.sock -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal tra-hien-p1-02-tests:local sh /run.sh
```

Script giữ các lệnh exact ở bảng G3: locked restore/build/format, `dotnet test
backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory
/evidence/test-results`, test-results gate và `dotnet list ... package --vulnerable
--include-transitive`. Không delete/skip/weaken test. OpenAPI/client regeneration
**NOT APPLICABLE** trong delta remediation vì contract không đổi; OpenAPI regression
tests vẫn chạy trong full suite. `git diff --check origin/main` và `git diff --cached
--check`: exit 0. Kiểm UTF-8/fences/whitespace/local links/inventory 42 file và config
không chứa credential fields: PASS. Staged rỗng; base/HEAD/main vẫn a9f0bba….

**Bàn giao:** P2-2 correction CLOSED, cần reviewer độc lập xác nhận; P2-1 OPEN,
**READY_FOR_FOCUSED_G4: NO** để đóng cả hai finding. Next safe step: owner cấu hình
public dev bucket/token riêng theo contract hiện hành, xác minh private bucket domains/
lifecycle và cho chạy Admin qua HTTPS đúng flow bảo mật; credentials mới chỉ qua local
secret input. Sau đó mới tiếp tục actual R2/Media API/browser acceptance. Không tự đổi
Cloudflare hoặc hạ HTTPS/bucket isolation. Không commit/push/PR/merge/deploy.

## Final G3 acceptance — hai bucket và HTTPS, 19/11/2026

Phần này cập nhật trạng thái hiện hành; các blocker một bucket/HTTP origin ở phần
remediation trước là lịch sử. Owner xác nhận **P2-2 CLOSED**, không mở lại finding đó.
P2-1 **recommend CLOSED** sau final live provider acceptance; **READY_FOR_FOCUSED_G4: YES**.
Independent reviewer quyết định finding closure; không có quyền publish.

### Baseline và delta

`git fetch origin --prune`: exit 0; branch `feature/p1-02-media-r2`, HEAD và origin/main
cùng `a9f0bba0df6c42610d88481724374cc12402adf6`. Không spec diff, không migration mới,
staged rỗng. Reuse targeted G1/G2 GO và CI baseline đã ghi phía trên; không nhận là
CI chạy cho local delta. Inventory đầy đủ vẫn là 42 file ở mục Inventory.

So với `artifacts/p1-02/final-live/source-before.json`, delta phiên này là
`backend/src/TeaPremium.Api/appsettings.Development.json` (thêm tên public bucket dev),
evidence này và `docs/runbooks/media-r2.md`. Không đổi C# sản phẩm, tests, DDL, grants,
API contract hoặc frontend. Helpers mới chỉ trong ignored `artifacts/p1-02/final-live/`.
Provider mặc định vẫn Disabled; không lưu credentials. PublicBaseUrl dev được owner
cung cấp là `https://pub-0606f1f4f4fe427a97b402931548100f.r2.dev`; GET actual object
đúng bytes tại domain này đã **200 PASS** ở live attempt 02.

### Runtime đã thực thi, phân biệt với R2 live

- DNS `admin.localhost` trả `127.0.0.1` sau owner cập nhật hosts và flush DNS.
- Native Caddy **2.11.9** tải từ official GitHub release, khớp SHA-512 của release;
  dùng nguyên `infra/caddy/Caddyfile.admin-local`, upstream API 5080/Admin 3001.
- Owner đã trust local CA trong CurrentUser Root. Playwright Chromium mở
  `https://admin.localhost:8443/login` nhận **200**, không ignoreHTTPSErrors, không
  certificate-error/SPKI bypass. TLS request lúc API chưa khởi động nhận 502 chỉ
  chứng minh TLS/proxy connectivity, không được tính API health PASS.
- Next dev loopback 3001; API thật Kestrel bind `127.0.0.1:5080` trong harness.
  `/api/v1/admin/auth/csrf` qua Caddy **200**, unauthenticated Media **401**.
  Program hiện không map `/api/health`; không bịa endpoint health.
- Disposable PostgreSQL **17**: provision bằng `infra/postgres/init-roles.sh`, migration
  **chỉ** `powershell -NoProfile -File scripts/dev-migrate.ps1 -Container <disposable-id>`
  exit **0**, dùng tp_owner; truy vấn runtime xác nhận **tp_app** và major 17.
- Browser bootstrap/login synthetic owner, password gate **403**, change-password
  **204**, TOTP setup **200**, verify **204**, thiếu TOTP **401**, mã mới login **200**.
  Harness đợi bước TOTP mới, giữ replay guard thật; không sửa IAM hoặc clock.
- Cookie `__Host-tp_admin`: Secure, HttpOnly, host `admin.localhost`, path `/`,
  SameSite Strict được kiểm trong browser; không lưu cookie/token/CSRF/auth headers.
- Runtime preflight **18 PASS, 0 FAIL**, exit **0**, report
  `artifacts/p1-02/final-live/runtime-preflight.json`. Media provider đăng ký **Fake**
  trong preflight này; **không upload/complete media, không R2 transport**.
  Không dùng kết quả này thay cho real-R2 acceptance.

Đã phát hiện 5080 thuộc `teapremium-local-api-1` của workspace khác `am_tra`.
Owner cho phép tạm dừng và khởi động lại sau nghiệm thu; không sửa cấu hình/data của
container đó. Harness dùng DB riêng, không kết nối DB của service cũ.

### Routing và live matrix còn phải thực thi

| Resource | Route được duyệt | Thực tế phiên final |
|---|---|---|
| `teapremium-media-public-dev` | catalog/content/branding final sau validation | **PASS** real R2 + browser/API |
| `teapremium-media-dev` | mọi `pending/{uploadId:N}/{finalKey}` và evidence final | **PASS** actual routing/anonymous probes; domain settings owner-confirmed |
| Public key → public bucket | Allowed | **PASS**, HEAD/GET **200** |
| Public key → private bucket | Denied HEAD/GET trên object tồn tại do run tạo | **PASS**, HEAD/GET **403** |
| Private key → public bucket | Denied HEAD/GET trên object tồn tại do run tạo | **PASS**, HEAD/GET **403** |
| Private key → private bucket | Allowed | **PASS**, HEAD/GET **200** |
| API intent → browser PUT → actual HEAD/GET → complete → DB → retry/library | Real R2 + HTTPS browser | **PASS**, catalog/content/branding/evidence |
| Browser allowed/disallowed origin, ETag, signed headers | `https://admin.localhost:8443` | **PASS**, actual PUT **200** / disallowed rejected |
| Private anonymous denial / authorized signed GET | Synthetic existing evidence objects | **PASS**, private r2.dev **401**, anonymous S3 **400 XML InvalidArgument**, signed GET **200** |
| Signed PUT/GET expiry | Actual application TTL 600 seconds | **PASS**, HTTP **403** sau TTL thật; browser expired PUT rejected |
| App cleanup abandoned/completed staging / keep completed assets | Exact synthetic session keys | **PASS**, staging HEAD **404**, four completed assets HEAD **200** |
| Lifecycle multipart abort / private domain-settings independent read | Read-only config | **NOT VERIFIED**; owner confirms configuration, no detailed rule values |

Lần live đầu đã chạy bằng credentials operator nhập trực tiếp trong PowerShell.
`live-attempt-01.json`: **14 PASS, 2 FAIL, 2 NOT RUN**, runner exit **2**. Failure gốc
là public base URL được nhập trả **400**; `run_stopped` là hệ quả gate đó. Không gọi
API media hoặc browser upload sau gate lỗi. Production adapter đã ghi fixture PNG đã
decode vào public bucket; private client ghi probe riêng, positive HEAD/GET xác minh
hai object tồn tại trước cross-bucket negatives. Cleanup **2/2 exact keys**, HTTP
**204**; không xóa object có sẵn/list bucket/wildcard. Managed/custom domains và lifecycle
**NOT VERIFIED** vì không có approved read-only management token; không tăng quyền.

Không dùng secrets từ transcript. Secret-window đầu do tool mở đã thoát trước khi nhập;
operator sau đó được hướng dẫn và đã tự chạy
`artifacts/p1-02/final-live/secure-input.ps1` trong PowerShell tương tác cục bộ:
hai cặp S3 key nhập ẩn, JSON chỉ chuyển qua stdin trong bộ nhớ, không secret file/env
dump/argv. PublicBaseUrl của public bucket mới và toàn bộ private public-route URLs
cũng cần nhập; không tái dùng r2.dev URL cũ của private bucket làm PublicBaseUrl.
Read-only Cloudflare API token là tùy chọn; thiếu quyền đọc lifecycle/domains phải
ghi NOT VERIFIED, không tăng quyền app key hoặc tự sửa settings.

Sau lần live đầu, broker cũ đã giải phóng credentials. Owner cung cấp public URL đúng
và được hướng dẫn nhập lại qua local broker mới; broker mới điền sẵn public URL, hỗ
trợ retry bằng key giữ trong bộ nhớ đến khi operator đóng. Không gửi key qua chat.
Live attempt 02 (`live-attempt-02.json`): runner exit **2**, **16 PASS, 2 FAIL,
2 NOT RUN**. Public-base mapping **200**, private r2.dev known-existing probe **401**;
S3 endpoint anonymous **400** làm strict status assertion dừng. Kiểm XML chỉ mã lỗi
xác nhận **InvalidArgument**, không trả bytes object; harness đã sửa để nhận đúng
400 + XML Code=InvalidArgument + đúng S3 account hostname + không object bytes.
Không chấp nhận generic HTTP 400 ở public routes. Đây là sửa harness, không thay
security/product source. Cleanup lần 02 **2/2** exact run-owned keys, **204**.
Harness ignored đã build, browser script syntax check PASS. Các scenario còn lại là
**prepared code**, chưa phải evidence PASS; chỉ cập nhật theo report actual live.

Live attempt 03 (`live-attempt-03.json`): exit **2**, **45 PASS, 4 FAIL, 2 NOT RUN**.
Browser PUT bị chặn bởi CORS HTTPS origin: OPTIONS **403**, không allow-origin.
Hai FAIL CSRF là assertion harness sai expected 403; contract/source/tests vốn trả
**400 csrf_invalid**, đã sửa harness theo contract, không đổi app. Hai FAIL còn lại
là browser PUT và stop gate. Cleanup **4/4** keys **204**, HEAD sau xóa **404**.
Owner tự thêm HTTPS origin vào CORS private; Codex không sửa Cloudflare settings.
Sau đó OPTIONS allowed origin **204**, exact ACAO, PUT/GET/HEAD và content-type;
origin `https://disallowed.invalid` **403**, không ACAO. Đây là diagnostic riêng,
không thay cho actual browser PUT/GET đã PASS trong lần final đang chạy.

Live attempt 04 hoàn tất: `7731cb295a3d4ec6bbc8359d6ab04c8e`, **199 PASS, 0 FAIL,
3 NOT RUN**, runner exit **0**. Bốn purpose đều qua actual R2 provider/production adapter,
actual API/HTTPS browser và disposable PG17/tp_app. DB metadata SHA-256/dimensions/size,
completed session/asset đúng; catalog hai first-complete đồng thời tạo cùng một asset;
mọi purpose repeated/concurrent complete trả cùng ID. Public final GET **200** đúng
bytes, staging/final trước validation không public. Browser ETag exposed PASS.

Live security: missing/invalid CSRF **400 csrf_invalid**, invalid purpose **400**;
content staff unauthorized evidence/cross-user complete **403**; evidence bị lọc khỏi
library của content staff; đổi role qua API thật làm complete với session cũ **401**.
Missing object **404**; wrong size, spoof, truncated, expired session **422**; không
tạo asset. Library owner **200**, đúng bốn asset. Evidence signed GET bằng HTTP và
browser **200**, đúng bytes, Cache-Control private/no-store. Sau TTL thực **600 giây**,
application-issued signed GET và PUT đều **403**; browser expired PUT không dùng được
(fetch rejected/status 0), HTTP probe riêng xác nhận **403**, không suy CORS error là expiry.
Cleanup app đã xóa mười staging session (HEAD **404**) và giữ bốn completed R2 assets
(HEAD **200**) cùng metadata. Aging chỉ sửa synthetic disposable DB để đi đúng nhánh
24h; không chỉnh clock ứng dụng hoặc TTL signed URL.

Owner trả lời “có đủ rồi” khi được hỏi private public access/domain và lifecycle.
Ghi là owner confirmation, không suy ra giá trị enabled/prefix/abort days/expiration
cụ thể. Independent managed/custom-domain enumeration và actual lifecycle **NOT VERIFIED**.

Final teardown: production cleanup giữ completed assets trong test; sau khi xong expiry,
harness xóa **22/22 exact run-owned keys**, DELETE **204**, HEAD sau xóa **404** cho
**22/22**, không wildcard/list/preexisting deletion. Hai run trước xóa 2 keys/run, run
03 xóa 4 keys; không cộng retries vào final assertion totals. Disposable PostgreSQL và
API factory được dispose. Secret broker process đã dừng để giải phóng keys trong bộ
nhớ. Native test Caddy/Next được dừng; `teapremium-local-api-1` đã khởi động lại ở 5080
theo owner authorization. Không sửa hoặc xóa volume/data dịch vụ cũ; CA/hosts do owner
đã cấu hình được giữ nguyên. Provider config mặc định Disabled, không service R2 được
enable lâu dài bởi thay đổi source này.

Sanitized report `artifacts/p1-02/final-live/live-attempt-04.json`, current `live-report.json`,
broker exit `input-status.json`; harness `live/Program.cs`, `browser.cjs`, secret broker
`secure-input.ps1` và source manifest/inventory cùng thư mục. Command live do broker
gọi: `dotnet artifacts/p1-02/final-live/live/bin/Debug/net10.0/LiveAcceptance.dll`, JSON
secrets chỉ stdin/process memory, stdout/stderr không xuất provider details; exit **0**.
Không traces/HAR/screenshots/auth storage state; báo cáo chỉ booleans/status/hash ID.
199 checks gồm runtime/provider/browser/API/cleanup assertions, không phải 199 backend tests.

**Handoff:** targeted G1/G2 **GO** tái sử dụng; P2-1 đề xuất **CLOSED** cho actual R2
application/browser acceptance; P2-2 giữ **CLOSED** theo owner-provided G4. Independent
reviewer cần tập trung final live evidence và ba file delta phiên này, đồng thời thấy
đủ 42 file của slice. **READY_FOR_FOCUSED_G4: YES**; publish vẫn **NO**.
Operational follow-up: owner đọc Settings của cả hai bucket, lưu enabled/prefix/
abort-days/expiration và private domain list không bí mật. Không mặc định rule 7 ngày
vẫn tồn tại, không tự thêm TTL hoặc thay quyền. Không claim independently verified
domain enumeration/lifecycle khi chỉ có attestation và anonymous route probes.

### Verification final đã chạy

| Exact command | Exit / result |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | **0** |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | **0**, zero warnings/errors |
| `dotnet test backend/tests/TeaPremium.UnitTests --no-restore --filter FullyQualifiedName~Media --logger trx --results-directory artifacts/p1-02/final-live/unit` | **0**, **27/27**, zero skips |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-restore --filter FullyQualifiedName~Media --logger trx --results-directory artifacts/p1-02/final-live/integration` | **0**, **80/80**, zero skips |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~StaffIamTests --logger trx --results-directory artifacts/p1-02/final-live/iam` | **0**, **42/42**, zero skips |
| `dotnet build artifacts/p1-02/final-live/live/LiveAcceptance.csproj --no-restore -warnaserror` | **0**, local harness only |
| `node --check artifacts/p1-02/final-live/browser.cjs` | **0** |
| `dotnet artifacts/p1-02/final-live/live/bin/Debug/net10.0/LiveAcceptance.dll --runtime-preflight` | **0**, **18 PASS**, Fake registration, no media/R2 operations |
| `dotnet artifacts/p1-02/final-live/live/bin/Debug/net10.0/LiveAcceptance.dll` (broker stdin only) | **0**, **199 PASS**, **0 FAIL**, **3 NOT RUN**, actual R2/browser/API/PG17 |
| `python artifacts/p1-02/final-live/scan.py` | **0**, **310 text files**, 20 binary excluded, zero secret findings, staged rỗng |
| `git diff --check origin/main`, `git diff --cached --check` | **0** cả hai |

Focused backend totals **149/149** (27 + 80 + 42), no skipped. Controlled fake/fault
coverage includes `StorageFailureRollsBackIntentAndCompleteAndRecoverySucceeds`,
`DatabaseRollbackLeavesRecoverableObjectAndRetryCreatesOneAssetCleanupPreservesIt`,
`ExpiredIntentRejectsCompleteAndCleanupRetriesStorageFailureThenRemovesOrphans`;
không gọi chúng live transport. SDK method stubs/offline presign vẫn phân loại như
P2-2 correction. Reuse full **631/631** tại `artifacts/p1-02/remediation/linux/test-results`
(72 unit + 34 architecture + 525 integration, 0 skipped); đã đọc lại TRX counters,
không nhận đây là full run mới. OpenAPI/client/full frontend/Docker mới **NOT RUN**:
delta chỉ local config/docs, contract và application source giữ nguyên.

Logs/TRX final trong `artifacts/p1-02/final-live/`; không cộng runtime checks vào backend
test totals. Không claim independent G4. Không commit, push, PR, merge, deploy hoặc
thay đổi Cloudflare settings/credentials/lifecycle.

Final source inventory đối chiếu manifest đầu phiên: **42/42 paths**, không thêm/bớt
source file ngoài slice; ba file nội dung đổi đúng như mục delta. Markdown fences,
UTF-8 và local links PASS. Old API container sau khôi phục: **running/healthy**.
Không còn listener test 3001/8443. Readiness này không bao gồm việc tự review G4,
CI cho unpublished changes hoặc production acceptance.
