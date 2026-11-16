# ADR-PROPOSAL-G01 — Khởi tạo dữ liệu tỉnh/phường

**Trạng thái quyết định: OWNER APPROVED — Option B (16/11/2026). Bản sửa đặc tả: OWNER ACCEPTED (G4 GO).** Phạm vi phê duyệt là P0-04 tạo cấu trúc và P0-09 nhập CSV có phiên bản, không hard-delete mã lịch sử, bảo toàn snapshot đơn và phân biệt full snapshot/delta. Chưa phê duyệt CLI, dataset cụ thể hay kết quả kiểm thử. Tên file `ADR-PROPOSAL-*` được giữ để bảo toàn lịch sử đề xuất.

## Bối cảnh và ràng buộc nguồn

Ở baseline `origin/main` `9cc5429`, `docs/spec/03-database.md:80–82` (§3.6) nói `platform.provinces`/`platform.wards` được nhập từ CSV danh mục chính thức bằng CLI, cập nhật được. Cũng tại `03-database.md:141–143` (§6), tỉnh/phường nằm trong danh sách “Seed bằng migration (cần cho hệ thống chạy)”. `docs/spec/07-roadmap.md:20` giao bảng platform cho P0-04, dòng 25 giao `import-provinces <csv>` cho P0-09. Đây là G01 `MISSING_CONTRACT`, **chưa chứng minh hai câu không thể cùng đúng** (`docs/analysis/spec-gaps.md:28`). DDL `schema.sql:44–56` có code text PK, FK ward→province và index; đơn lưu snapshot mã/tên (`schema.sql:795–801`), còn địa chỉ khách tham chiếu mã hiện hành (`schema.sql:581–594`). Không có CSV, phiên bản, URL nguồn hoặc checksum được xác minh trong repo tại baseline; bản sửa đặc tả đã qua G4 và được owner chấp nhận.

## Phương án và đề xuất

| Phương án | Lợi ích | Đánh đổi/rủi ro |
|---|---|---|
| A. Đưa toàn bộ dataset vào migration | DB có danh mục ngay sau P0-04; gần câu §6. | Migration phình to, trộn vòng đời schema với dữ liệu hành chính hay đổi; không có dataset/nguồn đã kiểm chứng để đóng băng; cập nhật sau đó vẫn cần CLI hoặc migration mới. |
| B. Migration tạo cấu trúc; CLI P0-09 nạp/cập nhật CSV có phiên bản | P0-04 gọn, có thể kiểm schema từ DB rỗng; dữ liệu có provenance và import lặp được; đúng task CLI P0-09. | Cần sửa rõ §6 và gate dữ liệu rỗng; các luồng phụ thuộc địa chỉ chưa sẵn sàng trước import hợp lệ. |
| C. Migration seed snapshot, CLI cập nhật | Có dữ liệu ban đầu và đường cập nhật; có thể hòa giải câu §3.6/§6. | Hai nguồn ghi dễ lệch, cần quy tắc snapshot/version; hiện chưa có dữ liệu chính thức xác minh, nên không thể lập snapshot đáng tin. |

**Option B đã được chủ dự án duyệt.** Sở hữu dữ liệu hành chính thuộc quy trình nhập có provenance, không thuộc EF model. P0-04 tạo hai bảng, PK/FK/index, migration từ DB rỗng và rollback/recovery plan; không nhét dữ liệu giả. P0-09 triển khai `import-provinces <csv>`, nạp bộ dữ liệu được xác minh và kiểm readiness của luồng cần địa chỉ. Không chuyển CLI sang P0-04; không xóa mã lịch sử.

## Hợp đồng CSV/import đề xuất cho P0-09

Một file UTF-8 (chấp nhận BOM) có tối thiểu `province_code,province_name,ward_code,ward_name`. Header cố định, `province_sort_order` tùy chọn và cách biểu diễn province chưa có ward trong bản đề xuất ban đầu là **chi tiết cần chốt ở P0-09**, không thuộc phần owner đã phê duyệt. Code giữ dạng **text** nguyên mẫu theo nguồn đã xác minh, không ép số hay mất zero đầu; tên Unicode chuẩn hóa nhất quán và không rỗng. Toàn file phải có một mã tỉnh ứng với đúng một tên, một mã ward duy nhất và một parent duy nhất; mã trùng mâu thuẫn, mã/tên thiếu, ward không có province hợp lệ, mã sai định dạng nguồn, hierarchy không còn 2 cấp hoặc encoding lỗi đều từ chối trước khi ghi.

Provenance tối thiểu cho mỗi lần import: tên và URL cơ quan phát hành, bản dataset, ngày hiệu lực, ngày tải/xác minh, SHA-256 của **bytes CSV thực nhập** và tài liệu ánh xạ từ nguồn sang CSV. Đây là bằng chứng vận hành cần được kiểm tra trước P0-09, **không có giá trị nào được đoán trong ADR này**. CLI in báo cáo dry-run gồm tổng province/ward, thêm/sửa/không đổi, mã vắng mặt, chuyển parent, deactivate dự kiến và lỗi theo dòng; chỉ khi operator chấp nhận mới apply. Cần lưu hoặc đính kèm manifest ngoài DB ở hồ sơ vận hành; nếu muốn lưu provenance trong DB phải có quyết định/migration riêng, không thêm bảng vào P0-04.

