# 01 — Mục tiêu, phạm vi và yêu cầu

## 1. Mục tiêu kinh doanh (theo thứ tự ưu tiên)

1. **Khai thác 400–500 khách cũ** (hiện chỉ có tên + SĐT/Zalo): ghi nhận sở thích dần dần, giới thiệu đúng sản phẩm mới, đo được đơn phát sinh.
2. **Website là kênh bán mới**: bán lẻ có giá, giỏ hàng, COD và chuyển khoản.
3. **SEO** kéo khách mới: trang sản phẩm đủ thông số, trang nghệ nhân, trang chất đất, 1–2 bài kiến thức/tuần.
4. **Giảm thời gian tư vấn**: khách tự xem thông số, tồn kho, giá; nhân viên nhập đơn nhanh.
5. **Biết lãi thật**: doanh thu, giá vốn, ship, hàng vỡ/hoàn, marketing, chi phí cố định → lợi nhuận theo tháng và theo kênh.

**Thành công nghĩa là:** mỗi đơn (web hoặc nhập tay) đều đi qua hệ thống; tồn kho trên web luôn đúng; cuối tháng chủ shop xem được lãi ròng và kênh nào đem lại khách.

## 2. Phạm vi

### Trong phạm vi (toàn bộ, chia theo giai đoạn ở `07-roadmap.md`)

Catalog nhiều ngành hàng có biến thể · Kho · Giá và khuyến mãi theo sản phẩm · Giới thiệu bạn bè ·
Giỏ hàng, checkout khách vãng lai và có tài khoản · COD và chuyển khoản QR · Giao hàng thủ công (mở rộng được) ·
Đổi trả do vỡ/lỗi vận chuyển · CRM · Chiến dịch giới thiệu hàng mới · Bài viết/SEO · Báo cáo tài chính ·
Admin cho 2–3 người phân vai · Audit · Backup.

### Ngoài phạm vi bản này (không code, chỉ chừa chỗ mở rộng khi có ghi chú)

- Bán sỉ trên web, bảng giá theo khách (DEC-03).
- Nhiều kho, chuyển kho, ký gửi.
- Cổng thẻ/ví (VNPAY, MoMo). SePay tự đối soát → giai đoạn 5.
- Tích hợp API hãng ship → giai đoạn 5.
- Zalo OA API, ZNS, OTP, Zalo Login → giai đoạn 5 (cần OA đã xác thực).
- Hóa đơn điện tử → khi doanh thu/kế toán yêu cầu (DEC-18).
- Chatbot AI, app mobile, đa ngôn ngữ, điểm thưởng/loyalty, flash sale đếm ngược.
- Gửi tin hàng loạt tự động qua Zalo cá nhân (không được phép và không làm).

## 3. Vai trò người dùng

### Nhân viên (Admin)

| Vai trò | Mô tả | Không được |
|---|---|---|
| `owner` (Chủ shop) | Toàn quyền, gồm giá vốn, tài chính, quản lý nhân viên, xóa/ẩn danh dữ liệu khách | — |
| `sales` (Bán hàng/kho) | Xử lý đơn, khách hàng, thanh toán, giao hàng, đổi trả, nhập kho, kiểm kê, chiến dịch gửi tay | Xem giá vốn, lợi nhuận, chi phí; quản lý nhân viên |
| `content` (Nội dung) | Sản phẩm (trừ giá vốn và tồn kho), ảnh, bài viết, SEO, trang nghệ nhân/chất liệu | Xem PII khách, đơn hàng, tiền |

Quyền chi tiết ở `02-architecture.md §7`. Một người có thể có nhiều vai trò.

### Khách hàng (web)

- **Khách vãng lai:** xem, thêm giỏ, đặt hàng bằng tên + SĐT + địa chỉ. Tra đơn bằng mã đơn + SĐT.
- **Khách có tài khoản:** như trên, thêm xem lịch sử đơn, lưu sổ địa chỉ, lấy mã giới thiệu.

## 4. Yêu cầu chức năng (FR)

### 4.1 IAM — Định danh và phân quyền

