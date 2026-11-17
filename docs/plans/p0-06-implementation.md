# P0-06 — Outbox, dispatcher và khung PeriodicJob

**Trạng thái:** G3 remediation sau G4 BLOCK — READY_FOR_G4, chờ G4 độc lập kiểm lại. Không có commit, push hoặc PR từ task này.

## Tiền kiểm và nguồn

- Ngày 17/11/2026: `git fetch origin main`; nhánh `feature/p0-06-outbox-jobs`; HEAD và `origin/main` cùng `446fe358aa20ae6c32fbb3600e64e3dab43e0f3b` (merge PR #11); working tree sạch trước khi sửa.
- Đã đọc `AGENTS.md`, `docs/WORKFLOW.md`, `docs/CODEX_USAGE.md`, skill `implementation-slice`, các spec liên quan và mã/test P0-04/P0-05. G1/G2 có hồ sơ ở `docs/analysis/` và `docs/architecture/`; P0-06 là một task được owner giao riêng.
- Task gốc: `docs/spec/07-roadmap.md:22` (phụ thuộc P0-04). `FR-PLT-02`: `01-requirements.md:191`; `BR-PLT-02`: `04-business-rules.md:280`; `NFR-04/08/09`: `01-requirements.md:201,205–206`; `DEC-01`: `00-README.md:85`; `02-architecture.md:96,105–107,131–145`; `03-database.md:7–18`; `schema.sql:81–92`; `06-infrastructure.md` §1 và §7. `AC-P0-05` (`07-roadmap.md:38`) bảo vệ ranh giới Application/EF Core; `AC-P0-04` là hồi quy audit tích hợp với P0-09, chưa tự nhận hoàn thành bởi P0-06.
- Không có mâu thuẫn nguồn nào chặn P0-06. `BR-PLT-02` còn yêu cầu thông báo Admin/Telegram khi dead; UI/Telegram thực thuộc các task sau. P0-06 lưu trạng thái `dead`, không triển khai provider thật.

## Hợp đồng và phạm vi

`IUnitOfWork.ExecuteAsync` từ P0-04 mở một transaction cho command, lưu mọi thay đổi EF pending trước commit; Result lỗi, exception và cancellation rollback, rồi xóa change tracker. Outbox writer dùng cùng scoped `AppDbContext` và đòi transaction đang mở. Một command ghi business row và outbox row qua cùng UoW; không phát tác động bên ngoài trực tiếp từ command. ID outbox là UUID v7, thời điểm lấy từ `IClock`.

Dispatcher chạy `BackgroundService`, tạo scope cho mỗi lượt, lấy tối đa batch cấu hình bằng `FOR UPDATE SKIP LOCKED`. Row lock được giữ cho đến sau handler và commit trạng thái. Nhiều instance có thể xử lý các row khác nhau; một row bị khóa được bỏ qua. Lỗi tăng `attempts`, tính `available_at = now + min(2^attempts phút, 6 giờ)`; lần thứ 10 chuyển `dead`. `last_error` chỉ chứa mã cố định; log không ghi payload, exception hoặc PII. Chưa có handler SMTP/Telegram/webhook.

**At least once:** tiến trình có thể dừng sau khi handler tạo tác động bên ngoài và trước khi `processed` commit. Khi khởi động lại, cùng message ID có thể được giao lần nữa. Mọi handler tương lai phải dùng message ID làm khóa idempotency hoặc có phép xử lý lặp an toàn; P0-06 chỉ cung cấp interface và test mô phỏng, chưa bảo đảm idempotency của provider tương lai. Thay đổi DB của handler trên cùng context được rollback về savepoint nếu delivery thất bại, rồi mới ghi retry.

`PeriodicJob` là lớp nền chưa đăng ký job nghiệp vụ. Mỗi tên job ổn định sinh một advisory lock key xác định; một `NpgsqlConnection` vật lý được mở, giữ session lock và transaction cho toàn bộ công việc DB. Callback chỉ nhận `AppDbContext` gắn chính connection đó; scope nội bộ chỉ resolve audit interceptor, không được đưa cho callback. Failure/cancellation rollback trước unlock trên cùng session; cleanup dùng token riêng và loại pool nếu thất bại. Mất session hủy transaction của owner cũ, nên owner cũ không thể commit công việc được bảo vệ dù callback vẫn còn unwind. Chi tiết remediation và giới hạn ở phần cuối.

Ngoài phạm vi: P0-07 idempotency middleware, IAM, admin incident UI, notification provider, webhook, các job nghiệp vụ P1/P2/P3/P5. Không sửa `docs/spec/`, schema đích hoặc migration. Không tự chạy EF migration lúc API startup.

## Tình huống kiểm thử

| Nhóm | Chứng cứ cần có |
|---|---|
| Thành công | Business row + outbox row cùng commit; API worker nhận `ConnectionStrings__Default` và truy vấn PostgreSQL 17 thật; UUID v7, `IClock`. |
| Lỗi/cạnh | Result lỗi, exception, cancellation rollback cả hai; JSON sai/ghi ngoài transaction bị chặn; row bị khóa được skip; batch/poll có giới hạn. |
| Đồng thời/phát lại | Hai dispatcher xử lý row riêng; crash sau side effect cho phép giao lại cùng ID; handler mô phỏng idempotent. |
| BR-PLT-02 | Backoff đến trần 6 giờ, tối đa 10 lần rồi dead; không rò payload/PII qua `last_error` hoặc log. |
| PeriodicJob | Cùng tên chỉ một instance vào việc; tên khác có khóa riêng; lock được giải phóng khi thành công, lỗi, cancellation. |
| Hồi quy | P0-05 audit, full unit/architecture/integration, model không đổi, Compose dev. |

## Migration, triển khai và rollback

`platform.outbox_messages` đã được tạo bởi migration P0-04 với `ix_outbox_pending` và CHECK status. P0-06 không đổi model hoặc DDL, nên không cần forward/down migration. Triển khai cần schema P0-04 đã được áp riêng trước khi bật API worker. Nếu rollback ứng dụng, dừng worker rồi quay lại build trước P0-06; giữ nguyên outbox row `pending`/`dead` để xử lý lại khi triển khai phiên bản tương thích. Không xóa outbox hay audit history. Worker có thể chạy trên nhiều API instance nhờ row lock; không có bảo đảm exactly once cho tác động bên ngoài.

## File thay đổi

| Đường dẫn | Mục đích |
|---|---|
| `backend/src/TeaPremium.Application/Outbox/` | Port writer và handler, chuyển message ID cho handler; không tham chiếu EF Core. |
| `backend/src/TeaPremium.Infrastructure/Outbox/` | EF writer và batch processor với row lock, savepoint, retry/dead. |
| `backend/src/TeaPremium.Api/Outbox/`, `backend/src/TeaPremium.Api/Program.cs` | Hosted dispatcher, cấu hình batch/poll, scope và log cố định. |
| `backend/src/TeaPremium.Infrastructure/Jobs/PeriodicJob.cs` | Advisory lock theo tên ổn định, connection cùng session qua toàn bộ job. |
| `backend/src/TeaPremium.Infrastructure/Persistence/PersistenceRegistration.cs` | Đăng ký scoped writer/processor. |
| `backend/Directory.Packages.props`, `backend/src/TeaPremium.Infrastructure/TeaPremium.Infrastructure.csproj`, ba `packages.lock.json` bị tác động | Hosting/Logging abstractions cho khung job; giữ restore locked. |
| `backend/tests/TeaPremium.IntegrationTests/{OutboxTests,OutboxLoggingTests,OutboxAttemptLimitTests,PeriodicJobTests}.cs` | PostgreSQL 17 Testcontainers và log redaction. |
| `docs/plans/p0-06-implementation.md` | Hồ sơ G3/G4, bằng chứng và rủi ro. |

## Bằng chứng G3 ban đầu trước G4 BLOCK

| Lệnh/check | Kết quả tại diff cuối |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode -v:q` | PASS, exit 0. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror -v:q` | PASS, exit 0; 0 warnings, 0 errors. |
| `dotnet test backend/TeaPremium.sln --no-build -v:q` | PASS, exit 0; 11 unit + 6 architecture + 108 integration = 125/125, 0 skipped. Gồm hồi quy audit P0-05. |
| `dotnet test ... --filter FullyQualifiedName~OutboxTests` | PASS 10/10; host worker kết nối PostgreSQL 17 Testcontainer bằng `ConnectionStrings:Default` và xử lý row thật. |
| `dotnet test ... --filter FullyQualifiedName~PeriodicJobTests` | PASS 3/3; tranh khóa đa instance, tên riêng, release sau success/failure/cancellation. |
| `dotnet test ... --filter FullyQualifiedName~OutboxLoggingTests` và `~OutboxAttemptLimitTests` | PASS 1/1 mỗi nhóm; log không nhận exception/secret, row đã 10 attempts không giao lần 11. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS sau khi chuẩn hóa whitespace của test mới. |
| EF pending model changes | **Không áp dụng:** không sửa `AppDbContext`, migration hoặc snapshot; không có model delta. `dotnet-ef` không được cài trên máy này, không gọi check CLI rồi coi là PASS. |
| `git diff --check` và scan trailing whitespace file mới | PASS, exit 0; warning Git về CRLF của ba NuGet lock file không phải lỗi diff. |
| Docker Compose | PASS trên diff cuối: `config --quiet`, `up -d --build postgres api` (restore locked và build 0 warnings/errors trong image), `pg_isready` nhận kết nối, API HTTP 404 như scaffold. Project/volume và env tạm đã dọn bằng `down -v`. Hai lần script đầu chỉ lỗi lệnh HTTP của PowerShell 5 (`-SkipHttpErrorCheck`/thiếu assembly `HttpClient`), không phải lỗi sản phẩm; lần dùng `Invoke-WebRequest -UseBasicParsing` đã PASS. |

G4 cần reviewer độc lập đối chiếu diff với `P0-06`, `FR-PLT-02`, `BR-PLT-02`, `NFR-04/08/09`, `DEC-01` và các case ở trên. Chưa tự nhận G4 đã duyệt. Rủi ro còn lại: provider tương lai phải thực hiện idempotency theo message ID; UI Admin và Telegram cho `dead` chưa có trong P0-06; production DB role/notification settings và schema phải được triển khai riêng theo các task sau. Compose smoke không áp migration trên DB throwaway, nên kết nối dispatcher tới bảng outbox được chứng minh bởi WebApplicationFactory/Testcontainers, không suy từ HTTP 404.

## Remediation hai P1 của G4 BLOCK

Scope được owner giao: chỉ sửa P1-01 và P1-02 trong P0-06; giữ base `446fe358aa20ae6c32fbb3600e64e3dab43e0f3b`, nhánh `feature/p0-06-outbox-jobs`, mọi thay đổi tracked/untracked hiện có. Parent phụ trách lock boundary; implementer được giao riêng outbox recovery. Implementer không thay thế original G4 reviewer. Không sửa spec, schema/model/migration, frontend, dependency hoặc chức năng P0-07 trong vòng remediation.

### P1-01 — Lost advisory-lock ownership

**G4 BLOCK gốc:** `PeriodicJob.cs` bản trước tại dòng 61/69/81 mở connection giữ lock nhưng truyền `IServiceProvider` cho callback. Callback có thể lấy context/connection khác; khi PostgreSQL session giữ lock bị terminate, callback cũ vẫn chạy và có thể commit độc lập. Probe G4 được chạy lại trước sửa, trên PostgreSQL 17 Testcontainer: `terminated=True`, `second_acquired=True overlap=True`; A chỉ báo `InvalidOperationException` lúc cleanup.

**Sửa:** giữ `pg_try_advisory_lock` và SHA-256 lock key ổn định. Connection được pin với `Multiplexing=false`. Sau acquire, tạo một `AppDbContext` dùng connection đã mở và audit interceptor scoped; mở transaction trên context này. `ExecuteJobAsync(AppDbContext, CancellationToken)` chỉ nhận context thuộc boundary đó. Flush pending changes và commit trước khi unlock. Result `false` chỉ có nghĩa instance khác đang giữ lock; transaction/ownership failure ném lỗi, không báo thành công. Khi backend bị ngắt, PostgreSQL hủy cả transaction của A và giải phóng session lock; B có thể acquire/commit trong khi A không thể commit các thay đổi trước hoặc sau lock loss.

Exception/cancellation rollback bằng cleanup token có timeout riêng 5 giây. Unlock trên chính session đã acquire; unlock/rollback failure loại pool để connector đang dùng bị đóng khi dispose. Dispose transaction/context diễn ra trước unlock, rồi dispose connection. Không dùng heartbeat làm fencing; không thêm lease table. Scope chỉ cung cấp audit interceptor cho context nội bộ, không cung cấp một context độc lập cho protected work.

**Giới hạn bắt buộc:** scaffold P0-06 chỉ bảo vệ công việc DB trên context được cấp. Job không được tự mở/inject context/connection độc lập cho protected writes, tự commit/rollback/đổi connection của context, hoặc gọi provider ngoài DB. Đây là contract cho implementation job tương lai, không phải sandbox chống code cố tình bypass. Callback có thể còn chạy trong bộ nhớ sau khi mất session; không tuyên bố không bao giờ có hai callback cùng active. Bảo đảm là owner cũ không thể commit transaction được bảo vệ sau lock loss. External effect phải đi qua transactional outbox, provider idempotency hoặc fencing design được duyệt riêng; không có exactly-once claim.

### P1-02 — Handler flush failure không được tính retry

**G4 BLOCK gốc:** `OutboxBatchProcessor.cs:86` bản trước flush handler mutation ngoài delivery try/catch. Handler thêm Ward có ProvinceCode không tồn tại rồi trả thành công; FK failure rollback transaction nhưng attempts vẫn 0. Hai lượt xử lý đều lỗi và row đầu giữ hàng đợi.

**Sửa:** sau row claim trong transaction và savepoint `before_delivery`, chạy handler rồi `SaveChangesAsync` ngay trong delivery try/catch, trước khi đánh dấu processed. Handler exception hoặc flush failure rollback về savepoint, clear tracker, reload outbox row vẫn bị khóa, tăng attempts đúng một lần, tính backoff bằng `IClock` theo BR-PLT-02 hoặc chuyển dead ở lần 10. Chỉ lưu mã cố định, không payload/PII/exception; commit retry state rồi tiếp tục row đến hạn kế tiếp. Early SaveChanges của handler cũng nằm sau savepoint nên bị rollback cùng pending mutation lỗi.

Savepoint/transaction/commit failure thoát scope với diagnostic cố định `Outbox transaction failed; retry state was not confirmed.` và không đính kèm raw inner exception. Không thử lại trong transaction hỏng và không báo retry state đã lưu. Transaction dispose và tracker clear vẫn thực hiện; orderly cancellation tiếp tục rollback và truyền cancellation, không tính một lần thất bại giao hàng. At-least-once và contract idempotency theo message ID giữ nguyên, gồm tình huống external effect đã xảy ra nhưng DB commit không xác nhận.

### File hỗ trợ và regression tests

| File trong remediation | Lý do |
|---|---|
| `backend/src/TeaPremium.Infrastructure/Jobs/PeriodicJob.cs` | Same-session/context transaction boundary và contract database-only. |
| `backend/tests/TeaPremium.IntegrationTests/PeriodicJobTests.cs` | Điều chỉnh callback API và thêm test ownership/transaction/pooling. |
| `backend/src/TeaPremium.Infrastructure/Outbox/OutboxBatchProcessor.cs` | Flush trong recovery boundary và infrastructure diagnostic cố định. |
| `backend/tests/TeaPremium.IntegrationTests/OutboxRecoveryTests.cs` | 5 case mới cho poison mutation và recovery failure. |
| `docs/plans/p0-06-implementation.md` | G4 findings, root cause, thiết kế và bằng chứng sau remediation. |

`PeriodicJobTests` có 8 cases: hai instance cùng job không cùng vào protected writes, commit tuần tự khi lock còn sống; hai job khác nhau độc lập; release khi failure/cancellation; terminate backend giữ lock bằng connection riêng rồi B acquire/commit, A early/stale writes không tồn tại và A không báo success; success/failure/cancellation trên pool `MaxPoolSize=1, NoResetOnClose=true` xác nhận backend PID tái sử dụng đúng physical connection, không còn advisory lock và connection vẫn dùng được; callback không lộ scoped service provider/context độc lập.

`OutboxRecoveryTests` có 5 cases: invalid pending Ward với/không early save, attempts=1/backoff và valid row kế tiếp tiến triển; prior saves bị rollback; future/processed không giao lại; 10 FK failures chuyển dead; savepoint rollback failure bằng interceptor xác định và mất session PostgreSQL thật đều có diagnostic sanitized, không giả nhận retry đã commit. Test concurrency và audit/UoW cũ không bị bỏ.

### Bằng chứng sau remediation

| Lệnh/check thực chạy | Kết quả |
|---|---|
| `dotnet run --project C:/Users/ADMIN/AppData/Local/Temp/tra-hien-g4-independent/Probe.csproj -v:q` trước sửa | VERIFIED, exit 0; tái hiện cả hai P1 gốc trên PostgreSQL 17. Source probe gốc giữ ngoài repo; API callback mới có thay đổi signature. |
| `dotnet run --project C:/Users/ADMIN/AppData/Local/Temp/tra-hien-g4-remediation-probe/Probe.csproj -v:q` sau sửa | VERIFIED, exit 0; cùng terminate-session/invalid-Ward scenario, adapter callback mới. `terminated=True`, `second_acquired=True`, `first_failed=InvalidOperationException`, `first_reported_success=False stale_owner_rows=0 new_owner_rows=1`; `pass=1 handled=2`, `pass=2 handled=0`, mỗi poison row `pending attempts=1`; `BOTH_P1_REPRODUCTIONS_FIXED`. |
| `dotnet restore backend/TeaPremium.sln --locked-mode -v:q` | PASS, exit 0. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror -v:q` | PASS, exit 0; 0 warnings, 0 errors. |
| `dotnet test backend/TeaPremium.sln --no-build -v:q` | PASS, exit 0; 11 unit + 6 architecture + 118 integration = **135/135**, 0 skipped; audit/business UoW regressions vẫn PASS. |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --filter 'FullyQualifiedName~Outbox\|FullyQualifiedName~PeriodicJob' -v:q` | PASS **25/25**, PostgreSQL 17 Testcontainers thật. Dấu `\|` trong ô bảng chỉ escape Markdown; filter truyền cho CLI là ký tự pipe thông thường. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS, exit 0, toàn solution. |
| `git diff --check` | PASS, exit 0; Git chỉ cảnh báo CRLF lockfile từ G3 trước đó. |
| EF CLI 10.0.15 `migrations has-pending-model-changes --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --context AppDbContext --no-build` | PASS, exit 0; `No changes have been made to the model since the last migration.` Connection string dummy, không truy cập DB thật. |
| Disposable Compose: `config --quiet`, `up -d --build postgres api`, `exec -T postgres pg_isready`, áp script migration P0-04 riêng rồi seed event unknown | PASS, exit 0; build image 0 warnings/errors. Worker trong API container dùng `ConnectionStrings__Default`, row thành `pending:1:handler_unavailable`; HTTP 404 đúng scaffold, sentinel payload/password không xuất hiện trong API log được scan. `down -v --remove-orphans` chỉ dọn project/volume throwaway và xóa env tạm. Không tự migrate startup. |

Lần focused build đầu có MSB3026 retry do parent/implementer cùng build test assembly. Đã ngừng chạy đồng thời; các cổng build warning-as-error và test cuối nêu trên đều chạy sạch. Không có sửa dependency hoặc model trong remediation. Không thực thi production data hay provider thật.

**G4 readiness:** READY_FOR_G4 cho reviewer độc lập kiểm lại hai P1 và toàn diff P0-06; chưa tự duyệt G4 hoặc merge. Rủi ro/giới hạn mới duy nhất là contract database-only rõ ràng của PeriodicJob; external delivery tiếp tục at-least-once qua outbox. Không có forward/down migration mới; rollback triển khai dừng worker trước khi revert code, giữ dữ liệu outbox/audit, và không quay lại khung job cũ cho business jobs trước khi có phiên bản bảo đảm ownership tương đương.