Apply toàn bộ trong một transaction. Cùng bytes/version import lại phải cho cùng kết quả và không đổi các row tương đương; không ghi một nửa khi validation/SQL lỗi. Đổi tên hoặc parent ward là cập nhật tham chiếu hiện hành **sau khi dry-run nêu rõ diff** và operator xác nhận; snapshot đơn cũ giữ nguyên. Vì `crm.customer_addresses` có FK tỉnh và ward độc lập, chuyển parent có thể tạo cặp mã sai dù từng FK hợp lệ: không sửa địa chỉ khách âm thầm, phải kiểm lại trước khi tái dùng cho fulfillment; cơ chế xác nhận thuộc task CRM liên quan. Mã vắng trong bộ mới không bị DELETE. Với full snapshot đã xác minh, mã vắng chỉ **có thể** được đề nghị `is_active=false` sau phân tích tác động và operator duyệt; delta không được ngầm vô hiệu hóa. Mã bị tham chiếu từ địa chỉ khách vẫn tồn tại để FK hợp lệ. Nếu hierarchy có cấp mới/đổi quan hệ ngoài mô hình `schema.sql:43–55`, dừng import và mở quyết định schema riêng.

Nếu nhập thất bại, transaction rollback; nếu import hoàn tất nhưng dữ liệu nguồn bị xác định sai, cần bản backup trước import, manifest, script phục hồi hoặc import phiên bản đã xác minh trước đó theo chính sách deactivate, không đảo bằng DELETE. Khi bảng tham chiếu rỗng, P0-04 migration vẫn hợp lệ; các luồng chọn địa chỉ phải báo chưa sẵn sàng ở P0-09/P3 trước khi phục vụ khách, thay vì âm thầm dùng dữ liệu giả. `docs/spec/03-database.md` §8 chỉ nói `/health/ready` kiểm migration; thêm điều kiện dữ liệu vào readiness phải được duyệt rõ, không giả định đã là yêu cầu.

## Bản sửa spec đã qua G4 và được owner chấp nhận

1. `docs/spec/03-database.md` §3.6: định nghĩa source/provenance, hợp đồng CSV/import/version, không xóa mã lịch sử, snapshot đơn giữ nguyên và cách xử lý mã vắng/chuyển parent.
2. `docs/spec/03-database.md` §6: bỏ tỉnh/phường khỏi nhóm “Seed bằng migration”; ghi seed qua CLI P0-09 sau migration cấu trúc. Giữ role/permission và setting defaults theo task tương ứng; không chuyển chúng vào P0-04 chỉ vì cùng câu §6.
3. `docs/spec/07-roadmap.md` §P0: làm rõ P0-04 chỉ tạo cấu trúc hai bảng, P0-09 sở hữu dữ liệu, dry-run/import và failure/recovery; giữ task IDs. `schema.sql:43–56` giữ nguyên cột/khóa/quan hệ theo Option B; F-01 được owner duyệt riêng ngày 16/11/2026 và chỉ đặt tên PK/FK của hai bảng này, đã qua G4 độc lập.

## Nghiệm thu và rủi ro còn lại

| Loại | Tình huống cần kiểm |
|---|---|
| Happy | Migration tạo PK/FK/index trên DB rỗng; CSV UTF-8 tiếng Việt hợp lệ nhập đủ tỉnh/phường; re-import giống hệt báo 0 thay đổi; cập nhật tên giữ code, snapshot đơn cũ không đổi. |
| Failure | Duplicate code xung đột, thiếu mã/tên, ward tham chiếu tỉnh không có, encoding sai hoặc hierarchy ngoài 2 cấp → lỗi có số dòng, DB không đổi; SQL lỗi giữa import → rollback. |
| Edge | Code có zero đầu; BOM; province chưa có ward; ward chuyển province; file delta không vô hiệu hóa mã vắng; code cũ còn FK địa chỉ; snapshot đầy đủ muốn deactivate phải có phê duyệt. |
| Security/vận hành | Chỉ operator có quyền import; không tin URL/tên file là nguồn chính thức; dry-run không ghi; checksum sau khi import khớp bytes; backup/restore hoặc kế hoạch phục hồi được kiểm. |

**Tác động:** P0-04 (bảng rỗng), P0-09 (CLI/dataset), P0-10 (migration DB rỗng), P2/P3 khi địa chỉ bắt đầu dùng reference data. Nguồn dataset chính thức, version, ngày hiệu lực, URL và checksum vẫn **NOT VERIFIED**; cần người chịu trách nhiệm kiểm nguồn và cung cấp file trước P0-09.

**Lịch sử và việc còn lại:** B được chủ dự án phê duyệt ngày 16/11/2026; A/C là phương án đã cân nhắc, không được chọn. F-02 tại `docs/plans/task-readiness.md` là điều kiện P0-09: chốt cách mang/lưu metadata/import mode, xác minh nguồn dataset và chi tiết CSV; task CRM/đặt đơn liên quan phải xác định cách kiểm lại địa chỉ sau đổi parent. Không biến các việc này thành blocker giả cho migration cấu trúc P0-04. Bản sửa spec đã qua G4 và được owner chấp nhận; P0-04 implementation **NOT AUTHORIZED**.
