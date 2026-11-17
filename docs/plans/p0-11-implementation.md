# P0-11 — GitHub Actions CI

**G3: local verification PASS; GitHub Actions chờ push/PR. G4 NOT RUN.**
Ngày 17/11/2026, main Codex thread thực hiện trực tiếp, không subagents.
Base latest `origin/main`: `1ba65fa17fd8ab34a9e0d7d62cbf6fc8d3a88ede`;
nhánh `feature/p0-11-github-actions-ci`. P0-10 đã merge qua PR #13 tại base này;
working tree sạch trước khi tạo nhánh. Không merge hoặc tự duyệt G4.

## G0/G1/G2 và traceability

- G0: `00-README.md:3` ghi TeaPremium v2 2.0, ngày 16/11/2026; đủ bộ 10 Markdown + schema.sql.
- G1 full baseline `cadce575597738bf40ad7c45689482de5a5fbc3d`, inventory/gaps/traceability trong `docs/analysis/`.
  Tái sử dụng amendment G26/G01/F-01 đã duyệt tại `0bfc98e`; `git diff --stat 0bfc98e origin/main -- docs/spec` không có output.
  Targeted audit đọc P0, infrastructure §7, architecture §2/§3/§9/§10, API conventions và evidence P0-10; không có spec drift mới.
- G2: tái sử dụng `docs/architecture/conformance-review.md`, `docs/plans/task-readiness.md` và scoped evidence P0-10.
  GO: chỉ nối các gate có sẵn và bổ sung tooling OpenAPI được P0-11 yêu cầu; không đổi kiến trúc/module/schema.
- **P0-11**: `docs/spec/07-roadmap.md:27`, dependency **P0-10** đã merge.
- Hợp đồng CI: `06-infrastructure.md:224–230` §7 bước 1–4. Docker build regression là scope owner yêu cầu trực tiếp.
- OpenAPI/types: `02-architecture.md:45,241`, `05-api.md:7`; không viết DTO thủ công.
- **NFR-09**: `01-requirements.md:206`; giữ tất cả unit/integration/architecture regression P0-04/05/06/10.
  **AC-P0-05**: `07-roadmap.md:38` được kiểm lại bằng ArchitectureTests.
  **AC-P0-04**: dòng 36 là nghiệm thu tích hợp settings tương lai; CI giữ coverage interceptor hiện có, không tuyên bố đạt settings API.
- **DEC-01/15/16**: `00-README.md:85,99–100`; giữ modular monolith, hai Next apps và prefix `/api` cùng origin.
  Không có FR/BR mới riêng cho CI; bảo toàn regression hiện hữu, không gán mã giả hoặc mở thêm business feature.

## Hạ tầng tái sử dụng và thay đổi

| Hiện trạng audit | Cách xử lý |
|---|---|
| `backend/TeaPremium.sln`, 4 source + 3 test projects, central versions, NuGet locks, SDK 10.0.403/latestPatch | Restore locked; build warnings-as-errors; format verify; chạy solution tests đúng một lần. |
| `PlatformDatabaseFixture` dùng `postgres:17`, migrate DB rỗng và cleanup | Tái sử dụng nguyên fixture; Docker runner accessible; không PostgreSQL service riêng/EnsureCreated. |
| InitialPlatform migration, snapshot, AppDbContextFactory yêu cầu env connection | Pin EF CLI 10.0.15 trong local manifest; sinh idempotent SQL, không connect/apply DB. Dummy loopback port 1. |
| API chỉ middleware/outbox, chưa map business endpoint hoặc OpenAPI | AddOpenApi + MapOpenApi chỉ Development/CI; package .NET 10.0.15; build-time generation opt-in, không ảnh hưởng Docker build mặc định. |
| api-client chỉ có API_BASE_PATH viết tay | Giữ constant, thêm export types; generator riêng chỉ sở hữu openapi.json và src/generated/schema.ts. Pin openapi-typescript 7.13.0. |
| Hai Next apps chưa có test script | Thêm 2 SSR scaffold tests/app bằng node:test + tsx 4.23.15: heading/message và shared same-origin prefix. Không suite rỗng. |
| pnpm 12.16.1, Node 22.22.2, pnpm-lock.yaml | Giữ toolchain và frozen install; thêm lock graph cho generator/test tooling; chỉ cho phép build script esbuild dùng bởi tsx. |
| Dockerfiles và dockerignore hiện có | Build cả ba image đúng context thật; không sửa Dockerfiles/dockerignore. API build chứng minh asset schema bị exclude không gây build fail. |

