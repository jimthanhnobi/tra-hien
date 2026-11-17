# P0-05 — Audit interceptor: hồ sơ triển khai G3

Trạng thái hiện tại: **P0-05 đã merge vào `main` qua PR #10, G4 độc lập PASS**. Commit P0-05: `7c70a8cb205ed7d87b2a29dfc90e79dc6acb94e2`; merge commit: `a4b7083b4e5886ee457da674b9be8a1c9b561b25`. Phần dưới lưu bằng chứng G3/G4 trước khi publish, không phải trạng thái branch hiện tại. Base G3 ban đầu: `07b88671e93a3ae3f69a9b798c872b5957a20d41` (PR #8); base sau PR #9: `8d53174362892ccb9fca0fd81c4c902b6c979ff7`.

## Nguồn và phạm vi

| Nguồn | Nghĩa vụ P0-05 |
|---|---|
| `docs/spec/07-roadmap.md:21` — P0-05 | `[Audited]`, `SaveChangesInterceptor`, UUID/TEXT identity, redaction field/path kể cả JSON lồng nhau, audit cùng transaction, test rollback. Phụ thuộc P0-04 đã merge. |
| `docs/spec/01-requirements.md:190,201,205–206` — FR-PLT-01, NFR-04/08/09 | Ghi ai/lúc nào/cái gì/trước/sau và che PII; không lộ PII trong log; không tự migration khi API start; test integration trên PostgreSQL thật. |
| `docs/spec/04-business-rules.md:279` — BR-PLT-01 | Audit theo `02-architecture.md` §5.4; quyền đọc audit `audit.read` thuộc task API/IAM về sau. |
| `docs/spec/02-architecture.md:64–72,101–128` — §3, §5.1/5.2/5.4 | Giữ ranh giới project, dùng `IClock`, cùng DbContext/transaction và kiểm đường Result lỗi, exception, cancellation; chỉ entity `[Audited]`; không audit raw SQL tự động. |
| `docs/spec/03-database.md:93–99,115–144`; `schema.sql:36–79` | `platform.settings` khóa TEXT, `audit_logs` CHECK đúng một identity, `entity_type` gồm schema/tên bảng, append-only, cấu hình có JSONB nhạy cảm. |
| `docs/decisions/ADR-PROPOSAL-G26-audit-entity-identity.md` — Option B đã duyệt | UUID giữ ở `entity_id`, TEXT nguyên dạng ở `entity_key`; khóa ghép chưa có định dạng được duyệt. |
| `docs/spec/00-README.md:85,104` — DEC-01/20; `docs/spec/07-roadmap.md:36–38` — AC-P0-04/05 | Một PostgreSQL modular monolith; AC-P0-04 là nghiệm thu xuyên P0-04/P0-05/P0-09, chưa được tuyên bố đạt khi chưa có Settings API. |

`docs/analysis/spec-gaps.md` ghi G26 theo baseline cũ; Option B và bản sửa spec đã được owner chấp nhận. Không có xung đột đặc tả vật chất còn mở được xác định cho P0-05. P0-08 sở hữu IAM và quyền DB, P0-09 sở hữu Settings API/seed/import; không kéo các phần này vào P0-05.

## Precheck và thiết kế

Trước khi sửa: `HEAD=main=origin/main=07b88671e93a3ae3f69a9b798c872b5957a20d41`, working tree tracked/untracked sạch. P0-04 đã có `AppDbContext`, `EfUnitOfWork`, migration 9 bảng `platform` và test PostgreSQL 17. `Setting` có khóa TEXT và `Value` JSONB; chưa có metadata hay interceptor audit. `EfUnitOfWork` cho phép nhiều `SaveChanges` trong một transaction, rollback cả transaction khi Result lỗi, exception hoặc cancellation.

