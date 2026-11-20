# P1-04 — lịch sử public và hợp đồng publish

Ngày quyết định: 19/11/2026. Base `f2227a07aeb75e9e8e5af655378a3d85c6b4e6e7`.
Nguồn: hai câu trả lời owner và clarifications sửa finding trong session P1-04 tại `D:\tra-hien-p1-04`.
**Schema approved in principle; business contract owner-approved. Amendment local
đã nhận G4 BLOCK theo thông tin owner (P1 SEO fallback, P2 warning contract);
bản sửa pending focused G4. Không tự nhận G2/G3 migration hoặc G4 implementation PASS.**

## Quyết định owner

1. Duyệt nguyên tắc thêm `first_published_at timestamptz NULL` vào categories,
   collections, materials, artisans. Chỉ set ở public transition thành công đầu
   tiên; không reset/overwrite khi hidden/archived/republished/renamed. Giữ nguyên
   IDs, FKs, slug, current status và reference data. EF migration mới additive,
   entity/mapping/snapshot phải đồng bộ, không sửa migration lịch sử.
2. Trước migration phải assessment dữ liệu; không suy historical publication chỉ
   từ current status, không bịa timestamp. Có migration/backfill và rollback/
   recovery analysis tường minh; kiểm existing data/FK preservation.
3. Material/artisan draft/archived→published chỉ qua POST /publish với
   catalog.publish; PATCH catalog.write không được bypass. Validate name/slug/
   kind/schema; sanitize HTML server; media nếu có phải public-use image hợp lệ.
   SEO fallback theo BR-SEO-31/32, không áp điều kiện product BR-CAT-04.
4. **Bác bỏ đề xuất chặn publish dưới 400 từ.** Dưới 400 sanitized plain-text words
   chỉ cảnh báo chất lượng; không tự robots_noindex hoặc thêm publish restriction.
   Giữ mục tiêu ≥400 từ cho kế hoạch nội dung và SEO Audit tương lai.
5. Category/collection hidden/visible qua catalog.write; không thêm /publish nếu
   API spec chưa được sửa cho việc đó. Giữ hierarchy/slug/first publication;
   không bịa archive redirect ngoài luật đã duyệt.
6. Không sửa P1-03/CI worktree. P1-03 không sửa schema bốn bảng này. Không commit,
   push, PR, merge, production apply. Chuẩn bị amendment tối thiểu và tuân gate;
   dừng tại review amendment trước migration khi cần gate đó.

Traceability: P1-04, FR-CAT-02/03/08, BR-CAT-01/06/08/09/10/14,
BR-SEO-03/31/32/35, DEC-01/21/26, NFR-08/09. Không tạo requirement ID/roadmap khác.

## Lý do và phương án đã cân nhắc

Current status mất thông tin sau hide/archive; redirect table không có lịch sử nếu
chưa từng đổi slug. Audit hiện chưa bật taxonomy; dùng audit làm business state sẽ
tạo coupling không có contract. Một timestamp nullable trong mỗi aggregate là
thay đổi nhỏ, giữ vĩnh viễn trạng thái ever public cho các write mới.

Đề xuất ban đầu dùng timestamp migration cho row hiện public đã được **rút bỏ**
theo owner rule không bịa historical timestamp. Chưa khảo sát DB thật. Amendment
định nghĩa HOLD target có lịch sử chưa rõ; executable backfill plan cho populated
target chưa được phê duyệt hay thực thi. Bảng rỗng/test fixture không chứng minh
target thật rỗng. Không để application mới coi NULL của lịch sử chưa rõ là never public.

Không hard-block 400 từ vì owner chọn warning; không đổi sang thin/noindex của
product. Mọi manual noindex rule hiện hành giữ nguyên.

## Delta authoritative chuẩn bị review

- `schema.sql`: đúng bốn cột nullable, không default/backfill/UPDATE/DDL khác.
- `03-database.md` §3.10: lifecycle, assessment/evidence và recovery.
- BR-CAT-08: nhớ ever-public sau hide/archive, cùng transaction.
- `05-api.md` §4.2: read/write/publish, không bypass, warning, media/SEO fallback.
- BR-SEO-35: phân biệt content goal với publish gate, không tự noindex.
- `00-README.md`: ghi amendment local pending G4 và nguồn quyết định này.

