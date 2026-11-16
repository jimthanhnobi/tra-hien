# P0-02 — môi trường Docker Compose development

**Trạng thái:** G4 review độc lập đã kết luận GO trên `feature/p0-02-compose-dev`; không có finding P0–P3 cần sửa. Chờ commit, PR và người phụ trách duyệt merge.

## Nguồn và phạm vi

| Mục | Nguồn |
|---|---|
| Task, phụ thuộc | `docs/spec/07-roadmap.md:18` — P0-02, phụ thuộc P0-01 đã merge vào main tại `16cffc6`. |
| Kiến trúc | `docs/spec/02-architecture.md:3–24,27–62`; DEC-01/15/16 tại `docs/spec/00-README.md:85,99–100`. |
| Dev environment | `docs/spec/06-infrastructure.md:251–258` — 4 service và hot reload. |
| FR/BR/AC | Không có FR/BR/AC riêng cho P0-02; AC-P0-01…05 trong roadmap là nghiệm thu các task nền tảng sau, không gán vào Compose. |
| NFR | Không có NFR vận hành riêng cần chứng minh ở P0-02; đây là local dev, không tuyên bố đạt NFR production. |

Phạm vi: Docker Compose với `postgres`, `api`, `store`, `admin`; Dockerfile build từ solution và pnpm workspace theo lockfile; `.env.example`, hướng dẫn Windows PowerShell; Next dev rewrite `/api/*` để giữ URL cùng origin. Không thêm migration, seed, endpoint health, IAM hay business logic. Caddy production thuộc hạ tầng sau; rewrite chỉ bật khi `API_INTERNAL_URL` được đặt trong Compose.

## File và topology

| File | Nội dung |
|---|---|
| `infra/compose.dev.yml` | 4 service, `postgres` health check, `pgdata` named volume, startup order, cổng loopback; Compose Watch đồng bộ source API/Store/Admin, bỏ qua `bin/obj` của host. |
| `infra/Dockerfile.api`, `infra/Dockerfile.web`, `.dockerignore` | Build .NET 10.0.404 và Node 22/Corepack từ lockfile; loại output và secret khỏi context. |
| `.gitattributes` | Giữ LF cho Dockerfile, Compose và tài liệu mới khi checkout trên Windows. |
| `infra/.env.example` | Placeholder local và cổng cấu hình; `infra/.env` không track. |
| `apps/{store,admin}/next.config.ts` | Rewrite dev `/api/:path*` tới service `api` khi có `API_INTERNAL_URL`. |
| `infra/README.md`, `README.md` | Lệnh cài, cấu hình, build, chạy, log, dừng, reset, xử lý lỗi. |

Host `127.0.0.1:5432/8080/3000/3001` lần lượt tới PostgreSQL/API/Store/Admin; có thể đổi cổng host bằng `POSTGRES_PORT`, `API_PORT`, `STORE_PORT`, `ADMIN_PORT`. Mạng Compose nội bộ dùng `postgres:5432` và `api:8080`. API P0-01 không có DbContext nên chưa mở kết nối DB, cũng chưa có endpoint health; `depends_on postgres: service_healthy` kiểm startup order, `store/admin` chờ API process start. Store/Admin health check `/`; API được kiểm qua log và HTTP `404` từ `/`.

Compose đặt `ASPNETCORE_ENVIRONMENT=Development`, `ASPNETCORE_HTTP_PORTS=8080`, `DOTNET_USE_POLLING_FILE_WATCHER=1`, `DOTNET_WATCH_SUPPRESS_STATIC_FILE_HANDLING=1` và `DOTNET_WATCH_RESTART_ON_RUDE_EDIT=1` cho API; `API_INTERNAL_URL=http://api:8080`, `PORT=3000/3001` cho Store/Admin. Bốn biến `*_PORT` trong `infra/.env` chỉ đổi cổng host; không đổi DNS/cổng nội bộ. `POSTGRES_USER`, `POSTGRES_DB`, `POSTGRES_PASSWORD` dùng để khởi tạo PostgreSQL khi volume còn mới.

