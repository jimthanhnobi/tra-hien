# TeaPremium v2 — Bộ đặc tả hệ thống

> Phiên bản: 2.0 · Ngày chốt: 16/11/2026 · Trạng thái: **Đã chốt yêu cầu, chưa code**
> Đây là nguồn sự thật (source of truth) cho việc viết code. Code mâu thuẫn với spec là bug của code,
> trừ khi spec được sửa trước bằng một mục trong `## Nhật ký quyết định` bên dưới.

## 1. Cách đọc bộ spec

| File | Nội dung | Ai đọc |
|---|---|---|
| `00-README.md` | Mục lục, quy ước, thuật ngữ, nhật ký quyết định | Mọi người, AI viết code |
| `01-requirements.md` | Mục tiêu, phạm vi, vai trò, yêu cầu chức năng (FR) và phi chức năng (NFR) | Chủ shop, dev |
| `02-architecture.md` | Kiến trúc, cấu trúc solution, quy tắc tầng/module, cross-cutting | Dev, AI |
| `03-database.md` | Giải thích mô hình dữ liệu, quy ước, chỉ mục, vòng đời dữ liệu | Dev, AI |
| `schema.sql` | DDL tham chiếu PostgreSQL 17 (EF Core migration phải sinh ra schema tương đương) | Dev, AI |
| `04-business-rules.md` | Luật nghiệp vụ (BR), state machine, công thức tiền, báo cáo | Dev, AI, QA |
| `05-api.md` | Hợp đồng API: quy ước, endpoint, quyền, lỗi | Dev, AI |
| `06-infrastructure.md` | Hạ tầng, triển khai, backup, CI/CD, bảo mật vận hành | Dev/Ops |
| `07-roadmap.md` | Giai đoạn, task, Definition of Done, acceptance test | Chủ shop, dev |
| `08-chatgpt-playbook.md` | Cách giao việc cho ChatGPT theo từng task, prompt mẫu | Người giao việc |
| `09-seo.md` | SEO: URL, index, nội dung, liên kết nội bộ, JSON-LD, sitemap, Merchant feed, ảnh, tốc độ, đánh giá, đo lường, SEO Audit | Dev, AI, người viết nội dung |

## 2. Hệ thống mã định danh trong spec

- `FR-<MODULE>-<số>`: yêu cầu chức năng. Ví dụ `FR-INV-03`.
- `NFR-<số>`: yêu cầu phi chức năng.
- `BR-<MODULE>-<số>`: luật nghiệp vụ, **bắt buộc có test tương ứng**.
- `AC-<PHASE>-<số>`: tiêu chí nghiệm thu.
- `DEC-<số>`: quyết định thiết kế đã chốt.

Mã module: `SEO` (tìm kiếm), `IAM` (định danh), `CAT` (catalog), `INV` (kho), `PRC` (giá, khuyến mãi), `CRT` (giỏ, checkout),
`ORD` (đơn), `PAY` (thanh toán), `SHP` (giao hàng), `RET` (đổi trả), `CRM` (khách hàng),
`MKT` (marketing, giới thiệu), `CNT` (nội dung, SEO), `FIN` (tài chính, báo cáo), `PLT` (nền tảng).

Code và test phải ghi mã luật ở comment hoặc tên test, ví dụ:
`[Fact] public void BR_INV_04_reservation_cannot_exceed_available()`.

## 3. Quy ước bắt buộc (tóm tắt — chi tiết ở 02 và 03)

1. **Tiền:** VND, kiểu `bigint` (đồng, số nguyên). Không dùng `decimal`/`numeric` cho tiền bán lẻ. Không có số lẻ.
2. **Thời gian:** lưu `timestamptz` UTC. Ngày nghiệp vụ, kỳ báo cáo, mã đơn dùng múi giờ `Asia/Ho_Chi_Minh`.
3. **ID:** `uuid` sinh bằng `Guid.CreateVersion7()` ở ứng dụng.
4. **Enum:** lưu `text` + `CHECK` constraint. Giá trị `snake_case` tiếng Anh.
5. **Tên DB:** `snake_case`; mỗi module một PostgreSQL schema.
6. **Ngôn ngữ:** code, tên bảng, API bằng tiếng Anh; nội dung hiển thị và thông báo lỗi cho người dùng bằng tiếng Việt.
7. **Không tự thêm tính năng ngoài spec.** Thiếu thông tin thì ghi `TODO(spec): ...` và hỏi lại, không đoán.

## 4. Thuật ngữ