`[Audited]` là attribute BCL-only trong Domain; `Setting` được đánh dấu, các bảng hạ tầng khác không bị audit tự động. `AuditSaveChangesInterceptor` chọn entity theo EF metadata/reflection, thêm `AuditLog` vào cùng DbContext trong `SavingChangesAsync` và không gọi lại `SaveChanges`. `entity_type` lấy từ schema và tên bảng trong EF; khóa đơn Guid đi vào `entity_id`, khóa đơn TEXT nguyên dạng vào `entity_key`. Khóa rỗng, kiểu khác và khóa ghép chưa có hợp đồng đều bị từ chối bằng thông báo cố định. Test UUID dùng entity nghiệp vụ và bảng chỉ tồn tại trong PostgreSQL Testcontainers, không thêm bảng production.

Khi update/delete, interceptor yêu cầu transaction đang mở, khóa row nguồn bằng `SELECT ... FOR UPDATE` với identifier lấy từ metadata và key truyền dưới dạng parameter, rồi đọc giá trị DB trước khi tạo changes. Điều này hỗ trợ detached entity và giữ snapshot trước/sau nhất quán trong `EfUnitOfWork`; nhiều lần `SaveChanges` vẫn ở một transaction. Lệnh `SaveChanges` đồng bộ có thay đổi `[Audited]` bị từ chối rõ ràng. Raw SQL/`ExecuteUpdate`/`ExecuteDelete` không đi qua interceptor và vẫn bị cấm trên bảng audited theo §5.4. Lỗi ghi audit không được nuốt; UoW rollback business và audit cùng nhau theo contract P0-04.

`IAuditContext` scoped có mặc định `system` khi không có HTTP. API adapter chỉ nhận actor từ principal đã xác thực với scheme `AdminCookie` hoặc `CustomerCookie` và UUID claim hợp lệ; header actor tự khai không được đọc. Correlation lấy từ `HttpContext.TraceIdentifier` của middleware hiện có, chỉ giữ dạng UUID N/D; timestamp từ `IClock`. P0-08 còn sở hữu luồng IAM thực, role DB và kiểm quyền đọc audit.

## Chính sách che giá trị

Policy là allowlist có thể test, áp dụng giống nhau cho `before` và `after` trước khi serialize `changes`. Các field PII/secret như phone, email, address, full_name, receiver, notes, account, credential, token, password, secret, cookie, chat ID và hotline thành `"[redacted]"`; mọi string/JSON path chưa phân loại cũng vậy. `payment.bank` (gồm BIN/số/tên tài khoản), `notify.telegram_chat_id`, `notify.shop_emails`, `site.*`, `seo.brand_name` và `seo.analytics` không giữ giá trị nguyên văn. Tên key JSON cũng không phải dữ liệu tin cậy: chỉ tên trong allowlist cố định được giữ; tên khác gộp thành `unclassified` với giá trị đã che. Object/array được duyệt đệ quy, không serialize entity graph tùy ý; lỗi JSON dùng thông báo cố định, không đưa raw value vào message.

Các giá trị được giữ rõ ràng: số của shipping fee/threshold, orders hours/quantity/discount, returns window, marketing/referral counters, `seo.sold_unique_keep_days`, retention counters; bool của `orders.cod_requires_call`, `orders.auto_confirm_on_full_payment`, `seo.organization.has_store`; min/max của `seo.handling_days`/`seo.transit_days`; `seo.return_policy.days`; số trong `referral.reward_voucher`; enum `payment.methods_enabled` (`cod`, `bank_transfer`) và `referral.reward_voucher.type` (`fixed`, `percent`). Với entity thông thường, chỉ số tiền/giá/giá vốn/số lượng/thứ tự, bool `is_active`, timestamp `updated_at` và status thuộc allowlist mới giữ giá trị. Giá trị không khớp kiểu hoặc enum được che.

## File thay đổi trong G3

