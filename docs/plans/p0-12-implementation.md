# P0-12 — Admin frontend shell

**G4 độc lập PASS — READY_FOR_OWNER_PUBLISH_APPROVAL.** Nhánh `feature/p0-12-admin-shell`; approved base: `7728a9d28078c21f8bfd3b15ffae985b49d71e03`. Owner đã cho phép đúng một commit implementation local với message `feat(admin): implement P0-12 admin shell`; chưa cho phép push/PR/merge. Các mục G3 bên dưới giữ lịch sử triển khai ngày 17/11/2026 (Asia/Saigon); kết quả G4 và kiểm tra trước commit ở mục cuối.

## G0/G1/G2 và phạm vi

- Đã đọc `AGENTS.md`, `docs/WORKFLOW.md`, `docs/CODEX_USAGE.md`, skill `implementation-slice`. Một main thread, không subagent. Worktree đầu vào sạch; `git fetch origin --prune` xác nhận đúng expected main. Commit main là merge PR #17, parent chứa `c9b6d1e`/`4f39d48` P0-08. Tạo nhánh trực tiếp từ `origin/main`, không reset/clean/stash.
- G1 TARGETED: tái sử dụng full audit `cadce575597738bf40ad7c45689482de5a5fbc3d` (`docs/analysis/spec-inventory.md`, gaps/traceability) và amendments được duyệt tại `0bfc98ecc4074c5e66503c8dbb83b8eb1bd81856`. `git diff 0bfc98e origin/main -- docs/spec` rỗng. Đã đọc phần IAM và scope dưới đây; không có broad spec drift, không chạy lại audit toàn dự án.
- G2 scoped GO: tái sử dụng `docs/architecture/conformance-review.md`, `docs/plans/task-readiness.md`, evidence G4/focused G4 P0-08. DEC-14/15/16 giữ nguyên: app Admin riêng, AdminCookie thật, proxy cùng origin. Không BFF, auth server, state framework, DB/migration hoặc endpoint mới. Các gap G04/G09/G13 và Q1–Q8 thuộc scope khác không chặn shell.
- Audit scaffold: trước sửa Admin chỉ có layout/page, Tailwind import, hai test SSR scaffold; Next rewrite HTTP chung chưa đáp ứng IAM Secure cookie/HTTPS. Workspace sẵn Next 16.4.6, React 19.3.1, strict TS, Tailwind 4, OpenAPI client thật. `next dev` sinh `apps/admin/AGENTS.md`; đã đọc hướng dẫn Next đi kèm package về server/client, caching, dynamic params, distDir/TypeScript. File hướng dẫn tự sinh được đưa vào inventory để reviewer thấy rõ.

| Nguồn / ID | Thực hiện và ranh giới |
|---|---|
| `07-roadmap.md:28,30,33–35`, P0-12, AC-P0-01/02/03 | Shell, login/TOTP, quyền, Query, form, table. Lockout do API; UI hiển thị lỗi chung. P0 DoD toàn pha/CI chưa được nhận hoàn thành. |
| `01-requirements.md:54–56,204`, FR-IAM-01/02/03, NFR-07 | Login, first-password onboarding, enrollment, Secure cookie/HTTPS/CSRF. Không xây staff CRUD của FR-IAM-02. |
| `04-business-rules.md:10–11,20`, BR-IAM-01/02/06 | Password 12 ký tự/bốn nhóm, gate bằng flags backend, đổi password làm hết phiên; 204 quay lại login. Lockout/session lifetime do backend thực thi. |
| `02-architecture.md` §2/3/6/7/9, `00-README.md:98–100`, DEC-14/15/16 | Admin riêng, permission claims, client sinh từ OpenAPI, không JS-managed auth token. |
| `05-api.md:3–26,90–101`, §1/4.1 | Bảy endpoint auth thật; camelCase, ProblemDetails, CSRF, 204 không parse JSON. Table map pagination page 1-based/max 100. |
| `backend/src/TeaPremium.Api/Iam/AdminEndpoints.cs:13–66,104–121`; `AdminSecurity.cs:18–61,137–176` | Đã kiểm endpoint, flags, cookie, CSRF và HTTPS thật. `packages/api-client/openapi.json`/`src/generated/schema.ts` không sửa tay hoặc regenerate để né lỗi. |

