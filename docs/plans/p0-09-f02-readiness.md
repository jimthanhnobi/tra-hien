# P0-09 / F-02 — đề xuất quyết định dữ liệu hành chính

**PROPOSED — READY_FOR_FOCUSED_G4 (tài liệu); P0-09 coding: HOLD chờ owner duyệt contract/DDL và giao G3; production APPLY: HOLD riêng.** Ngày 17/11/2026 (Asia/Ho_Chi_Minh). Sửa đúng P1-01/P1-02/P2-03 sau independent G4 BLOCK; chưa nhận focused G4 PASS hoặc owner approval. Không suy từ G01 Option B rằng dataset, CLI hoặc migration mới đã được duyệt.

## Phạm vi và baseline

- Repository `jimthanhnobi/tra-hien`; branch local `planning/p0-09-f02-admin-data`; base = HEAD = `origin/main` = `0b3add67eb2bca7c21887076a346e8a2d7270295` sau `git fetch origin --prune`; working tree ban đầu sạch.
- TeaPremium v2.0, ngày chốt 16/11/2026 (`00-README.md:1–5`). Tái dùng G1 full audit `cadce575597738bf40ad7c45689482de5a5fbc3d` (inventory/gaps/traceability) và G2 `docs/architecture/conformance-review.md`. Diff từ G1 chỉ có bốn file trong amendment G26/G01/F-01; `git diff --exit-code 0bfc98e HEAD -- docs/spec` exit 0. Không có spec drift tiếp theo, không cần full audit mới.
- Targeted G1/G2: `03-database.md` §3.6:80–85, §5:115–146, §6:148–172, §8:187–194; `07-roadmap.md:25,36–37` (P0-09, AC-P0-04); `schema.sql:35–56,589–602,802–810`; `02-architecture.md` §3, §5.1–5.4, §8; `06-infrastructure.md` §6. G01 Option B đã duyệt; F-02 là hợp đồng chi tiết còn mở tại `task-readiness.md:75`. Code hiện hành đã đọc: `PlatformEntities.cs:6–32`, `AppDbContext.cs:32–56` trong `backend/src/TeaPremium.Infrastructure/Persistence/` giữ đúng ba bảng, TEXT codes, flags và FK theo DDL; chưa có import ledger.
- Traceability: FR-PLT-01, NFR-04/05/08/09; DEC-01/19; FR-SHP-01, BR-SHP-01, FR-CNT-06, DEC-11 và AC-P0-04 thuộc phần Settings của P0-09, **chưa được nghiệm thu** bởi tài liệu này. Không tạo requirement/task ID mới.
- P0-04 cung cấp cấu trúc; P0-05 cung cấp audit/UoW; P0-08 cung cấp IAM cho Settings; P0-10 cung cấp fixture PostgreSQL 17/parity tests. Giữ modular monolith .NET 10/PostgreSQL 17 và roadmap hiện có. Chỉ lập quyết định/readiness, không viết application code, migration, Settings API hay importer.

## 1. Pin và bằng chứng nguồn