- **FR-IAM-01** Nhân viên đăng nhập bằng email + mật khẩu; khóa tài khoản sau 5 lần sai trong 15 phút.
- **FR-IAM-02** Owner tạo, khóa, đổi vai trò nhân viên; bắt buộc đổi mật khẩu lần đầu.
- **FR-IAM-03** Bật được TOTP (2 lớp) cho nhân viên; **bắt buộc với `owner`** trước khi go-live.
- **FR-IAM-04** Khách đăng ký/đăng nhập bằng SĐT + mật khẩu (giai đoạn 2).
- **FR-IAM-05** Khách đăng nhập bằng Google (giai đoạn 2). Liên kết với hồ sơ khách theo luật BR-IAM-03.
- **FR-IAM-06** Khách quên mật khẩu: qua email đã xác minh (giai đoạn 2); qua OTP SMS/ZNS (giai đoạn 5).
- **FR-IAM-07** OTP SMS/Zalo ZNS và Zalo Login (giai đoạn 5).

### 4.2 CAT — Catalog

- **FR-CAT-01** Quản lý Product type, mỗi type có danh sách thuộc tính (kiểu: text, số + đơn vị, lựa chọn, có/không, kích thước).
  Thuộc tính đánh dấu được: bắt buộc khi publish, dùng để lọc, là trục biến thể.
- **FR-CAT-02** Quản lý danh mục dạng cây (tối đa 3 cấp) có SEO.
- **FR-CAT-03** Quản lý Material (chất liệu/chất đất) và Artisan (nghệ nhân): tên, slug, mô tả, ảnh, SEO, trang công khai.
- **FR-CAT-04** Quản lý Product: thông tin chung, thuộc tính, danh mục chính + phụ, material, artisan, mô tả rich text, hướng dẫn bảo quản, ảnh, SEO, sản phẩm liên quan.
- **FR-CAT-05** Quản lý Variant: SKU, tên biến thể, giá trị trục biến thể (ví dụ dung tích 180/245 ml), giá bán, cân nặng, kích thước gói hàng, cờ hàng độc bản, trạng thái.
- **FR-CAT-06** Ảnh: upload lên R2 qua signed URL, ALT bắt buộc, sắp xếp, ảnh bìa, gắn ảnh cho variant.
- **FR-CAT-07** Workflow Product: `draft → active → archived`, kiểm tra điều kiện publish (BR-CAT-04).
- **FR-CAT-08** Bộ sưu tập (collection) chọn tay: "Hàng mới về", "Quà tặng", "Nghệ nhân tiêu biểu"...
- **FR-CAT-09** Nhập sản phẩm hàng loạt từ Excel/CSV có chế độ chạy thử (dry-run) và báo lỗi từng dòng.
- **FR-CAT-10** Storefront: danh sách sản phẩm lọc theo danh mục, type, material, artisan, khoảng giá, dung tích, còn hàng; sắp xếp mới nhất/giá.
- **FR-CAT-11** Tìm kiếm không dấu tiếng Việt theo tên, SKU, nghệ nhân, chất liệu.

### 4.3 INV — Kho

- **FR-INV-01** Xem tồn theo variant: on hand, reserved, available, giá vốn bình quân (chỉ owner).
- **FR-INV-02** Phiếu nhập kho (receipt): nhà cung cấp (text), nhiều dòng variant + số lượng + giá nhập; xác nhận phiếu mới cộng tồn.
- **FR-INV-03** Nhập tồn đầu kỳ từ Excel (bảng giá nhập hiện có): SKU, số lượng, giá vốn. Dry-run trước.
- **FR-INV-04** Điều chỉnh tồn (kiểm kê, vỡ trong kho, mất) có lý do bắt buộc.
- **FR-INV-05** Phiên kiểm kê: nhập số đếm thực tế, hệ thống tính chênh lệch, owner duyệt thì sinh điều chỉnh.
- **FR-INV-06** Lịch sử biến động theo variant (sổ kho), lọc theo loại/ngày.
- **FR-INV-07** Cảnh báo tồn thấp theo ngưỡng của từng variant.
- **FR-INV-08** Giữ hàng tự động khi có đơn, nhả hàng khi hủy/hết hạn (BR-INV).

### 4.4 PRC — Giá và khuyến mãi

- **FR-PRC-01** Giá bán niêm yết theo variant.
- **FR-PRC-02** Giá khuyến mãi theo variant có thời gian bắt đầu/kết thúc; web hiện giá gạch ngang.
- **FR-PRC-03** Voucher dùng cho phần thưởng giới thiệu (và tạo tay bởi owner): giảm số tiền hoặc %, có trần, đơn tối thiểu, hạn dùng, giới hạn lượt.
- **FR-PRC-04** Lịch sử thay đổi giá được audit.