| Thuật ngữ | Nghĩa trong hệ thống |
|---|---|
| Product | Một mẫu sản phẩm có trang riêng trên web (ví dụ ấm "Vân Lô"). |
| Variant | Một phiên bản bán được của Product, có SKU, giá, tồn kho riêng. Product không có biến thể vẫn có đúng 1 variant. |
| Hàng độc bản (unique piece) | Variant có `is_unique_piece = true`: tồn tối đa 1, bán xong là hết. |
| Product type | Loại sản phẩm (ấm, chén, khay, hũ trà, xông trầm...), quyết định bộ thuộc tính. |
| Material | Chất liệu/chất đất (tử nê, lục nê, đoạn nê, chu nê, gang, thủy tinh, gốm...). Có trang SEO riêng. |
| Artisan | Nghệ nhân/tác giả. Có trang SEO riêng. |
| On hand | Số lượng thực có trong kho. |
| Reserved | Số lượng đang giữ cho đơn chưa xuất kho. |
| Available | `on_hand − reserved`, số được phép bán. |
| Delivered / Đã giao | Đơn giao thành công. **Đây là thời điểm ghi nhận doanh thu.** |
| Paid / Đã thu | Tiền đã thực nhận (chuyển khoản đã xác nhận hoặc COD đã đối soát). Khác với Delivered. |
| Customer | Hồ sơ khách trong CRM (khách cũ, khách web, khách sỉ). Định danh chính là SĐT chuẩn hóa. |
| Customer account | Tài khoản đăng nhập web của một Customer (tùy chọn). |
| Acquisition source | Nguồn khách biết đến shop lần đầu. Gắn vào Customer. |
| Sales channel | Kênh chốt đơn (web, Zalo cá nhân, điện thoại...). Gắn vào Order. |

## 5. Câu hỏi còn mở (không chặn P0–P2, phải chốt trước go-live P3)

| # | Câu hỏi | Mặc định tạm trong spec |
|---|---|---|
| Q1 | Phí ship đồng giá và ngưỡng freeship bao nhiêu? | 30.000 đ, freeship từ 1.000.000 đ |
| Q2 | Mức giảm cho người được giới thiệu và quà cho người giới thiệu? | Giảm 100.000 đ cho đơn ≥ 500.000 đ; voucher 100.000 đ hạn 90 ngày |
| Q3 | Nhân viên bán hàng được tự giảm tối đa bao nhiêu %? | 10% |
| Q4 | Đơn COD có bắt buộc gọi xác nhận trước khi gửi? | Có |
| Q5 | Danh sách khách cũ: tất cả đều đã từng mua? | Có (`had_purchase = true` mặc định khi import) |
| Q6 | Kế toán xác nhận căn cứ ghi nhận doanh thu cho kê khai (ngày giao hay ngày thu tiền) | Ngày giao (DEC-07), xuất kèm cả ngày thu tiền |
| Q7 | Tư vấn pháp lý xác nhận cách xin consent khách cũ qua chiến dịch `care` (BR-CRM-09) | Như BR-CRM-09 |
| Q8 | Nhà cung cấp VPS, object storage backup, SMTP | Chọn khi làm P3-10 |

## 6. Nhật ký quyết định (DEC)

