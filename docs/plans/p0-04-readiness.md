# P0-04 — Architecture Readiness (G26/G01)

**G26/G01 SPEC AMENDMENT — OWNER ACCEPTED (G4 GO); F-01 DDL AMENDMENT — OWNER APPROVED (G4 GO); P0-04 IMPLEMENTATION — NOT AUTHORIZED.** Hai Option B G26/G01 và bản sửa đặc tả đã được chủ dự án chấp nhận ngày 16/11/2026. Review readiness ban đầu dựa trên `origin/main`/`HEAD` `9cc54294f44ff60cc7406d33e8769bdd264b10dc`; các tham chiếu baseline trong phần lịch sử được ghi rõ, không phải chứng cứ migration đã chạy. Nguồn ưu tiên: `AGENTS.md`, `docs/spec/00-README.md` (§6 DEC-01..24), `01-requirements.md`, `02-architecture.md`, `03-database.md`, `schema.sql`, `07-roadmap.md`; tài liệu phân tích chỉ nêu gap. Owner đã duyệt ma trận F-01 bên dưới ngày 16/11/2026; DDL mục tiêu đã được sửa đúng phạm vi và qua G4 độc lập.

## Precheck và bằng chứng đã xác nhận

`git fetch origin --prune` thành công; branch `main`, working tree trước khi viết tài liệu sạch, `HEAD=origin/main=9cc5429`. Merge commit `9cc5429` có thông điệp PR #6 và parent thứ hai `735e205d68d2f573a4460c3bd4e847b3773afcda`; cả hai là ancestor của `origin/main`. `docs/decisions/` ban đầu chỉ có `README.md`, không có ADR được chấp nhận. Chưa suy ra CI/test pass từ mô tả PR.

Ở đầu **task amendment hiện tại**, working tree đã có 5 file tài liệu readiness/ADR chưa commit từ review trước; không có sửa đổi source spec/code. Sau `git fetch origin --prune`, `HEAD=origin/main=9cc5429` vẫn giữ nguyên. Bốn file `docs/spec/` được sửa trong task này theo phê duyệt Option B; chưa commit/push/merge.

## Traceability và ranh giới G3

| Mục | Bằng chứng | Ranh giới P0-04 |
|---|---|---|
| Task/dependency | `docs/spec/07-roadmap.md:19–26` | P0-04 sau P0-03; tạo `AppDbContext` nhiều schema, quy ước snake_case, constraint tường minh, mapping `xmin`, migration đầu cho đúng 9 bảng `platform` đã liệt kê, extension và `f_unaccent`. P0-05/06/07/08/09/10 phụ thuộc P0-04. |
| Yêu cầu/AC | FR-PLT-01 `01-requirements.md:190`; NFR-04/08/09 `01:201,205–206`; AC-P0-04 `07:36`; DEC-01/20 `00-README.md:85,104` | Audit before/after, che PII, migration additive sau go-live và test PostgreSQL thật là yêu cầu theo chuỗi. AC-P0-04 chỉ chứng minh sau P0-05 interceptor và P0-09 settings API, không thể khẳng định ở P0-04 chỉ có bảng. |
| DB conventions | `docs/spec/03-database.md:7–18,187–194`; `schema.sql:3–29` | UTC `timestamptz`, UUID v7 app-generated, text enum + CHECK, tên PK/FK/UX/IX/CK tường minh; `xmin` là system column, không tạo cột DDL. Kiểm script idempotent và migration từ DB rỗng. |
| G26/G01 | `docs/analysis/spec-gaps.md:13,28` ghi gap tại baseline; hai ADR trong `docs/decisions/` | Owner đã duyệt Option B cho cả hai và chấp nhận bản sửa `docs/spec/` sau G4. P0-04 còn cần giao việc triển khai riêng; F-01 đã được duyệt về tên và bản sửa DDL đã qua G4 độc lập. |

## Hợp đồng P0-03 phải tích hợp

