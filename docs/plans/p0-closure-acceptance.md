# P0 closure — runbook nghiệm thu theo A01

**Trạng thái 18/11/2026 (B01):** Closure #1 [PR #22](https://github.com/jimthanhnobi/tra-hien/pull/22) đã MERGED, G4 independent PASS theo evidence reviewer owner cung cấp; head `86d8b561dc5a2db22a151eb499fb2a8512769cd0`, merge `606fd2e279eb766976db18473cd45e7cbd142108`. PR CI run 44270404911 success; merge CI run 44271243633 cần kiểm kết quả cuối. DB disposable tests 463/463 được reviewer báo PASS, nhưng HTTPS/owner thật/backup-restore **NOT RUN**. B01 hoãn Closure #2 CLI recovery/coverage, không đánh dấu P0/production-ready DONE. Hồ sơ Closure #1: [p0-closure-01-implementation.md](p0-closure-01-implementation.md).

## 1. Điều kiện và an toàn

- Operator được owner ủy quyền, xác nhận đúng môi trường giả/dev và backup nếu DB đã có dữ liệu. Không down -v/drop DB/reset password/REASSIGN OWNED để áp quyền. Dừng khi ownership bất thường; remediation từng object cần owner cho phép.
- Chỉ dùng identity giả khi diễn tập. Không ghi password, cookie, CSRF token, QR/shared TOTP secret, authenticator code hoặc backup/plaintext keys vào log/evidence/argv. Secret nhập tương tác hoặc công cụ an toàn được duyệt; không copy nguyên HTTP body/headers vào hồ sơ.
- Owner sử dụng một phiên HTTP/cookie jar riêng qua HTTPS cùng origin Admin, sau login/logout/change-password/verify TOTP lấy CSRF mới; mọi POST/PATCH có `X-CSRF-TOKEN`. Nếu tạo owner thứ hai, hai phiên tách riêng. Không bỏ auth/CSRF vì thao tác từ server.
- Không giả định Staff UI hoàn chỉnh; owner B là tùy chọn. Không chạy `bootstrap-owner` lần hai hoặc sửa thẳng dữ liệu Identity/TOTP trong DB để xử lý mất quyền truy cập.

## 2. Chuỗi DB/HTTPS (closure #1)

| Bước | Phương án / kết quả mong đợi | Kết quả thật |
|---|---|---|
| DB mới | Provision infra/postgres/init-roles.sql qua wrapper init-roles.sh, cấu hình maintenance/app DB đúng owner; migrate bằng tp_owner qua dev-migrate preflight, API tp_app | PASS automation PostgreSQL 17.12: init hook, fresh EF và entry point; toàn stack Compose thủ công NOT RUN |
| DB hiện hữu | Backup/preflight, chạy cùng SQL ngoài init hook; giữ data/IAM/settings/audit/ledger/password/sequence. Bất thường ownership dừng, không xóa DB/volume | PASS baseline 3 migrations + data giả → migration thứ 4, fingerprint không đổi; backup/restore thực NOT RUN |
| Re-run/quyền | Chạy lại provisioning/idempotent migration trên DB giả; login thật app/backup, kiểm DML/DDL/append-only theo 03 §4; không dùng grant giả trong test | PASS G3/reviewer: 103/103 focused và full 463/463, 0 skipped; F1–F4 và GRANT OPTION table/sequence CLOSED; không thay vận hành manual |
| HTTPS | Caddy local được trust + API/Admin đúng origin; Compose HTTP/config --quiet không chứng minh login được | NOT RUN |
| Owner A | Bootstrap lần đầu khi chưa có owner; login, đổi password, login lại, setup/verify TOTP rồi login bằng mã mới. Kiểm onboarding/ready và lấy CSRF theo phiên | NOT RUN |

Artifact Closure #1 đã publish/merge qua PR #22; chỉ bằng chứng trên DB disposable, không tự coi là đã chạy trên DB operator hoặc production. Các lệnh dưới dành cho operator trên DB giả, không phải chỉ dẫn áp dụng production. `.env` chứa TP_DATABASE và ba password riêng + bootstrap POSTGRES_USER/PASSWORD; không truyền secret qua argv/log. SQL idempotent chỉ tạo role/DB thiếu; role đã có sai thuộc tính/membership/ownership/quyền phải dừng. Volume cũ do superuser sở hữu không được tự chuyển owner; owner phê duyệt remediation từng object riêng.

**Volume mới**, từ repo root, sau khi cấu hình `infra/.env` theo [infra/README.md](../../infra/README.md):

```powershell
docker compose -f infra/compose.dev.yml config --quiet
docker compose -f infra/compose.dev.yml up -d postgres
./scripts/dev-migrate.ps1 -PreflightOnly
./scripts/dev-migrate.ps1
docker compose -f infra/compose.dev.yml up -d --build api store admin
```

**DB hiện hữu**, xác nhận backup an toàn/đúng DB giả, giữ bootstrap credentials và tên DB cũ trong TP_DATABASE; không reset volume:

```powershell
docker compose -f infra/compose.dev.yml stop api
docker compose -f infra/compose.dev.yml up -d postgres
docker compose -f infra/compose.dev.yml exec -T postgres sh /opt/teapremium/init-roles.sh
./scripts/dev-migrate.ps1 -PreflightOnly
./scripts/dev-migrate.ps1
docker compose -f infra/compose.dev.yml up -d --build api
```

