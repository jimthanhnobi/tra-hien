# P0 closure — runbook nghiệm thu theo A01

**Trạng thái:** yêu cầu/runbook được bổ sung trong amendment owner-approved 18/11/2026; **NOT RUN**. Chưa có quyền triển khai closure, chạy lệnh vận hành hoặc dùng dữ liệu thật. Base amendment `288ca8b55cab2017c9ee42bd35036e2d2e5bd87c`; chỉ giao G3 riêng sau G4 amendment. Remediation P0-02/P0-04 (#1), P0-08/P0-12 (#2), không tạo ID roadmap mới. FR-IAM-02/03, BR-IAM-01/02/06/07/08, AC-P0-01…06, DEC-29/30; 03 §4, 05 §4.1, 06 §11.

## 1. Điều kiện và an toàn

- Operator được owner ủy quyền, xác nhận đúng môi trường giả/dev và backup nếu DB đã có dữ liệu. Không down -v/drop DB/reset password/REASSIGN OWNED để áp quyền. Dừng khi ownership bất thường; remediation từng object cần owner cho phép.
- Chỉ dùng identity giả khi diễn tập. Không ghi password, cookie, CSRF token, QR/shared TOTP secret, authenticator code hoặc backup/plaintext keys vào log/evidence/argv. Secret nhập tương tác hoặc công cụ an toàn được duyệt; không copy nguyên HTTP body/headers vào hồ sơ.
- Hai phiên HTTP/cookie jar riêng A/B qua HTTPS cùng origin Admin, không dùng chung CSRF. Sau mỗi login/logout/change-password/verify TOTP phải lấy token mới theo session; mọi POST/PATCH có X-CSRF-TOKEN. Không bỏ auth/CSRF vì thao tác từ server.
- Không giả định Staff UI hoàn chỉnh. Dùng Admin API/OpenAPI đúng published head đã review; body 05 §4.1, không sửa DB tay hoặc chạy bootstrap-owner lần hai để thêm owner.

## 2. Chuỗi DB/HTTPS (closure #1)

| Bước | Phương án / kết quả mong đợi | Kết quả thật |
|---|---|---|
| DB mới | Provision infra/postgres/init-roles.sql qua wrapper init-roles.sh, cấu hình maintenance/app DB đúng owner; migrate bằng tp_owner qua dev-migrate preflight, API tp_app | NOT RUN |
| DB hiện hữu | Backup/preflight, chạy cùng SQL ngoài init hook; giữ data/IAM/settings/audit/ledger/password/sequence. Bất thường ownership dừng, không xóa DB/volume | NOT RUN |
| Re-run/quyền | Chạy lại provisioning/idempotent migration trên DB giả; login thật app/backup, kiểm DML/DDL/append-only theo 03 §4; không dùng grant giả trong test | NOT RUN |
| HTTPS | Caddy local được trust + API/Admin đúng origin; Compose HTTP/config --quiet không chứng minh login được | NOT RUN |
| Owner A | Bootstrap lần đầu khi chưa có owner; login, đổi password, login lại, setup/verify TOTP rồi login bằng mã mới. Kiểm onboarding/ready và lấy CSRF theo phiên | NOT RUN |

Các script provisioning/dev-migrate/helper/migration thuộc G3 closure #1, chưa tạo trong amendment. Closure implementation phải điền exact commands, exit/status, environment, published SHA và log đã che dữ liệu cho cả nhánh mới/existing.

## 3. Tạo và kiểm owner thứ hai qua Admin API bảo vệ

Tất cả đường dẫn dưới có base `/api/v1/`. A là owner đã hoàn tất password/TOTP, B là tài khoản mới trên thiết bị TOTP thứ hai. Dùng API client có cookie session, CSRF và quản lý secret trong bộ nhớ; không đưa credentials thật vào ví dụ tài liệu.

| Bước | Request / kiểm chứng | Kết quả mong đợi | Kết quả thật |
|---|---|---|---|
| A ready | GET admin/auth/me và GET admin/auth/csrf trong session A | A active, roles có owner, permission staff.manage, đủ onboarding; token CSRF của A | NOT RUN |
| Tạo B | POST admin/staff với cookie/CSRF A, JSON `{email,displayName,initialPassword,roles:["owner"]}`; dữ liệu nhập bí mật | Tạo đúng 1 B; A dùng GET admin/staff/{idB} kiểm B active/owner/MustChangePassword=true; không cần Staff UI | NOT RUN |
| B login | Session/cookie jar B mới, GET admin/auth/csrf rồi POST admin/auth/login với mật khẩu tạm | Chỉ onboarding, chưa có quyền nghiệp vụ | NOT RUN |
| B password | POST admin/auth/change-password `{currentPassword,newPassword}` với CSRF B; login lại bằng mật khẩu mới, lấy lại CSRF | Mật khẩu tạm hết hiệu lực/phiên cũ bị thu hồi; onboarding TOTP còn bắt buộc | NOT RUN |
| B TOTP | POST admin/auth/totp/setup, chuyển QR/secret trực tiếp vào thiết bị riêng; POST admin/auth/totp/verify `{code}`; login lại bằng password + mã TOTP mới | Verify thành công; không ghi secret/mã/QR vào evidence; login thiếu/sai TOTP không được ready | NOT RUN |
| B ready | GET admin/auth/me trong session B; A GET staff/{idB}; B gọi GET admin/staff/{idB} cần staff.manage | B active/owner, MustChangePassword=false, TotpEnabled=true, quyền nghiệp vụ hoạt động sau onboarding | NOT RUN |
| Hai owner | Cờ ownerMfaCoverageOk sau closure #2 + checklist operator xác nhận hai thiết bị/secret độc lập | True khi ≥2 owner đủ điều kiện; count DB/API không tự chứng minh thiết bị độc lập | NOT RUN |
| Kiểm âm | Không cookie; sales/content không staff.manage; POST tạo B thiếu/sai CSRF; B chưa xong onboarding | 401/403 hoặc 400 csrf_invalid đúng contract; không tạo/đổi staff hay cấp quyền; kiểm audit an toàn | NOT RUN |

Đây là **phương án nghiệm thu**, chưa là kết quả API chạy thật. Closure phải ghi exact HTTP status thực tế theo OpenAPI đã review, không coi trạng thái UI là bằng chứng. Test không làm mất owner cuối cùng; dùng identity giả cho lockout/negative cases.

## 4. Audit, recovery và backup/điều tra

| Hạng mục | Mong đợi | Kết quả thật |
|---|---|---|
| Settings/audit | Owner ready sửa setting qua API/CSRF; đọc admin/audit bằng audit.read, đúng trước/sau đã che secret/PII, cùng transaction | NOT RUN |
| CSRF/lockout/logout | Thiếu CSRF không tác động; tài khoản thử sai 5 lần khóa 15 phút; logout cookie cũ bị từ chối | NOT RUN |
| Recovery #2 | Chạy reset-staff-mfa đúng operator cho identity giả; request kế tiếp cookie cũ 401; cả hai mode reset password đều bắt đổi password + TOTP lại; audit đúng 1 dòng không secret. Operator sai/audit lỗi/cancel → không thay đổi DB; concurrency không mất audit | NOT RUN |
| Backup | tp_readonly chỉ cho job backup được kiểm soát; credential/OS/network/execution hạn chế, full backup mã hóa và restore cô lập (có IAM/Data Protection nhạy cảm) | NOT RUN |
| Điều tra | Không dùng credential tp_readonly để query audit/staff. Dùng admin/audit hoặc identity read riêng theo vụ việc, cột/view đã che, thời hạn/audit/thu hồi; mặc định không quyền IAM credentials/tokens/stamps/authenticator secret hoặc DP xml | NOT RUN |

SELECT không phân biệt pg_dump và SQL tay; kiểm credential/access controls riêng. Quyền backup đầy đủ không phải quyền điều tra. Closure/P3-10 phải kiểm cả deny sensitive columns và không thừa kế role backup/owner cho identity điều tra.

## 5. Tiêu chí đóng và bằng chứng

Chỉ ghi DONE khi closure #1/#2 qua G4 độc lập, owner cho publish, CI đúng published head, owner merge; runbook đã nghiệm thu thật và owner thứ hai kiểm qua API. Ghi SHA, command/HTTP status/log an toàn, PASS/FAIL/NOT RUN từng bước. Bất kỳ bước bắt buộc còn NOT RUN/FAIL → chưa đóng P0/chưa giao P1-01. Source/DDL amendment này không tự chứng minh implementation, không cho phép deploy hoặc publish.
