# 06 — Hạ tầng, triển khai và vận hành

## 1. Lựa chọn (tối ưu chi phí, chạy lâu dài)

| Thành phần | Chọn | Ghi chú |
|---|---|---|
| Máy chủ | 1 VPS tại Việt Nam, **2 vCPU / 4 GB RAM / 60–80 GB SSD NVMe**, Ubuntu 24.04 LTS | Đủ cho vài nghìn lượt xem/ngày và vài chục đơn/ngày khi storefront có cache. Chọn nhà cung cấp có snapshot VPS và object storage S3-compatible cùng hệ sinh thái. |
| DNS/CDN/WAF | Cloudflare gói Free, bật proxy (đám mây cam) | Ẩn IP VPS, cache tĩnh, chặn bot cơ bản. SSL mode **Full (strict)**. |
| Ảnh | Cloudflare R2 bucket `teapremium-media`, gắn domain `media.<domain>` | Không tính phí băng thông tải ra; chỉ ảnh sản phẩm/bài viết (không PII). Ảnh bằng chứng đổi trả để bucket **riêng, private** (`teapremium-private`), truy cập qua presigned GET. |
| Resize ảnh | Next.js Image (sharp) trên app store, nguồn R2 | Không phát sinh dịch vụ trả phí. Cache ảnh đã resize ở Cloudflare. |
| Backup DB | Object storage S3-compatible **tại Việt Nam**, bucket private, mã hóa `age` trước khi upload | Dữ liệu khách không ra nước ngoài (DEC-19). Hằng tháng chủ shop tải 1 bản về máy riêng. |
| Email giao dịch | Một SMTP provider có gói miễn phí/rẻ, domain có SPF + DKIM + DMARC | Chỉ email giao dịch (xác nhận đơn...), không gửi marketing hàng loạt. |
| Thông báo shop | Telegram Bot | Miễn phí. |
| Giám sát | Uptime monitor miễn phí (kiểm `/health/ready` qua domain mỗi 1–5 phút) + Telegram | NFR-06. |
| Lỗi ứng dụng | Log JSON trên VPS (xoay vòng); tùy chọn Sentry gói free cho API + 2 app Next | Không gửi PII vào Sentry (bật scrub). |
| Mã nguồn/CI | GitHub + GitHub Actions + GHCR | |

**Ước tính:** VPS là khoản chính; Cloudflare, R2 (≤ 10 GB), Telegram, uptime monitor miễn phí; object storage backup và SMTP vài chục nghìn đồng/tháng. Mục tiêu NFR-13 (≤ 500.000 đ/tháng). Giá cụ thể kiểm tra khi mua.

## 2. Sơ đồ trên VPS

```
Internet → Cloudflare → VPS :443 (chỉ mở 80/443 cho dải IP Cloudflare, 22 cho IP quản trị)
  caddy (172.30.0.10)
   ├─ shop.<domain>   → store:3000      (/api/* → api:8080)
   ├─ admin.<domain>  → admin:3001      (/api/* → api:8080)
   └─ (api không có domain riêng)
  api   (172.30.0.20)  ASP.NET Core :8080 + background jobs
  store (172.30.0.30)  Next.js standalone
  admin (172.30.0.31)  Next.js standalone
  postgres (172.30.0.40) PostgreSQL 17, volume pgdata, không mở cổng ra ngoài
  backup (cron trong container)  pg_dump | age → S3 VN
```

## 3. `infra/compose.prod.yml` (khung)

