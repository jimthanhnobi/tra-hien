# Rà soát phù hợp kiến trúc Trà Hiên V2

**Trạng thái:** PROPOSED — tài liệu rà soát, chưa phê duyệt sửa đặc tả hay triển khai. **Nguồn:** `AGENTS.md`; toàn bộ `docs/spec/00-README.md` đến `09-seo.md` và `schema.sql`; `docs/analysis/spec-inventory.md`, `spec-gaps.md`, `traceability.md`. Nhánh `docs/audit-spec-v2`, HEAD `06c43babb9df37bf1a57f8c5dfee0e1e7eb57b58`; `docs/spec/` không đổi so với commit audit `cadce575597738bf40ad7c45689482de5a5fbc3d`. Các đường dẫn và số dòng dưới đây tham chiếu đúng nguồn spec, không biến tài liệu phân tích thành yêu cầu mới.

## Kết luận cho P0-01

**GO về mức sẵn sàng kiến trúc và đặc tả của P0-01.** `docs/spec/07-roadmap.md:17` xác định scope scaffold và không có dependency; cấu trúc tại `docs/spec/02-architecture.md:27–77` đủ để tạo 4 project nguồn, 3 project test và hai app Next.js. DEC-01/15/16 (`docs/spec/00-README.md:85,99–100`) đã chốt modular monolith .NET 10, PostgreSQL 17, hai app Next.js và proxy API cùng origin. Không phát hiện mâu thuẫn buộc chọn công nghệ hay thay đổi nghiệp vụ để scaffold. Kết luận này không thay thế việc người dùng duyệt G2 và giao một task triển khai G3 theo `AGENTS.md`; chưa có build/test ứng dụng vì repo chưa có scaffold.

## Đối chiếu kiến trúc đã chốt

| Mặt kiểm tra | Bằng chứng | Nhận định và ranh giới |
|---|---|---|
| Thành phần và vận hành | DEC-01/15/16; `02-architecture.md:3–24`; `06-infrastructure.md:224–242` | Một API, một PostgreSQL 17 nhiều schema, hai Next.js app, Caddy `/api/*` cùng origin. Không có bằng chứng cần đổi kiến trúc. Compose và reverse proxy thuộc P0-02/P3-10, không thuộc scaffold P0-01. |
| Layer/module | `02-architecture.md:27–77`; `07-roadmap.md:17,26` | Project graph được quy định rõ. Domain chỉ BCL; Application không tham chiếu EF Core; handler liên module qua contract. P0-01 tạo graph; P0-10 kiểm tự động luật kiến trúc. |
| Database/EF | `schema.sql:1–29`; `03-database.md:180–185`; `07-roadmap.md:20,45,78` | `schema.sql` là schema đích, không phải một migration P0-04. Các migration phải cộng dồn, có kiểm tra từ DB rỗng và đối chiếu đích. G26/G01 phải chốt trước P0-04. |
| Tồn, đơn, thanh toán, CRM | DEC-05/07/08 (`00-README.md:89,91–92`); `02-architecture.md:105–118`; `07-roadmap.md:51–52,82,179–185` | Khóa tồn nguyên tử, idempotency và outbox phù hợp modular monolith. G03/G21 là rủi ro thứ tự DDL; G22 là rủi ro side effect giao hàng; các luồng nghiệp vụ cần test ở task tương ứng, chưa phải yêu cầu của scaffold. |
| API và quyền | DEC-14/16 (`00-README.md:98,100`); `02-architecture.md:182–215`; `05-api.md:7–12,57–66` | Tách cookie admin/khách, CSRF, permission và API cùng origin đã được định hướng. G09 thiếu hợp đồng lấy CSRF cho guest trước P3-01/P3-02/P3-04; G04 cần chốt quyền refund trước P2-10. Không chặn P0-01. |
| SEO | DEC-21/22/24 (`00-README.md:105–108`); `09-seo.md:20–36,117–139`; `07-roadmap.md:121–129` | SSR/URL/index/JSON-LD/feed đã có task P3. Hai rủi ro brand feed và redirect tác giả/chủ đề được nêu riêng dưới đây; không chặn scaffold. |
| Build và máy phát triển | `07-roadmap.md:17`; `06-infrastructure.md:226–230` | Trên máy rà soát có .NET SDK 10.0.204/.302/.403, Node v22.22.2, Corepack 0.34.6; `pnpm` chưa có lệnh toàn cục. P0-01 cần pin phiên bản và dùng Corepack trong lệnh kiểm tra; đây là việc của task, không phải blocker đã xác nhận. |