### 4.5 CRT — Giỏ hàng và checkout

- **FR-CRT-01** Giỏ hàng cho khách vãng lai (cookie) và khách đăng nhập; gộp giỏ khi đăng nhập.
- **FR-CRT-02** Giỏ luôn tính lại giá, khuyến mãi, tồn tại thời điểm xem; báo dòng hết hàng/đổi giá.
- **FR-CRT-03** Checkout một trang: người nhận, SĐT, email (tùy chọn), địa chỉ (tỉnh/thành + phường/xã + số nhà, đường), ghi chú, mã giới thiệu/voucher, phương thức thanh toán, đồng ý điều khoản.
- **FR-CRT-04** Tính phí ship theo BR-SHP-01 và hiện tổng tiền trước khi đặt.
- **FR-CRT-05** Sau khi đặt: trang xác nhận có mã đơn, tổng tiền; nếu chuyển khoản thì hiện **VietQR** + số tài khoản + nội dung = mã đơn + hạn thanh toán.
- **FR-CRT-06** Email xác nhận đơn cho khách (nếu có email); thông báo đơn mới cho shop qua Telegram và email.
- **FR-CRT-07** Tra cứu đơn bằng mã đơn + SĐT (không cần đăng nhập).

### 4.6 ORD — Đơn hàng

- **FR-ORD-01** Danh sách đơn: lọc theo trạng thái, trạng thái thanh toán, kênh, ngày, khách, tìm theo mã đơn/SĐT/tên.
- **FR-ORD-02** Chi tiết đơn: dòng hàng, tiền, thanh toán, vận chuyển, lịch sử trạng thái, ghi chú nội bộ, audit.
- **FR-ORD-03** Admin tạo đơn tay (khách sỉ, khách nhắn Zalo/điện thoại): chọn/tạo nhanh khách, tìm variant, giá có thể sửa (lý do bắt buộc nếu khác giá niêm yết), giảm giá toàn đơn, phí ship, kênh chốt, ghi chú.
- **FR-ORD-04** Chuyển trạng thái theo state machine (BR-ORD), mỗi lần chuyển ghi lịch sử.
- **FR-ORD-05** Sửa dòng hàng/địa chỉ khi đơn còn `pending` hoặc `confirmed`.
- **FR-ORD-06** Gia hạn hạn thanh toán của đơn chuyển khoản.
- **FR-ORD-07** In phiếu giao hàng/phiếu đóng gói (HTML in được).

### 4.7 PAY — Thanh toán

- **FR-PAY-01** Ghi nhận thanh toán chuyển khoản tay: số tiền, thời điểm, mã giao dịch ngân hàng, ghi chú.
- **FR-PAY-02** COD: khi giao thành công tạo khoản COD chờ đối soát; đối soát hàng loạt theo phiên của hãng ship.
- **FR-PAY-03** Hoàn tiền: số tiền, phương thức, lý do, liên kết đổi trả.
- **FR-PAY-04** Trạng thái thanh toán của đơn tính từ sổ thanh toán (BR-PAY-02).
- **FR-PAY-05** Màn hình "chờ đối soát": đơn chuyển khoản chưa xác nhận, COD chưa nhận tiền.

### 4.8 SHP — Giao hàng

- **FR-SHP-01** Cấu hình phí ship đồng giá và ngưỡng miễn phí.
- **FR-SHP-02** Tạo vận đơn thủ công: hãng, mã vận đơn, phí ship thực tế shop trả, số tiền COD.
- **FR-SHP-03** Cập nhật trạng thái vận đơn thủ công; giao thất bại/hoàn hàng.
- **FR-SHP-04** Interface provider để tích hợp GHN/GHTK sau (giai đoạn 5).

### 4.9 RET — Đổi trả

- **FR-RET-01** Tạo yêu cầu đổi trả cho đơn đã giao: dòng hàng, số lượng, lý do (vỡ/lỗi/giao nhầm), ảnh/video bằng chứng.
- **FR-RET-02** Xử lý: duyệt/từ chối, phương án (hoàn tiền một phần/toàn phần, gửi hàng thay thế), hàng có nhập lại kho được không.
- **FR-RET-03** Ghi nhận chi phí hàng vỡ (giá vốn) và chi phí ship phát sinh vào báo cáo.