```yaml
name: teapremium
x-logging: &logging
  driver: json-file
  options: { max-size: "20m", max-file: "5" }

networks:
  internal:
    ipam: { config: [{ subnet: 172.30.0.0/24 }] }

volumes: { pgdata: {}, caddy_data: {}, caddy_config: {} }

services:
  caddy:
    image: caddy:2-alpine            # pin digest khi triển khai
    restart: unless-stopped
    ports: ["80:80", "443:443"]
    volumes:
      - ./caddy/Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy_data:/data
      - caddy_config:/config
    networks: { internal: { ipv4_address: 172.30.0.10 } }
    logging: *logging

  api:
    image: ghcr.io/<owner>/teapremium-api:${APP_VERSION}
    restart: unless-stopped
    env_file: .env.api
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_HTTP_PORTS: "8080"
      TRUSTED_PROXY_IP: 172.30.0.10
      STORE_REVALIDATE_URL: http://store:3000/api/revalidate
    depends_on: { postgres: { condition: service_healthy } }
    healthcheck:
      test: ["CMD", "curl", "-fsS", "http://127.0.0.1:8080/health/ready"]
      interval: 15s
      timeout: 5s
      retries: 5
    deploy: { resources: { limits: { memory: 768m } } }
    networks: { internal: { ipv4_address: 172.30.0.20 } }
    logging: *logging

  store:
    image: ghcr.io/<owner>/teapremium-store:${APP_VERSION}
    restart: unless-stopped
    env_file: .env.store            # API_INTERNAL_URL=http://api:8080, REVALIDATE_SECRET, SITE_URL, SITE_INDEXABLE...
    healthcheck:
      test: ["CMD", "node", "-e", "fetch('http://127.0.0.1:3000/api/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"]
      interval: 15s
    deploy: { resources: { limits: { memory: 512m } } }
    networks: { internal: { ipv4_address: 172.30.0.30 } }
    logging: *logging

  admin:
    image: ghcr.io/<owner>/teapremium-admin:${APP_VERSION}
    restart: unless-stopped
    env_file: .env.admin
    healthcheck:
      test: ["CMD", "node", "-e", "fetch('http://127.0.0.1:3001/api/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"]
      interval: 15s
    deploy: { resources: { limits: { memory: 384m } } }
    networks: { internal: { ipv4_address: 172.30.0.31 } }
    logging: *logging

  postgres:
    image: postgres:17-alpine        # pin digest
    restart: unless-stopped
    env_file: .env.postgres
    command: >
      postgres -c shared_buffers=1GB -c effective_cache_size=2GB -c work_mem=16MB
               -c maintenance_work_mem=256MB -c max_connections=60
               -c log_min_duration_statement=500 -c timezone=UTC
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./postgres/init-roles.sh:/docker-entrypoint-initdb.d/10-roles.sh:ro
      - ./postgres/init-roles.sql:/opt/teapremium/init-roles.sql:ro # wrapper gọi SQL chung với biến psql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres"]
      interval: 10s
    deploy: { resources: { limits: { memory: 1600m } } }
    networks: { internal: { ipv4_address: 172.30.0.40 } }
    logging: *logging

  backup:
    image: ghcr.io/<owner>/teapremium-backup:${APP_VERSION}
    restart: unless-stopped
    env_file: .env.backup            # PG* (tp_readonly), AGE_RECIPIENT, S3_ENDPOINT, S3_BUCKET, S3 keys, TELEGRAM...
    networks: [internal]
    logging: *logging

  migrate:                           # chỉ chạy tay khi deploy
    profiles: [tools]
    image: ghcr.io/<owner>/teapremium-migrate:${APP_VERSION}   # EF Core migration bundle
    env_file: .env.migrate           # chuỗi kết nối bằng tp_owner
    networks: [internal]
```

Tất cả file `.env.*` nằm ở `/opt/teapremium/`, quyền `600`, chủ sở hữu user deploy; không commit.

## 4. `infra/caddy/Caddyfile` (khung)

