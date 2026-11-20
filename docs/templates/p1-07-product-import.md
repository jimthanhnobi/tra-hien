# Mẫu nhập sản phẩm Excel/CSV

- Tải [mẫu Excel](p1-07-products.xlsx) hoặc [mẫu CSV](p1-07-products.csv). Excel dùng trang **SanPham**. Nếu lưu CSV từ Excel, chọn **CSV UTF-8** dùng dấu phẩy.
- Giữ tiêu đề ở hàng 1. Thay dữ liệu mẫu trước khi nhập. Mỗi dòng là một biến thể. Điền cùng `product_slug` ở các dòng thuộc một sản phẩm. Với một sản phẩm chỉ có một dòng, có thể để slug trống để hệ thống tự sinh.
- Chạy xem trước, sửa lỗi, rồi tải lại tệp để nhập thật. File được lưu theo từng nhóm sản phẩm. Nếu cần gửi lại, chỉ giữ các nhóm đã sửa lỗi. Điền slug cố định giúp tránh tạo trùng khi nhập lại. Khi cả slug lẫn SKU đều thiếu, nhập lại có thể tạo thêm bản nháp. Nếu kết quả nhóm là `unknown`, kiểm tra danh sách sản phẩm trước khi gửi lại.

| Cột | Cách điền |
|---|---|
| `name`, `product_type_code` | Bắt buộc ở mỗi dòng. Dùng mã loại đã cấu hình, ví dụ `teapot` hoặc `cup`. |
| `product_slug` | Đường dẫn ngắn không dấu, chữ thường và dấu gạch ngang. Cùng slug thì tên, loại và dữ liệu chung đã điền phải giống nhau. Hệ thống báo lỗi nếu slug đã tồn tại. |
| `variant_name`, `price_vnd`, `sku` | Tên như `180 ml`. Giá nguyên VND như `1200000`, để trống nếu chưa định giá. Cần quyền ghi giá. Để trống SKU để hệ thống cấp. Owner có thể nhập SKU viết hoa từ 2 đến 40 ký tự gồm chữ, số hoặc dấu gạch ngang, bắt đầu bằng chữ hoặc số. Định dạng cột mã là Text để giữ số 0 đầu. |
| `category_slug`, `material_slug`, `artisan_slug` | Dùng slug có sẵn trong danh mục. Nếu bỏ trống trên một dòng, nhóm có thể dùng giá trị đã điền ở dòng khác. |
| `description` | Mô tả chung dạng văn bản. |
| `weight_g`, `package_l_cm`, `package_w_cm`, `package_h_cm` | Khối lượng tính bằng gram nguyên dương. Kích thước kiện theo cm, dùng dấu chấm thập phân. |
| `p.<code>`, `v.<code>` | Mã thuộc tính đã cấu hình theo loại, ở cấp sản phẩm hoặc biến thể. Ví dụ `v.capacity_ml` = `180`. Số dùng dấu chấm thập phân với tối đa hai chữ số sau dấu chấm. Boolean dùng `true`/`false`, option dùng mã như `7_lo`, dimensions dùng JSON như `{"l":12,"h":7}`. |

Tệp tối đa 5 MiB, 500 dòng dữ liệu và 100 cột. XLSX tối đa 32 MiB sau giải nén. Chuyển công thức thành giá trị và lưu tệp dạng `.xlsx` thông thường. Sản phẩm nhập vào là bản nháp và chưa có tồn kho.