Ngoài scope: Settings P0-09, mọi business CRUD, dashboard số liệu, customer authentication, recovery/reset TOTP, endpoint IAM mới, migration/production deploy. Navigation có placeholder rõ ràng; không gọi API nghiệp vụ giả.

## Kiến trúc ứng dụng và auth flow

- Server layout `force-dynamic`; HTML ban đầu chỉ có loading trung tính. Không đọc cookie/auth/secret ở server, không dehydrate Query cache. Response Admin đặt no-store/private, noindex, no-referrer, nosniff và deny iframe. Build xác nhận các route Admin dynamic. Redirect login diễn ra ở client sau `/me`; backend vẫn là lớp authorization.
- `AdminApi` dùng `openapi-fetch<paths>` và aliases từ generated components; request/response DTO không viết lại. Mọi browser request `/api/v1/admin/*`, credentials include, cache no-store. CSRF request token chỉ trong memory, lấy mới trước mọi unsafe request, không retry mutation hoặc tự replay khi CSRF lỗi.
- `createAdminRuntime` sở hữu QueryClient theo app instance: không retry tự động, không persist cache. Login/logout/204 cancel pending query, xóa mọi module query và mutation cache, reset CSRF. Giữ auth query object đang được observe, chỉ set dữ liệu null để không làm observer mất kết nối. Login lấy lại CSRF rồi `/me`; không dùng login response thay cho authority `/me`.
- `/me` kiểm lại khi mount/focus và mỗi 60 giây foreground. 401 xóa module data và về login; quyền/identity đổi thì bỏ module cache. Lúc đang kiểm lại, UI cũ bị ẩn khỏi tương tác/accessible tree; form/QR giữ trong memory nếu phiên vẫn hợp lệ. Hết phiên/unmount loại bỏ chúng. Không đồng bộ session qua storage/event bus.
- Flow bắt buộc: anonymous → email/password + `totpCode` nullable → `/me` → password gate trước TOTP gate → đổi password 204 → login → setup TOTP → QR render local + khóa thủ công trong details → verify 204 → login bằng mã time-step mới → shell. Không có MFA challenge endpoint.
- Staff chưa bật TOTP có thể mở enrollment từ account menu; `totpRequired` của backend quyết định bắt buộc. Không suy luận Production/role ở browser. Enrollment key/QR nằm trong local component state, không Query cache, storage, URL, log, server HTML hoặc dịch vụ QR ngoài.
- Lỗi 400 validation/field errors, 401, 403, 429 và lỗi mạng có phản hồi tiếng Việt. Login báo chung cho email/password/TOTP sai, không tiết lộ account tồn tại. Server giữ quyền quyết định lockout/rate limit.

## Routes và permissions

| Route | Điều kiện / trạng thái |
|---|---|
| `/login` | Anonymous; enrolled staff nhập TOTP trong cùng form. |
| `/change-password` | `passwordChangeRequired` do `/me`; mọi URL shell bị chuyển tới đây. |
| `/totp-setup` | Sau password gate; required hoặc staff chưa bật TOTP. Đã bật thì về tổng quan. |
| `/` | Ready session; welcome và danh sách khu vực được cấp quyền, không thống kê giả. |
| `/catalog`, `/inventory`, `/orders`, `/customers` | Lần lượt `catalog.read`, `inventory.read`, `orders.read`, `customers.read`. |
| `/marketing`, `/content`, `/finance` | `marketing.write`, `content.write`, `finance.read`. |
| `/settings`, `/staff` | `settings.write`, `staff.manage`; chỉ placeholder. |
| `/403` hoặc URL module thiếu quyền | Màn từ chối có hướng dẫn liên hệ quản lý và link tổng quan. |
| URL không khai báo | Next `notFound()`, trang 404. |

