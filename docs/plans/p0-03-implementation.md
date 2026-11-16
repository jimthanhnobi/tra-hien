# P0-03 — Backend cross-cutting

**Trạng thái:** G3 trên `feature/p0-03-cross-cutting`; chờ G4 review độc lập. Không commit, push hoặc merge trong task này.

## Nguồn, phụ thuộc và phạm vi

| Nguồn | Liên hệ |
|---|---|
| `docs/spec/07-roadmap.md:19` | P0-03; chỉ phụ thuộc P0-01 đã có trên `main` tại `16cffc6`. |
| `docs/spec/02-architecture.md` §3–5 | Luật phụ thuộc, handler/result/decorator, Money, clock/calendar, transaction và logging. |
| `docs/spec/05-api.md` §1 | `application/problem+json`, mã lỗi, HTTP status và `traceId`. |
| `docs/spec/00-README.md` §3, DEC-01, DEC-20 | VND nguyên, thời gian UTC/ngày VN, modular monolith. |
| `docs/spec/01-requirements.md:201` | NFR-04: không đưa PII vào log/response public. |
| `docs/spec/07-roadmap.md:37` | AC-P0-05: Application không tham chiếu EF Core hoặc gọi clock hệ thống trực tiếp; ArchitectureTests kiểm. |

Không có FR/BR riêng cho nền tảng P0-03. AC-P0-03 (CSRF) thuộc IAM giai đoạn sau, không phải nghiệm thu của task này. P0-02 đã merge vào `main` tại `1b6e8ec`; nhánh P0-03 đã cập nhật base đó sau khi giữ nguyên toàn bộ thay đổi P0-03. Không thêm AppDbContext, migration, endpoint nghiệp vụ, auth hoặc fake production persistence.

## Thiết kế và file thay đổi

- `TeaPremium.Domain`: `Money(long Vnd)` là `readonly record struct`; constructor và toán tử từ chối giá trị âm, `Adjustment(long)` cho trường hợp điều chỉnh có chủ ý; cộng/trừ dùng `checked`. `DomainException` mang code nội bộ, response dùng thông báo public chung để không lộ PII trong exception message.
- `TeaPremium.Application`: `IClock`, `BusinessCalendar` cố định UTC+7 theo `Asia/Ho_Chi_Minh`, kỳ `[from,to)`; `Result<T>`, `Error` và năm category đã chốt. `ICommandHandler`/`IQueryHandler` được scan assembly; FluentValidation chạy trước handler và bên ngoài transaction decorator. `IUnitOfWork` là port: implementation P0-04 phải commit chỉ khi `Result` thành công, rollback khi lỗi, exception hoặc cancellation. Chưa đăng ký concrete UoW.
- `TeaPremium.Infrastructure`: `SystemClock` bọc `TimeProvider`; không có DB adapter ở P0-03.
- `TeaPremium.Api`: map `Error` sang RFC 9457 ProblemDetails; `business_rule` có code thuộc nhóm 409 trong 05 §1 được map 409, còn lại 422. Middleware chấp nhận inbound correlation ID chỉ khi là GUID N/D; nếu không tạo GUID v7. JSON console ra stdout với `correlation_id` trong structured state của request/error log; tắt framework scopes vì chúng chứa `RequestPath`. Request path/query/body/header và exception message không được log.
- Cập nhật `Directory.Packages.props`, Application/UnitTests project references và NuGet lockfiles; thêm UnitTests, IntegrationTests, ArchitectureTests. Các project references theo P0-01 được giữ nguyên.

Failure/boundary đã kiểm: Money âm và overflow; ngày VN ở hai đầu kỳ và khoảng ngày không hợp lệ; validation sai không mở transaction; success, Result failure và DomainException theo thứ tự decorator/rollback; cancellation trước handler; query không cần UoW; lỗi 400/403/404/409/422; correlation ID chứa số điện thoại hoặc quá dài bị thay; PII trong exception không vào log hoặc public response. Regression test capture JSON console từ WebApplicationFactory với fake PII path/query xác nhận output không có `RequestPath`, số điện thoại hay email và vẫn có `correlation_id`. Không có endpoint auth ở P0-03, nên chỉ kiểm mapping `forbidden` → 403; 401/CSRF chờ IAM.

## Bằng chứng G3

| Lệnh | Kết quả thực tế |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS, SDK 10.0.404, 7 project restore theo lockfile. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS; 0 warning, 0 error. |
| `dotnet test backend/TeaPremium.sln --no-build` | PASS; 11 UnitTests, 16 IntegrationTests, 6 ArchitectureTests; 0 failed, 0 skipped. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS, exit 0. |
| Frontend pnpm checks | NOT APPLICABLE: không sửa frontend hoặc pnpm workspace. |
| `docker compose -f infra/compose.dev.yml config --quiet`; `build api`; `up -d`; `ps` | PASS sau merge P0-02: Docker API locked restore 7 project/build 0 warning/error; 4 service started, PostgreSQL/Store/Admin healthy và API up. Sau fix log, `build api` và `up -d` chạy lại PASS với cùng trạng thái 4 service. |
| HTTP/DNS/PostgreSQL sau merge P0-02 | PASS trước fix log: Store/Admin `/` = 200; API `/` và proxy `/api/p0-03-proxy-probe` từ hai app = 404 Kestrel kèm correlation header; `pg_isready` accepting, `psql SELECT 1` = `1`. `down` PASS, `ps --all` trống, volume vẫn còn. |
| Docker API JSON log sau fix | PASS: GET `/api/0901234567?email=secret@example.com` với `X-Correlation-ID` GUID trả 404 Kestrel và echo ID. `docker compose logs --no-log-prefix --tail 100 api` được parse: `request_log_count=1`, `State.correlation_id` đúng GUID, không có phone/email, `RequestPath` hoặc `Scopes`. Sau đó `down` PASS, `ps --all` trống, named volume `teapremium-dev_pgdata` còn. |

## Giới hạn và rollback

- Transaction decorator mới được kiểm bằng UoW giả trong **tests**; không có transaction DB production cho đến P0-04. Không có migration hay dữ liệu cần rollback.
- API scaffold chưa có endpoint nghiệp vụ hoặc health readiness/DB connection; `/health/ready` phụ thuộc P0-04. Không tuyên bố đã kiểm quyền truy cập hay bảo mật production.
- PII từ message lỗi do handler tương lai trả về vẫn cần được kiểm tại từng endpoint khi triển khai nghiệp vụ; middleware P0-03 không log PII của request/exception và DomainException không phản chiếu message public.
- Lượt Compose đầu sau merge P0-02 cho thấy `IncludeScopes=true` đã xuất `RequestPath` từ scope mặc định ASP.NET, vi phạm NFR-04 nếu URL chứa PII. Fix hẹp: `IncludeScopes=false`, ghi `correlation_id` tường minh vào structured state; regression test capture JSON stdout và Docker runtime sau fix đều PASS.
- Rollback code bằng cách bỏ diff P0-03 trên nhánh này; không cần thao tác DB. `docker compose down` giữ volume PostgreSQL; không chạy `down -v` để tránh mất dữ liệu dev.
