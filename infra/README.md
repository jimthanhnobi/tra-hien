# Chạy Trà Hiên V2 trên máy phát triển

`compose.dev.yml` chạy PostgreSQL 17, API .NET 10, Store và Admin Next.js. Chỉ dùng dữ liệu thử. Đây là cấu hình **development**. Migration nền tảng đã có từ P0-04 nhưng **phải áp dụng riêng**, không tự chạy khi API khởi động. Seed, IAM, backup và các giới hạn bảo mật production chưa được triển khai.

## Chuẩn bị trên Windows PowerShell

Cài Docker Desktop với Linux containers và Docker Compose; kiểm tra `docker version` và `docker compose version`. Từ gốc repository:

```powershell
if (-not (Test-Path -LiteralPath infra/.env)) {
  Copy-Item -LiteralPath infra/.env.example -Destination infra/.env
}
notepad infra/.env
docker compose -f infra/compose.dev.yml config --quiet
```

Đổi `POSTGRES_PASSWORD` thành mật khẩu chỉ dùng cho máy này trước khi khởi động. `infra/.env` được `.gitignore` bỏ qua. Dùng `config --quiet` để không in mật khẩu ra terminal. Không đưa dữ liệu khách thật vào DB dev. Nếu đã có `infra/.env`, lệnh trên giữ nguyên file đó; chỉ cập nhật các biến cần thiết.

| Biến trong `infra/.env` | Mặc định trong ví dụ | Vai trò |
|---|---|---|
| `POSTGRES_USER` / `POSTGRES_DB` | `teapremium_dev` | Tài khoản và database local. |
| `POSTGRES_PASSWORD` | placeholder cần thay | Mật khẩu local; Compose yêu cầu giá trị không rỗng. |
| `POSTGRES_PORT` | `5432` | Cổng PostgreSQL trên máy host. |
| `API_PORT` | `8080` | Cổng API trên máy host. |
| `STORE_PORT` / `ADMIN_PORT` | `3000` / `3001` | Cổng hai app trên máy host. |

**Kết nối API tới PostgreSQL:** Compose nội suy `POSTGRES_USER`, `POSTGRES_DB`, `POSTGRES_PASSWORD` từ `infra/.env` vào biến môi trường `ConnectionStrings__Default` của container API, dùng `Host=postgres;Port=5432`. ASP.NET Core đọc thành `ConnectionStrings:Default`, theo `PersistenceRegistration.cs`. Mật khẩu được đặt trong dấu nháy kép của connection string để chứa được dấu `;`; **quy ước development: `POSTGRES_PASSWORD` không được chứa dấu nháy kép `"`**. Giá trị có dấu `;` được hỗ trợ; không dùng cách thêm `\` hay nhân đôi `"` để né giới hạn này vì có thể làm mật khẩu API khác mật khẩu PostgreSQL đã khởi tạo. Nếu `.env` hiện chứa `"`, hãy chọn mật khẩu dev khác và đồng bộ mật khẩu của role trong DB hiện hữu (đổi `.env` không tự thay mật khẩu đã lưu trong volume PostgreSQL). Không in đầu ra `docker compose config` (không có `--quiet`) hoặc `docker inspect` để chia sẻ vì chúng có thể lộ mật khẩu. Compose chỉ cung cấp cấu hình, **không tự tạo schema/migrate**; áp migration P0-04 lên DB dev dùng EF CLI riêng theo hướng dẫn `docs/plans/p0-04-implementation.md`, không chạy `Migrate`/`EnsureCreated` từ API.

Các cổng chỉ bind vào `127.0.0.1`. Nếu cổng đã bị chiếm, đổi cổng host tương ứng trong `infra/.env`; cổng **trong container** vẫn là PostgreSQL `5432`, API `8080`, Store `3000`, Admin `3001`. Docker DNS dùng tên `postgres` và `api`, không dùng `localhost` để kết nối giữa container.

## Build, chạy và kiểm tra

```powershell
docker compose -f infra/compose.dev.yml build
docker compose -f infra/compose.dev.yml up -d
docker compose -f infra/compose.dev.yml ps
docker compose -f infra/compose.dev.yml logs -f postgres api store admin
```

Mở `http://localhost:3000/` cho Store và `http://localhost:3001/` cho Admin. API ở `http://localhost:8080/`; P0-01 chưa đăng ký endpoint nên HTTP `404` tại `/` là bình thường, nhưng container phải lắng nghe và log không có lỗi khởi động. Kiểm tra PostgreSQL bằng:

```powershell
docker compose -f infra/compose.dev.yml exec postgres pg_isready -U teapremium_dev -d teapremium_dev
docker compose -f infra/compose.dev.yml exec -T postgres psql -U teapremium_dev -d teapremium_dev -tAc "SELECT 1;"
```

`SELECT 1` xác nhận PostgreSQL có thể truy vấn, **không** chứng minh API đã truy cập DB. P0-06 phải kiểm thử thực tế đường kết nối của dispatcher khi background worker được đăng ký. Nếu đổi thông tin đăng nhập trên `infra/.env` mà vẫn giữ volume cũ, PostgreSQL **không tự đổi** user/password/database đã khởi tạo.

Hai app proxy `/api/*` cùng origin tới `http://api:8080/api/*` bằng Next.js rewrite **chỉ khi** `API_INTERNAL_URL` được đặt bởi Compose. Đây là proxy transport của dev, không có BFF hay xử lý auth. Chưa có endpoint API nên `/api/*` hiện trả `404`. Production dùng Caddy theo DEC-16; cấu hình dev này không thay Caddy và chưa mô phỏng các quy tắc chặn đường dẫn của production. API không có health endpoint ở P0-01, nên Compose chỉ chờ process API khởi động; `ps` và log xác nhận API. Store/Admin có health check HTTP cho `/`.

`up -d` chạy source đã đóng gói trong image. Để bật hot reload cho API, Store và Admin, mở PowerShell khác từ gốc repo và chạy:

```powershell
docker compose -f infra/compose.dev.yml watch
```

Compose Watch đồng bộ `backend/` (bỏ qua `bin/obj` của host), `apps/*/src` và `packages/api-client/src` vào container; `dotnet watch` và Next.js dev server áp dụng thay đổi. API đặt `DOTNET_WATCH_SUPPRESS_STATIC_FILE_HANDLING=1` vì scaffold không dùng static assets, tránh lỗi đường dẫn `staticwebassets.development.json` của .NET watch trên POSIX. `DOTNET_WATCH_RESTART_ON_RUDE_EDIT=1` tự restart API khi thay đổi C# không thể hot reload, tránh prompt trong container detached. Giữ terminal watch mở khi sửa source. Khi sửa manifest, Dockerfile, `next.config.ts` hoặc dependency, chạy lại `build` rồi `up -d`. Docker Desktop trên Windows cần quyền chia sẻ ổ đĩa chứa repository.

## Dừng, khởi động lại, reset

```powershell
docker compose -f infra/compose.dev.yml stop
docker compose -f infra/compose.dev.yml start
docker compose -f infra/compose.dev.yml down
```

`down` dừng và bỏ container/network nhưng **giữ** volume `teapremium-dev_pgdata`; `up -d` sau đó dùng lại DB. Muốn reset ứng dụng mà giữ DB, dùng `down`, `build`, `up -d`. **Xóa DB dev là thao tác mất dữ liệu**: chỉ sau khi xác nhận không còn dữ liệu cần giữ, chạy `docker compose -f infra/compose.dev.yml down -v`, rồi `up -d` để tạo volume mới. Không chạy lệnh này trên dữ liệu thật.

## Khi có lỗi

- `config` báo thiếu biến: kiểm tra `infra/.env` ở cạnh compose file, nhất là `POSTGRES_PASSWORD`.
- Cổng host bị chiếm: đổi `*_PORT` trong `infra/.env` và chạy `up -d` lại.
- PostgreSQL chưa healthy: xem `docker compose -f infra/compose.dev.yml logs postgres`; với volume cũ, đổi `POSTGRES_USER/DB/PASSWORD` trong `.env` không đổi thông tin đã tạo trong DB.
- API chưa lên: xem `logs api`; phải có SDK .NET 10.0.404 trong Docker image build và các NuGet package restore theo lockfile. Lỗi `staticwebassets.development.json` trong `dotnet watch` có thể do đường dẫn POSIX của SDK, không chứng minh build API thất bại; Compose tắt riêng static-file handling vì P0-01 chưa có static assets.
- Store/Admin lỗi module hoặc proxy: `build` lại sau khi đổi package/config; kiểm `API_INTERNAL_URL=http://api:8080` trong container. Không dùng `localhost:8080` trong cấu hình container.
- Hot reload không nhận file: xác nhận Docker Desktop đã chia sẻ thư mục dự án và lệnh `docker compose -f infra/compose.dev.yml watch` đang chạy trong terminal riêng. Dùng `up -d --build` nếu sửa ngoài các source path được đồng bộ.
