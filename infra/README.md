# Chạy Trà Hiên V2 trên máy phát triển

Chỉ dùng dữ liệu giả trên PostgreSQL 17. Migration chạy riêng bằng `tp_owner`; API dùng `tp_app`, không tự migrate kể cả Production. `tp_readonly` chỉ dành cho backup được kiểm soát, có thể đọc IAM/Data Protection nhạy cảm; không cấp credential này cho người điều tra. Backup job/mã hóa/restore drill Production thuộc P3-10.

## Cấu hình và volume mới

Cần Docker Desktop Linux containers, Docker Compose, .NET 10 theo global.json và Windows PowerShell 5.1 hoặc pwsh. Từ repo root:

```powershell
if (-not (Test-Path -LiteralPath infra/.env)) {
  Copy-Item -LiteralPath infra/.env.example -Destination infra/.env
}
notepad infra/.env
docker compose -f infra/compose.dev.yml config --quiet
docker compose -f infra/compose.dev.yml up -d postgres
./scripts/dev-migrate.ps1 -PreflightOnly
./scripts/dev-migrate.ps1
docker compose -f infra/compose.dev.yml up -d --build api store admin
```

Đổi mọi placeholder; dùng password riêng từng role, hạn chế ACL file `.env` cho operator. Không in `config` thiếu `--quiet`, `docker inspect`, environment hoặc connection string ra log. Compose giữ `POSTGRES_DB=postgres` (maintenance); SQL tạo `TP_DATABASE` riêng với owner tp_owner. Init hook gọi wrapper và SQL chung trong `/opt/teapremium`, chỉ tự chạy trên volume rỗng. Không chạy nguyên schema.sql trên DB EF.

| Biến | Ý nghĩa |
|---|---|
| POSTGRES_USER / POSTGRES_PASSWORD | Bootstrap administrator; giữ identity cũ khi dùng volume hiện hữu. Không dùng cho API. |
| TP_DATABASE | Identifier `[a-z][a-z0-9_]{0,62}`, khác postgres/template0/template1. |
| TP_OWNER_PASSWORD | Migration; không superuser/CREATEDB/CREATEROLE/BYPASSRLS, không membership. |
| TP_APP_PASSWORD | Runtime; Compose quote password trong connection string nên **không chứa `"`**, hỗ trợ `;`. Native Npgsql builder hỗ trợ punctuation. |
| TP_READONLY_PASSWORD | Chỉ job backup được ủy quyền, không cấp API/người điều tra. |
| POSTGRES_PORT / API_PORT / STORE_PORT / ADMIN_PORT | Host loopback; mặc định 5432/8080/3000/3001. |

Trong `.env`, dùng literal single-quoted phù hợp Compose khi secret có `$`. Không truyền secret qua argv. Thay `.env` không đổi password role đã tồn tại; rotation là thao tác riêng được owner duyệt. Khi đổi environment DB container, recreate service bằng `up -d postgres` trước migrate; giữ volume.

## Database/volume hiện hữu

Operator xác nhận đúng DB dev giả, bảo toàn backup theo quy trình đã duyệt và kiểm ownership. Giữ POSTGRES_USER/PASSWORD của cluster, chuyển tên DB cũ từ POSTGRES_DB sang TP_DATABASE trong `.env`. Init hook không tự chạy lại:

```powershell
docker compose -f infra/compose.dev.yml stop api
docker compose -f infra/compose.dev.yml up -d postgres
docker compose -f infra/compose.dev.yml exec -T postgres sh /opt/teapremium/init-roles.sh
./scripts/dev-migrate.ps1 -PreflightOnly
./scripts/dev-migrate.ps1
docker compose -f infra/compose.dev.yml up -d --build api
```

Không DROP/RECREATE role/DB, REASSIGN OWNED hoặc reset volume. Wrapper dùng admin qua socket, đọc secret từ env bằng psql `\getenv`, không qua argv và không reset password. Preflight chung kiểm attributes/membership, DB/schema/relation/function owner, CREATE, object/column/default ACL bất thường. **DB/object cũ thuộc superuser hoặc role khác → dừng**, ghi object cho owner duyệt remediation riêng; không tự chuyển owner. Đường existing chỉ tiếp tục khi ownership hợp lệ. Public schema có thể thuộc pg_database_owner hoặc tp_owner; member objects của trusted extensions giữ owner bootstrap theo cơ chế PostgreSQL.

SQL tạo role/DB thiếu; CREATE DATABASE ngoài transaction. Khối quyền DB/extension trong transaction. Khi provisioning dừng giữa chừng, giữ object đã tạo và chạy lại sau khi xử lý nguyên nhân, không reset dữ liệu. Administrator bootstrap trusted extensions và delegate grant-option đúng các hàm unaccent/advisory; migration owner cấp EXECUTE cho runtime. Không membership đặc quyền, không cấp quyền ứng dụng qua PUBLIC.