| Mã | Quyết định | Lý do |
|---|---|---|
| DEC-01 | Modular Monolith: 1 API .NET 10, 1 PostgreSQL, nhiều schema theo module | 2–3 người dùng, 1 dev; tránh chi phí vận hành microservices |
| DEC-02 | Website bán lẻ thật: giá công khai, giỏ hàng, COD + chuyển khoản QR | Yêu cầu chủ shop 16/11/2026 |
| DEC-03 | Khách sỉ không mua trên web; đơn sỉ do Admin nhập | Giá sỉ thương lượng riêng |
| DEC-04 | Product → Variant; thuộc tính theo Product type (EAV có kiểu dữ liệu) | Nhiều ngành hàng, thông số khác nhau, cần lọc |
| DEC-05 | Tồn kho theo variant, sổ biến động append-only, giữ hàng khi đặt | Hàng độc bản, tránh bán trùng |
| DEC-06 | Giá vốn bình quân gia quyền di động theo variant | Đã có bảng giá nhập; hỗ trợ nhập thêm lô |
| DEC-07 | Doanh thu ghi nhận khi Delivered; tiền thực thu theo sổ Payments riêng | Không nhầm doanh thu với tiền mặt |
| DEC-08 | Chuyển khoản QR xác nhận tay; nội dung CK = mã đơn; interface PaymentProvider để cắm SePay sau | Chi phí thấp, mở rộng được |
| DEC-09 | Giao hàng nhập mã vận đơn tay; interface ShippingProvider để cắm GHN/GHTK sau | Như trên |
| DEC-10 | Đơn chuyển khoản chưa thanh toán tự hủy sau 24 giờ; nhân viên được hủy sớm hoặc gia hạn | Giải phóng hàng độc bản |
| DEC-11 | Phí ship đồng giá + miễn phí từ ngưỡng, cấu hình trong Admin | Đơn giản, dễ hiểu với khách |
| DEC-12 | Đổi trả chỉ khi vỡ/lỗi do vận chuyển, có bằng chứng | Chính sách shop |
| DEC-13 | Đăng nhập khách tùy chọn: giai đoạn 2 có SĐT+mật khẩu và Google; OTP SMS/Zalo ZNS và Zalo Login ở giai đoạn 5 | Shop chưa có Zalo OA; giảm chi phí |
| DEC-14 | Tài khoản nhân viên và tài khoản khách tách hoàn toàn (bảng, cookie, domain) | Giảm rủi ro leo quyền |
| DEC-15 | Admin là app Next.js riêng trên subdomain `admin.`; storefront là app riêng | Cô lập cookie và bundle; storefront UX sẽ làm lại sau |
| DEC-16 | Không dùng BFF; reverse proxy Caddy chuyển `/api/*` cùng origin sang API | Ít thành phần hơn bản v1 |
| DEC-17 | Giới thiệu hàng mới cho khách cũ: hệ thống lọc khách + tạo link theo dõi, người bán gửi tay qua Zalo cá nhân | Không scrape/không tự động Zalo cá nhân |
| DEC-18 | Hộ kinh doanh: chưa tích hợp hóa đơn điện tử; schema có chỗ gắn số hóa đơn; báo cáo doanh thu năm xuất được | Tùy doanh thu năm, xác nhận với kế toán |
| DEC-19 | VPS tại Việt Nam; ảnh trên Cloudflare R2; backup mã hóa đặt ở object storage tại Việt Nam | Chi phí thấp, dữ liệu cá nhân ở trong nước |
| DEC-20 | Tiền là `bigint` VND | Tránh lỗi làm tròn; VND không có phần lẻ |
| DEC-21 | SEO là yêu cầu cốt lõi; `09-seo.md` thắng các quy định SEO ở file khác | Kênh khách mới chính |
| DEC-22 | URL sản phẩm phẳng `/san-pham/{slug}`; bộ lọc noindex; tổ hợp có giá trị tìm kiếm làm trang chủ đề `/chu-de/{slug}` | Tránh trùng lặp/chỉ mục rác, vẫn bắt được từ khóa dài |
| DEC-23 | Hàng độc bản đã bán giữ trang 180 ngày (SoldOut + gợi ý tương tự), sau đó 301 về nghệ nhân/chất liệu/danh mục | Giữ giá trị SEO, tránh trang chết |
| DEC-24 | Feed Google Merchant Center cho hiển thị sản phẩm miễn phí; đánh giá chỉ từ người mua thật | Thêm kênh hiển thị trên Google, tín hiệu tin cậy |
| DEC-25 | Nhập liệu nháp được thiếu dữ liệu; publish đòi giá của active, inactive được NULL nhưng không bán; kích hoạt validate giá | BR-CAT-13/15, A01 D2/D5 |
| DEC-26 | SKU sequence toàn catalog + UNIQUE + retry có giới hạn; mã đã cấp không tự đổi theo type/material/thêm variant | BR-CAT-14, A01 D3 |
| DEC-27 | Mô tả < 150 từ không chặn publish do số từ, chỉ thin-description noindex; manual/điều kiện SEO khác và metadata không nới | BR-SEO-34, A01 D4 |
| DEC-28 | Evidence private: ảnh ≤15 MiB, mp4/mov ≤50 MiB chỉ purpose=evidence | A01 D7 |
| DEC-29 | ≥2 owner TOTP/thiết bị riêng và recovery CLI được ủy quyền, audit nguyên tử/revoke/password+TOTP lại; owner thứ hai qua Admin API bảo vệ | BR-IAM-07/08, A01 D8/D9 |
| DEC-30 | Role SQL + grants migration chung; provisioning mới/existing giữ dữ liệu; tp_readonly chỉ backup kiểm soát, điều tra dùng quyền riêng | 03 §4, A01 D11 + bổ sung owner |

## 7. Amendment A01 — owner approved 18/11/2026

Owner phê duyệt D1–D14 và finalize §8 tại ADR A01, bổ sung backup/điều tra tách quyền và owner thứ hai qua Admin API bảo vệ. Base `288ca8b55cab2017c9ee42bd35036e2d2e5bd87c`; amendment local **READY_FOR_LOCAL_G4**, chưa có commit SHA mới/G4 PASS/publish permission. Chỉ sửa đặc tả/workflow; không triển khai code/migration. Hợp đồng import variant P1-07 và các gap ngoài scope giữ OPEN theo task. Kiểm chứng ở `docs/reviews/a01-amendment-evidence.md`.
