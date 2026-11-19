# P1-02 — Media foundation / local G3

Ngày 19/11/2026. Owner giao đúng P1-02, chỉ local G1/G2/G3; không publish/merge/deploy.
Base/HEAD `a9f0bba0df6c42610d88481724374cc12402adf6`, branch `feature/p1-02-media-r2`.
Worktree đầu vào sạch, từ nhánh P1-01 đã merge; fetch xác nhận main đúng expected SHA.
[Merge CI 44463204951](https://github.com/jimthanhnobi/tra-hien/actions/runs/44463204951)
completed/success: Backend/API, Frontend, Docker và scope đều PASS tại đúng base SHA.
Đây là CI baseline, không phải CI cho delta local chưa publish.

## Targeted G1/G2: GO

Tái sử dụng full G1 `cadce575597738bf40ad7c45689482de5a5fbc3d`, A01
`024ea998c43baf0afde30b069ed6b4876cb96f4a`, G2
[conformance-review](../architecture/conformance-review.md), B01 và
[readiness P1-01](p1-01-local-evidence.md). `git diff 8791e8ac61f5de42815a84edbc0a0602ff177c26 origin/main -- docs/spec`
rỗng; P1-01 không đổi spec. Coverage TARGETED, không nhận là full audit mới.

Nguồn đọc/đối chiếu:

| Nguồn | Requirement / scope P1-02 |
|---|---|
| `07-roadmap.md:51` P1-02 | R2 intent/complete/library, private evidence; dependency P1-01 đã merge |
| `02-architecture.md:182` §5.10 | Sáu MIME, giới hạn bytes, TTL 10 phút, kiểm HEAD + nội dung, dimensions, purpose/quyền, slugHint |
| `schema.sql:144` media.assets, `:161` upload_sessions | Tái sử dụng nguyên entities/mappings/migration P1-01, không thêm DDL |
| `03-database.md` §1, §4, §7 | UUID v7/IClock, runtime tp_app, upload dở giữ 24 giờ rồi xóa object/expired |
| `05-api.md:118` §4.2; §1 | Đúng ba endpoint, body/response, permission, pagination, CSRF, OpenAPI/client |
| `06-infrastructure.md:9` §1; §8–9 | Public media/private evidence, dev/prod/credentials tách, không secrets trong source |
| `09-seo.md:148` BR-SEO-82 | Final key `{purpose}/{slug}/{slug}-{6 ký tự ngẫu nhiên}.{ext}` |
| A01 D6/D7, B01 | R2 token theo bucket, ảnh 15 MiB/video evidence 50 MiB; không mở lại closure/Q1–Q8 ngoài scope |

Traceability: **FR-CAT-06**, phần upload của FR-CAT-03/04 và FR-CNT-01,
**FR-RET-01**, **NFR-04/09**, BR-SEO-82; DEC-01/14/15/16/19/21/28/30.
Các AC-P1-01/06 về publish/ALT/giá vốn thuộc slice sau; không nhận PASS toàn AC đó.
G05/G14/G36 đã CONTRACT_ALIGNED trong A01; phần runtime được kiểm trong tests P1-02.
Không có gap contract mới buộc đổi kiến trúc, schema hay roadmap. Live provider evidence
được tách khỏi local coding gate theo chỉ dẫn owner; xem runbook và evidence local.

## Hành vi và ranh giới

Trước task: có media entities/schema/grants nhưng chưa có use case/API/storage.
Sau task: ba endpoint dưới đây dùng IAM cookie đã ready, HTTPS, no-store và CSRF
cho POST. `MediaActor` nhận permissions từ cookie được IAM xác minh lại mỗi request.

| Endpoint | Authorization |
|---|---|
| POST `/api/v1/admin/media/upload-intents` | catalog → catalog.write; content/branding → content.write; evidence → returns.write |
| POST `/api/v1/admin/media/upload-intents/{id}/complete` | Quyền purpose hiện tại + created_by đúng actor; không có owner bypass |
| GET `/api/v1/admin/media` | catalog.read; evidence thêm returns.write + chỉ asset do actor tạo; explicit purpose=evidence thiếu quyền → 403 |

`branding` được xử lý như nội dung site dưới `content.write` theo nhóm permission
05 §4.2/§4.7; không tạo permission mới. Chưa có liên kết return object trong P1-02:
quyền đối tượng evidence dùng creator của media; chia sẻ theo return/order cần tích hợp
ở task đổi trả sau này. Không thêm endpoint đọc evidence riêng: complete/library trả
presigned GET tối đa 10 phút sau kiểm quyền/creator. Public response không có filename,
storage key, actor ID hoặc secrets. Private URL chỉ có trong authorized no-store response.

Mọi PUT client đi private staging `pending/{uploadId}/{finalKey}`. Intent hết hạn 10 phút;
complete khóa row FOR UPDATE trong transaction của command decorator, HEAD/conditional
GET đúng ETag, byte count/MIME khớp, signature/container + full decode bằng FFmpeg.
Ghi checksum SHA-256/dimensions; dùng đúng bytes đã đọc để PUT final key với If-None-Match,
không copy nguồn client có thể đổi. Final key chưa từng được cấp client PUT URL.
Suffix/key collision không ghi đè asset: unique session key/conditional PUT và checksum.
MIME giả, truncated, oversized, missing/wrong key/expired/unauthorized không tạo asset.

Repeat/concurrent complete trả cùng asset ID. Sau 24 giờ cleanup xóa staging; session
completed được dọn, complete retry vẫn tìm asset cùng ID và kiểm lại creator/quyền.
Abandoned/failed upload: xóa staging và final orphan không được asset nào tham chiếu,
đánh dấu expired. Cleanup khóa row SKIP LOCKED, batch 100 mỗi giờ; storage failure
rollback DB để retry. DB failure sau final PUT giữ orphan đã validate và staging cho
retry/cleanup; không xóa mù khi COMMIT có thể đã thành công. Completed asset không bị dọn.

FFmpeg/ffprobe là dependency giải mã local, không thêm dịch vụ/queue. Tắt network demux,
chỉ định format, không shell/client path; timeout 30s/process, một decoder thread,
max allocation 64 MiB, max 40 triệu pixels, concurrency HTTP media 2 (429 khi bận).
File tạm thư mục riêng, dọn trong finally. Đây là ngân sách xử lý; không phải giới hạn
dung lượng sản phẩm mới. Decoder/system failure đóng an toàn; operator cần cập nhật
bản vá decoder. EF provider exception/SQL categories không đưa vào app log; middleware
giữ event/trace ID an toàn, storage exception bỏ message/inner exception của SDK.

Không Product CRUD/publish, UI gallery, inventory, import hay storefront. ALT mặc định
null; việc bắt buộc ALT khi gắn cover/publish vẫn thuộc P1-04/05, không bịa thêm input
cho complete. Không `[Audited]` mọi upload/UI read theo B01.

## Tests, migration và rollback

Tests: valid sáu định dạng; boundaries; spoof/truncated/wrong size/MIME/key; expiration;
repeat/concurrent complete; auth/onboarding/CSRF/purpose/cross-user/revocation/private;
storage failures/timeout/cancellation; DB rollback + recovery; orphan cleanup; logs;
SDK presign/HEAD/If-Match/If-None-Match/config; OpenAPI + TS contract.

Không migration/model/grants/spec change, không production operation. Rollback code và
configuration về base binary; giữ DB/assets, không Down P1-01 hoặc xóa media để rollback
ứng dụng. Nếu provider tắt, cleanup cũng ngừng; xử lý backlog khi bật lại hoặc qua operator
được giao. Signed URL đã phát chỉ tồn tại tối đa TTL; không đưa URL/credential vào evidence.

Kết quả lệnh, inventory đầy đủ, giới hạn live/G4 và trạng thái bàn giao:
[p1-02-local-evidence.md](p1-02-local-evidence.md).