Các file thay đổi:

- `.github/workflows/ci.yml`: triggers, permissions, concurrency, caches, 3 jobs, artifacts.
- `.config/dotnet-tools.json`, `scripts/migrations.ps1`, `scripts/check-test-results.ps1`: pin EF, fail khi SQL rỗng/tool lỗi; reject skipped/empty/failed TRX.
- `backend/Directory.Packages.props`, API csproj/Program.cs và 3 NuGet locks của API/ArchitectureTests/IntegrationTests: OpenAPI thật và dependency graph.
- `backend/tests/TeaPremium.IntegrationTests/OpenApiTests.cs`: Development/CI trả document không cần connection; Production trả 404.
- `packages/api-client/generate.mjs`, `openapi.json`, `src/generated/schema.ts`, `src/index.ts`, package.json: generation/canonicalization/drift check và type exports.
- Root/app package.json, `pnpm-lock.yaml`, `pnpm-workspace.yaml`, `apps/{store,admin}/tests/home.test.tsx`: scripts, pinned tooling, scaffold tests.
- `README.md`, hồ sơ này: hướng dẫn chạy và evidence.

## CI jobs và dependency graph

Ba job độc lập, chạy song song; tất cả phải xanh để owner đặt required checks:

```text
push main/feature/** hoặc PR -> main
  ├─ Backend and API contract
  │    locked restore -> build -> format -> docker info -> all tests -> reject skips
  │    -> EF SQL -> migrations.sql artifact -> frozen install -> actual API/client drift gate
  ├─ Frontend verification
  │    frozen install -> workspace lint -> typecheck -> 4 scaffold tests -> workspace build
  └─ Docker regression builds
       API build -> store build -> admin build
```

`contents: read`, action refs pin SHA, Ubuntu 24.04; không secrets, publish, deploy hoặc PR privileged event.
Concurrency theo workflow + source branch hủy run cũ, kể cả push/PR trên cùng branch; không matrix hoặc lặp suite backend.
NuGet cache keyed theo locks; pnpm store cache keyed theo pnpm-lock; mọi cache hit vẫn chạy locked/frozen restore.
SQL artifact bắt buộc có file; script còn kiểm length > 0. TRX upload kể cả job fail để chẩn đoán.

OpenAPI build-time tooling chạy entrypoint thật bằng mock server, không khởi động DB/production host.
Script xóa đúng cache file list của MSBuild để không bỏ qua generation khi JSON cũ đã bị xóa;
chuẩn hóa key order/LF, không xóa schema/path. `check:api` sinh mới trong bộ nhớ và so cả hai file committed;
thiếu file hoặc stale JSON/types đều fail; sau đó CI vẫn chạy `git diff --exit-code -- packages/api-client`.
API hiện chưa có business endpoint nên `paths: {}` và generated types rỗng phản ánh API thật,
không phải placeholder gate. Khi endpoint được thêm, cùng machinery tự lấy contract mới.

## Verification thực tế tại local

Windows, SDK 10.0.403, Node v22.22.2, Corepack 0.34.6, pnpm 12.16.1, Docker Desktop Linux server 29.4.1.
Các lệnh từ root; exit 0 trừ negative probes cố ý đòi exit khác 0.