## Mâu thuẫn đã xác nhận và ADR-PROPOSAL

**G26 — `CONFIRMED_CONFLICT`, chốt trước P0-04.** FR-PLT-01 (`01-requirements.md:190`), audit interceptor (`02-architecture.md:121–123`) và AC-P0-04 (`07-roadmap.md:36`) đòi audit lần sửa setting. `schema.sql:36–41` định nghĩa `platform.settings.key text` là khóa chính, trong khi `schema.sql:58–69` bắt `platform.audit_logs.entity_id uuid NOT NULL`. Không thể lưu trực tiếp khóa setting vào trường audit hiện có. Việc P0-05/P0-09 triển khai trước khi chốt định danh sẽ tạo migration hoặc log khó sửa. Đây là mâu thuẫn DDL/hành vi đã xác nhận; **không ảnh hưởng P0-01**.

**ADR-PROPOSAL — định danh audit cho bản ghi khóa text.** Lựa chọn A: thêm UUID surrogate cho `platform.settings` và giữ `key` unique; ít đổi cấu trúc audit nhưng thay khóa của settings, và các bảng khóa text khác vẫn cần xử lý riêng. Lựa chọn B: thêm `audit_logs.entity_key text`, cho `entity_id uuid` nullable và đặt CHECK buộc đúng một trong hai trường có giá trị; hỗ trợ settings và các entity khóa text, nhưng phải đổi DDL, index và contract truy vấn audit. **Đề xuất xem xét B** vì phù hợp hơn với các khóa text hiện có (`schema.sql:44–55,1113–1119`); chưa chọn thay người dùng. Cần duyệt mô hình, cách hiển thị/truy vấn và kiểm thử AC-P0-04 trước khi làm P0-04. Không sửa spec/schema tại bước này.

## Hợp đồng và ranh giới migration cần chốt theo pha

| Mốc | Bằng chứng | Việc đề xuất, chưa phải quyết định |
|---|---|---|
| **P0-04/P0-09 — G01** `MISSING_CONTRACT` | `03-database.md:81` nhập CSV chính thức qua CLI; `03-database.md:143` gọi tỉnh/phường là seed bằng migration; `07-roadmap.md:20,25` | Chốt migration tạo bảng và có seed snapshot hay không; nếu có, ghi nguồn/phiên bản CSV. CLI `import-provinces <csv>` chịu trách nhiệm nạp/cập nhật thế nào, xử lý mã đổi và idempotency ra sao. Hai câu trong spec có thể phối hợp nên G01 không phải xung đột xác nhận. Không đặt dữ liệu địa giới giả trong scaffold. |
| **P0-04/P0-08** | `schema.sql:10–29,36–99,1207+`; `07-roadmap.md:20,24` | P0-04 chỉ tạo phần `platform` được liệt kê, extension và `f_unaccent`; Identity tables và role/permission seed làm ở P0-08. `schema.sql` tạo 11 schema đích nhưng không được chạy nguyên file như migration đầu. |
| **P1-01/P5-07 — G21** `DESIGN_RISK` | `07-roadmap.md:45,185`; `schema.sql:471–483,880–882,1154–1173` | Không đem nguyên DDL `catalog` vào P1-01: reviews có FK tới `sales.order_lines` và `crm.customers`; để review tables đến P5-07. Với `inventory.reservations`, tạo ở P1 rồi thêm FK `order_line_id` ở P2, hoặc lùi cả bảng sang P2 sau khi xét luồng P1; kiểm chuỗi migration. |
| **P1-04/P1-05 — G02** `MISSING_CONTRACT` | `04-business-rules.md:43–47` (BR-CAT-08); `07-roadmap.md:48–49,121`; `schema.sql:1113–1119` | Slug redirect P1 cần bảng và transaction tối thiểu dù P3-07 ghi redirects. Đề xuất tạo phần lưu/tra redirect đúng lúc P1-04/P1-05, giữ editor và quản trị nội dung cho P3-07; xác nhận phạm vi trước P1-04. |
| **P2-01 — G03** `MISSING_CONTRACT` | `07-roadmap.md:78,179–183`; `schema.sql:690–725,814–815,853,1197` | `sales.orders`, `crm.interactions`, `finance.expenses` tham chiếu marketing tables trong khi nghiệp vụ marketing P5. Chọn tạo bảng marketing nền ở/ trước P2-01 hoặc trì hoãn các FK/cột đến P5; ghi bảng/FK từng migration. |
| **Mọi DB task** | `03-database.md:180–185`; `06-infrastructure.md:226–234` | Kiểm migration liên tiếp từ DB rỗng, script idempotent, tên constraint/index và parity với schema đích; production dùng bundle sau backup, API không auto-migrate. Audit chưa chạy PostgreSQL, nên tuyên bố DDL “đã test” tại `03-database.md:3` không được coi là log kiểm chứng (G23). |