### 4.10 CRM — Khách hàng

- **FR-CRM-01** Hồ sơ khách: tên, SĐT (chuẩn hóa, duy nhất), email, tên Zalo, nhóm (lẻ/sỉ/đại lý/VIP), tags, nguồn khách, người giới thiệu, trạng thái liên hệ, ghi chú tóm tắt.
- **FR-CRM-02** Sổ địa chỉ nhiều địa chỉ.
- **FR-CRM-03** Màn hình 360°: đơn đã mua, tổng chi tiêu, lần mua gần nhất, sở thích, tương tác, consent, chiến dịch đã nhận.
- **FR-CRM-04** Nhập khách cũ từ CSV/Excel (tên, SĐT, ghi chú, tags) có dry-run; trùng SĐT thì bỏ qua, không ghi đè.
- **FR-CRM-05** Ghi chú tương tác (kênh, loại, nội dung, thời điểm có thể ghi lùi) kèm hẹn chăm sóc lại.
- **FR-CRM-06** Danh sách "Cần liên hệ hôm nay": các hẹn chăm sóc đến hạn/quá hạn.
- **FR-CRM-07** Sở thích khách (material, artisan, product type, danh mục, khoảng giá): tự suy từ đơn đã giao + nhân viên gắn tay.
- **FR-CRM-08** Consent theo kênh/mục đích có bằng chứng; rút consent.
- **FR-CRM-09** Sửa SĐT/nguồn khách qua thao tác riêng có lý do và audit.
- **FR-CRM-10** Gộp 2 hồ sơ trùng (owner), chuyển đơn/tương tác sang hồ sơ giữ lại.
- **FR-CRM-11** Yêu cầu xóa dữ liệu: ẩn danh hóa hồ sơ, giữ đơn hàng ở dạng ẩn danh để báo cáo.
- **FR-CRM-12** Xuất dữ liệu của một khách (JSON/CSV) khi khách yêu cầu.

### 4.11 MKT — Marketing và giới thiệu

- **FR-MKT-01** Chiến dịch: tên, loại (hàng mới, khuyến mãi, chăm sóc), kênh, sản phẩm liên quan, thời gian, ngân sách.
- **FR-MKT-02** Chọn người nhận theo bộ lọc: sở thích, nhóm, tags, đã mua trong X ngày, chưa mua từ Y ngày, khoảng chi tiêu; loại khách `do_not_contact`.
- **FR-MKT-03** Mỗi người nhận có link theo dõi riêng (`/r/{code}`) và tin nhắn soạn sẵn; nhân viên sao chép gửi Zalo tay rồi bấm "Đã gửi".
- **FR-MKT-04** Theo dõi: đã gửi → đã click → đã đặt → đã giao, doanh thu theo chiến dịch.
- **FR-MKT-05** Link theo dõi chung cho bài Facebook/Zalo OA/QR in (không gắn khách).
- **FR-MKT-06** Mã giới thiệu cho mỗi khách đã có đơn giao thành công; người được giới thiệu nhận giảm giá đơn đầu; người giới thiệu nhận voucher khi đơn kia đủ điều kiện (BR-MKT).
- **FR-MKT-07** Nhập chi phí marketing theo ngày/kênh/chiến dịch.
- **FR-MKT-08** Ghi nhận UTM/link theo dõi/mã giới thiệu vào đơn web (attribution).

### 4.12 CNT — Nội dung và SEO

- **FR-CNT-01** Bài viết: rich text, ảnh, danh mục bài, liên kết tới sản phẩm/nghệ nhân/chất liệu, workflow `draft → in_review → published → archived`.
- **FR-CNT-02** Trang tĩnh: giới thiệu, chính sách đổi trả, vận chuyển, thanh toán, bảo mật, điều khoản (bắt buộc với website bán hàng).
- **FR-CNT-03** SEO: title, meta description, canonical, noindex, OG image cho product/category/material/artisan/article/page.
- **FR-CNT-04** Đổi slug của nội dung đã công khai tự tạo redirect 301; không chuỗi/vòng redirect.
- **FR-CNT-05** sitemap.xml, robots.txt, JSON-LD `Product` (có `Offer` giá thật và tình trạng còn hàng), `BreadcrumbList`, `Article`, `Organization`.
- **FR-CNT-06** Cấu hình website: tên, logo, hotline, Zalo, Facebook, địa chỉ, giờ làm việc, thông tin chuyển khoản.