```
{
    servers {
        trusted_proxies static 173.245.48.0/20 103.21.244.0/22 103.22.200.0/22 103.31.4.0/22 141.101.64.0/18 108.162.192.0/18 190.93.240.0/20 188.114.96.0/20 197.234.240.0/22 198.41.128.0/17 162.158.0.0/15 104.16.0.0/13 104.24.0.0/14 172.64.0.0/13 131.0.72.0/22
        client_ip_headers CF-Connecting-IP
    }
}
# Dải IP Cloudflare phải lấy lại từ https://www.cloudflare.com/ips/ khi triển khai và cập nhật định kỳ.

(security) {
    header {
        Strict-Transport-Security "max-age=31536000; includeSubDomains"
        X-Content-Type-Options "nosniff"
        Referrer-Policy "strict-origin-when-cross-origin"
        Permissions-Policy "camera=(), microphone=(), geolocation=()"
        -Server
    }
}

shop.example.vn {
    import security
    encode zstd gzip
    handle /api/v1/admin/* { respond 404 }
    handle /api/* {
        reverse_proxy api:8080 {
            header_up X-Forwarded-For {client_ip}
        }
    }
    handle { reverse_proxy store:3000 }
}

admin.example.vn {
    import security
    header X-Frame-Options "DENY"
    header Cache-Control "no-store"
    header X-Robots-Tag "noindex, nofollow"
    encode zstd gzip
    handle /api/v1/store/* { respond 404 }
    handle /api/v1/account/* { respond 404 }
    handle /api/* {
        reverse_proxy api:8080 {
            header_up X-Forwarded-For {client_ip}
        }
    }
    handle { reverse_proxy admin:3001 }
}
```

- API tin `X-Forwarded-For` **chỉ** từ `TRUSTED_PROXY_IP=172.30.0.10` (IP tĩnh của Caddy) → rate limit đúng IP khách.
- CSP: storefront và admin tự đặt Content-Security-Policy trong Next.js (nonce cho script), cho phép ảnh từ `media.<domain>`.
- Có thể khóa `admin.<domain>` thêm bằng Cloudflare Access (gói free) theo email nhân viên.

## 5. Database

`infra/postgres/init-roles.sql` là nguồn provisioning chung theo 03 §4/DEC-30; wrapper init-roles.sh gọi SQL với biến psql được truyền qua kênh bí mật. Chỉ mount file SQL không tự chuyển env thành biến psql. Các artifact/script này triển khai ở closure #1, không được tạo bởi amendment này.

- Volume mới: tạo tp_owner/tp_app/tp_readonly nếu thiếu, thuộc tính tối thiểu; tạo DB chỉ nếu chưa có, owner tp_owner (CREATE DATABASE ngoài transaction). Cấu hình init phân biệt maintenance DB và application DB, không để POSTGRES_DB tự tạo application DB sai owner rồi âm thầm đổi ownership.
- Existing DB/volume: init hook không chạy lại; operator được owner ủy quyền chạy **cùng SQL**, backup/preflight DB/object owner trước. Không drop/recreate role/DB, reset password, xóa volume/down -v hoặc REASSIGN OWNED hàng loạt. Ownership bất thường dừng, owner duyệt remediation theo object riêng. Rotation password là thao tác riêng.
- scripts/dev-migrate.ps1 kiểm roles/ownership trước cả chuỗi, chạy migration bằng tp_owner; helper GrantsSql sinh SQL cố định cho migration bổ sung theo 03 §4 (USAGE/grants/revoke ngoại lệ, default privileges đúng creator). Không sửa migration cũ. Missing role báo đúng path; EF chạy thẳng bỏ preflight có thể lỗi ở GRANT P0-09 cũ. API dev/prod dùng tp_app; không dùng owner/superuser để né thiếu quyền.
- Test fixture dùng cùng role SQL/helper/migration, login thật app/backup, kiểm quyền âm/append-only/DDL và existing-data fingerprint, provisioning/re-run/idempotent không đổi dữ liệu/password/sequence. Không nhầm DDL target test với EF migration hoặc triển khai thật.
- tp_readonly **chỉ backup có kiểm soát**, credential chỉ cho job backup, không truy vấn tay tùy ý. Full backup có thể chứa IAM credentials và Data Protection keys: hạn chế truy cập/secret/network, mã hóa, audit và restore cô lập. Điều tra dùng identity/quyền read riêng theo vụ việc, cột/view đã che dữ liệu, không thừa kế role backup/owner, mặc định không đọc IAM credentials/tokens/security stamps hoặc Data Protection xml (03 §4). SELECT không tự phân biệt pg_dump/SQL tay; phải kiểm credential và execution controls.

