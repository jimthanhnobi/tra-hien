# B01 — Thu gọn vận hành P0 cho Trà Hiên quy mô shop nhỏ

- **Trạng thái:** OWNER DECIDED 18/11/2026; amendment tài liệu cần G4 độc lập + CI/merge trước khi là baseline mới.
- **Phạm vi:** điều chỉnh A01 D8/D9 và D12/DEC-29, không thay DDL/role/grants hay code IAM đang hoạt động.
- **Quy mô:** 10–20 kết nối đồng thời, 100–1.000 lượt khách/ngày; 3–4 tài khoản nhân viên Admin. Mục tiêu ưu tiên Catalog, Admin, kho, nội dung SEO và kênh Zalo; không thiết kế vận hành enterprise chỉ vì tương lai có thể mở rộng.
- **Baseline:** PR #20/#21 A01; PR #22 Closure #1 MERGED, merge SHA `606fd2e279eb766976db18473cd45e7cbd142108`. PR CI #22 run `44270404911` và merge-push run `44271243633` đều completed/success, mỗi run 3/3 jobs PASS, xác minh qua GitHub.

## Quyết định owner

| Mục | Quyết định hiệu lực sau amendment | Không làm / rủi ro |
|---|---|---|
| IAM/TOTP | Giữ ASP.NET Identity, cookie, CSRF, rate limit, lockout, TOTP cho owner và đổi mật khẩu khởi tạo; một owner đủ tối thiểu | Không khuyến khích chia sẻ account giữa 3–4 người; tài khoản riêng giúp truy vết |
| Khôi phục MFA (A01 D8) | Hoãn `reset-staff-mfa` CLI và workflow recovery. Sự cố dùng vận hành ngoại tuyến được ủy quyền SSH + công cụ Identity-aware, backup và revoke sessions | Không có recovery tự động ở P0. Không trực tiếp UPDATE một cột DB để bypass Identity. Rủi ro lockout nếu owner mất TOTP vẫn còn |
| Owner coverage | Không bắt ≥2 owner, không `ownerMfaCoverageOk`/banner tại P0; thêm owner dự phòng qua Admin API là tùy chọn | Không khẳng định recovery khả dụng hoặc hai thiết bị được độc lập kiểm tra |
| P0→P1 (A01 D9) | Đủ Closure #1 G4/CI/merge, B01 G4/CI/merge, scoped C1–C3 và lệnh G3 riêng thì cho làm P1-01 trên PostgreSQL disposable, dữ liệu giả | Không chờ Closure #2 hoặc nghiệm thu HTTPS/owner thật trước DDL giả; tuyệt đối chưa cho dữ liệu thật/go-live nếu chưa nghiệm thu |
| Audit (A01 D12) | Giữ interceptor/redaction/atomicity đã triển khai, chỉ thêm `[Audited]` ở sự kiện quan trọng: giá, kho, đơn/hoàn tiền, staff permission, settings, PII/consent/xóa dữ liệu; giảm noise/không log UI reads | Không tắt audit đang hoạt động bằng comment, không ghi secrets/raw PII, vẫn audit nếu FR/BR riêng bắt buộc |
| Nền tảng | Giữ .NET 10, Next.js, PostgreSQL 17, modular monolith, Docker Compose, CI, least-privilege DB và backup bảo vệ | Không thêm microservices/Kubernetes/monitoring phức tạp trong P0 |

## Phân định gate

**Được code P1-01 DB giả sau khi B01 merge và owner giao G3 riêng:** migration schema/catalog 25 bảng và test parity, grants, rollback trên disposable PG17. Không tự chạy dữ liệu thật hoặc production.

**Trước nhập dữ liệu thật/go-live:** nghiệm thu HTTPS; ít nhất một owner đăng nhập thực tế sau đổi mật khẩu+TOTP, CSRF/lockout/logout; backup mã hóa và restore cô lập; kiểm settings/audit và DB runtime `tp_app`; chính sách/điều kiện kinh doanh tương ứng. Các mục NOT RUN vẫn NOT RUN cho tới khi có evidence. Owner có thể cân nhắc thêm owner dự phòng nhưng không bắt buộc.

**Không đổi:** D1–D7, D10–D14 còn lại, SEO, SKU/pricing, P1-07 import contract OPEN, role DB và nguyên tắc PII/security. Không sửa historical ADR A01; quyết định hiện hành chỉ rõ phần nào được B01 supersede. Không tạo roadmap ID mới.

## Hậu kiểm tài liệu

Các file cần đồng bộ: `docs/spec/00-README.md`, `01-requirements.md`, `02-architecture.md`, `04-business-rules.md`, `05-api.md`, `06-infrastructure.md`, `07-roadmap.md`; `docs/plans/task-readiness.md`, `p1-01-implementation.md`, `p0-closure-acceptance.md`. Scope docs only, không sửa source/migrations/AGENTS/WORKFLOW. G4 tầng A vì thay đổi spec auth và gate bảo mật, dù không có code.