| File | Vai trò |
|---|---|
| `backend/src/TeaPremium.Domain/AuditedAttribute.cs` | Metadata BCL-only, giữ graph phụ thuộc của §3. |
| `backend/src/TeaPremium.Infrastructure/Persistence/PlatformEntities.cs` | Chỉ đánh dấu `Setting` là audited; không đánh dấu hạ tầng khác. |
| `backend/src/TeaPremium.Infrastructure/Persistence/AuditSaveChangesInterceptor.cs` | Chọn entity, khóa row và lấy before, tạo audit cùng SaveChanges, kiểm key/actor/correlation và từ chối sync bypass. |
| `backend/src/TeaPremium.Infrastructure/Persistence/AuditValuePolicy.cs` | Phân loại field/path và lọc JSON/array; mặc định che. |
| `backend/src/TeaPremium.Infrastructure/Persistence/IAuditContext.cs` | Ngữ cảnh audit scoped, `system` mặc định cho non-HTTP. |
| `backend/src/TeaPremium.Infrastructure/Persistence/PersistenceRegistration.cs` | Đăng ký interceptor scoped cùng `AppDbContext`/UoW. |
| `backend/src/TeaPremium.Api/Middleware/HttpAuditContext.cs`, `backend/src/TeaPremium.Api/Program.cs` | Actor từ principal đã xác thực, correlation từ middleware, DI cho HTTP. |
| `backend/tests/TeaPremium.IntegrationTests/AuditInterceptorTests.cs` | PostgreSQL 17: TEXT CRUD, before/after persisted, nested PII, transaction, actor và failure. |
| `backend/tests/TeaPremium.IntegrationTests/UuidAuditTests.cs` | UUID entity nghiệp vụ test-only, key rỗng/ghép; không thêm schema production. |
| `backend/tests/TeaPremium.IntegrationTests/AuditValuePolicyTests.cs` | Policy allowlist và che key/value không tin cậy. |
| `docs/plans/p0-05-implementation.md` | Hồ sơ truy vết, chứng cứ và giới hạn. |

Không sửa `docs/spec/**`, migration, model snapshot, P0-03 decorator, frontend hay lock file. AppDbContext mapping P0-04 giữ nguyên; vì vậy P0-05 không có forward migration hoặc `Down()` mới. Triển khai phụ thuộc migration P0-04 được chạy riêng bằng `tp_owner` theo kế hoạch backup/recovery P0-04; không tự migrate lúc API start. Nếu rollback ứng dụng P0-05, giữ bảng/audit history đã ghi; không xóa hay viết lại audit log.

## Ma trận tình huống cần chứng minh

| Nhóm | Trường hợp |
|---|---|
| Happy | Setting TEXT create/update ghi đúng `platform.settings`, trước/sau và field thực sự đổi; UUID-key business entity ghi `entity_id` mà không ép thành TEXT; actor/correlation/time đúng. |
| Edge | Setting không đổi không tạo audit; nhiều `SaveChanges` trong một UoW; audit row không tự audit; sync save không bypass; key rỗng/composite/không hỗ trợ bị từ chối. |
| Security | Che PII/secret ở before và after, JSON object/array lồng nhau; `payment.bank`, `notify.*`, `site.*`, `seo.*` và key không rõ được phân loại fail closed; dữ liệu nhạy cảm không xuất hiện trong `changes`, log hoặc lỗi công khai; header actor giả không thành staff. |
| Failure | Result.Failure, exception, cancellation và lỗi ghi audit đều không commit business/audit; rollback lỗi giữ ngữ nghĩa hạ tầng của P0-04, không rollback hai lần. |
| Hồi quy | Migration/catalog parity P0-04 và architecture tests P0-03 vẫn xanh. |

## Kiểm chứng thực chạy (17/11/2026)

