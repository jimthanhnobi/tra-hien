# P1-02 — cấu hình và kiểm chứng R2

Phạm vi: development/test trước; không có lệnh production đã chạy trong P1-02 G3.
Default `MediaStorage:Provider=Disabled`; chọn `Fake` chỉ ở Development/Test/CI.
Fake cấp URL `.invalid`, không có HTTP upload server; local tests nạp bytes qua adapter.
Production không đăng ký/chọn fake. Live R2 **NOT RUN** nếu chưa có credentials và
evidence vận hành; signed URL do SDK tạo offline không chứng minh bucket/token/CORS đúng.

## Configuration và secrets

.NET đọc environment theo `MediaStorage__...`. Đặt riêng cấu hình và secret store của
dev/prod; không commit secret, không dùng chung token/bucket, không dump env/config/log.

| Setting | Development R2 | Production R2 |
|---|---|---|
| Provider / Environment | R2 / Development | R2 / Production |
| Endpoint | `https://<account-id>.r2.cloudflarestorage.com` | endpoint account đã kiểm |
| Public:Bucket | teapremium-media-public-dev | teapremium-media |
| Private:Bucket | teapremium-media-dev | teapremium-private |
| PublicBaseUrl | HTTPS media domain dev | HTTPS media domain production |
| Public:AccessKeyId / SecretAccessKey | Secret store dev, chỉ bucket dev public | Secret store prod, chỉ bucket prod public |
| Private:AccessKeyId / SecretAccessKey | Token riêng, chỉ bucket dev private | Token riêng, chỉ bucket prod private |
| FfmpegPath / FfprobePath | PATH hoặc đường dẫn executable tin cậy | PATH executable đã vá, chạy bằng OS user hạn chế |

Owner xác nhận sau đó đã tắt Public Development URL và dùng `teapremium-media-dev`
làm bucket **private dev**. `appsettings.Development.json` chỉ lưu endpoint,
hai bucket và public base URL dev không bí mật; không cấu hình domain r2.dev cũ làm PublicBaseUrl.
Trạng thái private/no custom domain còn cần kiểm độc lập. Provider vẫn kế thừa
`Disabled` từ appsettings chung. Credentials chỉ qua process environment
đúng các tên `MediaStorage__Public__AccessKeyId`, `MediaStorage__Public__SecretAccessKey`,
`MediaStorage__Private__AccessKeyId`, `MediaStorage__Private__SecretAccessKey` hoặc secret
input cục bộ; không đưa giá trị vào JSON/source/evidence.

**Contract cấu hình:** `MediaStorageOptions.Validate()` yêu cầu đầy đủ public
bucket/PublicBaseUrl/public credentials và private bucket/private credentials, hai
bucket và hai Access Key ID khác nhau, kể cả request chỉ purpose=evidence. Một bucket
private duy nhất không đủ để enable provider qua DI thật. Không bypass validation,
dùng credential giả cho nửa public, tự tạo bucket hoặc đổi Cloudflare settings.
Owner đã duyệt public bucket `teapremium-media-public-dev` và private bucket
`teapremium-media-dev`, mỗi bucket một credential riêng. Cần public HTTPS base URL
đúng bucket mới và bốn trường S3 secret qua input cục bộ; không dùng transcript secrets.
PublicBaseUrl dev owner cung cấp: `https://pub-0606f1f4f4fe427a97b402931548100f.r2.dev`.
Scope HEAD/GET bốn ô, public-base mapping và actual application/browser upload đã
PASS bằng synthetic objects; application-issued signed PUT/GET sau TTL thật đều **403**.
Domain settings owner-confirmed; independent enumeration/lifecycle chưa verified.
Final run **199 PASS, 0 FAIL, exit 0**; cleanup **22/22** exact run-owned keys, DELETE
204 và HEAD 404. Provider mặc định vẫn Disabled; không lưu credentials. P2-1 đề xuất
CLOSED để reviewer độc lập xác nhận; không phải quyền publish.
Origin nghiệm thu hiện hành là **`https://admin.localhost:8443`**, native Caddy theo
`infra/caddy/Caddyfile.admin-local`, upstream API `127.0.0.1:5080`/Admin `127.0.0.1:3001`.
Không dùng HTTP 3001 làm origin browser hoặc hạ `AdminTransportMiddleware`.
DNS/TLS/browser/IAM runtime preflight đã PASS (provider Fake, chưa media operations),
không đồng nghĩa R2 acceptance. Xem phần Final G3 trong
[local evidence](../plans/p1-02-local-evidence.md).

Hai token mỗi environment có Object Read & Write cho đúng **một bucket**; không dùng
account/admin API key trong app. Chúng phục vụ HEAD/GET/PUT/DELETE, không cần quyền quản
trị Cloudflare. App kiểm endpoint HTTPS, account/jurisdiction hostname, environment,
hai bucket/key ID khác nhau và credentials không trống. App không thể tự chứng minh
token chỉ có đúng bucket hay production không reuse secret dev: operator phải kiểm
scope trong trang token và thử quyền âm bằng dữ liệu giả, lưu evidence đã che key ID.
Không cấp thêm admin permission chỉ để app tự đọc cấu hình bucket/CORS.

