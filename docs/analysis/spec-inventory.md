# Kiểm kê nguồn đặc tả TeaPremium V2

**Đầu vào audit:** commit `cadce575597738bf40ad7c45689482de5a5fbc3d`, bộ spec ghi phiên bản 2.0 ngày 16/11/2026 tại `docs/spec/00-README.md:3`. Đọc toàn bộ 10 Markdown và `schema.sql`; không có file nguồn nào bị bỏ qua. Đây là kiểm kê nội dung, không xác nhận tuyên bố “đã chạy thử SQL” (`03-database.md:3`) hay chữ “đã chốt” là bằng chứng kiểm thử hoặc phê duyệt độc lập.

## Nguồn và vai trò

| File | Vai trò | Mục chính (dòng) |
|---|---|---|
| `docs/spec/00-README.md` | Thứ tự nguồn, thuật ngữ, Q1–Q8, DEC-01…24 | Cách đọc 7; ID 23; quy ước 38; thuật ngữ 48; câu hỏi 68; quyết định 81 |
| `docs/spec/01-requirements.md` | Mục tiêu, phạm vi, vai trò, FR và NFR | Mục tiêu 3; phạm vi 13; vai trò 33; FR 50; NFR 194 |
| `docs/spec/02-architecture.md` | Kiến trúc, ranh giới module, giao dịch, bảo mật, test | Tổng quan 3; repo 27; phụ thuộc 68; cross-cutting 98; xác thực 182; phân quyền 197; test 241 |
| `docs/spec/03-database.md` | Quy ước, ý nghĩa dữ liệu, seed, migration | Quy ước 5; bản đồ 20; giải thích 49; DB roles 93; settings 108; seed 141; vòng đời 165; migration 178 |
| `docs/spec/schema.sql` | DDL đích PostgreSQL 17, đối chiếu với 03/04/05/09 | Platform 32; media 130; catalog 163; inventory 391; pricing 486; CRM 525; marketing 688; sales 758; content 1041; reviews 1152; finance 1187; IAM chú giải 1207 |
| `docs/spec/04-business-rules.md` | BR nghiệp vụ và công thức | IAM 8; catalog 22; kho 55; giá 77; checkout 89; đơn 121; thanh toán 161; ship 181; đổi trả 189; CRM 198; marketing 219; content 235; báo cáo 244; nền tảng 277 |
| `docs/spec/05-api.md` | Quy ước API, đường dẫn, quyền, lỗi | Chung 3; store 30; account 68; admin 86; nội bộ 214 |
| `docs/spec/06-infrastructure.md` | Vận hành, backup, CI/CD, bảo mật | Lựa chọn 3; VPS 20; compose 35; Caddy 136; DB 190; backup 211; CI/CD 224; bảo mật 242; go-live 260 |
| `docs/spec/07-roadmap.md` | Task P0–P6, phụ thuộc, AC | P0 13; P1 41; P2 74; P3 111; P4 155; P5 175; P6 195 |
| `docs/spec/08-chatgpt-playbook.md` | Cách giao việc cho AI; ví dụ theo task | Nguyên tắc 3; prompt 11; mẫu 28; ví dụ 44; review 81; cập nhật spec 98 |
| `docs/spec/09-seo.md` | BR-SEO và ưu tiên DEC-21 | URL 17; index 38; vòng đời 70; metadata 77; landing 102; link 109; JSON-LD 119; sitemap/feed 134; ảnh 142; tốc độ 148; review 155; đo lường 161; audit 167 |

**Thứ tự áp dụng:** `00-README.md:7–21` xác lập nguồn; DEC-21 (`00-README.md:105`) cho `09-seo.md` ưu tiên với SEO. `schema.sql:1–7` là DDL đích, cần hòa giải với luật miền trước migration. `docs/analysis/` chỉ là kết quả audit, không tự sửa nguồn.

## Phạm vi định danh

| Loại | Số ID định nghĩa | Nguồn |
|---|---:|---|
| FR | 99 | `01-requirements.md:50–193` |
| BR nghiệp vụ | 118 | `04-business-rules.md:8–291` |
| BR-SEO | 58 | `09-seo.md:17–177` |
| NFR | 13 | `01-requirements.md:194–210` |
| AC | 47 | `07-roadmap.md:31–191` |
| DEC | 24 | `00-README.md:81–108` |
| Task | 81 | `07-roadmap.md:13–206` (P0 12, P1 14, P2 16, P3 18, P4 6, P5 7, P6 8) |