`backend/src/TeaPremium.Application/UseCases/Contracts.cs:19–25` khai báo `IUnitOfWork.ExecuteAsync<T>(Func<CancellationToken,Task<Result<T>>>, CancellationToken)` và yêu cầu commit chỉ khi Result thành công; rollback khi Result lỗi, exception hoặc cancel. `TransactionCommandDecorator.cs:6–21` dùng port này, chuyển `DomainException` thành `Result` **sau khi** UoW rollback. `ServiceRegistration.cs:25–48` bọc validation ngoài transaction, command trong transaction; query không dùng UoW. `backend/tests/TeaPremium.UnitTests/PipelineTests.cs:9–112,181–200` chỉ dùng `ProbeUnitOfWork` giả để xác nhận thứ tự/Result; chưa chứng minh DB transaction thật. `TeaPremium.Infrastructure` hiện chỉ có `SystemClock.cs`; không có `DbContext`/production UoW. P0-04 phải đăng ký concrete adapter cùng scoped DbContext, quản lý transaction và commit/rollback tương ứng; kiểm bằng PostgreSQL thật, bao gồm nhiều `SaveChanges`, Result failure, exception và cancellation. Không đổi port/decorator P0-03 trong slice này.

`IClock` (`Application/Time/IClock.cs:3–5`) và `SystemClock.cs:5–7` cho thời điểm UTC; `BusinessCalendar.cs:3–24` chuyển ranh giới ngày VN, không dùng đồng hồ hệ thống trực tiếp trong domain/application. `CorrelationIdMiddleware.cs:15–23` đặt `TraceIdentifier` và response header; P0-05 sẽ lấy correlation ID qua adapter ngữ cảnh, P0-04 không phải tạo audit interceptor. `ProblemMapping.cs:14–41` ánh xạ `ErrorType.Conflict` sang 409; `ProblemExceptionMiddleware.cs:19–46` che nội dung lỗi nhạy cảm. [Issue #5](https://github.com/jimthanhnobi/tra-hien/issues/5) là P2 logging còn mở: log ở business handler chưa truyền correlation ID khi `IncludeScopes=false`; không có dependency cụ thể với migration P0-04, tiếp tục theo dõi trước khi có business handler production.

## Phạm vi migration đầu và các đường biên

| Hạng mục | Nguồn/việc cần làm | Ngoài P0-04 |
|---|---|---|
| `platform` | `schema.sql:19,35–135`: `settings`, `provinces`, `wards`, `audit_logs`, `outbox_messages`, `idempotency_keys`, `inbound_webhooks`, `notification_logs`, `data_protection_keys`; PK/FK/CHECK/index/default theo DDL G26 đã được chấp nhận. | `platform.web_vitals_samples` (`schema.sql:1184–1192`) là bảng pha sau. |
| Extension/function | `schema.sql:10–17`: `unaccent`, `pg_trgm`, `btree_gist`, `public.f_unaccent(text)` với semantics đ→d, lowercase. Dùng migration có quyền cần thiết, kiểm function trên PostgreSQL 17. | Không chạy nguyên `schema.sql` làm migration đầu; các schema media/catalog/…/iam ở `schema.sql:20–29` chỉ thêm khi task cần. |
| Tên constraint/index | `03-database.md:18,189` yêu cầu `pk_`, `fk_`, `ux_`, `ix_`, `ck_`, `ex_`; 19 constraint trong `schema.sql:36–135` của chín bảng P0-04 đã được đặt tên theo F-01. | Migration/EF mapping sau này phải khớp tên, cột và biểu thức; không tạo index trùng PK/UNIQUE. |
| `xmin` | `schema.sql:7`, `03-database.md:15`, `02-architecture.md:105–108`: chuẩn bị convention `uint Version`/`.IsRowVersion()` cho bảng được liệt kê ở pha sau; không có platform table trong danh sách này. [Npgsql concurrency](https://www.npgsql.org/efcore/modeling/concurrency.html). | Không thêm cột vật lý `xmin` vào migration, không gắn concurrency lên tất cả platform table khi spec chưa yêu cầu. |
| Sau P0-04 | P0-05 `[Audited]`/interceptor (`07:21`); P0-06 dispatcher; P0-07 idempotency middleware; P0-08 IAM schema/tables, CSRF, role/permission seed; P0-09 settings API/CSV CLI; P0-10 architecture tests/Testcontainers (`07:21–26`). | Không thêm IAM, interceptor hoạt động, CLI, API admin, schema nghiệp vụ hoặc migration pha sau vào P0-04. |

## F-01 — Quy tắc tên constraint được owner duyệt cho 9 bảng platform

**OWNER APPROVED 16/11/2026 — DDL AMENDMENT G4 GO.** `03-database.md:18,189` yêu cầu tên tường minh và EF Core migration tương đương `schema.sql`; owner đã duyệt ma trận 19 constraint dưới đây. `schema.sql:36–135` ghi `CONSTRAINT <tên>` cho từng PK/FK/UNIQUE/CHECK trong **đúng 9 bảng**; EF model/migration khi được giao P0-04 phải phát cùng tên, cùng cột, cùng biểu thức. Tên không có tiền tố schema vì constraint nằm trong schema `platform`; PK/UNIQUE tạo chỉ mục ngầm cùng tên theo [PostgreSQL 17 CREATE TABLE](https://www.postgresql.org/docs/17/sql-createtable.html). Không tạo thêm chỉ mục trùng PK/UNIQUE, không đổi kiểu khóa, CHECK, FK hay default.

| Bảng (`schema.sql` hiện tại) | Constraint → tên đã duyệt |
|---|---|
| `platform.settings` (`36–41`) | PK `key` → `pk_settings`; CHECK mẫu `key` → `ck_settings_key_format` |
| `platform.provinces` (`44–49`) | PK `code` → `pk_provinces` |
| `platform.wards` (`50–55`) | PK `code` → `pk_wards`; FK `province_code → provinces(code)` → `fk_wards_provinces_province_code` |
| `platform.audit_logs` (`58–73`) | PK `id` → `pk_audit_logs`; CHECK `actor_type` → `ck_audit_logs_actor_type`; CHECK `action` → `ck_audit_logs_action`; CHECK định danh → **giữ** `ck_audit_entity_identity` |
| `platform.outbox_messages` (`81–91`) | PK `id` → `pk_outbox_messages`; CHECK `status` → `ck_outbox_messages_status` |
| `platform.idempotency_keys` (`94–102`) | PK **ghép** `(scope, key)` → `pk_idempotency_keys` |
| `platform.inbound_webhooks` (`105–115`) | PK `id` → `pk_inbound_webhooks`; CHECK `status` → `ck_inbound_webhooks_status`; UNIQUE `(provider, external_id)` → `ux_inbound_webhooks_provider_external_id` |
| `platform.notification_logs` (`117–128`) | PK `id` → `pk_notification_logs`; CHECK `channel` → `ck_notification_logs_channel`; CHECK `status` → `ck_notification_logs_status` |
| `platform.data_protection_keys` (`131–135`) | PK `id` (`serial` giữ nguyên) → `pk_data_protection_keys` |

Các chỉ mục tường minh đã có giữ nguyên: `ix_wards_province`, `ix_audit_entity_id`, `ix_audit_entity_key`, `ix_audit_time`, `ix_audit_actor`, `ix_outbox_pending`, `ix_idem_created`, `ix_notif_ref` (`schema.sql:56,74–79,92,103,129`). `NOT NULL` và `DEFAULT` giữ nguyên biểu thức/semantics, không thêm CHECK thay thế. F-01 chỉ đặt tên trong DDL mục tiêu; khi P0-04 được giao riêng, EF mapping/migration phải dùng đúng tên và kiểm PostgreSQL catalog cùng migration script. Đã kiểm riêng DDL chín bảng trong container PostgreSQL 17 dùng một lần: tạo thành công 19 constraint và 18 index (10 index ngầm PK/UNIQUE, 8 index tường minh); chưa tạo migration. **Bản sửa F-01 đã qua G4 độc lập; owner chưa cấp quyền tạo migration.**

## Checklist sơ bộ để lập `implementation-slice` sau khi được duyệt

1. G4 đã kết luận GO và owner đã chấp nhận bản sửa `docs/spec/` cho hai Option B; DEC-01..24 không đổi. F-01 đã được owner duyệt về ma trận tên và qua G4 độc lập; migration parity vẫn phải kiểm trong P0-04 khi được giao riêng.
2. Tạo model/mapping và migration **chỉ** cho 9 bảng, `platform` schema, 3 extension, `f_unaccent`; so sánh cột, kiểu, default, CHECK, FK, index và tên với DDL đã duyệt. Không tự migrate lúc API boot (`03-database.md:192–194`).
3. Nối production `IUnitOfWork` vào DI; cùng một DbContext/transaction cho handler, nhiều lần save, outbox tương lai; commit chỉ Result success; rollback Result failure/exception/cancel. Kiểm `DbUpdateConcurrencyException` thành `concurrency_conflict` ở mốc có entity `xmin` thật (`02-architecture.md:108`).
4. Test PostgreSQL thật: migration từ DB rỗng, script idempotent; 9 bảng/extension/function, ràng buộc G26, FK ward/province, duplicate key, transaction commit/rollback/cancel, quyền role audit khi role tồn tại. Test negative và failure không được chỉ dựa vào fake. Test migration downgrade/recovery trên DB dev; không chạy trên dữ liệu thật.
5. Ghi kế hoạch forward/rollback: trước go-live có thể reset DB dev; sau dữ liệu thật cần backup trước migration, script kiểm tra/backfill nếu có dữ liệu và restore plan nếu downgrade làm mất `entity_key`/dữ liệu. `03-database.md:189–194`, NFR-08 (`01:205`) áp dụng.
6. Chỉ báo AC-P0-04 **chưa đạt** đến khi P0-05 + P0-09 có test tích hợp sửa setting/audit; không suy từ việc bảng audit tồn tại.

## Findings/gate

| Mức | Phát hiện; tái hiện/hậu quả | Cách xử lý |
|---|---|---|
| P1 tại baseline, đã xử lý | G26: `schema.sql:37,64` ở `origin/main` có text PK và UUID-only audit; bản sửa hai định danh đã được G4/owner chấp nhận. | P0-05/P0-09 vẫn phải kiểm AC-P0-04 tích hợp; đây không còn là blocker đặc tả P0-04. |
| P1 tại baseline, đã xử lý | G01: `03-database.md:81,143` ở `origin/main` chưa chốt seed migration/CLI; bản sửa tách cấu trúc P0-04 khỏi dữ liệu P0-09 đã được chấp nhận. | Xác minh provenance dataset trước import P0-09. |
| P2 — F-01, đã qua G4 | 19 constraint của chín bảng trong `schema.sql:36–135` có tên theo ma trận owner duyệt; DDL riêng đã chạy trong PostgreSQL 17 dùng một lần và catalog trả đúng 19 tên. Chưa có migration EF để đối chiếu. | Khi triển khai P0-04, kiểm tên/cột/biểu thức trong migration script và PostgreSQL catalog, không tạo index trùng PK/UNIQUE. |
| P2 — F-02, P0-09 | `03-database.md:82–83` chưa định dạng metadata/import mode và nơi lưu version hiện hành. | Chốt hợp đồng manifest/provenance, thứ tự phiên bản và recovery trước khi viết CLI P0-09; không kéo vào P0-04. |
| P2 theo dõi | Issue #5: correlation ID chưa đi vào mọi business-handler log; chưa cản P0-04. | Giữ issue mở, giải quyết trước log handler production; không bật `IncludeScopes` vì có nguy cơ log đường dẫn chứa PII. |

**Gate hiện tại: G26/G01 SPEC AMENDMENT — OWNER ACCEPTED (G4 GO); F-01 DDL AMENDMENT — OWNER APPROVED (G4 GO); P0-04 IMPLEMENTATION — NOT AUTHORIZED.** F-03 về số dòng đã được sửa. F-01 được owner duyệt, áp dụng vào DDL mục tiêu và qua review độc lập; F-02 là điều kiện trước P0-09. Chỉ chạy DDL chín bảng trong container PostgreSQL 17 dùng một lần; chưa chạy migration EF, integration test hay CI.