| Lệnh/check | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode -v:q` | PASS, exit 0. |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror -v:q` | PASS, 0 warnings, 0 errors. |
| `dotnet test backend/tests/TeaPremium.IntegrationTests/TeaPremium.IntegrationTests.csproj --no-build --filter 'FullyQualifiedName~AuditInterceptorTests\|FullyQualifiedName~UuidAuditTests\|FullyQualifiedName~AuditValuePolicyTests\|FullyQualifiedName~ScopedProductionRegistrationUsesRealDatabase' -v:q` | PASS, 45/45. PostgreSQL 17 Testcontainers cho các case persistence; policy tests chạy trong cùng test project. |
| `dotnet test backend/TeaPremium.sln --no-build -v:q` | PASS, 11 unit, 6 architecture, 93 integration; 0 failed/0 skipped. Gồm migration/parity P0-04 và P0-03 architecture tests. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | FAIL duy nhất do `backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611161624_InitialPlatform.cs:1` có CHARSET/encoding từ baseline P0-04; file này không thay đổi trong P0-05. |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore --exclude backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611161624_InitialPlatform.cs` | PASS, toàn bộ phần còn lại. |
| EF CLI 10.0.15: `migrations has-pending-model-changes --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --context AppDbContext --no-build` với connection string dummy | PASS: `No changes have been made to the model since the last migration.` Không kết nối database thật. |
| `git diff --check`; kiểm trailing whitespace của 9 file untracked | PASS, không có lỗi. `git status` xác nhận đúng 3 file tracked sửa và 9 file mới, không có spec/migration/frontend/lock file thay đổi. |

Lần chạy full suite đầu phát hiện `ScopedProductionRegistrationUsesRealDatabase` thất bại vì registration mới cần `IClock` khi được gọi độc lập. `PersistenceRegistration` đã dùng `TryAddSingleton` cho clock mặc định; test P0-04 gốc không bị sửa. Sau fix, focused 45/45 và full suite 11/6/93 đều PASS. Regression còn kiểm `updated_at` do PostgreSQL sinh không bị ghi trong audit bằng CLR sentinel trước INSERT; row nghiệp vụ nhận timestamp thật.

### P2-CHARSET — điều tra baseline độc lập (17/11/2026)

- `git fetch origin --prune`: `HEAD=origin/main=07b88671e93a3ae3f69a9b798c872b5957a20d41`. Workspace P0-05 có 3 file tracked sửa, 9 file untracked, không có staged change; migration không đổi. Blob local và `origin/main:backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611161624_InitialPlatform.cs` cùng SHA `b90255e7bb28de0042e4bde82bc453e3a7d68f40`.
- `.editorconfig` đặt `charset = utf-8`, `end_of_line = lf`. File migration trên bắt đầu bằng bytes `EF BB BF` (UTF-8 BOM), SHA-256 `A6155D6CC1F326BE338109EBAA4A15CB481E388E80FFB98EE0B38BAAB4A9F881`. Hai file generated `202611161624_InitialPlatform.Designer.cs` và `AppDbContextModelSnapshot.cs` cũng có BOM, nhưng `dotnet format` chỉ báo `CHARSET` ở file migration `.cs` nêu trên; không suy diễn thêm lỗi format chưa được công cụ báo.
- Worktree tách biệt được tạo bằng `git worktree add --detach C:\Users\ADMIN\AppData\Local\Temp\tra-hien-p005-baseline-fe9be669a8974f9482581a300d57eb44 origin/main`. Trong worktree sạch đó, `dotnet restore backend/TeaPremium.sln --locked-mode -v:q` exit **0**; `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` exit **1**, chẩn đoán duy nhất `202611161624_InitialPlatform.cs(1,1): error CHARSET: Fix file encoding`; chạy lại cùng lệnh với `--exclude backend/src/TeaPremium.Infrastructure/Persistence/Migrations/202611161624_InitialPlatform.cs` exit **0**.
- Trong workspace P0-05, whole-solution format cùng lệnh exit **1** với cùng một chẩn đoán; loại **chỉ** file migration đó exit **0**. Lệnh `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore --include <11 đường dẫn C# P0-05>` exit **0**; danh sách chính xác là 3 file tracked C# và 8 file C# mới trong bảng file thay đổi ở trên. Không loại rộng các file khác.
- **Kết luận phạm vi:** lỗi có trên `origin/main` nguyên vẹn và file lỗi có blob y hệt; không do hành vi hoặc định dạng của P0-05. `--exclude` là phép chẩn đoán theo phạm vi, **chưa phải ngoại lệ gate được duyệt**. Whole-solution format vẫn **FAIL**.

