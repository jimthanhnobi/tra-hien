# ADR-PROPOSAL-G26 — Định danh entity trong audit

**Trạng thái quyết định: OWNER APPROVED — Option B (16/11/2026). Bản sửa đặc tả: OWNER ACCEPTED (G4 GO).** Phạm vi phê duyệt là hai kiểu định danh UUID/TEXT, CHECK đúng một, hai partial index và `entity_type` ổn định theo schema/bảng. Chưa phê duyệt migration, interceptor hay kết quả kiểm thử. Tên file `ADR-PROPOSAL-*` được giữ để bảo toàn lịch sử đề xuất.

## Bối cảnh và nguồn mâu thuẫn

Ở baseline `origin/main` `9cc5429`, `docs/spec/01-requirements.md:190` (FR-PLT-01) yêu cầu audit trước/sau và che PII; `docs/spec/02-architecture.md:121–125` (§5.4) yêu cầu `SaveChanges` interceptor cho `[Audited]`; `docs/spec/07-roadmap.md:36` (AC-P0-04) yêu cầu sửa setting có audit. Nhưng DDL baseline `docs/spec/schema.sql:36–41` dùng `platform.settings.key text PRIMARY KEY`, trong khi `schema.sql:58–71` bắt `audit_logs.entity_id uuid NOT NULL`. `platform.provinces` và `platform.wards` cũng có khóa text (`schema.sql:44–55`); một số bảng về sau có khóa ghép (ví dụ `schema.sql:295–298`). Đây là G26 `CONFIRMED_CONFLICT` trong `docs/analysis/spec-gaps.md:13`. Bản sửa DDL theo Option B đã qua G4 và được owner chấp nhận; đây là lịch sử của baseline.

## Tiêu chí và phương án

Phải giữ lịch sử audit bất biến, định danh entity ổn định sau khi đổi tên lớp C#, truy vấn được theo entity/actor/thời gian, hỗ trợ UUID/text/khóa ghép và không lộ PII. Giữ DEC-01, tên schema/bảng và nhiệm vụ P0-04 → P0-05/P0-09. `docs/spec/03-database.md` §4 còn yêu cầu role ứng dụng không có quyền sửa/xóa audit.

| Phương án | Lợi ích | Đánh đổi/rủi ro |
|---|---|---|
| A. Thêm UUID PK cho `platform.settings`, giữ `key UNIQUE` | Không đổi `audit_logs.entity_id`; setting có ID nội bộ ổn định. | Đổi khóa chính và API/data mapping của setting; vẫn chưa giải quyết tỉnh/phường khóa text hay entity khóa ghép. Cần backfill và đổi FK nếu đã có dữ liệu. |
| B. `entity_id uuid NULL`, `entity_key text NULL`, CHECK đúng một giá trị | Giữ khóa nghiệp vụ hiện có; một bảng audit hỗ trợ UUID, text và khóa ghép; ít thay đổi bảng nguồn. | Phải sửa DDL/EF mapping, hai chỉ mục và hợp đồng định danh. Cần tránh tự ép UUID thành text và quy ước khóa ghép ổn định. |
| C. Mã UUID suy ra từ khóa text | Giữ một cột UUID, không đổi settings. | Mất khả năng đọc khóa nghiệp vụ trực tiếp; phụ thuộc thuật toán/namespace dài hạn, rủi ro va chạm hay đổi thuật toán. Không có bằng chứng cần cách này. |

**Option B đã được chủ dự án duyệt.** `entity_type` là tên bảng ổn định có schema, ví dụ `platform.settings`, không dùng tên CLR; nếu đổi tên bảng phải có chính sách chuyển tiếp truy vấn lịch sử. UUID đơn ghi vào `entity_id`, khóa text đơn vào `entity_key` nguyên dạng; không tự chuyển UUID sang chuỗi. Khóa ghép chỉ cần định dạng xác định, không va chạm và tương thích phiên bản khi thực sự có entity `[Audited]`; ví dụ JSON chuẩn trong bản đề xuất ban đầu **chưa được duyệt thành định dạng bắt buộc** và không tạo framework serialize ở P0-04. Không đánh dấu mọi bảng khóa ghép là audited chỉ vì cơ chế này tồn tại. FK từ audit đến mọi entity khác schema/kiểu khóa là không thực tế với một cặp cột đa hình; tính toàn vẹn nguồn do interceptor và transaction bảo đảm, audit vẫn giữ định danh khi entity không còn.

## Bản sửa spec/DDL đã qua G4 và được owner chấp nhận

