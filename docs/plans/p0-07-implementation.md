# P0-07 — Idempotency ở endpoint

Trạng thái: G4 độc lập tại `0842fc53a70c6e3f9121652107f4e3eaaec7e938` kết luận **BLOCK** vì P1 cancellation của joined command. Đã sửa tối thiểu và thêm regression bên dưới; **chờ G4 re-review độc lập**, không tự approve/merge. Chỉ P0-07, không bắt đầu task P0 khác.

## G0/G1/G2 và nguồn

- Ngày 17/11/2026: working tree ban đầu sạch, đang ở `feature/p0-11-github-actions-ci`. `git fetch origin main` lấy main `6e3206b205870ffa653c21bb8652ad0c9cdc9126`; GitHub xác nhận PR #14 merged, merge SHA đúng main này. Tạo `feature/p0-07-idempotency` trực tiếp từ `origin/main`. Không reset, clean hoặc ghi đè local changes; không dùng subagent.
- G0: TeaPremium v2.0, 16/11/2026 tại `docs/spec/00-README.md:3`; base triển khai ở trên. Không suy diễn tuyên bố vận hành/pháp lý trong spec thành kiểm chứng thực tế.
- G1 full baseline: `cadce575597738bf40ad7c45689482de5a5fbc3d`, inventory/gaps/traceability tại `docs/analysis/`. Đã đối chiếu diff từ baseline, tái sử dụng amendment G26/G01/F-01 được duyệt tại `0bfc98e`; `git diff 0bfc98e origin/main -- docs/spec` rỗng. Scoped audit đọc các nguồn trong bảng dưới và gap register; không có xung đột trực tiếp chặn P0-07. Không sửa authoritative specs.
- G2: tái sử dụng `docs/architecture/conformance-review.md`, `docs/plans/task-readiness.md` và evidence P0-04/05/06/10/11. Dependency P0-04 đã ở main; audit/outbox/PeriodicJob/CI cũng đã có. Scoped GO: thêm transaction owner cho endpoint ngay trong `EfUnitOfWork`, giữ nguyên port Application và decorator. Không có transaction framework, UoW, bảng hoặc service khóa thứ hai.

| Nguồn/ID có sẵn | Phạm vi thực hiện |
|---|---|
| `docs/spec/07-roadmap.md:23`, P0-07 → P0-04 | Filter opt-in, không gắn business endpoint sản xuất. |
| `02-architecture.md:105–107`, §5.2; DEC-01 (`00-README.md:85`) | Một transaction cho reservation, command, audit, outbox và persisted response. |
| `02-architecture.md:118–120`, §5.3; `05-api.md:3–26`, §1 | UUID, scope/hash, replay status/body, 409 `idempotency_key_reused`, ProblemDetails và camelCase. |
| `01-requirements.md:200`, NFR-03 | Nền tảng chống trùng khi retry; business wiring và AC nghiệp vụ thuộc task sau. |
| `02-architecture.md:131–146`, §5.5; `03-database.md:10,16,144,180` | Job `PurgeIdempotencyKeys` mỗi giờ, giữ đúng 48h; được phép xóa bảng này. |
| `schema.sql:94–103`; `03-database.md` §1/§7 | Tái dùng PK `(scope,key)`, jsonb response, created_at và index. Không đổi DDL. |
| AC-P0-03 (`07-roadmap.md:35`), `05-api.md:12` | Test thứ tự CSRF thật trên endpoint test, không tự nhận IAM/AC toàn dự án đã xong. |
| AC-P0-04/05 (`07-roadmap.md:36–38`), NFR-04/08/09 | Hồi quy audit, bảo vệ PII, phân tầng và CI. AC-P0-04 đầy đủ còn cần settings P0-09. |

P0-07 không có FR/BR/AC riêng trong roadmap. Không tạo ID yêu cầu mới; T01–T14 dưới đây chỉ là nhãn test-plan do owner giao.

## Foundation đã audit và tái sử dụng