Đề xuất sửa baseline riêng: chuẩn hóa encoding của đúng migration P0-04 theo `.editorconfig` trong một thay đổi bảo trì được review riêng, kiểm byte/diff, EF pending-model-changes, migration script/catalog parity và full tests trước khi chốt. Không sửa migration đó trong P0-05. Nếu muốn chấp nhận ngoại lệ format theo phạm vi, owner phải chấp nhận rõ và reviewer độc lập đánh giá; hiện **chưa có phê duyệt**. Nếu whole-solution format là gate không thể miễn, trạng thái P0-05 vẫn CONDITIONAL/HOLD cho tới khi baseline được sửa.

## Rủi ro, giới hạn và G4 handoff

- `ExecuteUpdate`, `ExecuteDelete` và raw SQL không đi qua EF SaveChanges interceptor. Theo `02-architecture.md` §5.4, thao tác trên bảng `[Audited]` phải đi qua đường audited hoặc có cơ chế được đặc tả riêng; P0-05 chưa tạo cơ chế chặn toàn cục cho raw SQL.
- Với update/delete audited, caller phải dùng transaction/UoW để giữ row lock đến commit. Không có Settings API ở P0-05; P0-09 cần đi qua UoW và kiểm định quyền/validation của key, cấu trúc và kích thước JSON. `Setting.Key` là identity TEXT nguyên dạng theo G26; schema hiện cho phép nhiều key hợp cú pháp, nên P0-09 phải giới hạn key được phép dùng để tránh key do người dùng tự tạo mang thông tin nhạy cảm.
- P0-08 mới tạo IAM và role `tp_app` append-only cho audit. Adapter chỉ nhận principal có scheme/UUID hợp lệ; hiện chưa có luồng đăng nhập production. P0-08 phải kiểm lại claim/scheme thực tế và quyền DB. Issue #5 về correlation ID trong business-handler log không bị đóng/sửa bởi slice này.
- Test rollback failure dùng interceptor xác định; không mô phỏng mất kết nối vật lý khi commit/rollback. Outcome thật có thể không xác định và cần điều tra DB trước retry non-idempotent command. Không chạy test destructive trên dữ liệu thật.
- Trước PR #9, full `dotnet format` lỗi encoding của migration P0-04, ngoài diff P0-05. Bằng chứng baseline và quyết định sửa riêng ở mục P2-CHARSET; sau merge PR #9, cổng toàn solution đã PASS trên base mới.

### Đồng bộ sau PR #9 (17/11/2026)