Public bucket chỉ chứa final catalog/content/branding. Private bucket không bật r2.dev,
không custom domain public, không anonymous read. Mọi staging cũng ở private bucket.
Không cấu hình CDN cache cho presigned evidence; response GET ký `private, no-store`.
Không tự sửa lifecycle trong remediation. Prefix staging thật là
`pending/{uploadId:N}/{finalKey}` (`MediaKeys.Staging`); app cleanup sau 24 giờ là cơ chế
chính. Mọi đề xuất expiration cho **chỉ pending/** cần owner xem/duyệt riêng, không TTL
toàn bucket/evidence/final assets.

FFmpeg và ffprobe phải có trên host; CI/Dockerfile API cài gói FFmpeg hệ điều hành.
Chạy `ffmpeg -version`, `ffprobe -version` và focused MediaContentTests để kiểm đủ sáu
định dạng. Decoder thiếu/không chạy không được coi là media hợp lệ. Không download
binary tự động trong request hay bật decoder delegates/network.

## Phân biệt evidence và live probes

Fake storage tests; tạo presigned URL offline; SDK method overrides/stubs; real R2 HTTP
transport; browser CORS/Media API end-to-end là năm loại evidence riêng. Method overrides
trong R2MediaStorageTests không chạy HTTP transport. Owner báo smoke SDK/presign HTTP,
OPTIONS, expiry và cleanup 17 checks/0 FAIL sau G4; đây là owner-reported evidence,
không phải browser upload hoặc end-to-end với adapter ứng dụng đã độc lập xác minh.

Theo tài liệu Cloudflare: [presigned URLs](https://developers.cloudflare.com/r2/api/s3/presigned-urls/),
[CORS](https://developers.cloudflare.com/r2/buckets/cors/),
[API tokens](https://developers.cloudflare.com/r2/api/tokens/).
Presign dùng S3 API hostname, không dùng public custom domain. Content-Type được ký;
client gửi đúng header trong intent response. TTL PUT/GET 600 giây.

Private bucket CORS mẫu, thay origin bằng đúng admin HTTPS origin của environment:

```json
[
  {
    "AllowedOrigins": ["https://admin.localhost:8443"],
    "AllowedMethods": ["PUT", "GET", "HEAD"],
    "AllowedHeaders": ["Content-Type"],
    "ExposeHeaders": ["ETag"],
    "MaxAgeSeconds": 600
  }
]
```

Public bucket CORS chỉ GET/HEAD từ store/admin origins thực sự cần; không cho browser
PUT public bucket. Không wildcard origin/headers. CORS không thay authentication; token
và signed method/key mới quyết định quyền. CORS không giới hạn dung lượng PUT: complete
luôn xác minh size/content; chưa tạo final public asset trước bước đó.

Operator kiểm trên bucket dev dùng fixture tổng hợp, không dữ liệu thật:

1. Xác minh token restriction, dev/prod không trùng, endpoint account/jurisdiction;
   đúng token đọc/ghi/xóa fixture trong bucket được cấp, truy cập bucket kia bị từ chối.
2. OPTIONS từ admin origin cho PUT/Content-Type → cho phép; origin lạ/method ngoài
   GET/HEAD/PUT không nhận CORS allow. Public bucket không cho browser PUT.
3. Tạo intent qua authenticated API/CSRF; PUT bằng URL chỉ ở bộ nhớ, đúng bytes/header;
   đổi key/method/MIME bị từ chối. Sau 10 phút URL không còn hiệu lực.
4. Complete → HEAD/If-Match GET/decode/conditional final PUT thành công; repeat/concurrent
   một asset. PUT lại staging không đổi asset final; gọi complete thiếu quyền/khác user
   bị chặn. Metadata đúng nhưng bytes giả/truncated không tạo asset.
5. Evidence anonymous URL/r2.dev/custom-domain read bị từ chối; signed GET chỉ do API
   sau kiểm permission/creator, hết hiệu lực sau TTL; không trả domain public.
6. Thử mất kết nối/rollback trên DB disposable; retry không trùng, cleanup xóa staging
   và orphan sau 24 giờ nhưng giữ asset completed. Kiểm log chỉ status/code/trace ID.

Lưu thời điểm, environment, phiên bản app, bucket alias, status codes/hash fixture và
kết quả PASS/FAIL/NOT RUN; không lưu URLs ký, headers auth, tokens, secrets hoặc nội dung
khách hàng. Các checklist HTTPS/owner/backup/pháp lý và vị trí xử lý dữ liệu trước
dữ liệu thật/go-live theo B01/DEC-19 vẫn cần evidence riêng.

## Lifecycle — kiểm read-only, không khôi phục rule tự động

Owner báo đã xóa một lifecycle rule. [Cloudflare mô tả mặc định abort incomplete
multipart sau 7 ngày](https://developers.cloudflare.com/r2/objects/upload-objects/),
và [rule lifecycle mặc định/bucket configuration](https://developers.cloudflare.com/r2/buckets/object-lifecycles/).
Thông tin mặc định không chứng minh rule vẫn tồn tại ở bucket này sau khi bị xóa.
Bucket thực tế hiện **NOT VERIFIED**: cần đọc Settings → Object Lifecycle Rules hoặc
GET lifecycle bằng quyền read-only, không PUT/DELETE lifecycle hay thay permissions.

Evidence cần ghi không bí mật: rule Enabled, filter/prefix, AbortIncompleteMultipartUpload
DaysAfterInitiation (thường 7), có/không object Expiration/transition và phạm vi áp dụng.
Không tạo expiration toàn bucket hoặc áp vào completed catalog/evidence. Abort incomplete
multipart không xóa object staging đã PUT thành công: implementation hiện dùng single
PutObject; prefix `pending/` do app cleanup 24 giờ xử lý. Không suy rằng multipart rule
thay thế cleanup này. Không tự khôi phục rule mà owner đã xóa.