- `AppDbContext`/`IdempotencyKey`: đúng `platform.idempotency_keys`, PK PostgreSQL thật `(scope,key)` đã có trong InitialPlatform; status/body nullable để reservation chưa hoàn tất chỉ tồn tại trong transaction.
- `EfUnitOfWork`: transaction thật, nhiều SaveChanges, flush pending trước commit, rollback Result lỗi/exception/cancellation, giữ primary error khi rollback/dispose lỗi và clear tracker. `TransactionCommandDecorator` vẫn gọi `IUnitOfWork` và map DomainException; không sửa decorator/port.
- Request pipeline: correlation ID an toàn, ProblemExceptionMiddleware và Error.ToProblem hiện có. Thêm buffering sau routing/trước binding chỉ trên endpoint có metadata idempotency.
- Audit interceptor và `IOutboxWriter`: dùng cùng scoped context/transaction, không phát side effect ngoài DB trong command.
- `PeriodicJob`: session lock và ownership guard hiện hữu, một transaction, callback dùng context được cấp. Guard cấm raw/bulk SQL; cleanup dùng tracked batch 500 row thay vì nới guard.
- Testcontainers `postgres:17`, migration fixture và WebApplicationFactory hiện hữu. Test endpoints, authentication scheme và CSRF wiring chỉ ở assembly test.
- CI hiện hữu chạy locked restore/build/format, PostgreSQL integration, architecture/unit, API drift, migrations SQL, frontend và Docker; không sửa workflow.

## Transaction và concurrency

1. Authorization middleware và mọi security/resource/CSRF filter phải chạy trước filter idempotency. Actor resolver chỉ đọc identity đã xác minh; thiếu identity → 403. Không lấy actor từ request header trong code production. Scheme/issuer/subject hoặc guest identity đã xác minh phải được phân biệt.
2. Scope là SHA-256 của JSON array có version, operation version và actor scope. Request hash là SHA-256 của JSON array có version, scope, method, PathBase, path, query nguyên văn, content-type và SHA-256 của body bytes. JSON framing tránh nhập nhằng dấu phân cách. Whitespace/query order/content-type khác được coi là request khác; không canonicalize JSON hoặc bỏ field tùy ý. Không lưu/log raw actor/body/token; UUID chỉ ở cột key theo spec.
3. Filter gọi `EfUnitOfWork.ExecuteRequestAsync`, chính UoW hiện hữu mở transaction. `pg_advisory_xact_lock` theo namespace + scope + UUID được lấy trước đọc/xóa/insert. Hai host/connection phải chờ nhau tại PostgreSQL; hash khóa 64-bit va chạm chỉ gây chờ thêm, không gây replay nhầm vì PK/hash đầy đủ vẫn được kiểm tra.
4. Trong lock: xóa đúng scope/key nếu `created_at < now - 48h`; đọc kết quả còn hiệu lực. Cùng hash → kiểm schema/giới hạn và replay. Khác hash → rollback, trả 409 ProblemDetails. Không có read-then-insert ở transaction rời.
5. Chưa có key: insert reservation trong transaction này, rồi gọi endpoint. DI bảo đảm `EfUnitOfWork` concrete và `IUnitOfWork` là cùng scoped instance. Decorated commands chỉ tham gia outer transaction do endpoint mở; mỗi command thành công vẫn flush SaveChanges như trước. Result lỗi hoặc exception đặt rollback-only; endpoint nuốt lỗi rồi trả success vẫn không thể commit. Ngoài request owner, hành vi transaction cũ giữ nguyên. Không hỗ trợ command chạy song song trên cùng context.
6. Endpoint chỉ trả `IdempotentResponse` với public DTO và chính sách allowlist bắt buộc cho tên/kiểu/giá trị, kể cả string. JSON camelCase được serialize vào stream có trần 64 KiB, kiểm status 2xx có body (không 204/205) và policy trước commit. Không chạy arbitrary successful IResult; chặn body writes, StartAsync, streaming, pipe, send-file, complete và mutation status/header trong endpoint. Không lưu/replay Set-Cookie, token header, redirect hoặc arbitrary response metadata.
7. `response_body` jsonb chứa envelope version 1 + base64 của JSON wire bytes, `response_status` chứa HTTP status. Đây là encoding nội bộ để jsonb normalization không đổi whitespace/thứ tự field khi replay; không phải encryption hoặc API wrapper. Replay kiểm envelope/version/size/policy lại. Operation phải đổi version khi đổi request/response contract. Key cũ/incomplete/corrupt không được coi là success; trả lỗi và không chạy business, giữ fail-closed đến hết retention để tránh lặp tác động không rõ nguồn.
8. UoW flush response cùng business/audit/outbox rồi commit đúng một lần. Filter chỉ trả HTTP success sau commit. Constraint PK vẫn là hàng rào bổ sung. Không có migration/bảng mới; không Redis, IAM hay business endpoint.

## Failure windows và retry