## Rủi ro SEO dành cho task sau

| Rủi ro | Bằng chứng và tác động | Mốc xử lý |
|---|---|---|
| **Merchant feed brand identity** | BR-SEO-71 (`09-seo.md:121–127`) cấm `brand` giả trong Product JSON-LD; BR-SEO-80 (`09-seo.md:138`) yêu cầu `g:brand = seo.brand_name` của shop; setting nằm ở `03-database.md:131`. Chưa có bằng chứng shop là thương hiệu của mọi sản phẩm nghệ nhân. Nếu điền tên shop cho hàng không mang thương hiệu đó, feed và dữ liệu cấu trúc có thể sai thực tế; đây là **rủi ro hợp đồng**, không khẳng định xung đột tuyệt đối hay chính sách Merchant hiện hành. | Trước P3-15: chủ shop xác nhận danh tính brand theo từng nhóm hàng; định nghĩa mapping feed/JSON-LD nhất quán và xác minh chính sách Merchant tại thời điểm làm. Kiểm giá/tình trạng theo BR-SEO-81. Không chặn P0-01. |
| **Redirect slug tác giả/trang chủ đề** | BR-SEO-03 (`09-seo.md:36`) yêu cầu mọi đổi slug 301, không chuỗi; URL `/tac-gia/{slug}` và `/chu-de/{slug}` ở `09-seo.md:26,30`. BR-CAT-08 (`04-business-rules.md:43–47`) liệt kê entity khác, chưa nêu hai loại này; `05-api.md:192–193` có PATCH và `schema.sql:1051–1054,1121–1123` có slug. | Trước P3-14/P3-07: xác nhận prefix, transaction tạo redirect và test 301 một bước khi đổi slug đã publish; P3-09/P3-14 phải phối hợp sitemap và canonical (G07). Không chặn P0-01. |

## Cải thiện và xác minh về sau

- `docs/plans/task-template.md:3–4` còn `TASK-XXX`/`REQ-###`, lệch ID P0–P6 và FR/BR/NFR/AC/DEC đã chốt. Khi tạo hồ sơ task P0-01, dùng đúng ID `P0-01` và tham chiếu spec; cập nhật template ở việc tài liệu riêng nếu cần. Đây là cải thiện quy trình, không blocker.
- `06-infrastructure.md:7,76,87,98,106–124` dự kiến VPS 4 GB với giới hạn container; cần đo tải, RAM và restore drill tại P3-10/P3-12. Chưa có benchmark, không suy ra kiến trúc thiếu tài nguyên. G24 yêu cầu xác minh chi phí, vị trí lưu dữ liệu và nghĩa vụ pháp lý trước go-live.
- G04/G27 (refund và chính sách đổi trả), G05 (upload video evidence), G06 (checkout consent), G09 (guest CSRF) là blocker **của task được ghi trong `spec-gaps.md`**, không phải blocker scaffold. Các G còn lại giữ đúng loại `MISSING_CONTRACT`/`DESIGN_RISK`/`SUGGESTION`; không nâng thành mâu thuẫn chỉ vì chưa chốt.

**Phê duyệt còn mở:** người dùng duyệt kết luận G2 và scope P0-01 trước G3; chọn phương án G26 và trách nhiệm seed G01 trước P0-04; chốt các hợp đồng theo mốc P1–P6 trong bảng. **Task kế tiếp đề xuất:** chỉ `P0-01` bằng skill `implementation-slice`, sau khi scope tại `docs/plans/task-readiness.md` được duyệt.
