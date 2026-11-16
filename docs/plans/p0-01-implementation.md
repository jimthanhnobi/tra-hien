# P0-01 — hồ sơ triển khai nền tảng

**Trạng thái:** đã triển khai trên `feature/p0-01-foundation`, chờ review độc lập G4. Không commit, push hoặc merge. Nguồn spec ở commit `cadce575597738bf40ad7c45689482de5a5fbc3d`; G2 và scope P0-01 được người dùng duyệt ngày 16/11/2026.

## Truy vết và phạm vi

| Mục | Bằng chứng |
|---|---|
| Task | `docs/spec/07-roadmap.md:17` — P0-01, không có dependency. |
| Kiến trúc | `docs/spec/02-architecture.md:27–77` (§2–3), `:235–248` (§9–10). |
| Quyết định | DEC-01, DEC-15, DEC-16 trong `docs/spec/00-README.md:85,99–100`. |
| FR/BR/AC | Không có FR, BR hoặc AC riêng cho scaffold. AC-P0-01 (`07-roadmap.md:33`) là khóa tài khoản của P0-08, chưa được thực hiện ở đây. |
| NFR | NFR-09 (`01-requirements.md:206`) áp dụng khi có BR; P0-01 chưa có BR. Nền tảng test tuân `02-architecture.md:241–248`. |
| Vận hành | CI mong đợi locked restore và frozen install tại `06-infrastructure.md:224–230`; cấu hình CI là P0-11. |

Phạm vi đã làm: 4 project nguồn, 3 project test trong một solution .NET 10; project references theo §3; hai app Next.js App Router với TypeScript strict và Tailwind; package `api-client` có export chung, chưa có DTO; pnpm workspace; pin SDK/pnpm và lockfile NuGet/pnpm; analyzer .NET và warning-as-error; smoke tests và tài liệu lệnh chạy. `.editorconfig` sẵn có ở repo áp dụng cho C#, TypeScript, JSON và YAML; `.gitattributes` mới giữ LF cho file scaffold trên cả Windows và Linux.

Ngoài phạm vi: Compose P0-02, cross-cutting P0-03, DB/migration P0-04, IAM P0-08, Testcontainers/NetArchTest đầy đủ P0-10, CI P0-11, OpenAPI generation khi có API. Không có endpoint nghiệp vụ, schema hoặc secret.

## Cấu trúc và graph

```text
backend/TeaPremium.sln
  src/TeaPremium.Domain             → BCL
  src/TeaPremium.Application        → Domain
  src/TeaPremium.Infrastructure     → Application, Domain
  src/TeaPremium.Api                → Application, Infrastructure
  tests/TeaPremium.UnitTests        → Domain, Application
  tests/TeaPremium.IntegrationTests → Api
  tests/TeaPremium.ArchitectureTests → bốn source project
apps/store, apps/admin               → @teapremium/api-client
packages/api-client                  → chưa có OpenAPI types
```

`Domain` không có package; `Application` không có EF Core. ArchitectureTests kiểm chính xác project references và hai điều kiện đó. IntegrationTests khởi động API host và chứng minh `/` trả 404 (chưa vô tình phát hành endpoint). UnitTests có project và runner nhưng 0 case vì chưa có domain logic. Hai trang frontend import export chung để typecheck/build kiểm workspace link. Không có luồng ủy quyền ở scaffold; auth test thuộc P0-08.

## Toolchain và lệnh chạy tại gốc repo

Máy kiểm: Windows; .NET SDK được pin `10.0.403` bằng `global.json` (`latestPatch`); Node `v22.22.2`; Corepack `0.34.6`; pnpm `12.16.1` pin bằng `packageManager`. Cần .NET SDK 10.0.403 hoặc bản patch mới hơn của cùng feature band, Node >= 20.9 và Corepack. Không cần lệnh `pnpm` toàn cục khi dùng các lệnh `corepack pnpm` bên dưới. Các root scripts trong `package.json` dùng lệnh `pnpm` trực tiếp nên cần pnpm shim trong `PATH` nếu gọi qua `pnpm run` hoặc `corepack pnpm run`; máy kiểm hiện chưa bật shim này.