## 6. Backup và khôi phục

| Việc | Tần suất | Chi tiết |
|---|---|---|
| `pg_dump -Fc` → `age` → S3 VN | 02:30 hằng ngày + trước mỗi migration | Không ghi bản rõ ra đĩa; kèm file SHA-256; Telegram báo thành công/thất bại |
| Giữ lại | 14 bản ngày, 8 bản tuần, 12 bản tháng | Lifecycle rule của bucket hoặc script dọn |
| Khóa giải mã `age` | Lưu **ngoài VPS** (máy chủ shop + 1 bản giấy/USB cất riêng) | Mất khóa = mất backup |
| Snapshot VPS | Hằng tuần (nếu nhà cung cấp hỗ trợ) | Khôi phục nhanh cả máy |
| Diễn tập restore | Hằng tháng | Script `restore-drill.sh`: tải bản mới nhất → giải mã → restore vào container tạm → chạy truy vấn kiểm (số đơn, số khách, Σ tồn) → báo Telegram |
| R2 media | Bật versioning/không xóa object khi xóa asset trong 30 ngày | |

Mục tiêu: RPO ≤ 24h, RTO ≤ 4h (NFR-05). Khi đơn > 30/ngày: bật WAL archiving (pgBackRest/wal-g) để RPO ≤ 15 phút.

## 7. CI/CD

**`ci.yml`** (mỗi push/PR):
1. Backend: restore (locked) → build → unit + architecture test → integration test (Testcontainers PostgreSQL 17).
2. `dotnet ef migrations script --idempotent` → artifact `migrations.sql`.
3. Sinh OpenAPI → sinh `packages/api-client` → kiểm `git diff --exit-code` (client phải được commit cùng thay đổi API).
4. Frontend: pnpm install (frozen) → lint → typecheck → test → build `store`, `admin`.
5. (nhánh `main`) Build & push image `api`, `store`, `admin`, `migrate`, `backup` lên GHCR, tag = commit SHA.

**`deploy.yml`** (chạy tay, chọn tag):
1. SSH vào VPS (key riêng, user `deploy`, không root).
2. Chạy backup ngay (`docker compose run --rm backup /backup.sh`), dừng nếu lỗi.
3. `APP_VERSION=<tag> docker compose pull`.
4. `docker compose --profile tools run --rm migrate` (bundle, `tp_owner`).
5. `docker compose up -d api store admin` → chờ healthcheck.
6. Smoke test: `GET https://shop.<domain>/api/v1/store/settings` = 200, trang chủ = 200, `admin` login page = 200.
7. Lỗi → `APP_VERSION=<tag trước> up -d` (migration additive nên bản cũ vẫn chạy được).

## 8. Bảo mật máy chủ

- SSH chỉ bằng key, tắt đăng nhập root và mật khẩu; đổi cổng không bắt buộc.
- Firewall: mở 80/443 **chỉ cho dải IP Cloudflare**, 22 cho IP quản trị (hoặc dùng Cloudflare Tunnel/Tailscale cho SSH). **Lưu ý:** cổng Docker publish đi vòng qua UFW; phải đặt luật trong chain `DOCKER-USER` hoặc dùng firewall của nhà cung cấp VPS. Kiểm tra bằng cách gọi thẳng `https://<IP VPS>` từ ngoài → phải bị chặn.
- `unattended-upgrades` cho bản vá bảo mật; fail2ban cho SSH.
- Docker: không mở cổng postgres/api ra host; container chạy user không phải root.
- Secret xoay vòng mỗi 6 tháng hoặc khi nhân viên nghỉ.
- Tài khoản nhân viên nghỉ việc: khóa ngay (FR-IAM-02).