Các mã gốc và dòng định nghĩa riêng được liệt kê trong [traceability.md](traceability.md). Các chuỗi như `BR-ORD` hoặc `BR-SEO` không có số là tên nhóm, không phải ID yêu cầu. `FR-CNT-06` và `AC-P3-09` nằm ngoài thứ tự số ở file gốc nhưng vẫn được giữ nguyên.

## Quyết định và câu hỏi mở

- DEC-01…06 (`00-README.md:85–90`): modular monolith, bán lẻ web, tách sỉ, product/variant/EAV, tồn và giá vốn.
- DEC-07…12 (`00-README.md:91–96`): doanh thu lúc giao, đối soát thủ công, giữ đơn QR, phí ship, đổi trả.
- DEC-13…18 (`00-README.md:97–102`): tài khoản khách, tách IAM, hai app, không BFF, marketing gửi tay, hóa đơn.
- DEC-19…24 (`00-README.md:103–108`): vị trí hạ tầng, VND bigint, ưu tiên SEO, URL, hàng độc bản đã bán, Merchant/review.
- Q1–Q8 (`00-README.md:68–79`) được spec ghi là cần chốt trước go-live P3, không tự động chặn P0–P2. Q1/Q3/Q5 cần đối chiếu khi tới task sử dụng giá trị tạm; Q6/Q7/Q8 cần xác minh bên ngoài/chủ shop.

## Mục lục đầy đủ task gốc