| Thời điểm lỗi | Kết quả và cách retry |
|---|---|
| Trước reservation hoặc trong lúc chờ lock | Không business write; cancellation/timeout giải phóng transaction. Retry cùng key. |
| Sau reservation/SaveChanges nhưng trước commit | Business failure, HTTP failure, exception, cancellation, policy/size/serialization failure rollback cả reservation/business/audit/outbox. Retry có thể thực thi lại; không có false successful cache. |
| Commit bị từ chối trước khi PostgreSQL commit | Không trả success; rollback. Test interceptor lỗi trước commit và test thật `pg_terminate_backend` chứng minh retry bằng connection mới chỉ có một bộ committed effects. |
| PostgreSQL commit xong nhưng acknowledgement bị mất | Không đoán rollback thành công, không tự chạy lại business trong request lỗi. Response và effects cùng tồn tại hoặc cùng không tồn tại. Retry từ request/context mới lấy cùng DB lock, đọc row đã commit và replay. Test ném lỗi ở TransactionCommittedAsync sau commit thật chứng minh không thực thi lần hai. Đây là fault injection ở boundary, không phải network-proxy test. |
| HTTP send/disconnect sau commit | Key/response vẫn còn; client retry cùng actor/key/hash nhận lại bytes. Không cam kết client nhận success ngay ở request mất kết nối. |
| Cleanup chọn row cũ rồi request thay key hết hạn | `CreatedAt` là EF concurrency token: DELETE tracked có cả timestamp đã đọc. Không xóa nhầm replacement; concurrency exception rollback lượt cleanup, PeriodicJob thử lại giờ sau. EF metadata/snapshot thay đổi, không DDL; tooling xác nhận không cần migration. |

Retention: timestamp UTC lấy từ `IClock` lúc chuẩn bị response trước commit. Tại đúng 48h vẫn replay; lớn hơn 48h có thể dùng lại key, kể cả job chưa chạy. Job chạy lúc startup và mỗi giờ theo infrastructure hiện hữu, chỉ đăng ký khi có connection string; OpenAPI/scaffold vẫn khởi động được không cần DB. Cleanup chỉ xóa row cũ, batch 500 để giới hạn tracker; một transaction cho toàn lượt job, không giảm ownership guard. Lượt cleanup gặp cạnh tranh có thể rollback và dọn ở lần sau. Client không được kỳ vọng dedup quá 48h.

## Test-plan và bằng chứng

`backend/tests/TeaPremium.IntegrationTests/IdempotencyTests.cs`: **30 case pass**, PostgreSQL 17 thật; factory bỏ hosted workers để các phép đếm chỉ phản ánh request, cleanup chạy explicit RunOnceAsync.

| Nhãn local | Chứng cứ |
|---|---|
| T01/T02 | First 201; replay cùng status/content-type/exact bytes; execution=1; reader context độc lập kiểm 1 key, setting, audit và outbox. |
| T03/T04 | Body/query/path khác → 409 đúng code/ProblemDetails/traceId; UUID thiếu, sai format, format không dấu gạch, nhiều giá trị → 400 trước business. |
| T05 | Cùng key, operation/actor khác chạy riêng; actor gốc nhận đúng response cũ. |
| T06 | 20 request qua hai WebApplicationFactory host, DbContexts/connections riêng; xác nhận có backend đang chờ advisory lock qua pg_stat_activity trước release; một execution và một bộ side effects. |
| T07 | Hai hash cạnh tranh trên cùng key, backend thứ hai chờ DB lock; một 201, một 409; không cùng thực thi. |
| T08/T09 | Result lỗi, exception, lỗi bị endpoint nuốt, client cancellation sau SaveChanges đều không để lại success; retry cùng hash/key có đúng một bộ committed effects. |
| T10 | Pre-commit exception, post-commit lost acknowledgement injection, terminate PostgreSQL session thật trước commit; không tự trả success khi commit lỗi; retry thực thi hoặc replay theo trạng thái đã commit. |
| T11 | Đúng 48h replay; 48h+1s replace khi chưa cleanup; job thật xóa old và giữ boundary/fresh; stale tracked delete không xóa replacement. |
| T12 | Captured ILogger gồm formatted message và exception: request body/email, actor, key và authorization token không xuất hiện, dù failure/success/replay. Test P0-03 JSON stdout vẫn chạy trong full regression. |
| T13 | ASP.NET authorization thật (test auth scheme), IAntiforgery token/cookie thật; anonymous 401, missing permission 403, thiếu CSRF 403 cả trước/sau key được cache; valid replay không chạy business. Không nhận là đã implement P0-08. |
| T14 | Full audit/outbox/UoW/PeriodicJob/architecture/unit regressions pass. |
| Bổ sung | Secret/large/arbitrary result/header/cookie/body write/StartAsync bị rollback; oversized request bị reject trước binding; unmarked endpoint không yêu cầu key; test endpoints vắng ở host production. Oracle kiểm side effects bị fail khi test cố ý gọi command hai lần, không chỉ kiểm response. |