## Chạy và kiểm tra

Từ gốc repository trong PowerShell: tạo `infra/.env` từ `infra/.env.example` nếu chưa tồn tại, thay `POSTGRES_PASSWORD` local, rồi chạy:

```powershell
docker compose -f infra/compose.dev.yml config --quiet
docker compose -f infra/compose.dev.yml build
docker compose -f infra/compose.dev.yml up -d
docker compose -f infra/compose.dev.yml ps
docker compose -f infra/compose.dev.yml logs postgres api store admin
docker compose -f infra/compose.dev.yml down
```

`down` không xóa volume. Rollback code: revert các file P0-02; rollback dữ liệu không áp dụng vì không tạo migration. Xóa volume bằng `down -v` chỉ khi chủ máy chấp thuận mất dữ liệu dev. Không dùng dữ liệu thật.

`up -d` chạy source có trong image; muốn hot reload khi sửa code, chạy thêm `docker compose -f infra/compose.dev.yml watch` trong terminal khác. Compose Watch đồng bộ backend (bỏ qua `bin/obj`) và source Next.js/shared package; output build ở trong container riêng.

## Bằng chứng kiểm tra

| Kiểm tra | Kết quả |
|---|---|
| `docker compose -f infra/compose.dev.yml config --quiet` | PASS sau khi tạo `infra/.env` từ ví dụ; exit 0. |
| `docker compose --env-file infra/.env.missing-test -f infra/compose.dev.yml config --quiet` | Kiểm tra âm PASS: file tạm thiếu `POSTGRES_PASSWORD` làm lệnh exit 1 với thông báo `required variable POSTGRES_PASSWORD is missing a value`. File thử đã xóa. |
| `docker compose -f infra/compose.dev.yml build` | PASS, exit 0; 3 image API/Store/Admin build được. API `dotnet restore` locked + build 0 warning/0 error; Store/Admin `next build` PASS. |
| `docker compose -f infra/compose.dev.yml up -d` và `ps` | PASS; 4 service chạy, PostgreSQL/Store/Admin healthy, API up. API chưa có healthcheck vì scaffold chưa có endpoint health. |
| Log và HTTP | Log API ghi host/watch khởi động, không còn lỗi `staticwebassets.development.json` hoặc warning URLS; PostgreSQL ready. Host GET `/`: Store 200, Admin 200, API 404 đúng scaffold. Store/Admin GET `/api/p0-02-proxy-probe` đều trả 404 dự kiến do API chưa có endpoint. |
| PostgreSQL readiness/kết nối | `pg_isready` báo accepting; `docker compose -f infra/compose.dev.yml exec -T postgres psql -U teapremium_dev -d teapremium_dev -tAc 'SELECT 1;'` PASS, exit 0, output `1`. |
| Docker DNS và proxy | Từ container Store và Admin, Node `fetch('http://api:8080/')` đều PASS (exit 0), nhận status 404 của API scaffold. `curl.exe -sS -D - -o NUL` tới `/api/p0-02-proxy-probe` trên API trực tiếp và qua Store/Admin đều trả 404 với `Server: Kestrel`, `Content-Length: 0`; xác nhận hai rewrite chuyển request tới API, không phải trang 404 của Next.js. |
| Persistence | Tạo database thử `p0_02_persistence_probe` bằng `psql` PASS; `restart postgres` rồi truy vấn `pg_database` vẫn thấy marker. Sau `down`, `docker volume inspect teapremium-dev_pgdata` xác nhận volume tồn tại; `up -d` lần nữa và marker DB vẫn còn. |
| Compose Watch API | `docker compose -f infra/compose.dev.yml watch --no-up --quiet api` đồng bộ file C# tạm; `dotnet watch` ghi `C# and Razor changes applied in 1193ms`. Xóa file gây rude edit; với `DOTNET_WATCH_RESTART_ON_RUDE_EDIT=1`, API tự restart và trả 404. File thử đã xóa, không track. |
| Compose Watch Store/Admin | Sau khi `up -d` và GET `/` ở cả hai app trả 200 không có marker, chạy `docker compose -f infra/compose.dev.yml watch --no-up --quiet store admin`. Thêm thuộc tính tạm `data-p002-probe` vào hai `page.tsx`: Watch báo sync mỗi service, GET `/` trả 200 có marker. Xóa thuộc tính: Watch sync lại, GET `/` trả 200 và marker biến mất. Hai file thử không còn diff. Bind mount source trước đó không nhận lần hoàn tác ổn định trên Windows; đã thay bằng Compose Watch. |
| `docker compose -f infra/compose.dev.yml down` | PASS, exit 0; đã chạy sau kiểm tra persistence và lần cuối, stack hiện dừng; named volume vẫn tồn tại. Không chạy `down -v`. |
| `corepack pnpm install --frozen-lockfile`, `corepack pnpm run lint`, `typecheck`, `build` | PASS (exit 0), lockfile up to date; cả `api-client`, Store, Admin; Next build tạo route `/` và `/_not-found`. |
| `dotnet restore backend/TeaPremium.sln --locked-mode`, `dotnet build backend/TeaPremium.sln --no-restore -warnaserror`, `dotnet test backend/TeaPremium.sln --no-build`, `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS (exit 0) trên host với SDK 10.0.404; restore 7 project, build 0 warning/0 error, 5 ArchitectureTests và 1 IntegrationTest đạt; UnitTests có 0 case. Docker build cũng restore/build theo lockfile. |

## Rủi ro và giới hạn

G4 đã chạy lại Compose `config`, `build --no-cache`, `up`, `ps`, HTTP/proxy, DNS, truy vấn PostgreSQL, persistence qua restart và `down`/`up`; các lệnh pnpm và .NET trong bảng trên đều PASS độc lập. Reviewer không chạy lại thử nghiệm sửa source cho Compose Watch; bằng chứng G3 ở trên là kết quả của lượt triển khai. API chưa có health endpoint hoặc DbContext nên G4 chưa xác minh API readiness hay kết nối DB. Sau review, stack đã dừng và named volume vẫn được giữ.

- Mật khẩu ví dụ là placeholder, phải thay và chỉ dùng local; cổng bind loopback. `.env` bị ignore. PostgreSQL volume giữ dữ liệu qua `down` và đổi password trong env không thay password của volume cũ.
- API chưa có endpoint hoặc kết nối DB; không thể chứng minh nghiệp vụ hay health readiness. Không có migration hoặc seed ở P0-02.
- Rewrite trong Next dev giữ cùng origin nhưng chưa mô phỏng các quy tắc đường dẫn, trusted proxy, TLS và cookie của Caddy production. Không dùng cấu hình này làm production.
- Compose Watch đồng bộ source cho API/Store/Admin để hot reload trên Docker Desktop; chỉ `up -d` dùng source đã build trong image. Thay manifest/config/dependency cần rebuild image.
- Lần khởi động đầu `dotnet watch` báo lỗi đọc `obj\\Debug/.../staticwebassets.development.json`; lỗi lặp lại sau khi bỏ bind mount, nên không quy cho artifact Windows. Đây là lỗi đường dẫn POSIX của .NET watch với static assets ([dotnet/sdk #54309](https://github.com/dotnet/sdk/issues/54309)); API scaffold không có static assets nên Compose đặt `DOTNET_WATCH_SUPPRESS_STATIC_FILE_HANDLING=1` theo [tài liệu Microsoft](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-watch). Log sau sửa sạch lỗi này. Compose Watch tránh đồng bộ `bin/obj` của host; C# hot reload và auto restart sau rude edit đã được kiểm thực tế.