- PR [#9](https://github.com/jimthanhnobi/tra-hien/pull/9) đã được owner merge. Head sửa BOM `dcb206a62ca47067045edf39631bd7e8e036fbc8`; merge commit và `origin/main` là `8d53174362892ccb9fca0fd81c4c902b6c979ff7`. GitHub head PR có **0 check-runs, 0 workflow runs, 0 commit statuses**: CI **NOT RUN / NOT CONFIGURED**. Bằng chứng local và G4 độc lập cho fix ở mô tả PR; không coi CI đã PASS.
- Trước đồng bộ, workspace P0-05 có đúng 3 file tracked sửa, 9 file untracked và không có staged change. Đã chép **đủ 12 file** tới checkpoint ngoài repo `C:\Users\ADMIN\source\tra-hien-p005-checkpoint-a6ecf04e10cd48e189b398d82556ac30`, ghi manifest trạng thái, byte length và SHA-256 từng file, rồi xác minh bản sao trùng nguồn. Checkpoint còn được giữ.
- `git diff --name-only HEAD origin/main` chỉ có migration P0-04; `git merge-base --is-ancestor HEAD origin/main` thành công. `git merge --ff-only origin/main` trên nhánh P0-05 đã đưa HEAD từ `07b88671e93a3ae3f69a9b798c872b5957a20d41` tới `8d53174362892ccb9fca0fd81c4c902b6c979ff7`, không stash, reset hay ghi đè file task. Sau fast-forward, đã đối chiếu lại **12/12** file với checkpoint: SHA-256, kích thước và trạng thái tracked/untracked đều khớp.
- Kiểm chứng sau đồng bộ: `dotnet restore backend/TeaPremium.sln --locked-mode -v:q` exit **0**; `dotnet build backend/TeaPremium.sln --no-restore -warnaserror -v:q` exit **0**, 0 warnings/errors; `dotnet test backend/TeaPremium.sln --no-build -v:q` exit **0**, 11 unit + 6 architecture + 93 integration trên PostgreSQL 17 Testcontainers; **toàn solution** `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` exit **0**, không cần `--exclude`.
- EF CLI 10.0.15 `migrations has-pending-model-changes --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --context AppDbContext --no-build` exit **0**, không có pending changes; dùng connection string dummy, không kết nối DB thật. `git diff --check` exit **0**; 9 file untracked được kiểm trailing whitespace, không có lỗi. Diff task so với base mới vẫn chỉ có 3 file tracked sửa và 9 file untracked; không đổi `docs/spec/**`, migration, frontend hoặc lock file trong P0-05.

Phần P2-CHARSET phía trên là lịch sử trước PR #9; cổng whole-solution format **hiện PASS** trên base mới. Reviewer độc lập đã kiểm diff cuối (3 tracked + 9 untracked), xác nhận 11/11 file mã/test khớp checkpoint tại thời điểm G4 và tự chạy locked restore, build warning-as-error, full tests (11 unit, 6 architecture, 93 integration), focused audit tests (45/45), whole-solution format, EF pending-model-changes và `git diff --check`: tất cả exit **0**. Trước commit, đã bỏ đúng một byte LF thừa ở cuối `AuditInterceptorTests.cs` để `git diff --cached --check` PASS; 10/11 file mã/test vẫn khớp checkpoint từng byte, file test còn lại chỉ khác byte LF đó. Reviewer đã đối chiếu staged diff và xác nhận thay đổi này không ảnh hưởng ngữ nghĩa, **G4 PASS** giữ nguyên. Không tìm thấy P0/P1 hoặc lỗi cần sửa trong phạm vi review; verdict **G4 PASS**, chưa phải phê duyệt merge. CI P0-05 **NOT RUN** vì chưa có commit/PR. Không tuyên bố **AC-P0-04 end-to-end PASS**: Settings API thuộc P0-09.

### Publish hoàn tất sau G4 (17/11/2026)

- [PR #10](https://github.com/jimthanhnobi/tra-hien/pull/10) **MERGED**: head `7c70a8cb205ed7d87b2a29dfc90e79dc6acb94e2`, base `8d53174362892ccb9fca0fd81c4c902b6c979ff7`, merge commit `a4b7083b4e5886ee457da674b9be8a1c9b561b25`. Đúng 12 file P0-05 được review; không sửa spec, migration, frontend hoặc lock files.
- G4 độc lập **PASS**, theo hồ sơ reviewer trước merge. Locked restore, build `-warnaserror`, full tests (11 unit, 6 architecture, 93 integration/PostgreSQL 17 Testcontainers), whole-solution format và EF pending-model-changes đều **PASS theo báo cáo local trước merge**. GitHub CI trên PR #10 **NOT CONFIGURED / NOT RUN**; không suy diễn thành CI PASS.
- **AC-P0-04 end-to-end chưa hoàn tất**: cần Settings API P0-09. Raw SQL/`ExecuteUpdate` bypass interceptor, còn IAM/DB role theo P0-08.