```powershell
dotnet --list-sdks
node --version
corepack pnpm --version
dotnet sln backend/TeaPremium.sln list
dotnet restore backend/TeaPremium.sln --locked-mode
dotnet build backend/TeaPremium.sln --no-restore -warnaserror
dotnet test backend/TeaPremium.sln --no-build
dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore
dotnet list backend/TeaPremium.sln package --vulnerable
corepack pnpm install --frozen-lockfile
corepack pnpm peers check
corepack pnpm audit
corepack pnpm --dir packages/api-client run lint
corepack pnpm --dir packages/api-client run typecheck
corepack pnpm --dir packages/api-client run build
corepack pnpm --dir apps/store run lint
corepack pnpm --dir apps/store run typecheck
corepack pnpm --dir apps/store run build
corepack pnpm --dir apps/admin run lint
corepack pnpm --dir apps/admin run typecheck
corepack pnpm --dir apps/admin run build
git diff --check
```

## Kết quả xác minh thực tế

| Lệnh | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS, cả 7 project restored từ `packages.lock.json`. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, `Build succeeded. 0 Warning(s), 0 Error(s)`. |
| `dotnet test backend/TeaPremium.sln --no-build` | PASS cho 5 ArchitectureTests và 1 IntegrationTest, 0 failed/skipped; UnitTests: `No test is available` (0 case, không tính PASS nghiệp vụ). |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS, exit 0, không có output sau khi chuẩn hóa LF trong `Program.cs`. |
| `dotnet list backend/TeaPremium.sln package --vulnerable` | PASS, cả 7 project báo `no vulnerable packages given the current sources`. |
| `corepack pnpm install --frozen-lockfile` | PASS, lockfile up to date; pnpm `12.16.1`. |
| `corepack pnpm peers check` | PASS, `No peer dependency issues found`. |
| `corepack pnpm audit` | PASS, `No known vulnerabilities found`. |
| Frontend `lint` | PASS cả store/admin với `--max-warnings=0`; api-client cũng PASS. |
| Frontend `typecheck` | PASS cả store/admin và api-client; `tsc --noEmit`. |
| Frontend `build` | PASS cả store/admin (`next build`, static `/` + `/_not-found`) và api-client (`tsc --project tsconfig.build.json`). |
| `git diff --check` | PASS, exit 0, không có output; đã dùng `git add -N` để Git kiểm cả file mới mà không stage nội dung. |

Lần chạy thử đầu: build .NET bị lỗi do ép `IDE0005` khi project chưa bật XML documentation; bỏ override và chạy lại PASS. pnpm ban đầu dùng ESLint 10/TypeScript 7 gây peer conflict; đã pin ESLint 9.39.5/TypeScript 5.9.3, `peers check` PASS. `api-client` ban đầu thiếu TypeScript parser cho ESLint; đã thêm `typescript-eslint`, lint PASS. Full `pnpm audit` phát hiện advisory moderate của `minimatch` trong dev dependency; pin transitive `11.0.1` ở `pnpm-workspace.yaml`, chạy lại lint/typecheck/build và audit đều PASS. `dotnet format` ban đầu phát hiện CRLF của template; đã chuẩn hóa LF và thêm `.gitattributes`, kiểm lại PASS.

## Rủi ro, rollback và review

- Chưa có kiểm thử BR, quyền, DB, migration hoặc SEO vì chưa có hành vi tương ứng. UnitTests có 0 case; không diễn giải `dotnet test` exit 0 thành kiểm chứng nghiệp vụ.
- `api-client` chỉ export prefix `/api` theo DEC-16. OpenAPI generated types sẽ được thêm khi API tồn tại, không có DTO giả.
- Pin `minimatch` 11.0.1 cho dependency lint dev; cần kiểm lại khi cập nhật công cụ vì override có thể không còn cần thiết.
- Rollback: bỏ các file scaffold mới của P0-01; không có dữ liệu, migration hoặc runtime production cần khôi phục.
- Review G4 cần đối chiếu diff với P0-01, DEC-01/15/16, graph §3 và log kiểm tra. PR chưa mở vì người dùng yêu cầu không commit/push.