G1 TARGETED đối chiếu sáu nguồn trên với baseline; kiến trúc modular monolith,
layers/UoW/outbox/IAM và roadmap giữ nguyên. **GO chuẩn bị amendment; HOLD migration/
application implementation tại handoff này để independent review amendment.**

Reviewer dùng cùng worktree, đọc toàn bộ diff origin/main, staged/unstaged và các
file untracked; review depth A (DDL/SEO/auth contract). Owner báo amendment G4
BLOCK với hai finding; sửa lần này chưa được independent focused G4 xác nhận.
Không cần owner duyệt lại các lựa chọn nghiệp vụ đã chốt; next gate là focused review.

## Clarifications sửa finding amendment G4 — 19/11/2026

Owner duyệt phạm vi P1/P2 dưới đây; mọi quyết định first publication, permissions,
hidden/visible, warning-only/no-auto-noindex và không áp BR-CAT-04 vẫn giữ nguyên.
Đây là sửa contract trong cùng P1-04, không khởi động lại implementation planning.

| Finding owner báo | Contract được chốt trong authoritative spec |
|---|---|
| **P1 — SEO fallback incomplete** | BR-SEO-31/32: ưu tiên explicit SEO; material title theo mẫu hiện hành; artisan mặc định `Nghệ nhân {name} \| {brand}` khi chưa có nguồn main material được xác minh, không suy từ sản phẩm bất kỳ. Description material: seo_description → summary → sanitized description_html text → `Thông tin về chất liệu {name}.`; artisan: seo_description → sanitized bio_html text → `Thông tin về nghệ nhân {name}.`. Nguồn optional rỗng được bỏ qua; brand từ seo.brand_name, thiếu thì bỏ cả separator. Rút gọn metadata sinh tại ranh giới từ/Unicode text elements; không bịa thông tin, không ghi fallback đè explicit SEO; không thêm publish blocker/noindex. |
| **P2 — publication warning incomplete** | BR-SEO-35/05 §4.2: chỉ description_html/bio_html; sanitize, loại toàn bộ script/style, trích visible text có block separators, decode entities, normalize Unicode/NBSP, collapse và count token không rỗng. Không tính summary/metadata/lists/articles/boilerplate. NULL/rỗng = 0. Publish thành công HTTP 200 `{id,status,warnings}` (rowVersion theo quy ước chung); <400 thêm warning code `content_below_recommended_words`, field descriptionHtml/bioHtml, actualWords thực tế, recommendedWords 400; ≥400 warnings rỗng. Không dùng warning của product/không đổi robots_noindex/không bỏ kiểm quyền. |

BR-SEO-31/32/35 trong `09-seo.md` là thẩm quyền SEO (DEC-21); `05-api.md` đặc tả
wire response, không tự đặt điều kiện SEO khác. Mẫu trung tính không khẳng định
xuất xứ, giải thưởng, kinh nghiệm, chứng nhận hay có sản phẩm. Không cần thêm
field chính-material, bảng hoặc migration cho hai clarifications này.

Coverage contract: 0/399/400/401; tags/entities/Unicode/NBSP; script/style;
NULL/empty; custom SEO; missing brand; artisan không main material; material không
summary/description; publish warning vẫn thành công; unauthorized không publish;
không thay noindex. Matrix và validation thực chạy ở
[task record](../plans/p1-04-implementation.md#sửa-finding-amendment-g4--19112026).
Các acceptance này chưa là runtime tests PASS; chưa có API/migration.

## Acceptance bắt buộc cho G3 tiếp theo

Never public rename không tạo history; public rename tạo redirect; public→hidden
và public→archived rồi rename vẫn redirect; hide→republish không đổi timestamp;
A→B→C flatten; reuse không loop; rollback/concurrent state-slug writes; old data/
FK/seed preservation. Thêm permission/CSRF và warning tests theo API contract;
400 từ không hard-block/noindex. Danh sách đầy đủ và evidence môi trường ở
[task record](../plans/p1-04-implementation.md).