Menu kiểm đúng array permission backend trả; không grant theo role `owner` hoặc tự expand role union. Multiple roles được backend tổng hợp. Menu hiding không bảo vệ API; backend kiểm lại mọi request. Nội dung/permission nhạy cảm không được suy đoán từ client.

## Shared foundations và UI

- `Field`: label/input, hint/error liên kết bằng aria, password visibility, focus rõ. RHF + Zod: summary, lỗi field/server, disabled/loading và ref guard chống submit lặp. Feedback role=status; lỗi role=alert.
- `Confirmation`/mobile drawer/account menu dùng Radix focus trap, keyboard, Escape và focus restoration. Sidebar collapse giữ accessible name, skip link vào main, breadcrumbs và responsive container. Không animation trang trí.
- `DataTable<T>` nhận typed columns/data/getRowId, controlled pagination, `manualPagination`/rowCount. API parent dùng `paginationParams` chuyển pageIndex 0 thành page 1. Page size 10/20/50/100, đổi size reset page, có loading/error/retry/empty, caption/th scope, vùng scroll có keyboard focus. Không gọi endpoint giả hoặc đưa fixture vào app production.
- Dependencies pin exact; TanStack Table 8.21.3 dùng [API pagination v8](https://tanstack.com/table/v8/docs/guide/pagination) đã đối chiếu. Lockfile chỉ thêm dependency graph Admin và peer resolution Next/Playwright; không nâng framework/store/API-client. Đã đối chiếu [transport middleware](https://openapi-ts.dev/openapi-fetch/middleware-auth) và [RHF submit](https://react-hook-form.com/docs/useform/handlesubmit) với bản đang cài.
- CI frontend cài Chromium + OS deps trước `pnpm test`. Cấu hình test dùng `.next-p0-12-test`, riêng cổng 3101 để không dừng server IDE của owner; thêm ignore Git/ESLint tương ứng.

## Ma trận test và bằng chứng

T01–T18 là nhãn kiểm tra của yêu cầu owner, không tạo ID spec mới.

| Nhãn | Bằng chứng thực chạy |
|---|---|
| T01 | Browser anonymous `/orders` → `/login`; real TLS lặp lại. |
| T02/T03 | Assertion đúng email/password/totpCode (null hoặc 6 số), không challenge. |
| T04 | Browser first-password gate, direct URL không bypass, body DTO đúng; real API 204. |
| T05 | Required/optional enrollment, QR local, secret chỉ trên setup, focus recheck; real Production verify và login bằng TOTP mới. |
| T06 | Unit transport và browser runtime xử lý body rỗng 204 cho ba endpoint. |
| T07 | Unit active QueryObserver + browser kiểm CSRF/me sau login; bắt được và sửa lỗi clear query detach observer. |
| T08 | Unit cancel pending requests/identity changes; browser hết phiên tại focus recheck, không cần reload và không còn UI user cũ. |
| T09/T10 | API 403 không retry; direct URL deny; role owner không giúp thấy finance nếu thiếu permission; unknown URL 404. |
| T11 | Unit xóa module/mutation cache và abort late result; browser account keyboard/Escape/logout. |
| T12/T13 | 400 field errors, 429, required/format validation, visibility, loading, duplicate-submit guard. |
| T14 | TanStack pagination state/server params, size reset, boundary disabled; RTL loading/empty/error/retry; browser fixture dùng CSS thật tại 390px kiểm empty state không overflow trang. Fixture chỉ thay DOM trong test, không có production route hoặc API giả. |
| T15 | Chromium desktop/tablet/mobile, axe, keyboard/menu/drawer focus và labels; actual table DOM actions. |
| T16 | Build dynamic + browser SSR no-store/noindex; real authenticated server HTML không chứa email/password/provisioning secret. |
| T17 | Mock handler fail mọi endpoint lạ; unit exact paths; real test assertion exact auth paths + staff permission probes. |
| T18 | Full frontend/store và backend regressions; OpenAPI drift. |

| Command | Kết quả |
|---|---|
| `corepack pnpm install --frozen-lockfile` | PASS, lockfile current; supply-chain policies PASS. |
| `corepack pnpm lint` | PASS toàn workspace, 0 warnings. |
| `corepack pnpm typecheck` | PASS toàn workspace, strict TS. |
| `corepack pnpm test` | PASS: Admin 8 unit/component + 16 Chromium browser; Store 2 tests; 0 fail/skipped. |
| `corepack pnpm build` | PASS api-client + Store + Admin. Có đặt `ADMIN_TEST_DIST_DIR=.next-p0-12-test` để không đụng dev build owner. Admin `/`, `/_not-found`, `/[section]` đều dynamic. |
| `corepack pnpm check:api` | PASS; build backend 0 warning/error, generated JSON/TS current. |
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS. |
| `dotnet test backend/TeaPremium.sln --no-restore --logger 'trx;LogFilePrefix=p0-12' --results-directory artifacts/p0-12-backend` | PASS 264/264: Unit 11, Architecture 34, Integration 219, 0 fail/skipped. |
| `./scripts/check-test-results.ps1 -Directory artifacts/p0-12-backend-final` | PASS, ba TRX của lượt full thành công, 11/34/219 tests, zero skipped. Thư mục gốc giữ thêm TRX Unit của lượt thiếu assets ban đầu nên không dùng để đếm lại. |
| `dotnet build apps/admin/tests/iam/seed/Seed.csproj -warnaserror` | PASS 0 warning/error. |
| `corepack pnpm --filter @teapremium/admin test:iam` | PASS 1/1 trên build production + API Production + PG17 + Caddy TLS, 24.7s (bao gồm đợi TOTP time-step mới). |
| `corepack pnpm audit --prod --audit-level high` | PASS, no known vulnerabilities found tại thời điểm chạy. |
| `git diff --check` | PASS. |

Lần backend `--no-restore` đầu tiên gặp assets NuGet cũ sau switch main; locked restore rồi full suite PASS. Lượt đầu browser bắt auth observer/cache và tương phản/accessible name, đều sửa và chạy lại. Lint đầu sau thêm dist riêng quét nhầm build artifacts; đã thêm global ignore chính xác, full lint PASS. Download Caddy lần đầu kiểm nhầm SHA256 với checksum SHA512; kiểm lại SHA512 đúng trước khi chạy. Lượt TLS đầu pin root SPKI không nằm trong chain gửi tới browser; chuyển pin đúng leaf SPKI rồi PASS. Không diễn giải các lỗi setup này thành test PASS trước khi sửa.

Logs/TRX/screenshots nằm trong `artifacts/` bị Git ignore. Real IAM dùng DB/container ephemeral riêng, certificate Data Protection thử nghiệm và credential ngẫu nhiên; không dữ liệu khách thật, không thay backend. Test tắt trace/screenshot và đóng context trước failure snapshot; credential/secret không được đưa vào hồ sơ.

## Kiểm tra giao diện

Đã xem screenshot thực ở 1440×1000, 768×1024, 390×844: desktop/sidebar, tablet/mobile drawer, login mobile; browser kiểm thêm password/onboarding, 403/404 và enrollment. Screenshot enrollment chỉ lưu bước hướng dẫn trước khi sinh secret. Có ảnh 403 desktop và empty table mobile. Tên file: `artifacts/p0-12-browser/**/{shell-1440,shell-768,shell-390,drawer-768,drawer-390,login-mobile,forbidden-desktop,totp-instructions,table-empty-mobile}.png`.

Sửa phát hiện cụ thể: tăng tương phản eyebrow/breadcrumb/footer/nav caption; thay avatar initial bằng icon để tên truy cập khớp nhãn nhìn thấy; dùng aria-labelledby cho account/brand, giữ tên menu khi collapse; tách dist test khi owner mở dev server; giữ input/QR qua focus recheck nhưng ẩn stale UI. Các viewport không overflow ngang và axe trả 0 violations tại các trạng thái đã kiểm. Screenshot mock chỉ có dữ liệu synthetic, không commit ảnh. Browser cross-engine/manual screen reader **NOT RUN**.

## Chạy local HTTPS

Plain `http://localhost:3001` và Compose hiện tại không thể xác thực IAM: backend yêu cầu HTTPS, cookie __Host/Secure/HttpOnly/SameSite Strict, proxy phải được trust rõ ràng. Không dùng Next HTTP rewrite hoặc hạ policy cookie để né vấn đề.

Từ repo root, dùng DB dev riêng đã migrate theo P0-08 và biến secret ở environment (không in ra log):

```powershell
corepack pnpm install --frozen-lockfile
corepack pnpm --filter @teapremium/admin exec next dev --hostname 127.0.0.1 --port 3001
```

Terminal API riêng: cấu hình `ConnectionStrings__Default`, `Iam__AdminHost=admin.localhost`, `Iam__TrustedProxies__0=127.0.0.1`, chạy `dotnet run --project backend/src/TeaPremium.Api --no-launch-profile --urls http://127.0.0.1:5080`. `ASPNETCORE_ENVIRONMENT=Production` mới bắt owner enrollment; chế độ này bắt buộc `Iam__KeyCertificatePath`/`Iam__KeyCertificatePassword` cho certificate X.509 có private key. Development không cưỡng chế owner TOTP, vẫn có optional enrollment; không dùng nó làm bằng chứng Production gate.

Terminal proxy: `caddy run --config infra/caddy/Caddyfile.admin-local --adapter caddyfile`. Caddy native bind loopback 8443, giữ `/api/*` nguyên path và Host, chuyển tới API 5080; phần còn lại tới Next 3001. Cấu hình theo [Caddy TLS](https://caddyserver.com/docs/caddyfile/directives/tls) và [reverse proxy](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy), đã chạy thực với Caddy 2.11.9 (artifact Windows kiểm SHA512 trước khi dùng). File cấu hình không dùng cho Docker network hoặc production. Caddy CA/storage nằm trong ignored `artifacts/p0-12-local/caddy-data`; không tự cài trust vào OS. Để truy cập thủ công, kiểm CA rồi trust `.../pki/authorities/local/root.crt` trong user store của máy dev, ví dụ `certutil -user -addstore Root <absolute-root.crt>` trên Windows; quản lý/gỡ trust khi không còn dùng. Mở `https://admin.localhost:8443`. Nếu OS không resolve `admin.localhost`, cấu hình hosts loopback cho hostname này. Không bỏ qua cảnh báo chứng chỉ trên hệ thống thật.

Owner đầu tiên dùng `dotnet run --project backend/src/TeaPremium.Api --no-launch-profile -- bootstrap-owner` với đúng environment/DB/certificate; CLI nhập mật khẩu ẩn. Không truyền password qua argument/pipe. Migrate riêng trước CLI; API không auto-migrate. Không đổi `Iam:TrustedProxies` thành wildcard hoặc nhận header từ nguồn không trust.

## Chạy lại E2E IAM riêng

Chỉ trên PostgreSQL 17 mới hoàn toàn, tên DB `admin_shell_test`, host `127.0.0.1`, port dành riêng. Seed helper từ chối host/database khác. Tạo thư mục `artifacts/p0-12-local` và file `connection.secret` chứa connection string của DB ephemeral này (không log/commit). Từ repo root:

```powershell
dotnet run --project apps/admin/tests/iam/seed/Seed.csproj
```

Helper migrate bằng migrations hiện có, bootstrap owner qua handler thật, tạo certificate DP thử nghiệm và `fixture.secret` chứa credential ngẫu nhiên. Đây là fixture test, không dùng cho DB dev cần giữ dữ liệu/production. Nạp connection/keyPath/keyPassword từ fixture vào environment của API, đặt Production/AdminHost/trusted proxy như trên. API chạy ở 5080; Next production `next start` sau build chạy ở 3001; Caddy chạy cấu hình local. Nếu build dùng dist riêng, đặt cùng `ADMIN_TEST_DIST_DIR=.next-p0-12-test` khi start.

Fixture DLL đã build với warnings-as-errors. Sau lượt kiểm tra này, đã dừng đúng API/Next/Caddy test và container PG17 do G3 tạo; container dùng `--rm`/không volume nên DB thử bị loại bỏ. Đã xóa `fixture.secret`, `connection.secret`, certificate DP và file tên container. CA local còn trong ignored artifacts, chưa cài vào OS. Server IDE riêng của owner được giữ nguyên.

Browser test có hai cách trust TLS: trust CA local được kiểm hoặc chỉ pin **leaf** SPKI cho process Chromium test. Lượt này dùng cách thứ hai; không bật `ignoreHTTPSErrors` toàn cục, không thêm CA vào OS. Từ root:

```powershell
$env:ADMIN_IAM_FIXTURE = (Resolve-Path artifacts/p0-12-local/fixture.secret).Path
$env:ADMIN_LOCAL_CA_SPKI = node -e "const fs=require('node:fs'),c=require('node:crypto');const cert=new c.X509Certificate(fs.readFileSync('artifacts/p0-12-local/caddy-data/certificates/local/admin.localhost/admin.localhost.crt'));process.stdout.write(c.createHash('sha256').update(cert.publicKey.export({type:'spki',format:'der'})).digest('base64'));"
corepack pnpm --filter @teapremium/admin test:iam
```

Pin chỉ áp dụng process browser test và certificate đó; hostname/cookie/auth không bị đổi. Chứng chỉ Caddy renew thì tính lại pin. Chuẩn bị DB mới cho mỗi lần chạy vì test đổi password/bật TOTP; không reset tài khoản hoặc DB có dữ liệu cần giữ. Dừng đúng processes/container thử nghiệm do mình tạo, xóa credential/certificate fixture sau test. Không xóa volume Compose của owner.

## Giới hạn, rollback và G4

- Không có finding P0/P1 đã xác nhận còn mở trong kiểm tra G3. Đây không phải G4 approval. Read-only reviewer mới phải kiểm toàn bộ local diff và untracked trên cùng worktree.
- **NOT RUN:** GitHub Actions/Linux CI ở HEAD mới (chưa publish), Docker image/Compose full-stack mới, production deploy, Firefox/WebKit, screen reader thủ công, vận hành CA/trust thật. CI mới chỉ được chỉnh bước cài browser để không thiếu Chromium khi chạy tests.
- P0-09/AC-P0-04, business CRUD, backup và DoD toàn pha không được nhận hoàn thành. HTTP Compose chưa phải môi trường auth; dùng hướng dẫn HTTPS đã kiểm. Không có recovery TOTP contract, không tạo bypass.
- Dependency/browser/OS cần cài bằng `corepack pnpm --filter @teapremium/admin exec playwright install --with-deps chromium` trên Linux CI (`playwright install chromium` trên Windows). Port 3101 phải trống cho suite mock. Không dừng server riêng của owner; dev server IDE đã được giữ nguyên.
- Không migration mới, không dữ liệu production. Rollback bằng bỏ đúng file thay đổi P0-12 sau khi owner lưu phần cần giữ; khôi phục scaffold/config/dependencies và frozen install. Dừng proxy/test processes/container ephemeral, không xóa/reset DB thật. Không dùng reset/clean để che local changes.
- Handoff mục tiêu: **READY_FOR_LOCAL_G4**, không READY_FOR_PUSH; reviewer cần FR/BR/AC, cache isolation, CSRF, same-origin HTTPS, toàn bộ untracked/test fixture và CI step. Publish tiếp tục cần G4 độc lập và owner cho phép riêng.

## Inventory local tại mốc G3

Danh sách đầy đủ và `git status --short` được ghi bên dưới khi chốt verification; staged 0, không commit mới so với base.

Đã chốt: 12 tracked paths (gồm file test scaffold bị xóa), 21 untracked files; staged 0. Không đổi docs/spec, backend runtime/migrations hoặc generated API contracts. next-env.d.ts đã được Next typegen sinh lại về đường dẫn mặc định, không có diff.

Kết quả `git status --short --untracked-files=all`:

```text
 M .github/workflows/ci.yml
 M .gitignore
 M apps/admin/eslint.config.mjs
 M apps/admin/next.config.ts
 M apps/admin/package.json
 M apps/admin/src/app/globals.css
 M apps/admin/src/app/layout.tsx
 M apps/admin/src/app/page.tsx
 D apps/admin/tests/home.test.tsx
 M apps/admin/tsconfig.json
 M infra/README.md
 M pnpm-lock.yaml
?? apps/admin/AGENTS.md
?? apps/admin/playwright.config.ts
?? apps/admin/playwright.iam.config.ts
?? apps/admin/src/app/[section]/page.tsx
?? apps/admin/src/app/error.tsx
?? apps/admin/src/app/not-found.tsx
?? apps/admin/src/components/admin-screen.tsx
?? apps/admin/src/components/auth-forms.tsx
?? apps/admin/src/components/data-table.tsx
?? apps/admin/src/components/providers.tsx
?? apps/admin/src/components/ui.tsx
?? apps/admin/src/lib/api.ts
?? apps/admin/src/lib/navigation.ts
?? apps/admin/tests/browser/shell.spec.ts
?? apps/admin/tests/foundations.test.tsx
?? apps/admin/tests/iam/onboarding.spec.ts
?? apps/admin/tests/iam/seed/Program.cs
?? apps/admin/tests/iam/seed/Seed.csproj
?? apps/admin/tests/table.test.tsx
?? docs/plans/p0-12-implementation.md
?? infra/caddy/Caddyfile.admin-local
```

## G4 độc lập và kiểm tra trước local commit

Reviewer `/root/p0_12_independent_g4` dùng skill `review-gate`, context mới không thừa kế hội thoại implementer, đọc trực tiếp cùng worktree `D:\tra-hien`. Đã fetch latest `origin/main`, đọc toàn bộ diff và full contents của 21 file untracked, đối chiếu authoritative P0-12, FR-IAM-01/02/03, BR-IAM-01/02/06, AC-P0-01/02/03, DEC-14/15/16 với backend P0-08 và generated contracts. **Verdict PASS, không có actionable findings P0/P1/P2/P3 trong phạm vi đã review**; không nhận ứng dụng không còn bug hoặc DoD toàn pha đã hoàn thành.

Snapshot G4 gồm **13 tracked changes và 21 untracked files**, staged 0, HEAD/origin/main cùng approved base. So với inventory G3 lịch sử, `apps/admin/next-env.d.ts` đã được Next sinh lại sang `.next-p0-12-test/dev/types/*` trước G4; reviewer đã đọc và giữ đúng nội dung này. Không tự restore hoặc bỏ file đó khỏi commit.

### Bằng chứng reviewer thực chạy

| Command độc lập | Kết quả |
|---|---|
| `corepack pnpm --filter @teapremium/admin test:unit` | PASS 8/8. |
| `corepack pnpm --filter @teapremium/admin lint` | PASS, 0 warnings. |
| `corepack pnpm --filter @teapremium/admin typecheck` | PASS. |
| `corepack pnpm exec playwright test` trong ignored snapshot | PASS 16/16, 39.3s; desktop/tablet/mobile 1440/768/390, axe, keyboard/Escape, expiry, permissions, duplicate submission. Đã xem screenshot thực. |
| `node node_modules/next/dist/bin/next build` trong snapshot, `ADMIN_TEST_DIST_DIR=.next-g4-production` | PASS; Admin routes dynamic. |
| `corepack pnpm check:api` | PASS, generated OpenAPI/client current. |
| `corepack pnpm --filter @teapremium/store test` | PASS 2/2. |
| `corepack pnpm audit --prod --audit-level high` | PASS, không thấy vulnerability tại thời điểm chạy. |
| `dotnet build backend/src/TeaPremium.Api/TeaPremium.Api.csproj --no-restore -warnaserror` | PASS, 0 warnings/errors. |
| `dotnet run --project D:/tra-hien/apps/admin/tests/iam/seed/Seed.csproj --no-restore` | Thành công trên PostgreSQL 17 mới, riêng của reviewer. |
| `node node_modules/@playwright/test/cli.js test --config playwright.iam.config.ts` trong snapshot | PASS real IAM 1/1, 40.9s: API/Next Production, Caddy TLS, password/TOTP 204, re-login mã mới, __Host cookie Secure/HttpOnly/Strict, thiếu CSRF 400, onboarding staff 403 rồi ready 200, logout/me 401. |
| `git diff --check` | PASS. |

Review source diễn ra trên worktree thật. Browser/build chạy trong ignored `artifacts/g4-p0-12-review`, copy kiểm SHA-256 từng file để tránh Next tự sửa source; 13 runtime source files của snapshot vẫn hash-identical sau build. Fingerprint **212/212 file nonignored của worktree khớp trước/sau G4**. Lỗi harness ban đầu về tìm CLI/workspace metadata trong snapshot đã được phân biệt với các lệnh chạy thành công bên trên, không phải finding sản phẩm.

**NOT RUN trong G4:** frozen install độc lập, toàn bộ backend suite 264 tests, Docker image/Compose full-stack, GitHub Actions/Linux cho thay đổi chưa publish, Firefox/WebKit, screen reader thủ công. Kết quả G3 của các kiểm tra này (nếu có) vẫn là bằng chứng G3, không gán cho reviewer. **NOT APPLICABLE:** migration/rollback DB mới, business CRUD, storefront SEO. Chưa xác nhận P0-09/DoD toàn pha.

Reviewer đã dừng riêng process/container và xóa 6 credential/private-key files thử nghiệm; không dừng process owner. Caddy tự ghi `AppData/Roaming/Caddy/autosave.json` ngoài repo; không restore để che artifact. Reviewer không sửa source, commit, push, tạo PR hoặc merge.

### Snapshot được phép đóng gói

Owner đã chỉ thị tạo **ONE local implementation commit**, không sửa implementation và không push/PR/merge/P0-09. Trước bổ sung evidence vào tài liệu này, reviewer gốc kiểm lại fingerprint: **212/212 khớp G4, differences=[]**; main thread đối chiếu độc lập cùng aggregate và inventory 13+21. Branch, HEAD và `origin/main` vẫn đúng approved base sau fetch. Không có source thay đổi sau G4.

Aggregate dùng map sắp Ordinal, mỗi dòng `path<TAB>UPPERCASE-SHA256<LF>`, UTF-8 không BOM:

```text
212 file trước bổ sung G4 evidence:
21E322A6A1928F8976F5CDD4EB5EA0BDBF85116811A62441C27FD05A6C16EBF3

211 file, chỉ loại docs/plans/p0-12-implementation.md:
7E2754763AE1AA46AD4BE3D5ACF9E6990FB1B8756A5B6665541CA31B872FBF59

SHA256 tài liệu này trong snapshot G4 gốc:
80136E30E19C2F7D93E698B8F1E8AB49AFC918957BC133E06BCFBAAA9D456E2E
```

Sau G4 chỉ bổ sung hồ sơ review/owner authorization trong đúng file tài liệu đã review này. Aggregate 211 file còn lại phải giữ nguyên trước commit. Đã đọc lại đầy đủ 13 tracked changes/21 untracked; kiểm toàn bộ YAML lockfile (hai document, 593 package entries), chỉ importer `apps/admin` thay đổi. Kiểm path/file content không thấy secret thật, credential/certificate/.env/database/recording/screenshot/binary được đưa vào danh sách commit; các chuỗi credential hằng trong test là fixtures synthetic đã review, dữ liệu runtime ngẫu nhiên được tạo ngoài Git.

Stage chỉ 34 paths P0-12 đã review (bao gồm deletion `apps/admin/tests/home.test.tsx`, declarations `next-env.d.ts` và hồ sơ này), rồi kiểm staged diff/file list/statistics và `git diff --cached --check` trước đúng một local commit. CI tại commit mới vẫn **NOT RUN** cho tới khi owner cho phép publish và chạy workflow. Owner giữ quyền publish và merge riêng; trạng thái bàn giao vẫn **READY_FOR_OWNER_PUBLISH_APPROVAL**.