`protect-credentials.sql` bảo vệ riêng từng session provisioning trước nội suy credential và sau mỗi reconnect: tắt/kiểm effective statement, duration, sampling, error/parameter và debug logging; lỗi thiết lập/kiểm tra thì dừng trước secret SQL. Wrapper tắt shell xtrace trước đọc environment. Không thay logging policy global/DB/role; kết nối khác giữ cấu hình logging cũ. Server log của session này không giữ SQL nhạy cảm; dùng exit code và client error terse để chẩn đoán. Preflight từ chối schema grant option của app/backup qua ACL thực, không tự revoke/repair drift.

`dev-migrate.ps1` đăng nhập TCP bằng tp_owner trong container, preflight trước locked project restore/build/sinh SQL và kiểm lại trong cùng session ngay trước **toàn chuỗi** idempotent migrations. Checkout sạch không cần assets/build sẵn; `-PreflightOnly` chỉ kiểm DB, không restore/build/migrate. EF sinh SQL offline, không nhận password/connection qua argv. `-Container <id>` dành cho PostgreSQL 17 dùng một lần; mặc định dùng Compose postgres. Gọi EF update trực tiếp bỏ preflight có thể lỗi ở GRANT P0-09 cũ; migration mới không bảo vệ DDL đã chạy trước nó khi bỏ entry point.

## Kiểm quyền, migration và rollback

Catalog check không in password/hash:

```powershell
"SELECT rolname,rolsuper,rolcreatedb,rolcreaterole,rolbypassrls,rolinherit FROM pg_roles WHERE rolname ~ '^tp_';" |
  docker compose -f infra/compose.dev.yml exec -T postgres sh -c 'export PGUSER="$POSTGRES_USER" PGDATABASE="$TP_DATABASE"; exec psql -X'
./scripts/dev-migrate.ps1 -PreflightOnly
docker compose -f infra/compose.dev.yml ps
```

`DatabaseRolesTests` kiểm login TCP app/backup, DML/sequence/function/history, deny DDL/role switch/append-only/ledger/MAINTAIN, default ACL đúng creator, data P0/password/sequence giữ nguyên và missing role/owner lạ dừng trước EF history. IAM/Settings/Data Protection chạy bằng tp_app; migration dùng owner. Điều tra dùng identity theo vụ việc, cột/view đã che, thời hạn/audit/thu hồi; fixture kiểm không đọc IAM secrets/DP xml hoặc kế thừa backup/owner. Credential/OS/network/job controls và backup mã hóa/restore thực còn cần nghiệm thu vận hành.

Migration mới `ReconcileRuntimeGrants` chứa SQL cố định tạo từ `GrantsSql`, không gọi helper lúc replay và không sửa migration cũ. Default privileges gắn tp_owner: backup SELECT, app sequence USAGE; không blanket default DML. Mỗi migration cấp explicit/revoke ngoại lệ. Down giữ ACL an toàn, không trả lại quyền nguy hiểm hoặc xóa data/role/key/sequence. Ưu tiên forward fix; rollback app rồi kiểm schema/history, restore cô lập nếu cần.

## Local HTTPS và app

HTTP Compose không chứng minh Admin Secure cookie/TOTP. Caddy native `infra/caddy/Caddyfile.admin-local` dùng API loopback **5080**, Next Admin **3001**, origin **https://admin.localhost:8443**. Nếu dùng Compose API đặt API_PORT=5080; cấu hình Iam__AdminHost=admin.localhost và trusted proxy đúng địa chỉ thực qua Docker Desktop, không wildcard. Native API dùng Iam__TrustedProxies__0=127.0.0.1.

Operator nạp ConnectionStrings__Default bằng tp_app qua secret store/connection-string builder và cấu hình certificate/environment, rồi:

```powershell
dotnet run --project backend/src/TeaPremium.Api --no-launch-profile --urls http://127.0.0.1:5080
# Terminal proxy riêng, từ repo root:
caddy run --config infra/caddy/Caddyfile.admin-local --adapter caddyfile
```

Production-mode IAM local cần X.509 private key qua Iam__KeyCertificatePath/Iam__KeyCertificatePassword, giữ cùng certificate khi restart để giải mã keys. Caddy lưu CA trong ignored artifacts/p0-12-local/caddy-data, skip_install_trust: operator xác minh/trust root CA trong user store trước browser; closure #1 không tự sửa trust/hosts. Hostname phải resolve loopback. Hướng dẫn/evidence HTTPS cũ ở `docs/plans/p0-12-implementation.md` không thay nghiệm thu mới.

Bootstrap-owner qua CLI nhập ẩn dùng tp_app sau migration, cùng certificate/environment; không password argv/pipe. Runbook owner A/B, settings/audit/CSRF/lockout/logout tại `docs/plans/p0-closure-acceptance.md` còn cần nghiệm thu. Closure #2 MFA recovery ngoài slice này.

`docker compose -f infra/compose.dev.yml watch` bật hot reload theo develop. Đổi Dockerfile/manifest/config cần build lại. `stop` hoặc `down` giữ volume; không xóa volume để sửa quyền. Không xuất environment/credentials khi chẩn đoán lỗi.