Chọn **một nguồn chuyển đổi cộng đồng**: [thanglequoc/vietnamese-provinces-database tại commit cố định](https://github.com/thanglequoc/vietnamese-provinces-database/tree/62b1abdfe39625289c37a58ef6678e9db3d37f84), tag `v5.2.0`, commit `62b1abdfe39625289c37a58ef6678e9db3d37f84`. `git ls-remote ... refs/tags/v5.2.0 "refs/tags/v5.2.0^{}"` trả đúng SHA này (lightweight tag). Mọi lần tải dùng raw URL chứa **commit**, không dùng master/latest, mirror ZIP hay API danh mục live làm đầu vào import.

Prefix raw: `https://raw.githubusercontent.com/thanglequoc/vietnamese-provinces-database/62b1abdfe39625289c37a58ef6678e9db3d37f84/`. SHA-256 dưới đây đo trên bytes HTTP, không qua đổi newline/encoding:

| Path tại commit trên | Bytes | SHA-256 |
|---|---:|---|
| `json/full_json_generated_data_vn_units.json` — **đầu vào được chọn** | 1579241 | `965c9a2ad85481e18ed2464fd87666eda311856b86a72a87f1130c27376b8d2f` |
| `json/vn_provinces_metadata.json` | 107 | `14f7815413702837918e9aa0961308b072e0d75b2bdf5b5865fc4a23cdd8eda4` |
| `README.md` | 23685 | `f6ad9fe458b4fd486a7c5e6b00e3be0c7f36bb9aed5ed77d3e2ad2cfef6ae3dc` |
| `LICENSE` | 1070 | `0173d3551f805ee7fabad1948b1219e79dca42f5f93a1b07d4f9a12f9cbfb8da` |

Git blob SHA-1 của JSON được chọn = `598443d917f4672f22211d0aba7972f9f6e97e28`, khớp GitHub contents; metadata blob = `65546011533f3c1cd4414cecbc1b0554efa5f6d2`. License đọc thực tế là MIT, copyright Thang Le Quoc 2021; gói phân phối nguồn/CSV sau này phải giữ copyright và permission notice. Đây không phải kết luận về tính hợp pháp hay độ chính xác của mọi dữ liệu bổ sung.

Metadata nguồn: `DatasetVersion=v5.2.0`, `LatestDecree=388/NQ-UBTVQH16`, `GeneratedAt=2026-09-20T13:13:17Z`; **GeneratedAt không phải ngày hiệu lực**. README:17 vẫn nhắc 36/2026/QH16; bảng release README:39 gộp hai nghị quyết dưới ngày ban hành 04/08/2026. Không dùng hai chỗ này làm bằng chứng ngày ban hành của 39/2026/QH16.

| Kiểm chứng chính thức ngày 17/11/2026 | Đã xác minh / giới hạn |
|---|---|
| [Danh mục Cục Thống kê](https://danhmuchanhchinh.nso.gov.vn/) | Trang có cơ quan chủ quản Cục Thống kê; mã `24`, tên `Thành phố Bắc Ninh`, căn cứ 39/2026/QH16 ngày 24/08/2026 khớp JSON. Chỉ đọc trang để kiểm provenance; không tải dataset live. Chưa lưu được snapshot chính thức đầy đủ 34/3321 để đối chiếu từng mã/tên/parent. |
| [39/2026/QH16 — Cổng văn bản Chính phủ](https://vanban.chinhphu.vn/?classid=0&docid=219328&pageid=27160) | Quốc hội, thành lập thành phố Bắc Ninh; ban hành 24/08/2026, hiệu lực 20/09/2026. Link [PDF ký](https://datafiles.chinhphu.vn/cpp/files/vbpq/2026/9/nq39.nqtl_tpbn_0001.signed.pdf) mở được 2 trang; chưa chứng thực chữ ký số hoặc lập hồ sơ băm bản PDF. |
| [388/NQ-UBTVQH16 — Công báo](https://congbao.chinhphu.vn/van-ban/nghi-quyet-so-388-nq-ubtvqh16-470313.htm) | Thành lập các phường thuộc tỉnh Bắc Ninh; ban hành 04/08/2026, hiệu lực 20/09/2026; Công báo 495. Metadata đã đọc; tải PDF CDN timeout, **chưa đối chiếu nội dung từng phường**. |

Kết luận provenance: pin/checksum và sự tồn tại, chủ đề, mốc hiệu lực hai văn bản **VERIFIED**; tính tương đương toàn bộ dataset với danh mục chính thức **NOT VERIFIED**. Hai nghị quyết chỉ là các cập nhật liên quan release, không tự chứng minh tính đầy đủ toàn quốc. Trước **production APPLY**, operator phải lưu bản danh mục chính thức có mốc tra cứu 20/09/2026 (hoặc hồ sơ xác minh tương đương có người chịu trách nhiệm), URL/tham số/thời điểm thu thập/SHA-256, căn cứ pháp lý nền và kết quả đối chiếu mã/tên/parent; giải thích mọi khác biệt, đặc biệt Bắc Ninh. Không tự coi community repo là cơ quan phát hành pháp lý. Thiếu đối chiếu chính thức không chặn coding/test local sau coding gate tại §8.

## 2. Quyết định chuẩn hóa JSON → CSV

**Đề xuất hợp đồng `vn-admin-csv/1`** (chi tiết kỹ thuật cần owner duyệt):

1. Đọc UTF-8 strict, từ chối JSON duplicate keys/schema sai; top-level là array province, `Type=province`, `Wards` là array các `Type=ward`. Không flatten district hoặc tự suy mã. Trong file đã ghim, tên field thực tế là **`Code`, `FullName`, `Wards`, `ProvinceCode`** (PascalCase). Tên logic `fullName` trong mapping nghĩa là đọc đúng `FullName`, **không dùng `Name`**, tên English, slug hoặc tự thêm tiền tố.
2. Header chính xác `province_code,province_name,ward_code,ward_name`; delimiter comma, quote bằng `"`, escape bằng `""` khi có comma/quote/CR/LF. Xuất UTF-8 **không BOM**, newline LF, có LF cuối; importer chấp nhận một BOM đầu file và CRLF nhưng hash **bytes thực nhập** vẫn phải khớp manifest. Không tự thay bytes rồi so với hash cũ.
3. Province sắp theo code ordinal tăng; phát đúng một dòng khai báo province `code,FullName,,`, sau đó ward của province sắp code ordinal. Mỗi ward row lặp lại code/fullName province. Như vậy province không ward vẫn có một dòng; không tạo ward giả, không coi ô trống là mã `0`/NULL tỉnh. Không cho duplicate province declaration hoặc duplicate ward, kể cả trùng hệt. Ward row bắt buộc có declaration province và cùng tên/parent.
4. Codes là chuỗi xuyên suốt JSON/CSV/.NET/PostgreSQL TEXT; profile release này kiểm ASCII `[0-9]{2}` cho tỉnh, `[0-9]{5}` cho ward. Từ chối numeric JSON, whitespace, exponent, ký tự ẩn hoặc thiếu zero; không trim/pad/parse số để “sửa”. Tên Unicode NFC, không rỗng/whitespace, không control character; giữ dấu, tiền tố và nội dung nguồn, không unaccent. Một mã tỉnh chỉ có một tên; ward code duy nhất toàn quốc; `ward.ProvinceCode == enclosingProvince.Code`; cả code/name ward cùng trống hoặc cùng có giá trị. Cấm cấp thứ ba, orphan và parent không tồn tại trong trạng thái kết quả.
5. CSV không mang `sort_order`: giữ giá trị đã có, tỉnh mới dùng mặc định `0`; import không ghi đè thứ tự Admin. Manifest công bố rule này. Không nhập postal/GIS/English metadata không được schema yêu cầu.

Kiểm tra một lần trong bộ nhớ theo mapping trên, **không tạo importer hay CSV trong source tree**: đúng **34 provinces, 3321 wards, 0 province không ward**, 0 tên cần đổi NFC; codes/Type/uniqueness/parent PASS. CSV dự kiến có **3355 data rows** (34 declaration + 3321 ward), 154871 bytes, SHA-256 `c023b11aab28f64c652d26512a020f06a0e62005d0f64eeee5543362fb85ab4c`. Ví dụ `01,Thành phố Hà Nội,,` rồi `01,Thành phố Hà Nội,00004,Phường Ba Đình`.

**34/3321 chỉ là assertion fixture v5.2.0**, không là invariant mọi release. Manifest mỗi release có expected counts được kiểm riêng; số dòng đúng vẫn không thay thế bằng chứng full snapshot. P0-09 phải tái tạo và băm CSV thật trước apply, không chỉ tin hash dự tính trong đề xuất.

## 3. Manifest và lưu metadata

Chọn sidecar `provinces.manifest.json`, UTF-8, strict JSON, schema version `vn-admin-manifest/1`, không duplicate/unknown fields. Hash SHA-256 trên **bytes nguyên file** (manifest không tự chứa hash của chính nó). Các trường bắt buộc:

| Nhóm | Nội dung chính xác cần lưu |
|---|---|
| Identity | `schema_version`, `dataset_id="vn-administrative-units"`, `dataset_version="v5.2.0"`, `release_order=1`, `effective_on="2026-09-20"`, `mode="full"`, `operation="import"`, `normalization="vn-admin-csv/1"` |
| Nguồn | `community_source={repository,tag,commit,files:[{path,url,bytes,sha256}],license,license_sha256}` theo §1; `official_source={issuer,url,as_of,artifacts:[{url,retrieved_at,sha256}],legal_references:[{number,issued_on,effective_on,url}],verification_record,verified_by,verified_at,completeness_evidence}`. Nguồn chính thức và community tách biệt. |
| Payload | `csv={file,bytes,sha256,encoding:"utf-8",delimiter:",",newline:"LF",bom:false}`, `expected={provinces:34,wards:3321,province_only_rows:34,data_rows:3355}`; `retrieved_at`, `verified_at` là UTC timestamp có offset, không dùng thời gian mtime. |
| Update | `base_import_id` (null duy nhất cho DB chưa có import/dữ liệu); `deactivate={provinces:[],wards:[]}` cho delta; `change_evidence` liên kết bằng chứng rename/reparent/reactivation. Recovery thêm `recovery_of`, `reason`, `incident_record`, `target_import_id` và `re_advance_path` (danh sách ID normal import gốc được phép tiến lại, có thể rỗng). Re-advance dùng `operation="re_advance"`, `re_advance_of` (normal import gốc), `recovery_event_id` và `path_position` theo §4; không tái dùng manifest/approval/base cũ. |

Counts trong manifest mô tả payload CSV, không phải tổng số row lịch sử trong DB. Full luôn cần bằng chứng phạm vi đầy đủ của input; production còn bắt buộc đối chiếu chính thức đầy đủ. Delta khai `completeness_evidence=null` và có change evidence cho phạm vi thay đổi. Profile **LocalTest** tại §8 cho phép các phần official chưa kiểm mang giá trị null/array rỗng và `verification_record="NOT_VERIFIED_LOCAL_FIXTURE"`; không giả lập verified_by/verified_at. Profile **Production** từ chối các giá trị thiếu/chưa verified đó. Target profile nằm trong plan/approval, không phải cờ trong manifest tự cấp quyền bỏ kiểm tra. Manifest không chứa credential, đường dẫn backup có token hay customer PII. Lưu bundle bất biến gồm source bytes + LICENSE + CSV + manifest + dry-run plan + approval + receipt + checksum ở hồ sơ vận hành có ACL/backup. Hash chỉ chống đổi bytes; owner/operator phải duyệt provenance và checksum qua hồ sơ tin cậy, không tin sidecar do nguồn không tin cậy tự đưa.

**ADR-PROPOSAL trong F-02: thêm duy nhất `platform.administrative_data_imports`.** Giữ nguyên `provinces`, `wards`, `settings`. Hai bảng danh mục chỉ lưu trạng thái hiện tại, không lưu lịch sử/hash; `settings` là cấu hình nghiệp vụ có API sửa theo group (`05-api.md:34,199`), không phù hợp làm ledger bất biến. `idempotency_keys` có retention 48 giờ, không thay lịch sử import. Chọn một ledger thành công có parent pointer; không cần bảng job/staging lâu dài hoặc bảng current-version riêng.

Migration tương lai `yyyyMMddHHmm_AdministrativeDataImports` có **Up đề xuất chính xác** dưới đây, chưa tạo/chạy. `tp_owner` chạy sau backup; không seed hoặc đổi dữ liệu danh mục:

```sql
CREATE TABLE platform.administrative_data_imports (
  id uuid CONSTRAINT pk_administrative_data_imports PRIMARY KEY,
  previous_import_id uuid,
  dataset_version text NOT NULL,
  release_order bigint NOT NULL,
  effective_on date NOT NULL,
  operation text NOT NULL,
  manifest jsonb NOT NULL,
  manifest_sha256 text NOT NULL,
  csv_sha256 text NOT NULL,
  plan_sha256 text NOT NULL,
  catalog_sha256 text NOT NULL,
  receipt jsonb NOT NULL,
  applied_at timestamptz NOT NULL,
  recovery_of uuid,
  re_advance_of uuid,
  CONSTRAINT ux_administrative_import_previous
    UNIQUE NULLS NOT DISTINCT (previous_import_id),
  CONSTRAINT fk_administrative_import_previous FOREIGN KEY (previous_import_id)
    REFERENCES platform.administrative_data_imports(id),
  CONSTRAINT fk_administrative_import_recovery FOREIGN KEY (recovery_of)
    REFERENCES platform.administrative_data_imports(id),
  CONSTRAINT fk_administrative_import_re_advance FOREIGN KEY (re_advance_of)
    REFERENCES platform.administrative_data_imports(id),
  CONSTRAINT ck_administrative_import_version CHECK (length(dataset_version) > 0 AND release_order > 0),
  CONSTRAINT ck_administrative_import_operation CHECK (
    (operation = 'import' AND recovery_of IS NULL AND re_advance_of IS NULL) OR
    (operation = 'recovery' AND recovery_of IS NOT NULL AND re_advance_of IS NULL) OR
    (operation = 're_advance' AND recovery_of IS NULL AND re_advance_of IS NOT NULL)),
  CONSTRAINT ck_administrative_import_genesis CHECK (
    previous_import_id IS NOT NULL OR operation = 'import'),
  CONSTRAINT ck_administrative_import_hashes CHECK (
    manifest_sha256 ~ '^[0-9a-f]{64}$' AND csv_sha256 ~ '^[0-9a-f]{64}$' AND
    plan_sha256 ~ '^[0-9a-f]{64}$' AND catalog_sha256 ~ '^[0-9a-f]{64}$'),
  CONSTRAINT ck_administrative_import_json CHECK (
    jsonb_typeof(manifest) = 'object' AND jsonb_typeof(receipt) = 'object'),
  CONSTRAINT ck_administrative_import_no_self CHECK (
    previous_import_id IS DISTINCT FROM id AND recovery_of IS DISTINCT FROM id AND
    re_advance_of IS DISTINCT FROM id)
);
GRANT SELECT, INSERT ON platform.administrative_data_imports TO tp_app;
REVOKE UPDATE, DELETE, TRUNCATE ON platform.administrative_data_imports FROM tp_app;
GRANT SELECT ON platform.administrative_data_imports TO tp_readonly;
GRANT EXECUTE ON FUNCTION pg_catalog.pg_try_advisory_xact_lock(integer, integer) TO tp_app;
```

UUID v7 và timestamp qua `IClock` do app cung cấp; manifest JSONB là bản parse từ file đã băm, giữ raw bytes ở bundle vì JSONB không bảo toàn serialization. Receipt gồm operator identity được xác minh qua quyền chạy CLI, approved_at, approval-record hash, target identity, backup reference/hash, diff counts, before/after digests và incident nếu recovery; không lưu PII địa chỉ. Không tạo role/quyền business mới. Chỉ môi trường vận hành được owner giao quyền mới chạy CLI; cờ `--approve` hoặc một tên operator tự gõ không phải bằng chứng quyền.

PK, FK predecessor, `UNIQUE NULLS NOT DISTINCT(previous_import_id)` và CHECK genesis/no-self bảo vệ định danh, predecessor tồn tại, tối đa một genesis và một successor, chặn race cùng predecessor kể cả client quên advisory lock. Các constraint không DEFERRABLE; unique violation phải rollback toàn transaction, không bắt lỗi rồi commit data. Importer append **một row** sau khi kiểm tail; sau lock, tail là row chưa được row nào tham chiếu qua `previous_import_id`. FK `re_advance_of` giữ event gốc tồn tại; kiểm đó là normal import hợp lệ, payload bất biến và đường recovery là kiểm chéo row của importer dưới lock, không tuyên bố CHECK SQL tự thực thi các luật này. Không UNIQUE version/hash vì re-advance cần event mới. `tp_app` vẫn chỉ SELECT/INSERT ledger, không UPDATE/DELETE/TRUNCATE/MAINTAIN, không `FOR UPDATE` hay explicit table lock trên ledger. DB có danh mục nhưng không có ledger: HOLD apply, cần owner kiểm kê/adopt bằng baseline riêng, không suy version từ dữ liệu.

**Migration safety:** đây là extension chưa có trong `schema.sql`; cần owner duyệt quyết định và **ủy quyền amendment DDL/spec riêng** trước G3, cập nhật parity test/EF snapshot theo DDL đã duyệt. Không triển khai bảng vượt authoritative DDL. Down chỉ được `tp_owner` gọi trên DB dùng một lần đã xác minh target; khóa bảng rồi kiểm rỗng và drop trong cùng transaction (dưới đây), không phải đường apply của `tp_app`. Production hoặc ledger có row thì dừng, roll forward/restore theo hồ sơ, không xóa lịch sử. Mọi index ở trên do PK/UNIQUE tạo ngầm, không thêm index trùng.

```sql
LOCK TABLE platform.administrative_data_imports IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM platform.administrative_data_imports) THEN
    RAISE EXCEPTION 'Import history exists; use controlled recovery';
  END IF;
END $$;
DROP TABLE platform.administrative_data_imports;
```

## 4. Phiên bản, full snapshot và delta

- `release_order=1` là thứ tự registry **do owner duyệt cho baseline này**, không suy từ semver. Release sau được cấp ordinal lớn hơn sau khi kiểm provenance/phạm vi và ngày hiệu lực; cùng ngày hiệu lực chỉ tiến khi ordinal lớn hơn và có bằng chứng correction. Không so chuỗi `v5.10`/`v5.2`; không dùng tag/GeneratedAt làm legal chronology. Chỉ một dataset_id được hỗ trợ trong ledger này.
- Phân biệt **current active release** (release/payload ghi tại tail, kể cả recovery/re-advance) và **historical high-water** `(max release_order, max effective_on)` của mọi event `operation=import` đã commit. Recovery/re-advance không hạ hoặc cấp lại high-water. Normal import release mới phải có ordinal lớn hơn high-water và effective_on không nhỏ hơn high-water date; chặn ngày hiệu lực tương lai theo ngày VN. Ngày bằng nhau chỉ được nếu correction đã duyệt. Import mới đóng đường re-advance cũ.
- Identity release là `(dataset_id, source repository, dataset_version)`. Normal import gốc đăng ký bất biến tuple `(commit, source-file hashes, CSV hash/bytes, normalization, mode, deactivate list, release_order, effective_on)`. Cùng identity mà bất kỳ phần tuple khác → REJECT cho cả import/recovery/re-advance; correction phải mang **identity/version mới** và ordinal mới được duyệt. `manifest_sha256`/base/operation/approval thay đổi ở mỗi thao tác không tạo release mới; không đòi manifest thao tác mới có hash bằng manifest import gốc.
- **Controlled re-advance (`operation=re_advance`)** chỉ dành cho release đã được normal import chấp nhận/xác minh trước recovery. Recovery manifest/approval chọn `target_import_id` (normal import gốc của trạng thái đích) và ordered `re_advance_path` gồm ID normal import gốc trên lịch sử trước recovery. Path phải tăng nghiêm ngặt ordinal, effective_on không giảm từ target, không vượt high-water; rỗng nghĩa không cho re-advance. Không phát minh revision/ordinal mới cho release cũ.
- Sau recovery, tail phải là chính recovery event đó hoặc chuỗi re-advance liên tiếp thuộc cùng `recovery_event_id`. Chỉ **phần tử kế tiếp** chưa tiêu thụ trong path được phép; target ordinal phải lớn hơn current active và effective_on không nhỏ hơn current active date, tuple phải khớp event `re_advance_of`. Với delta, current active payload phải đúng release tiền đề của delta gốc; nếu sai thì REJECT, không áp delta lên base khác. Recovery khác mở path mới và vô hiệu path cũ; normal import mới hoặc hết path cũng đóng path. Không dùng lịch sử acceptance như quyền replay/downgrade tùy ý.
- Mỗi re-advance cần dry-run/base fingerprint theo tail **hiện tại**, manifest mới ghi `recovery_event_id/path_position/re_advance_of`, approval mới xác nhận bước path và thay đổi dữ liệu, rồi append event có `previous_import_id=tail`. Approval recovery chỉ cấp phạm vi path, không thay approval apply từng bước. `A1 → B2 → recovery A1 (path=[original B2]) → re_advance B2` hợp lệ; current=A rồi B, high-water vẫn 2 xuyên suốt. Lịch sử bốn event được giữ nguyên.
- **NO-OP chỉ dành exact current-tail retry**: manifest/CSV/plan và receipt/approval gốc khớp tail, catalog digest đúng; không sửa data/timestamp hay thêm ledger/audit. Release chỉ từng có trong lịch sử nhưng khác current tail không phải NO-OP; phải qua re-advance hợp lệ hoặc bị reject. Nếu exact tail nhưng catalog drift: BLOCK, không lặng lẽ “sửa bằng retry”.
- `full`: chỉ v5.2.0 bundle có completeness evidence được duyệt mới dùng làm full snapshot; LocalTest chỉ chứng minh phạm vi fixture, Production còn cần official reconciliation theo §8. Vắng mã đang active → **candidate deactivate**, liệt kê từng mã trong plan để operator duyệt, không tự suy từ counts. Không DELETE. Không cascade ẩn: deactivate tỉnh phải liệt kê và deactivate tất cả ward active của nó, hoặc chứng minh chúng chuyển sang tỉnh active khác.
- `delta`: vắng mã giữ nguyên; chỉ upsert mã xuất hiện và deactivate codes trong manifest tường minh. Province declaration trong delta chỉ mô tả/upsert tỉnh, **không** có nghĩa xóa ward không liệt kê. Mỗi ward row vẫn phải có declaration parent theo §2, kể cả parent đã có trong DB; trạng thái kết quả phải không có active ward dưới inactive/missing province. Reject cùng mã vừa upsert vừa deactivate. CSV không có `is_active`; trạng thái upsert là active, reactivation phải hiện riêng trong plan. Baseline v5.2.0 là full; không tự tạo delta từ diff hai file.

## 5. Dry-run, approval, apply và idempotency

1. P0-09 giữ CLI `import-provinces <csv>`; đề xuất thêm `--manifest`, `--dry-run --plan-out`, apply phải `--plan --approval`. Tên option là PROPOSED, chưa là CLI đã có. Kiểm input/size/encoding/schema/hashes/provenance/order/hierarchy toàn bộ trước ghi; dry-run chỉ SELECT, không tạo ledger/audit hay sửa settings.
2. Đọc snapshot nhất quán cho dry-run; plan chứa target DB/environment identity và profile (không connection secret), migration IDs, tail/base_import_id, current active release, historical high-water, recovery_event_id/path_position/re_advance_of nếu có, manifest+CSV hashes, importer/mapping version, before catalog digest, số tổng/thêm/rename/reparent/reactivate/unchanged/missing/deactivate theo cả hai cấp, danh sách thay đổi từng code, validation errors theo dòng, reference-impact digest/counts và expected after digest. Apply không tái dùng snapshot này: phải lấy advisory lock rồi đọc lại theo bước 4–6. Canonical catalog = SHA-256 của UTF-8 JSON compact dạng `["vn-admin-catalog/1",[[code,name,is_active,sort_order],...],[[code,province_code,name,is_active],...]]`, arrays theo code ordinal, bool JSON, không whitespace/BOM/LF cuối; gồm cả inactive rows. Reference-impact digest chỉ băm tuples table/PK/province_code/ward_code liên quan đã sắp thứ tự; plan chỉ lưu digest/counts, không lưu tên/SĐT/địa chỉ khách hay danh sách customer IDs.
3. Serialize plan theo schema fixed-order `vn-admin-plan/1`, UTF-8 không BOM, LF cuối; fingerprint = SHA-256 **exact plan bytes**, không field fingerprint tự tham chiếu. Approval file ngoài plan ghi fingerprint, target/profile, operator đã xác minh, approved_at, bước recovery/re-advance nếu có, các mã deactivate/reparent/reactivate được chấp nhận và backup reference/hash (Production bắt buộc; LocalTest ghi không áp dụng cho DB dùng một lần). Đổi manifest, input, binary/mapping, target/profile, DB data hoặc tác động FK → fingerprint cũ không còn hợp lệ, phải dry-run/duyệt lại. Hash không phải chữ ký/quyền; receipt liên kết hồ sơ approval có ACL.
4. Trước apply, kiểm target/profile và gate §8 (Production gồm backup thành công); mở **một** UoW transaction READ COMMITTED trên cùng connection. Lệnh đầu tiên trước mọi đọc mutable import state là `SELECT pg_catalog.pg_try_advisory_xact_lock(8300, 9)`: cặp **int32** cố định, namespace `8300=TeaPremium platform`, key `9=administrative-data imports`, dùng chung cho normal import/recovery/re-advance trong database, không hash từ tên file/version/operator. Không dùng overload bigint hoặc session lock. Nếu false: trả `import_busy`, rollback ngay, không đọc state/ghi/append và không tự chờ/retry. Khóa transaction được PostgreSQL giải phóng khi commit/rollback; không unlock sớm. [PostgreSQL 17 advisory functions](https://www.postgresql.org/docs/17/functions-admin.html#FUNCTIONS-ADVISORY-LOCKS).
5. Thứ tự cố định sau advisory lock: khóa `platform.provinces` rồi `platform.wards` ở `SHARE ROW EXCLUSIVE` (`tp_app` phải có UPDATE trên hai bảng theo `03` §4 và được kiểm bằng restricted-role test); nếu cần impact reference, khóa bảng reference theo tên schema/table ordinal ở `SHARE`, giới hạn timeout/cửa sổ bảo trì. **Không explicit table lock hoặc row-lock ledger**; chỉ SELECT/INSERT dưới advisory lock và constraint chain. Đọc lại tail, current active, high-water, recovery path/cursor và base catalog/reference fingerprint **sau lấy khóa**, không dùng trạng thái dry-run hoặc EF cache trước khóa. Nếu reference table chưa tồn tại thì ghi “not yet present”, không tạo migration CRM/đơn. Advisory lock là quy ước cho mọi đường import; thao tác owner/manual bỏ quy ước không được coi là concurrency path được hỗ trợ.
6. Kiểm lại hashes từ input bất biến và target/profile/migrations. **Trước stale base/order**, exact current-tail retry theo §4 có receipt/approval hợp lệ và catalog digest khớp thì NO-OP, không yêu cầu base cũ bằng tail mới. Mọi trường hợp khác phải khớp current base_import_id và fingerprint, đúng operation/order/path/tuple bất biến rồi recompute diff/after digest; khác → `stale_plan`/validation failure, rollback, dry-run và approval mới. Upsert province trước ward, xử lý parent/reactivation/deactivation theo trạng thái kết quả; UPDATE chỉ field thực sự đổi. Kiểm invariant/counts/after digest; append đúng một ledger event (`re_advance_of` nếu có) trong **cùng transaction giữ advisory lock và data mutations**, commit một lần. Không email/HTTP/download trong transaction; không dùng HTTP `platform.idempotency_keys` cho import.
7. SQL/validation/cancel/audit/history failure, kể cả unique predecessor/FK lỗi → rollback toàn bộ data+event, giữ tail/high-water/path cursor cũ. Timeout/deadlock/serialization conflict không tự retry plan cũ. Mất COMMIT acknowledgement: trạng thái **unknown**, reconnect chỉ đọc ledger/tail/hash để phân biệt committed/no-op với rollback trước retry. Hai apply đồng thời: contender lấy khóa thất bại nhận `import_busy`; nếu gọi lại sau commit với đúng plan đã thành công thì NO-OP, plan khác dùng base cũ thì `stale_plan`. Cả normal import và re-advance có **tối đa một append** cho cùng predecessor.

Không sửa global `/health/ready` (chỉ migration theo §8). Readiness của luồng địa chỉ là ledger/provenance hợp lệ và dữ liệu active/hierarchy đã kiểm, không chỉ `COUNT > 0`. P0-09 phải chứng minh guard cho các luồng chọn/tái dùng địa chỉ khi được triển khai; không thêm endpoint health mới trong F-02.

## 6. Parent, lịch sử và recovery

- Chuyển parent giữ ward code; phải có căn cứ nguồn, diff old→new và operator duyệt. Rename giữ code, dùng fullName mới; không tái sử dụng code cho thực thể khác khi chưa có quyết định mapping. Deactivate giữ row để FK/lịch sử còn đọc được; lọc lựa chọn mới chỉ active tỉnh và ward đúng parent.
- `crm.customer_addresses` có hai FK độc lập (`schema.sql:595–596`): importer chỉ báo số/reference digest, **không tự đổi địa chỉ khách**. Task sổ địa chỉ/đặt đơn P2-02/P2-06/P3-02/P3-05 phải kiểm active+parent trước tái dùng, yêu cầu xác nhận khi lệch; đến khi guard được kiểm thì giữ luồng chịu tác động chưa sẵn sàng. Không tự coi `ward_name_text` là cách né kiểm mã. P0-09 không tạo UI/cơ chế reconfirm CRM.
- `sales.orders` giữ snapshot code/name (`schema.sql:802–810`): import không cập nhật snapshot hoặc lịch sử đơn. Reference-impact test phải có fixture mã cũ, ward đổi tỉnh, địa chỉ lệch và đơn snapshot; không cần xây nghiệp vụ các pha sau.
- Backup bắt buộc trước migration và mỗi **production apply có thay đổi**: `pg_dump -Fc` stream qua `age`, checksum bản mã, lưu kho VN/ACL, khóa ngoài VPS theo NFR-05 và `06` §6. Không ghi plaintext dump vào repo/đĩa tạm. Backup job, restore drill và quyền chưa được chạy/xác nhận trong F-02; backup lỗi/không có chứng cứ restore khả dụng → chặn production APPLY. LocalTest trên DB dùng một lần có thể dùng fixture/recreate để test failure; không giả lập production backup/restore PASS.
- Lỗi trước commit: rollback và lỗi đã làm sạch PII trong hồ sơ ngoài transaction, giữ manifest/plan/correlation; không tạo failed row rồi vô tình dùng làm current version. Sau commit phát hiện nguồn sai: ngừng luồng địa chỉ chịu tác động, owner mở incident, backup trạng thái hiện tại, kiểm references phát sinh, dry-run **recovery riêng** nhắm import thành công trước đó; approval mới bắt buộc. Không thêm `--force` bỏ mọi kiểm tra.
- Recovery append ledger `operation=recovery`, `recovery_of=<event sai thuộc current chain>`, `target_import_id` và path được duyệt theo §4; parent vẫn là tail hiện tại. Áp payload đã xác minh của trạng thái đích, giữ mọi mã mới để FK không gãy, chỉ deactivate sau impact approval, không DELETE hay sửa history/snapshot đơn. Digest kết quả có thể khác backup đích vì giữ mã lịch sử; plan phải công bố chính xác. Tiến lại release cũ là event `re_advance` mới tham chiếu original import qua FK, **không** normal import hoặc historical-hash NO-OP. Path không duyệt, hết hạn do intervening event, sai hash/base/ordinal/date hoặc sai delta tiền đề → reject.
- High-water normal imports không bị hạ bởi recovery/re-advance; active release và path cursor chỉ tiến sau commit thành công. Nếu phải khôi phục toàn DB: restore backup vào PostgreSQL 17 cô lập trước, kiểm ledger/catalog/FK/snapshot và dữ liệu phát sinh sau backup; owner quyết định cửa sổ cutover/đối soát trước production. Không tự restore đè DB đang chạy hoặc dùng migration Down để đảo dữ liệu.

## 7. Ma trận kiểm thử cần cho P0-09 (PostgreSQL 17 thật)

Các test dưới đây **NOT RUN**, là acceptance plan cho G3 sau approval; không dùng SQLite/EnsureCreated để chứng minh transaction/FK/locks.

| Nhóm | Ca kiểm bắt buộc / kết quả mong đợi |
|---|---|
| Migration/parity | Migrate DB rỗng và DB sau P0-08; PK/FK/UNIQUE NULLS NOT DISTINCT/CHECK/grants đúng; chỉ thêm ledger. Down rỗng isolated OK; populated Down bị chặn; API không auto-migrate. |
| Pin/mapping | Hash source/metadata/LICENSE và CSV v5.2.0 khớp §1–2; 34/3321/3355 đúng fixture này. `FullName` giữ tiền tố/dấu; `01`/`00004` giữ TEXT. Fixture release sau khác counts vẫn được kiểm theo manifest mới. |
| Parser âm | UTF-8 lỗi, BOM giữa file, CSV quote/header/cột sai, duplicate JSON keys, numeric/malformed codes, empty/control name, duplicate declaration/ward, conflicting province name, half-empty ward, orphan/parent mismatch, district hierarchy → từ chối, DB không đổi. |
| Province-only | Tỉnh 0 ward hợp lệ; đúng một declaration; duplicate/thiếu declaration trong full bị chặn; empty ward row không tạo ward. Delta declaration không deactivate ward vắng mặt. |
| Manifest/trust | Hash/size/count/version/mapping khác, manifest duplicate/unknown field, approved fingerprint/target khác → reject; đủ count không tự thành full. Production thiếu official issuer/legal/reconciliation/completeness → reject; LocalTest fixture khai chưa verified phải bị chặn trên target Production. |
| Ordering | Normal import cũ hơn high-water, ngày tương lai, ordinal conflict, cùng identity khác tuple/hash hoặc so v5.10/v5.2 bằng chuỗi → reject; correction cùng ngày cần identity/ordinal/evidence mới. Re-advance chỉ theo path đã duyệt, tăng so current active, không sửa high-water. |
| Full/delta | Full missing chỉ deactivate đúng danh sách approved; delta absent giữ nguyên, explicit deactivate đúng; active ward dưới inactive tỉnh bị chặn; reactivation/reparent xuất hiện trong diff. |
| Dry-run/TOCTOU | Dry-run zero writes; đổi source/plan/binary/migration/tail/catalog/reference giữa dry-run/apply → reject; chỉnh approval không tạo quyền. Cùng bytes plan tái lập cùng hash. |
| Atomicity | Chèn lỗi SQL sau upsert tỉnh/ward, lỗi ledger, cancel, timeout → cả data/ledger rollback; rollback giữ setting/audit cũ; mất COMMIT acknowledgement được phân giải bằng ledger, không apply mù. |
| Concurrent/idempotent | Hai process cùng plan: lock contender nhận import_busy; retry sau commit NO-OP đúng tail, khác plan/base cũ stale_plan. Tối đa một successor/append; thử bypass advisory lock bằng hai insert cùng predecessor → một unique violation, transaction thua rollback cả data. Exact tail không đổi row/timestamp/log; DB drift có hash cũ → BLOCK. |
| Historical impact | Rename/reparent/deactivate giữ FK; customer address không bị sửa; snapshot đơn byte-equivalent; active parent invariant đúng; reference writers bị lock hoặc làm plan stale; fixture chưa có CRM chạy được. |
| Permissions/audit/API | PostgreSQL 17: owner chỉ tạo DB/schema/grants; **mọi bước runtime dùng connection đăng nhập thật tp_app**, xác nhận current_user/session_user, rolsuper=false và không membership tp_owner. Kiểm has_table_privilege: SELECT/INSERT ledger true, UPDATE/DELETE/TRUNCATE/MAINTAIN false; lấy advisory (8300,9), đọc tail, mutate danh mục và append/commit được, không table-lock ledger. Thử U/D/T ledger bị permission denied trong transaction riêng. Connection tp_app thứ hai kiểm import_busy, commit/rollback bên đầu giải phóng khóa; tp_readonly không ghi. Settings giữ permission/CSRF/AC-P0-04 redaction. |
| Backup/recovery | Production backup fail chặn apply; restore drill isolated khớp catalog/history; controlled recovery giữ mã/FK mới và event history. LocalTest DB dùng một lần test được mà không mạo nhận production evidence; không chấp nhận old release bằng normal path. |

Acceptance bổ sung đúng **P1-02** (đều chạy bằng restricted runtime role trên PostgreSQL 17; hiện **NOT RUN**):

| Chuỗi / điều kiện | Kết quả bắt buộc |
|---|---|
| `A1 → B2 → recovery A1 (path=[B2 gốc]) → re_advance B2` | SUCCESS; active A→B→A→B, high-water 1→2→2→2; bốn event, FK re_advance_of trỏ original B2; không sửa event cũ. |
| Chuỗi trên nhưng B2 có source/CSV hash khác | REJECT cùng identity khác payload; không ghi data/event hoặc tiêu thụ path. |
| Sau recovery dùng plan/base trước recovery, hoặc có intervening event | REJECT stale_plan/path; không append, cần dry-run current-base và fresh approval. |
| Lặp exact import/re-advance đã là current tail, digest đúng | NO-OP, ledger count/timestamps/data không đổi; B2 chỉ có trong lịch sử thì không được NO-OP. |
| Hai re-advance cùng current base | Tối đa một append; contender import_busy, retry exact successful plan NO-OP; plan khác cùng base stale_plan. |
| A1 sau B2 không có recovery được duyệt; target ngoài/sai thứ tự/hết path; sai effective date/delta tiền đề | REJECT downgrade/replay/path; high-water không hạ. |
| Re-advance lỗi SQL sau mutate hoặc lúc insert event / cancellation | Complete rollback: data/catalog digest/tail/high-water/path cursor/history như trước transaction; advisory lock được giải phóng. |

Các constraint được kiểm âm riêng: hai genesis, duplicate predecessor, dangling/self predecessor, recovery/re-advance làm genesis, sai operation/nullability và FK re_advance_of không tồn tại đều reject; rejection không để dữ liệu danh mục commit một phần. Test SQL/DDL là phần G3 local, không dùng superuser để chứng minh runtime thành công.

Lệnh G3 dự kiến: `dotnet test backend/TeaPremium.sln` (có Testcontainers PostgreSQL 17), EF idempotent migration script/parity/permission checks và restore drill cô lập theo runbook đã duyệt. Cần báo tên test/count/exit code thực tế lúc thực hiện; không coi bảng này là bằng chứng PASS.

## 8. Bằng chứng đã chạy, blocker và G4

**Bằng chứng phiên planning ban đầu, tái dùng trong remediation:** fetch origin; branch từ SHA ở trên; diff spec amendment exit 0; tag→commit đúng; tải bằng Node `fetch` chỉ raw URLs có commit; SHA-256 và Git blob kiểm trên bytes; Node assertions trong bộ nhớ cho UTF-8, Type, code format/uniqueness, FullName, parent, NFC và 34/3321 PASS, CSV projection §2 có hash cụ thể. Source cache local ở ignored `artifacts/f02-source-check/`, không thuộc handoff/publish. Independent G4 đã tái lập pin/four hashes/CSV round-trip nhưng BLOCK ba finding dưới đây; remediation không tải lại nguồn hoặc restart G0/G1/G2. Không chạy application tests/SQL/migration/backup/restore/CI trong lần sửa này.

| Command kiểm tra local | Kết quả |
|---|---|
| `git diff --exit-code 0bfc98e HEAD -- docs/spec` | PASS, exit 0; spec đúng amendment đã duyệt. |
| `git ls-remote https://github.com/thanglequoc/vietnamese-provinces-database.git refs/tags/v5.2.0 "refs/tags/v5.2.0^{}"` | PASS, exit 0; SHA tag đúng §1. |
| `Get-FileHash artifacts/f02-source-check/json__full_json_generated_data_vn_units.json, artifacts/f02-source-check/json__vn_provinces_metadata.json, artifacts/f02-source-check/README.md, artifacts/f02-source-check/LICENSE -Algorithm SHA256` | PASS; bốn hashes khớp §1, kiểm lại bằng PowerShell độc lập với Node. |
| `git diff --check` và `git diff --no-index --check -- NUL docs/plans/p0-09-f02-readiness.md` | Không có lỗi whitespace: tracked exit 0; no-index exit 1 vì file mới khác NUL, không có whitespace diagnostic. Git có cảnh báo LF→CRLF theo cấu hình checkout, không đổi bytes nguồn trong cache. |
| `git diff --exit-code origin/main -- docs/spec backend apps packages infra` | PASS, exit 0; không thay spec/code/migration. |

Handoff inventory: `git diff origin/main` chỉ có dòng F-02 ở `docs/plans/task-readiness.md`; staged/committed delta rỗng; `git ls-files --others --exclude-standard` chỉ có `docs/plans/p0-09-f02-readiness.md` (**phải đọc trực tiếp**). HEAD vẫn bằng base; không commit/push/PR/merge.

| Việc còn mở | Ảnh hưởng / cách đóng |
|---|---|
| Owner chưa duyệt F-02 | Duyệt pin, normalization, manifest/registry, full/delta, approval/recovery và migration proposal sau independent G4. Không coi yêu cầu lập plan là approval triển khai. |
| Official full reconciliation chưa có | Chặn **production APPLY**, không chặn coding/test local sau coding gate: lưu official evidence đã băm, đối chiếu toàn bộ 34/3321 mã/tên/parent, xác minh nội dung 388 và xử lý khác biệt README/ngày ban hành. Nêu người chịu trách nhiệm; community checksum không đóng việc này. Không yêu cầu tải dataset live không ghim. |
| Ledger ngoài DDL hiện hành | Chặn migration/G3 ledger: owner duyệt/ủy quyền amendment DDL/spec rồi scoped G1/G2 + G4; không sửa `docs/spec/**` trong lần này. |
| Quyền vận hành/backup/restore | Chặn **production APPLY** đến khi production runtime role/operator/ACL và backup/restore evidence được kiểm. Không chặn test role/SQL trong PostgreSQL 17 dùng một lần; không khẳng định production đã đáp ứng. |
| Address reuse guards ở pha CRM/đơn | Chặn mở luồng chịu tác động khi đã có reparent/deactivate; gắn P2-02/P2-06/P3-02/P3-05, không kéo nghiệp vụ vào F-02 hoặc đổi global health. |

**P2-03 — hai gate độc lập, cùng áp dụng cho cả hai planning documents:**

| Gate | Điều kiện và quyền tiếp tục |
|---|---|
| **CODING** | Owner duyệt F-02 contract/DDL sau focused G4, ủy quyền amendment DDL/spec cần thiết và giao P0-09 G3 riêng; source fixture/pin/checksum đã VERIFIED theo bằng chứng trên. Khi đủ các điều kiện này, **GO local/test importer development** bằng `$implementation-slice`; official full reconciliation và production backup/restore chưa có không giữ gate coding ở HOLD. Hiện chưa có owner approval nên coding vẫn HOLD. |
| **PRODUCTION APPLY** | Chỉ GO khi official full-catalog reconciliation và source→official mapping evidence đầy đủ; production runtime role permissions đã test; backup và restore drill verified; dry-run diff/current-base fingerprint/fresh operator approval hợp lệ; identity/version/effective date và recovery/re-advance policy thỏa. Thiếu bất kỳ mục nào → HOLD, kể cả coding/test đã xong. |

LocalTest chỉ chạy trong môi trường Development/Test với target DB dùng một lần được allowlist rõ (`teapremium_dev*`/`teapremium_test*` tại local hoặc Testcontainers), dùng pinned fixture/synthetic cases và ghi thiếu official evidence đúng thực tế. Plan/approval phải bind profile và target. Target Production không thể chọn LocalTest bằng cờ/manifest; unknown hoặc không khớp môi trường/allowlist thì từ chối trước SQL. Production luôn áp gate đầy đủ, không cung cấp `--force` bỏ gate. Đây là phân biệt evidence cho local/test và production, không nới kiểm hash/identity/order/transaction.

**READY_FOR_FOCUSED_G4** chỉ là handoff remediation, không tự đóng finding hoặc duyệt owner decision. P0-09 vẫn là một task được giao riêng, không tạo roadmap/task ID mới. Size S có rủi ro provenance/locks/recovery như bản đề xuất gốc; không tự sửa backlog.

| Finding G4 | Sửa trong đề xuất | Evidence / bước còn lại |
|---|---|---|
| **P1-01 — ledger lock privileges** | §3 giữ append-only grants, PK/FK/unique predecessor và CHECK; §5 dùng transaction advisory key `(8300,9)` trước state reads, không table-lock ledger; cùng transaction với data/event, thứ tự khóa và busy/stale behavior rõ. | Rà tĩnh DDL/algorithm và acceptance restricted tp_app tại §7; PostgreSQL role/concurrency tests **NOT RUN**, phải chứng minh ở G3. |
| **P1-02 — high-water after recovery** | §3 thêm FK re_advance_of/operation CHECK; §4/§6 tách active vs historical high-water, immutable payload, approved recovery path, fresh plan/approval và append re-advance. | Bảy acceptance cases tại §7; SQL rollback/concurrency thực **NOT RUN**. |
| **P2-03 — gate separation** | §1/§3/§6/§8 và dòng F-02 trong task-readiness thống nhất coding gate vs production APPLY gate; LocalTest không mạo nhận official/backup evidence. | Chưa có owner approval/production evidence; không dùng thiếu production evidence làm blocker coding sau approval. |

Kiểm tra remediation: `git diff --check` PASS (exit 0); `git diff --no-index --check -- NUL docs/plans/p0-09-f02-readiness.md` không có whitespace diagnostic (exit 1 vì file mới). `node --input-type=module` với harness stdin kiểm UTF-8/whitespace, DDL grants/chain/re_advance, advisory/tail/stale/atomicity contract, đủ **7** acceptance rows và gate trong hai tài liệu PASS (exit 0); đây chỉ là **static document checks**, không phải SQL/importer test. Harness ban đầu lỗi encoding stdin và lọc nhầm một row; đã sửa harness, không diễn giải thành lỗi runtime. Fingerprint SHA-256 toàn bộ **213 nonignored files** so với trước remediation: chỉ hai file planning được phép đổi, **211 file khác giữ nguyên**; source/CSV checksums trong đề xuất giữ nguyên. Staged/committed delta rỗng; HEAD/base không đổi. PostgreSQL role/constraint/concurrency/rollback, migration, application tests và production SQL đều **NOT RUN**.

Focused G4 tiếp theo dùng `$review-gate` trong context độc lập, **cùng worktree** `D:\tra-hien`, read-only: diff so với `origin/main`, staged/unstaged/committed và **nội dung file untracked**. Scope đúng ba finding: DDL/grants/advisory/chain và restricted-role acceptance; active/high-water/recovery path/NO-OP/races/rollback; gate coding/production trong hai tài liệu. Tái dùng source evidence đã kiểm, không restart audit hoặc redesign import. Không tự nhận G4 PASS từ self-check remediation; không commit/push/PR/merge.

Next action duy nhất: independent focused G4 ba finding đã sửa; sau đó owner quyết định contract/DDL trước khi giao P0-09 coding.