1. `docs/spec/schema.sql:63–79`, bảng `platform.audit_logs`: đổi `entity_id uuid NOT NULL` thành `entity_id uuid`; thêm `entity_key text`; thêm `CONSTRAINT ck_audit_entity_identity CHECK (num_nonnulls(entity_id, entity_key) = 1 AND (entity_key IS NULL OR entity_key <> ''))`. Giữ `entity_type text NOT NULL` và các cột actor/time. Đổi `ix_audit_entity` thành hai chỉ mục: `ix_audit_entity_id ON platform.audit_logs(entity_type, entity_id, occurred_at DESC) WHERE entity_id IS NOT NULL` và `ix_audit_entity_key ON platform.audit_logs(entity_type, entity_key, occurred_at DESC) WHERE entity_key IS NOT NULL`. Giữ `ix_audit_time` và `ix_audit_actor`. `num_nonnulls` là hàm PostgreSQL; kiểm cú pháp/plan trên PostgreSQL 17 trong P0-04. [PostgreSQL: comparison functions](https://www.postgresql.org/docs/17/functions-comparison.html), [partial indexes](https://www.postgresql.org/docs/17/indexes-partial.html).
2. `docs/spec/02-architecture.md` §5.4: bổ sung quy ước `entity_type`/hai dạng identity, không ép UUID thành text; tương thích khóa ghép và cách che giá trị JSON lồng nhau. `docs/spec/03-database.md` §1/§3.9/§4: ghi tên constraint/index, khả năng truy vấn theo cặp type+identity, role audit chỉ INSERT/SELECT cần thiết.
3. `docs/spec/07-roadmap.md:20–25,36`: ghi P0-04 tạo schema tương thích B, P0-05 triển khai interceptor và test, P0-09 triển khai settings; giữ AC-P0-04 là nghiệm thu tích hợp của chuỗi P0-04/P0-05/P0-09, không giả định P0-04 một mình đã tạo audit row. `docs/spec/01-requirements.md:190` không cần đổi FR-PLT-01.

Đoạn DDL mục tiêu theo quyết định đã duyệt (đã sửa `docs/spec/schema.sql`, **chưa chạy**):

```sql
-- Trong CREATE TABLE platform.audit_logs:
entity_id uuid,
entity_key text,
CONSTRAINT ck_audit_entity_identity
  CHECK (num_nonnulls(entity_id, entity_key) = 1
         AND (entity_key IS NULL OR entity_key <> '')),

-- Thay ix_audit_entity hiện tại:
CREATE INDEX ix_audit_entity_id ON platform.audit_logs
  (entity_type, entity_id, occurred_at DESC) WHERE entity_id IS NOT NULL;
CREATE INDEX ix_audit_entity_key ON platform.audit_logs
  (entity_type, entity_key, occurred_at DESC) WHERE entity_key IS NOT NULL;
```

## Hệ quả triển khai và kiểm chứng

P0-04 ánh xạ hai property nullable vào một row audit, đặt CHECK/index tường minh trong migration và kiểm EF migration SQL so với DDL đã duyệt. P0-05 mới thêm `[Audited]`/`SaveChangesInterceptor`; lấy key từ EF metadata sau khi có ID, tạo log trong **cùng transaction** với thay đổi. Nếu một bên lưu lỗi, rollback cả hai; không bỏ qua audit để ghi nghiệp vụ. Interceptor không được tự tạo audit cho chính `audit_logs` hoặc tái nhập `SaveChanges`; đường `ExecuteUpdate/ExecuteDelete` trên entity audit vẫn bị cấm theo `02-architecture.md:128`. [EF Core interception](https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors), [transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions).

`changes jsonb` phải che PII cả ở object/array lồng nhau, trước và sau, theo field/path có kiểm thử; các key setting nhạy cảm như `payment.bank` (`03-database.md` §5) cần chính sách che cấu trúc và giá trị riêng. Không ghi raw exception, request body hay secret vào audit/diagnostic log. AC-P0-04 cần giá trị trước/sau **được phép lưu** sau khi che; danh sách/path và hành vi chi tiết được review và test ở P0-05. `actor_id` nullable cho system nhưng `actor_type`, thời điểm UTC và correlation id phải đi theo ngữ cảnh hiện có.

| Loại test cần có | Tình huống/nghiệm thu |
|---|---|
| Happy | Sửa setting → một audit row `entity_type=platform.settings`, `entity_key=<key>`, `entity_id=NULL`, before/after đã lọc; UUID entity → chỉ `entity_id`; truy vấn lọc entity/actor/time. |
| Failure | CHECK từ chối 0 hoặc 2 identity; audit insert thất bại → thay đổi setting rollback; lỗi/cancel/Result failure trong command → không commit dữ liệu/audit. |
| Edge | Khóa text có ký tự hợp lệ, rename CLR không đổi `entity_type`; khóa ghép encode không va chạm/ổn định; hai bản ghi cùng key qua thời gian truy vấn đúng; migration rollback cần restore dữ liệu nếu đã có `entity_key`. |
| Security | PII/secret lồng nhau không có trong `changes` hay log; role `tp_app` không UPDATE/DELETE/TRUNCATE audit; truy vấn actor/time không tạo đường lộ dữ liệu nhạy cảm. |

Migration đầu P0-04 trên DB rỗng là đường dự kiến. Nếu schema UUID-only đã có dữ liệu, cần kế hoạch chuyển tiếp riêng: thêm cột/index, kiểm dữ liệu, triển khai writer rồi mới nới `NOT NULL`; nới constraint sau go-live cần xem xét rõ với quy tắc additive NFR-08, không tự coi là tương thích. Rollback sau khi có `entity_key` không thể đơn thuần `DROP COLUMN` vì mất lịch sử, cần backup/restore hoặc giữ cột và lùi code. `docs/spec/03-database.md:189–194` yêu cầu migration script review và chạy từ DB rỗng; chưa chạy SQL nào trong review này.

**Tác động:** P0-04 (DDL/EF), P0-05 (interceptor), P0-09 (settings/AC-P0-04), P0-10 (migration/integration tests); các task về sau chỉ khi đánh dấu entity khóa text/ghép là `[Audited]`.

**Lịch sử và việc còn lại:** B được chủ dự án phê duyệt ngày 16/11/2026; A/C là phương án đã cân nhắc, không được chọn. Định dạng cụ thể cho khóa ghép chỉ chốt ở task đầu tiên audit entity khóa ghép; chi tiết che field/path nhạy cảm và test thuộc P0-05. Bản sửa spec G26/G01 đã qua G4 và được owner chấp nhận; P0-04 implementation **NOT AUTHORIZED**. Ma trận tên constraint F-01 ở `docs/plans/p0-04-readiness.md` đã được owner duyệt riêng ngày 16/11/2026; bản sửa DDL F-01 đã qua G4 độc lập.
