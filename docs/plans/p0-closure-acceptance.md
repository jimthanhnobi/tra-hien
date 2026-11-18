# P0 closure — runbook nghiệm thu theo A01

**Trạng thái 18/11/2026:** Closure #1 được owner giao **G3 local**, remediation P0-02/P0-04/P0-09/P0-10, base/head `3a06ffb7b4f4f53a519c5032802be77ff5f04690` (PR #20/#21 đã merge). Independent G4 Tier A đã trả **BLOCK F1–F4**; owner chỉ cho sửa local và focused G4 tiếp theo chưa chạy. Test DB dùng một lần theo §2; nghiệm thu vận hành HTTPS/owner A/B, CI published HEAD và merge closure **chưa thực hiện**. Closure #2/P0 chưa DONE. Không tạo ID roadmap mới. FR-PLT-01, FR-IAM-02/03, BR-IAM-01/02/06/07/08, AC-P0-01…06, DEC-29/30; 03 §4, 05 §4.1, 06 §5/§11. Hồ sơ G3 và evidence fixes: [p0-closure-01-implementation.md](p0-closure-01-implementation.md).

## 1. Điều kiện và an toàn

- Operator được owner ủy quyền, xác nhận đúng môi trường giả/dev và backup nếu DB đã có dữ liệu. Không down -v/drop DB/reset password/REASSIGN OWNED để áp quyền. Dừng khi ownership bất thường; remediation từng object cần owner cho phép.
- Chỉ dùng identity giả khi diễn tập. Không ghi password, cookie, CSRF token, QR/shared TOTP secret, authenticator code hoặc backup/plaintext keys vào log/evidence/argv. Secret nhập tương tác hoặc công cụ an toàn được duyệt; không copy nguyên HTTP body/headers vào hồ sơ.
- Hai phiên HTTP/cookie jar riêng A/B qua HTTPS cùng origin Admin, không dùng chung CSRF. Sau mỗi login/logout/change-password/verify TOTP phải lấy token mới theo session; mọi POST/PATCH có X-CSRF-TOKEN. Không bỏ auth/CSRF vì thao tác từ server.
- Không giả định Staff UI hoàn chỉnh. Dùng Admin API/OpenAPI đúng published head đã review; body 05 §4.1, không sửa DB tay hoặc chạy bootstrap-owner lần hai để thêm owner.

## 2. Chuỗi DB/HTTPS (closure #1)

| Bước | Phương án / kết quả mong đợi | Kết quả thật |
|---|---|---|
| DB mới | Provision infra/postgres/init-roles.sql qua wrapper init-roles.sh, cấu hình maintenance/app DB đúng owner; migrate bằng tp_owner qua dev-migrate preflight, API tp_app | PASS automation PostgreSQL 17.12: init hook, fresh EF và entry point; toàn stack Compose thủ công NOT RUN |
| DB hiện hữu | Backup/preflight, chạy cùng SQL ngoài init hook; giữ data/IAM/settings/audit/ledger/password/sequence. Bất thường ownership dừng, không xóa DB/volume | PASS baseline 3 migrations + data giả → migration thứ 4, fingerprint không đổi; backup/restore thực NOT RUN |
| Re-run/quyền | Chạy lại provisioning/idempotent migration trên DB giả; login thật app/backup, kiểm DML/DDL/append-only theo 03 §4; không dùng grant giả trong test | PASS sau fixes F1–F4: 75 ca Closure #1 trong full Linux 435/435; aggressive logging, clean checkout, schema grant option âm; passwords/sequence giữ nguyên; focused G4 chưa chạy |
| HTTPS | Caddy local được trust + API/Admin đúng origin; Compose HTTP/config --quiet không chứng minh login được | NOT RUN |
| Owner A | Bootstrap lần đầu khi chưa có owner; login, đổi password, login lại, setup/verify TOTP rồi login bằng mã mới. Kiểm onboarding/ready và lấy CSRF theo phiên | NOT RUN |

Artifact đã có trong G3 Closure #1; chưa có published SHA mới. Các lệnh dưới dành cho operator trên DB giả, không phải chỉ dẫn áp dụng production. `.env` chứa TP_DATABASE và ba password riêng + bootstrap POSTGRES_USER/PASSWORD; không truyền secret qua argv/log. SQL idempotent chỉ tạo role/DB thiếu; role đã có sai thuộc tính/membership/ownership/quyền phải dừng. Volume cũ do superuser sở hữu không được tự chuyển owner; owner phê duyệt remediation từng object riêng.

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

**Lệnh đã kiểm chứng local sau fixes F1–F4**: focused `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~DatabaseRolesTests --logger trx --results-directory /evidence/focused` → PASS **75/75**. Full Linux `dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/test-results` → PASS **435/435** (45 unit, 34 architecture, 356 integration, gồm 75 ca Closure #1), exit 0/zero skipped. Evidence `artifacts/closure-01-g4fix-verification`; lượt G3 cũ 51/55 focused và 415 full là lịch sử. Test gọi wrapper/init SQL thật trên fresh/existing PostgreSQL 17, entry point từ checkout không bin/obj/assets và khi drift/missing role/lock mismatch. Cấu hình logging mạnh không lộ marker/credential SQL; logging policy của kết nối khác giữ nguyên; schema grant option/membership bị từ chối trước chain, không tự repair ACL. Ba tình huống reviewer cũng được chạy lại bằng script riêng, theo hồ sơ G3. Config Compose `--env-file infra/.env.example ... config --quiet` exit 0. Lệnh toàn stack trên volume của operator ở trên **NOT RUN**; không đổi test artifacts thành nghiệm thu vận hành. Focused independent G4 vẫn chờ session mới; chưa có publish permission.

**HTTPS/Caddy dependencies**: API native hoặc host port 5080, Admin 3001, Caddy native cấu hình `infra/caddy/Caddyfile.admin-local`, origin `https://admin.localhost:8443`, hostname resolve loopback và CA được operator kiểm/trust. API đặt Iam__AdminHost=admin.localhost và trusted proxy đúng địa chỉ thực; không wildcard. Production-mode local cần certificate X.509 private key cho Data Protection, cùng file/password giữa CLI/API/restart và runtime tp_app; không ghi secrets vào hồ sơ. Lệnh Caddy/native API ở infra README và hồ sơ P0-12. G3 này không tự cài CA, đổi hosts, bootstrap owner thật hoặc giả định HTTP Compose là nghiệm thu login; các bước HTTPS/owner A/B bên dưới vẫn NOT RUN.

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