**Role checks**: `dev-migrate -PreflightOnly` phải exit 0 bằng direct login tp_owner; kiểm catalog không in password/hash bằng lệnh ở infra README. SQL preflight chạy trước toàn chuỗi, không chỉ ở migration mới. Gọi EF update trực tiếp bỏ preflight vẫn có thể lỗi ở GRANT P0-09 cũ. Runtime ConnectionStrings__Default dùng tp_app; tp_readonly chỉ credential backup được kiểm soát, không dùng readiness/investigation/API. Không tự migrate ở API Production startup. Rollback app giữ ACL hạn chế; Down migration grants không đổi data/quyền về trạng thái thiếu an toàn. Không xóa keys/history/password/sequence.

**Kiểm chứng local/independent:** reviewer chạy `dotnet test backend/TeaPremium.sln --no-build --no-restore` trên disposable Linux PG17 và xác nhận **463/463 PASS**, gồm **103/103 focused** DatabaseRolesTests; locked restore/build/format/EF model/migration/Compose/Docker và permission regressions PASS theo G4 owner cung cấp. Runbook vẫn chưa chạy toàn stack thủ công; PR CI merge-head phải xác minh riêng. Chi tiết exact commands và SHA trong hồ sơ G3/G4; không coi tests này là bằng chứng vận hành.

**HTTPS/Caddy dependencies**: API native hoặc host port 5080, Admin 3001, Caddy native cấu hình `infra/caddy/Caddyfile.admin-local`, origin `https://admin.localhost:8443`, hostname resolve loopback và CA được operator kiểm/trust. API đặt Iam__AdminHost=admin.localhost và trusted proxy đúng địa chỉ thực; không wildcard. Production-mode local cần certificate X.509 private key cho Data Protection, cùng file/password giữa CLI/API/restart và runtime tp_app; không ghi secrets vào hồ sơ. Lệnh Caddy/native API ở infra README và hồ sơ P0-12. G3 này không tự cài CA, đổi hosts, bootstrap owner thật hoặc giả định HTTP Compose là nghiệm thu login; các bước HTTPS/owner A/B bên dưới vẫn NOT RUN.

## 3. Nghiệm thu Admin tối thiểu trước dữ liệu thật / go-live

Không chặn việc code P1-01 migration trên DB giả. Operator nghiệm thu các bước sau trên môi trường được phép, không dùng dữ liệu khách thật cho negative test:

| Hạng mục | Kỳ vọng | Kết quả thực tế |
|---|---|---|
| HTTPS | Caddy/chứng chỉ/domain/Admin cookie cùng origin và API qua reverse proxy đúng; CA/cert và proxy config được kiểm | NOT RUN |
| Owner đầu | Bootstrap khi DB chưa có owner; đổi mật khẩu đầu, setup/verify TOTP, đăng nhập lại qua HTTPS; owner nhận quyền đúng | NOT RUN |
| TOTP/CSRF | Thiếu/sai TOTP không sẵn sàng; POST thiếu/sai CSRF bị từ chối không tác động DB | NOT RUN |
| Lockout/logout/password | Sai mật khẩu 5 lần bị khóa 15 phút; logout/session cũ bị từ chối; đổi mật khẩu revoke phiên cũ | NOT RUN |
| Settings/audit | Owner sửa setting không nhạy cảm qua API, audit actor/thời điểm/trước-sau hợp lệ và không lộ PII/secret | NOT RUN |
| Backup/restore | Backup bằng identity riêng được kiểm soát, mã hóa và thử restore vào DB cô lập; không dùng `tp_readonly` để điều tra | NOT RUN |
| Owner thứ hai | **OPTIONAL** qua POST `admin/staff` với `staff.manage` + CSRF, không yêu cầu Staff UI; không chặn P1-01/go-live | NOT RUN (optional) |

Các thử nghiệm HTTP phải ghi endpoint/status đã che dữ liệu; không log cookie/password/TOTP/token. Không tự đánh dấu PASS khi chỉ có integration test.

## 4. Khôi phục quyền truy cập và audit tinh gọn

B01 **hoãn** `reset-staff-mfa` CLI, `ownerMfaCoverageOk`/banner và test coverage hai owner: **DEFERRED, không phải DONE**. Owner chấp nhận rủi ro nếu duy nhất một tài khoản mất TOTP. Lúc sự cố, operator được ủy quyền kiểm danh tính, backup, dùng SSH và quy trình Identity-aware được kiểm chứng để thu hồi session, tái enroll TOTP; tuyệt đối không UPDATE tùy tiện các cột Identity/TOTP bằng SQL tay hoặc cho API quyền superuser. Cơ chế chưa được code/diễn tập thì phải ghi NOT RUN, không mô tả là recovery sẵn sàng.

Audit ở P0/P1 chỉ cần các thao tác quan trọng (settings, giá, kho, trạng thái đơn, quyền nhân viên, dữ liệu khách nhạy cảm); giữ redaction PII/secret và transaction. Không bật log mọi lần xem màn Admin. `tp_readonly` chỉ backup, identity điều tra phải tách và mặc định không đọc IAM credentials/Data Protection keys.

## 5. Điều kiện tiếp tục

- **P1-01 dùng DB giả:** Closure #1 đã merge và CI head/merge PASS, B01 amendment được independent G4 + CI/merge, scoped readiness và owner giao G3 riêng. Không yêu cầu hoàn thành bảng §3 manual, owner thứ hai hoặc recovery CLI.
- **Dữ liệu thật/go-live:** Các dòng bắt buộc §3 phải có bằng chứng PASS, kể cả HTTPS/TOTP và backup/restore; PR/CI thành công không thay operator acceptance.
- **P0 DONE:** chỉ ghi khi đủ DoD và nghiệm thu bắt buộc tương ứng; không đổi NOT RUN thành PASS chỉ vì sửa spec.