| ID | Pha | Dòng định nghĩa |
|---|---|---|
| P0-01 | P0 | `docs/spec/07-roadmap.md:17` |
| P0-02 | P0 | `docs/spec/07-roadmap.md:18` |
| P0-03 | P0 | `docs/spec/07-roadmap.md:19` |
| P0-04 | P0 | `docs/spec/07-roadmap.md:20` |
| P0-05 | P0 | `docs/spec/07-roadmap.md:21` |
| P0-06 | P0 | `docs/spec/07-roadmap.md:22` |
| P0-07 | P0 | `docs/spec/07-roadmap.md:23` |
| P0-08 | P0 | `docs/spec/07-roadmap.md:24` |
| P0-09 | P0 | `docs/spec/07-roadmap.md:25` |
| P0-10 | P0 | `docs/spec/07-roadmap.md:26` |
| P0-11 | P0 | `docs/spec/07-roadmap.md:27` |
| P0-12 | P0 | `docs/spec/07-roadmap.md:28` |
| P1-01 | P1 | `docs/spec/07-roadmap.md:45` |
| P1-02 | P1 | `docs/spec/07-roadmap.md:46` |
| P1-03 | P1 | `docs/spec/07-roadmap.md:47` |
| P1-04 | P1 | `docs/spec/07-roadmap.md:48` |
| P1-05 | P1 | `docs/spec/07-roadmap.md:49` |
| P1-06 | P1 | `docs/spec/07-roadmap.md:50` |
| P1-07 | P1 | `docs/spec/07-roadmap.md:51` |
| P1-08 | P1 | `docs/spec/07-roadmap.md:52` |
| P1-09 | P1 | `docs/spec/07-roadmap.md:53` |
| P1-10 | P1 | `docs/spec/07-roadmap.md:54` |
| P1-11 | P1 | `docs/spec/07-roadmap.md:55` |
| P1-12 | P1 | `docs/spec/07-roadmap.md:56` |
| P1-13 | P1 | `docs/spec/07-roadmap.md:57` |
| P1-14 | P1 | `docs/spec/07-roadmap.md:58` |
| P2-01 | P2 | `docs/spec/07-roadmap.md:78` |
| P2-02 | P2 | `docs/spec/07-roadmap.md:79` |
| P2-03 | P2 | `docs/spec/07-roadmap.md:80` |
| P2-04 | P2 | `docs/spec/07-roadmap.md:81` |
| P2-05 | P2 | `docs/spec/07-roadmap.md:82` |
| P2-06 | P2 | `docs/spec/07-roadmap.md:83` |
| P2-07 | P2 | `docs/spec/07-roadmap.md:84` |
| P2-08 | P2 | `docs/spec/07-roadmap.md:85` |
| P2-09 | P2 | `docs/spec/07-roadmap.md:86` |
| P2-10 | P2 | `docs/spec/07-roadmap.md:87` |
| P2-11 | P2 | `docs/spec/07-roadmap.md:88` |
| P2-12 | P2 | `docs/spec/07-roadmap.md:89` |
| P2-13 | P2 | `docs/spec/07-roadmap.md:90` |
| P2-14 | P2 | `docs/spec/07-roadmap.md:91` |
| P2-15 | P2 | `docs/spec/07-roadmap.md:92` |
| P2-16 | P2 | `docs/spec/07-roadmap.md:93` |
| P3-01 | P3 | `docs/spec/07-roadmap.md:115` |
| P3-02 | P3 | `docs/spec/07-roadmap.md:116` |
| P3-03 | P3 | `docs/spec/07-roadmap.md:117` |
| P3-04 | P3 | `docs/spec/07-roadmap.md:118` |
| P3-05 | P3 | `docs/spec/07-roadmap.md:119` |
| P3-06 | P3 | `docs/spec/07-roadmap.md:120` |
| P3-07 | P3 | `docs/spec/07-roadmap.md:121` |
| P3-08 | P3 | `docs/spec/07-roadmap.md:122` |
| P3-09 | P3 | `docs/spec/07-roadmap.md:123` |
| P3-13 | P3 | `docs/spec/07-roadmap.md:124` |
| P3-14 | P3 | `docs/spec/07-roadmap.md:125` |
| P3-15 | P3 | `docs/spec/07-roadmap.md:126` |
| P3-16 | P3 | `docs/spec/07-roadmap.md:127` |
| P3-17 | P3 | `docs/spec/07-roadmap.md:128` |
| P3-18 | P3 | `docs/spec/07-roadmap.md:129` |
| P3-10 | P3 | `docs/spec/07-roadmap.md:130` |
| P3-11 | P3 | `docs/spec/07-roadmap.md:131` |
| P3-12 | P3 | `docs/spec/07-roadmap.md:132` |
| P4-01 | P4 | `docs/spec/07-roadmap.md:160` |
| P4-02 | P4 | `docs/spec/07-roadmap.md:161` |
| P4-03 | P4 | `docs/spec/07-roadmap.md:162` |
| P4-04 | P4 | `docs/spec/07-roadmap.md:163` |
| P4-05 | P4 | `docs/spec/07-roadmap.md:164` |
| P4-06 | P4 | `docs/spec/07-roadmap.md:165` |
| P5-01 | P5 | `docs/spec/07-roadmap.md:179` |
| P5-02 | P5 | `docs/spec/07-roadmap.md:180` |
| P5-03 | P5 | `docs/spec/07-roadmap.md:181` |
| P5-04 | P5 | `docs/spec/07-roadmap.md:182` |
| P5-05 | P5 | `docs/spec/07-roadmap.md:183` |
| P5-06 | P5 | `docs/spec/07-roadmap.md:184` |
| P5-07 | P5 | `docs/spec/07-roadmap.md:185` |
| P6-01 | P6 | `docs/spec/07-roadmap.md:199` |
| P6-02 | P6 | `docs/spec/07-roadmap.md:200` |
| P6-03 | P6 | `docs/spec/07-roadmap.md:201` |
| P6-04 | P6 | `docs/spec/07-roadmap.md:202` |
| P6-05 | P6 | `docs/spec/07-roadmap.md:203` |
| P6-06 | P6 | `docs/spec/07-roadmap.md:204` |
| P6-07 | P6 | `docs/spec/07-roadmap.md:205` |
| P6-08 | P6 | `docs/spec/07-roadmap.md:206` |

## Kiểm kê cấu trúc DB

`schema.sql` định nghĩa **11 schema và 78 lệnh `CREATE TABLE`**; bảng IAM chỉ liệt kê ở chú giải `schema.sql:1207–1210`, dự kiến để ASP.NET Identity/EF tạo. Có 3 extension (`unaccent`, `pg_trgm`, `btree_gist`) và hàm `public.f_unaccent` (`schema.sql:10–17`). Các FK chéo schema khiến thứ tự migration theo P0/P1/P2/P5 phải được giải quyết rõ trong [spec-gaps.md](spec-gaps.md).

## Kết luận phạm vi

Đã kiểm tra định danh, vai trò, luồng kho–đơn–thanh toán–CRM, API/CSRF/quyền, schema, SEO, roadmap và vận hành. Chưa chạy SQL trên PostgreSQL, chưa xác minh dịch vụ hoặc pháp luật bên ngoài, chưa chạy code/test vì đây là audit nguồn. Danh sách mâu thuẫn và khoảng trống nằm trong [spec-gaps.md](spec-gaps.md); ánh xạ task và kiểm thử nằm trong [traceability.md](traceability.md).