## Changed files

- `backend/src/TeaPremium.Api/Idempotency/IdempotencyEndpoint.cs`: filter, policies, exact replay, bounded serialization và guard HTTP response.
- `backend/src/TeaPremium.Api/Idempotency/IdempotencyRequestBufferingMiddleware.cs`: bounded buffering trước binding, metadata và `WithIdempotency` wiring.
- `backend/src/TeaPremium.Api/Program.cs`: buffering và hosted cleanup có điều kiện cấu hình DB.
- `backend/src/TeaPremium.Infrastructure/Persistence/EfUnitOfWork.cs`, `PersistenceRegistration.cs`: outer request owner, rollback-only joining và cùng scoped instance.
- `backend/src/TeaPremium.Infrastructure/Persistence/AppDbContext.cs`, `Persistence/Migrations/AppDbContextModelSnapshot.cs`: concurrency metadata `created_at` cho cleanup DELETE; không sửa InitialPlatform migration.
- `backend/src/TeaPremium.Infrastructure/Jobs/PurgeIdempotencyKeys.cs`: job mỗi giờ, guarded tracked batches.
- `backend/tests/TeaPremium.IntegrationTests/IdempotencyTests.cs`: 30 case tích hợp và test-only wiring.
- `docs/plans/p0-07-implementation.md`: hồ sơ này.

## Local verification tại `0842fc5` trước G4