## 9. Môi trường

| Môi trường | Ở đâu | Dữ liệu |
|---|---|---|
| Dev | Máy dev, `infra/compose.dev.yml` (postgres + api + store + admin, hot reload) | Seed demo |
| Test | GitHub Actions, Testcontainers | Tạo mới mỗi lần |
| Staging (khi cần) | Cùng VPS, project compose `teapremium-staging`, subdomain `staging-*`, DB riêng, bảo vệ bằng Cloudflare Access | Seed demo, **không** dùng dữ liệu thật |
| Production | VPS | Thật |

## 10. Checklist trước go-live

- [ ] Domain, Cloudflare (Full strict), Caddy cấp chứng chỉ OK.
- [ ] Email: SPF, DKIM, DMARC; gửi thử email đơn hàng không vào spam.
- [ ] Có ≥2 owner active/đã đổi password/TOTP trên hai thiết bị riêng; owner thứ hai tạo và kiểm qua Admin API bảo vệ (staff.manage + cookie/CSRF), không giả định Staff UI; diễn tập recovery BR-IAM-07 trên DB dev. Tạo sales/content; đổi mọi mật khẩu mặc định.
- [ ] Backup chạy bằng tp_readonly có kiểm soát, không dùng làm tài khoản điều tra; quyền điều tra riêng không đọc IAM credentials/Data Protection keys mặc định. Restore drill cô lập thành công, khóa age cất ngoài VPS.
- [ ] Uptime monitor + Telegram cảnh báo hoạt động.
- [ ] Trang chính sách đổi trả, vận chuyển, thanh toán, bảo mật, điều khoản đã publish (BR-CNT-03).
- [ ] **Kiểm tra quy định hiện hành về website thương mại điện tử bán hàng** (thông báo với Bộ Công Thương, thông tin bắt buộc trên website) và nghĩa vụ hóa đơn/thuế của hộ kinh doanh với kế toán.
- [ ] Rà consent và chính sách bảo mật theo Luật Bảo vệ dữ liệu cá nhân 2025 (NFR-12).
- [ ] Thông tin chuyển khoản (`payment.bank`) đúng; chuyển thử 1 đơn thật với số tiền nhỏ.
- [ ] Nhập tồn đầu kỳ, kiểm kê đối chiếu.
- [ ] Import khách cũ (dry-run → chạy thật).
- [ ] Google Search Console xác minh domain, gửi sitemap; `SITE_INDEXABLE=true`.
- [ ] SEO Audit (BR-SEO-60) không còn mục mức "Lỗi"; kiểm Rich Results Test 1 trang mỗi loại; crawl thử toàn site (công cụ crawl SEO) không có 4xx/5xx/chuỗi redirect.
- [ ] Google Merchant Center: xác minh website, cấu hình giao hàng/đổi trả, nộp feed `/feeds/google-merchant.xml`, bật hiển thị miễn phí.
- [ ] Google Business Profile (nếu có địa chỉ đón khách), NAP khớp website.
- [ ] Không còn dữ liệu demo trong DB production.


## 11. P0 closure acceptance (A01)

Hoàn tất closure #1 (roles/grants/runtime tp_app/runbook) và #2 (MFA recovery/coverage) rồi owner nghiệm thu trước P1-01. Runbook [p0-closure-acceptance.md](../plans/p0-closure-acceptance.md) có nhánh DB mới/existing, HTTPS, bootstrap owner đầu, **tạo owner thứ hai qua Admin API được bảo vệ**, đổi password/TOTP/kiểm quyền, settings/audit/CSRF/lockout/logout. Không dùng tp_readonly cho truy vấn kiểm audit; dùng admin/audit có audit.read hoặc quyền điều tra tối thiểu riêng. Chỉ cập nhật DONE bằng kết quả thật, G4 độc lập/CI đúng published SHA và owner merge theo workflow. Chưa cho phép thực thi runbook hay triển khai closure từ amendment tài liệu.