### 4.13 FIN — Tài chính và báo cáo

- **FR-FIN-01** Nhập chi phí: cố định theo tháng (lương, mặt bằng, server...), đóng gói, khác.
- **FR-FIN-02** Báo cáo lãi lỗ theo tháng (công thức BR-FIN).
- **FR-FIN-03** Báo cáo theo kênh chốt và theo nguồn khách: số đơn, doanh thu, lãi gộp, chi phí marketing, lãi sau marketing.
- **FR-FIN-04** Báo cáo dòng tiền: tiền vào (chuyển khoản, COD đối soát), tiền ra (hoàn tiền), COD đang treo.
- **FR-FIN-05** Báo cáo sản phẩm: bán chạy, lãi gộp theo sản phẩm/type/material/artisan, hàng tồn lâu.
- **FR-FIN-06** Báo cáo khách: khách mới, khách quay lại, tỷ lệ mua lại, giá trị đơn trung bình.
- **FR-FIN-07** Báo cáo doanh thu năm/tháng xuất Excel phục vụ kê khai thuế hộ kinh doanh.
- **FR-FIN-08** Dashboard tổng quan: đơn chờ xử lý, chờ thanh toán, chờ đối soát, tồn thấp, hẹn chăm sóc hôm nay, doanh thu tháng.

### 4.14 PLT — Nền tảng

- **FR-PLT-01** Audit mọi thay đổi trên dữ liệu nghiệp vụ (ai, lúc nào, cái gì, trước/sau, che PII).
- **FR-PLT-02** Job nền: hủy đơn quá hạn thanh toán, dọn giỏ hết hạn, dọn upload dở, tính lại chỉ số khách, gửi email/Telegram qua outbox, xóa dữ liệu theo thời hạn lưu.
- **FR-PLT-03** Nhật ký thông báo đã gửi (email/Telegram) và trạng thái.

## 5. Yêu cầu phi chức năng (NFR)

| Mã | Yêu cầu |
|---|---|
| NFR-01 | Trang storefront p95 < 800 ms ở server (đã có cache); API admin p95 < 500 ms với 10.000 đơn, 5.000 khách, 2.000 variant. |
| NFR-02 | Không bao giờ bán vượt tồn: 2 khách đặt cùng lúc chiếc ấm độc bản cuối cùng → đúng 1 đơn thành công (có test đồng thời). |
| NFR-03 | Mọi POST tạo đơn/thanh toán có idempotency; retry không tạo trùng. |
| NFR-04 | PII (SĐT, email, địa chỉ, tên) không xuất hiện trong log, analytics, URL, response public. |
| NFR-05 | Backup DB mã hóa tối thiểu mỗi ngày + trước mỗi migration; RPO ≤ 24h (giai đoạn đầu), RTO ≤ 4h; diễn tập restore hằng tháng. |
| NFR-06 | Uptime mục tiêu 99,5%/tháng; cảnh báo khi down > 5 phút. |
| NFR-07 | Bảo mật: HTTPS, HSTS, cookie HttpOnly/Secure/SameSite, CSRF cho mọi request thay đổi dữ liệu từ trình duyệt, rate limit đăng nhập/checkout/tra đơn, mật khẩu băm bằng ASP.NET Core Identity hasher. |
| NFR-08 | Migration DB chỉ bổ sung (additive) sau go-live; không tự migrate khi khởi động API. |
| NFR-09 | Mọi luật BR có unit/integration test; integration test chạy trên PostgreSQL thật (Testcontainers). |
| NFR-10 | Mobile-first cho storefront và admin (nhân viên dùng điện thoại xử lý đơn). |
| NFR-11 | Truy cập được (a11y) cơ bản: nhãn form, tương phản, điều hướng bàn phím. |
| NFR-12 | Tuân thủ Luật Bảo vệ dữ liệu cá nhân 2025: consent, quyền xem/sửa/xóa, audit truy cập dữ liệu nhạy cảm. |
| NFR-13 | Chi phí hạ tầng giai đoạn đầu ≤ 500.000 đ/tháng. |