| Command | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS, lockfiles không đổi. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warnings/errors. |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore` | PASS: Unit 11 + Architecture 34 + Integration 171 = **216**, 0 failed/skipped. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS. |
| `corepack pnpm check:api` | PASS, OpenAPI/client current; không public business endpoint mới, không regeneration/drift. |
| `docker build -f infra/Dockerfile.api -t tra-hien-p007:ci .` | PASS. |
| `git diff --check` | PASS. |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~IdempotencyTests` | PASS 30, 0 failed/skipped. |
| `dotnet ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS, no model changes since last migration; dummy loopback connection cấu hình chỉ trong command và khôi phục env sau đó. Lượt đầu thiếu env bị từ chối đúng factory contract, đã chạy lại đúng cấu hình. |

Lượt test phát triển đầu: 24/26; sửa cleanup từ bulk command bị guard chặn sang tracked batches, lấy CSRF token với cùng authenticated actor; sau bổ sung race/response/session-loss cases đạt số cuối ở trên. Không ghi các lượt đầu là PASS.

## Giới hạn, wiring tương lai và rollback

- Không có endpoint business sản xuất được opt-in. Khi triển khai task tương ứng: dùng `WithIdempotency` sau mọi security/resource/CSRF filter, khai báo operation version duy nhất, resolver identity tin cậy phân biệt scheme/issuer/subject, DTO/policy allowlist theo response thật và test auth/CSRF/resource ownership ở endpoint đó. Kiểm tra tham số điều khiển business nằm trong body/path/query/content-type đã hash; nếu thêm header ảnh hưởng kết quả phải bổ sung request identity trước khi dùng.
- P0-08 cần nối cookie schemes/identity/CSRF thật; guest identity phải được xác minh từ session/cookie phù hợp quyết định của task tương lai. Không được dùng static guest scope cho kết quả riêng tư hoặc tin actor header. Không đặt resource authorization/CSRF chỉ trong handler vì replay bỏ qua handler.
- JSON response có body, tối đa 64 KiB; request tối đa 64 KiB. Không supports arbitrary response headers, streaming, file, cookie/token issuance, direct response callbacks/writes, independent DbContext/transaction hoặc external side effects trong endpoint. Code endpoint là trusted application code, không sandbox chống code tự mở connection/bỏ guard. External effects chỉ enqueue outbox; delivery vẫn theo at-least-once contract P0-06.
- Response policy là trách nhiệm bảo mật bắt buộc của endpoint; filter không đoán string có PII hay không. Không bật EF sensitive-data logging. Base64 không che giấu dữ liệu: chỉ lưu DTO đã được allowlist và bảo vệ DB như dữ liệu nghiệp vụ.
- NFR-03 được chứng minh ở foundation với endpoint test; chưa tự nhận đơn/thanh toán thật, IAM, DoD P0 hoặc go-live đã đạt. Không network-proxy fault campaign/load benchmark/legal verification/G4 trong session này.
- Không đổi schema/database thực; tests dùng container disposable. Rollback bằng revert PR và restart API (dừng cleanup); giữ các idempotency rows đủ retention, không xóa thủ công key thành công. Phiên bản mới phải hiểu envelope version hiện hữu hoặc dùng operation version phù hợp. Không downgrade khi có business endpoint đang dựa vào guarantee này; revert cùng endpoint wiring nếu về sau có.
- GitHub Actions được kiểm tra trên PR sau push; kết luận và HEAD cụ thể nằm trong PR/handoff. G4 đầu đã BLOCK P1; owner giao `review-gate` re-review độc lập cho remediation trước quyết định merge.

## Remediation P1 G4 — cancellation của joined command (17/11/2026)

Base remediation: `0842fc53a70c6e3f9121652107f4e3eaaec7e938`, cùng branch `feature/p0-07-idempotency` và PR #15. Working tree sạch trước sửa; latest main vẫn `6e3206b205870ffa653c21bb8652ad0c9cdc9126`; spec diff từ approved amendment `0bfc98e` rỗng. Tái dùng G0/G1/G2 ở trên; scope vẫn P0-07/NFR-03, `02-architecture.md:105–107,118–132` và port `IUnitOfWork`. Không sửa specs, middleware, filter, DDL hoặc transaction framework.

Finding đã xác nhận: handler save hết business/audit/outbox, token command riêng bị hủy nhưng trả Success; `SaveChangesAsync` không còn pending có thể không quan sát cancellation. Outer request token vẫn active nên transaction ngoài có thể commit. Command thường có bước commit với chính command token nên rollback được; joined path cần kiểm tra token tường minh.

Thay đổi production duy nhất trong `EfUnitOfWork.ExecuteAsync`: `ThrowIfCancellationRequested()` ngay sau callback và lần nữa sau SaveChanges, trước khi trả joined success. Cả hai nằm trong `try` hiện hữu để `catch` đặt `_rollbackOnly`; outer transaction vẫn là owner duy nhất và xử lý rollback như cũ. Ngoài nhánh joined không đổi mã.

Thay đổi test trong `IdempotencyTests.cs`: thêm 6 case dùng fixture PostgreSQL 17 và production DI/decorator/UoW. Bốn HTTP case qua WebApplicationFactory/TestServer, production filter: CTS của command độc lập hoàn toàn với RequestAborted, cancel sau handler save cuối hoặc tại event SavedChanges một lần của UoW save kế tiếp; mỗi phase kiểm cả exception thoát ra lẫn endpoint bắt exception rồi cố trả 201. Test xác nhận OperationCanceledException mang command token đã cancel trong khi HTTP token vẫn active; rollback business/audit/outbox/key về 0 bằng reader context độc lập. Retry cùng key tạo đúng 1 bộ committed effects và replay cùng bytes, tổng 2 lần handler (1 cancelled + 1 success). Hai case command thường gọi production decorator trực tiếp không outer filter, xác nhận cancellation vẫn rollback cả ba bảng và retry thành công, không tạo key.

Red/green: trước sửa production, `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter 'FullyQualifiedName~T09CommandTokenCancellation|FullyQualifiedName~T09OrdinaryCommandCancellation'` **FAIL 4, PASS 2, zero skipped**: cả bốn joined cases không quan sát cancellation, hai ordinary controls vẫn đúng. Sau sửa, toàn bộ focused suite **36 PASS**; không sửa expectation để che lỗi.

| Command local sau remediation | Kết quả thực tế |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS; lockfiles không đổi. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warnings/errors. |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~IdempotencyTests` | PASS 36, zero failed/skipped; PostgreSQL 17 Testcontainers thật. |
| `dotnet test backend/TeaPremium.sln --no-build --no-restore` | PASS Unit 11 + Architecture 34 + Integration 177 = **222**, zero failed/skipped. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS. |
| `corepack pnpm check:api` | PASS, OpenAPI/client current, không contract drift. |
| `git diff --check` | PASS. |

Changed files remediation: `EfUnitOfWork.cs`, `IdempotencyTests.cs`, hồ sơ này. Không có migration; rollback kỹ thuật bằng revert remediation commit nhưng sẽ khôi phục P1 đã biết, do đó không bật endpoint dựa vào guarantee bị lỗi nếu downgrade. Kiểm tra token là điểm quan sát cooperative cancellation, không hứa hủy mọi race sau thời điểm command đã kết thúc; token HTTP/commit failure vẫn theo outer boundary hiện hữu. Kết luận GitHub Actions tại HEAD mới nằm trong PR #15/handoff sau push. **G4 re-review NOT RUN trong lượt sửa**, implementer không tự duyệt finding hay merge.