| Trạng thái | Lệnh chính xác | Kết quả |
|---|---|---|
| PASS | `git fetch origin --prune`; `git status --short`; `git log -5 --oneline origin/main` | Base/P0-10 đã xác nhận; sạch trước edits. |
| PASS | `git diff --stat 0bfc98e origin/main -- docs/spec` | Không spec drift. |
| PASS | `./artifacts/actionlint/actionlint.exe -shellcheck= .github/workflows/ci.yml` | actionlint 1.7.7, không lỗi YAML/expression/action configuration; shellcheck NOT RUN trên Windows. Action SHA đối chiếu bằng git ls-remote tags v6/v5. |
| PASS | `dotnet restore backend/TeaPremium.sln --locked-mode` | 7 projects restored. |
| PASS | `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | 0 warnings/errors. |
| PASS | `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | Không diff format. |
| PASS | `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory artifacts/test-results` | Unit 11, Architecture 34, Integration 141: **186 PASS, 0 failed, 0 skipped**. PostgreSQL 17 bằng fixture hiện có. |
| PASS | `./scripts/check-test-results.ps1` | 3 TRX, tất cả total=executed=passed > 0. |
| PASS | `./scripts/migrations.ps1` | Tool restore EF 10.0.15; idempotent migrations.sql **9050 bytes**, InitialPlatform và migration history guards. |
| PASS | `corepack pnpm generate:api` | Sinh từ API thật, JSON + TypeScript. |
| PASS | `corepack pnpm check:api` | Lặp generation vẫn deterministic, committed outputs khớp. |
| PASS | Drift probes: append LF lần lượt vào `packages/api-client/openapi.json` và `packages/api-client/src/generated/schema.ts`, chạy `corepack pnpm check:api`, restore nguyên bytes trong finally | Cả hai bị reject bằng exit 1; chạy lại check sau restore PASS. |
| PASS | `corepack pnpm install --frozen-lockfile` | Đủ workspace, không thay lock. |
| PASS | `corepack pnpm lint` | store/admin/client. |
| PASS | `corepack pnpm typecheck` | store/admin/client. |
| PASS | `corepack pnpm test` | 4 tests, 0 failed/skipped; kiểm scaffold thật. |
| PASS | `corepack pnpm build` | Shared client tsc + store/admin Next production builds. |
| PASS | `docker build -f infra/Dockerfile.api -t tra-hien-api:ci .` | Linux image build thật, locked restore/full solution build; 0 warnings/errors dù docs bị exclude. |
| PASS | `docker build -f infra/Dockerfile.web --build-arg APP=store -t tra-hien-store:ci .` | Image + Next build thành công. |
| PASS | `docker build -f infra/Dockerfile.web --build-arg APP=admin -t tra-hien-admin:ci .` | Image + Next build thành công. |
| PASS | `git diff --check` | Không whitespace errors. |
| NOT RUN | GitHub Actions | Chờ push/PR, sẽ kiểm run thật; không suy diễn local PASS thành Actions PASS. |
| NOT RUN | G4 independent `$review-gate` | Cần session khác; implementer không tự phê duyệt. |

Trong phát triển, lần thêm tsx đầu bị pnpm chặn esbuild build script; đã thêm allowBuilds riêng cho esbuild rồi frozen install/tests/build PASS.
Lần generator đầu với incremental cache không tái sinh JSON bị ENOENT; đã sửa invalidate file-list cache, chạy lặp và negative probes PASS.
Không có lỗi kiểm chứng local chưa xử lý. actionlint tải vào artifacts (gitignored), không vendored vào repo.

## Giới hạn, rủi ro và rollback

- Không blocker task-specific đang biết. Frontend chỉ có scaffold, nên test SSR hiện tại không chứng minh login/IAM/hành vi nghiệp vụ; các phần đó thuộc task sau, không chặn P0-11.
- Chưa business endpoint/DTO trong API; không thêm endpoint giả để làm document có path. OpenAPI Production exposure có negative test 404.
- Testcontainers dùng tag `postgres:17` hiện hữu; Docker base tags/global.json/latestPatch giữ nguyên chính sách P0 trước. Cần mạng tới GitHub/NuGet/npm/Docker registries; cache không thay thế lock validation.
- Branch protection/required checks cần owner cấu hình; workflow chỉ cung cấp tên check ổn định. Không claim đã cấu hình protection hoặc G4.
- Không migration/table mới và không chạy SQL lên DB thật. Rollback: revert PR, xóa generated/tooling additions nếu cần; không cần data rollback. SQL artifact chỉ phục vụ review, không deploy.
- Bước tiếp theo: inspect Actions ở PR thật, sửa lỗi trong scope nếu có; handoff G4 session độc lập rồi owner quyết định merge. Không bắt đầu P0-07/P0-08.

Tooling OpenAPI dựa trên [tài liệu .NET](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0)
và [openapi-typescript](https://openapi-ts.dev/cli); hành vi thực tế đã kiểm bằng các lệnh trên.
