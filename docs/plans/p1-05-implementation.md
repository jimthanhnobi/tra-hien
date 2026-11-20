# P1-05 — Products & Variants, local G3

**Current handoff:** owner báo F1/F2/F3 independently CLOSED và giao sửa hai blocker
F4/P1 search, F5/P1 archive target, kèm scoped spec amendment. Trạng thái hiện hành
tại [final blocker evidence](p1-05-final-blocker-evidence.md); các kết quả bên dưới
là lịch sử, không thay independent focused G4/publish permission.

Final F4/F5 local fix PASS: focused74/74 và full1055/1055, READY_FOR_FOCUSED_G4 YES;
chờ independent focused G4, READY_FOR_OWNER_PUBLISH NO. SDK server cleanup và
baseline harness limitation ghi minh bạch trong evidence hiện hành.

**Cập nhật focused fix:** owner báo independent G4 BLOCK với F1/P1, F2/P2, F3/P2;
đã giao remediation đúng ba findings và triage-only hai gap search/redirect.
Trạng thái/results hiện hành tại [focused G4 fix evidence](p1-05-g4-fix-evidence.md).
Local fix PASS, READY_FOR_FOCUSED_G4 YES; regression mới981/981 +frontend26/26,
F1/F2/F3 local CLOSED chờ reviewer. Extra search/redirect acceptance gaps OPEN.
G3 PASS bên dưới là lịch sử handoff, không là G4 approval.

Owner đã duyệt G1 TARGETED PASS / G2 GO / READY_FOR_G3 YES và giao G3 tại
`D:\tra-hien-p1-05`, branch `feature/p1-05-products-variants`.
Base/HEAD lúc bắt đầu: `bb9a901044d41a0783d226fcd678e3865390fa30`.
Main sau fetch khớp base; post-merge push CI run 44664344998 success, ba required
jobs thực chạy. Main worktree và các feature worktree khác không thuộc phạm vi ghi.

**G3 PASS / READY_FOR_LOCAL_G4: YES.** Slices A/B/C/D hoàn tất local, full backend
931/931 và frontend26/26 PASS; chưa independent G4, không cấp quyền publish/merge/
production. Evidence cuối tại [G3 evidence](p1-05-g3-evidence.md); không cộng focused
reruns vào full-suite total.

## Nguồn và traceability

Tái sử dụng G1 full `cadce575597738bf40ad7c45689482de5a5fbc3d`, targeted A01/B01/
P1-04 và G2 conformance/task-readiness. Spec tree của integrated P1-04
`81afe9e3e837f7f5df347368bffb092bc978f5b8` giống base hiện hành. Không full audit lại.

| Scope | Authoritative anchors |
|---|---|
| Task, dependencies, AC | `07-roadmap.md:54`, `:68–78`: P1-05 sau P1-02/03/04; AC-P1-01/02/03, phần giá AC-P1-09, phần data/warning/outbox AC-P1-10; AC-P1-06/07 hỗ trợ |
| Product/variant/media/workflow | `01-requirements.md:68–71`, FR-CAT-04/05/06/07; FR-CAT-11 search |
| Business | `04-business-rules.md:34–71`, BR-CAT-01…15; BR-CAT-13 import chỉ boundary, P1-07 |
| SEO | `09-seo.md:23–36`, `:73–76`, `:111–116`: BR-SEO-03/20/21/22/23/34; DEC-21 precedence |
| DB | `03-database.md` §3.1/3.2/3.7/3.10/8; `schema.sql:281–393`, products/variants/EAV/media; content.redirects |
| Architecture/API | `02-architecture.md` §3/4/5.2/5.4/5.5/5.8/7/10; `05-api.md` §1/4.2/4.3 |

DEC-01/04/14/15/16/20/21/22/25/26/27/30; NFR-04/07/08/09.
Owner xác nhận giữ ngoại lệ draft active + price NULL; không sửa AC-P1-09.
BR-CAT-05/11/12 và BR-SEO-20/21 chỉ chuẩn bị dữ liệu/lifecycle, không claim
storefront/JSON-LD/feed/sold-out job trong P1-05.

## Slices và ownership

Một main implementer sở hữu toàn bộ writable paths, thực hiện các nhóm acceptance
A → B → C → D; không spawn implementer/reviewer cho G3 này.

| Slice | Implementation và proof |
|---|---|
| A | ProductData/ports/workflow, draft create cùng một default variant, relations, typed EAV, type-change confirmation; ProductApiTests |
| B | Variant CRUD, bounded SKU allocation, price/owner usage guards; ProductRulesTests/ProductVariantTests và price cases ProductPublicationTests |
| C | Completed public assets, media/cover, full publish errors, archive/restore/redirects; ProductMediaSecurityTests/ProductPublicationTests |
| D | Derived count/manual/thin/site eligibility, search canonical reuse, business audit, invalidation contract/delivery, OpenAPI/client; ProductOutboxContractTests và full regression |

Application/Domain giữ rules/ports; Infrastructure giữ EF/SQL/R2-image projection;
Api chỉ binding/permission/map. Inventory chỉ cung cấp port SELECT, không có stock
mutation. Không P1-06 calculator, P1-07 import, P1-08 inventory use case, UI hay
storefront. Không sửa `docs/spec/**` hoặc migration/snapshot.

## Lock protocol và finding đã tái hiện

Readiness phát hiện cycle: product SHARE materials → graph, material PATCH graph
→ RowExclusive materials. `ProductLockOrderTests` trước sửa: 1 PASS/1 FAIL,
TRX `artifacts/p1-05/lock-before.trx`; tại failure material giữ graph trong khi
đang chờ RowExclusive table lock. Đây là actual PG17 observation, không suy từ timing.

Fix tối thiểu: material writer lấy eventual `ROW EXCLUSIVE` table lock ở đầu
write, trước namespace/row/graph, kể cả PATCH metadata không đổi slug. Không đổi
key `(8104,100)`, row/graph order, snapshot/flatten/reclaim semantics P1-04.
Không lấy thêm exclusive reference-catalog lock hoặc khóa process/global mới.

Product write:

1. UoW READ COMMITTED, SHARE reference tables theo thứ tự product_types →
   attribute_definitions → attribute_options → materials, trước read metadata.
2. Routing mutation: namespace `(8104,4)`; parent product `FOR NO KEY UPDATE`; existing
   variants theo ID `FOR UPDATE`; cần thiết thì lock category/artisan trước graph.
   Không material row lock sau SHARE table lock.
3. Đọc/validate metadata trong cùng transaction; new/changed values dùng P1-03
   TypedAttributeValidator. Historical inactive assignments không bị rewrite/bác
   bỏ chỉ vì một metadata field khác thay đổi.
4. Shared graph `(8104,100)` → redirect rows. Không lấy aggregate/namespace khác
   sau graph, không HTTP/R2 IO dưới graph lock.
5. Business, audit, invalidation/outbox, search cùng commit; Result failure/
   exception/cancellation rollback cả early SaveChanges.

ProductConcurrencyTests dùng barriers trong DB command interceptor và pg_locks:
actual product/material HTTP ở cả hai thứ tự; publish/NULL price cả hai thứ tự;
metadata/value writes cả product và variant scopes. Không chấp nhận HTTP500 hay
deadlock là kết quả cạnh tranh hợp lệ.

## SKU, giá, media và audit

SKU base dùng nextval duy nhất khi tạo product; min bốn chữ số, không truncate số.
Default variant ưu tiên base; suffix giữ sequence và chống trùng, SKU ≤40. Giới hạn
64 generated candidates; chỉ retry `23505/ux_variants_sku`. Savepoint cleanup detach
failed candidate và audit entries do attempt đó tạo, giữ prior tracked state.
Manual collision không retry; inactive giữ UNIQUE. Parent/variant row locks bảo vệ
usage check và writes; stock movement FK inserts tuân row-lock conflict. Sales
implements IVariantUsage tại P2 khi có order lines, không probe/đoán schema Sales.

Draft active NULL được lưu; activation NULL và active product price NULL bị từ
chối. Publish gom đầy đủ errors, không nới metadata do thin. Product type chỉ đổi
khi draft, confirmation trước cleanup incompatible values. Không DELETE product/
variant endpoint; aggregate luôn ≥1 variant qua các use cases hiện có.

ProductMedia chỉ tham chiếu media.assets public image; pending/evidence/video và
cross-product variant bị từ chối. Đổi cover clear/flush old trước set new trong
cùng UoW do partial UNIQUE không deferred. DELETE relation không xóa R2 asset.

Variant chủ động [Audited], entity/path classification: giá/trạng thái/flags/
numeric/time an toàn, tên/barcode/free text/JSON/SKU mặc định redacted. Product audit
chỉ business state/manual noindex/word count/thin; reason Data Protection cùng
key infrastructure hiện có. Product không [Audited] để canonical search-only raw
SQL không vô tình bypass interceptor. Không thay global settings/PII policy.

Search saves dùng cùng SQL của canonical RebuildProductSearchTextHandler;
reference rename producer/message/batch 1..200/consumer duy nhất giữ nguyên.
StorefrontInvalidate tags: old/new product slug/category, catalog/sitemap/feed;
Distinct và chunk≤200, không truncate affected tags; atomic với
write. Consumer POST receiver cấu hình, secret header, timeout, không auto-follow
redirect; thiếu cấu hình/HTTP failure đi qua retry/dead, không no-op success.

## Failure cases và validation

Matrix gồm happy/bad/boundary/auth/CSRF/IDOR/rowVersion/concurrency; SKU collision,
exhaustion, sequence gap, price NULL/range, typed scope/type/option, cover rollback,
full publish error list, numeric axis uniqueness, first-public history, graph
flatten/reclaim, archive 301 vs never-public 404, 149/150/151 và manual/site priority.
Failure injection ở audit/outbox/redirect sau early saves kiểm aggregate versions
và toàn ledger fingerprint không đổi; fixture HTTP500 không đại diện race behavior.

## Migration, rollback và giới hạn

Migration requirement **NO**: P1-01/P1-04 có đủ structure và grants. Kiểm EF pending
changes + existing PG17 parity/migration tests; không sửa snapshot để che drift.
Backfill word count/assessment publication history ở target có dữ liệu cũ là gate
trước dùng dữ liệu đó; không suy lịch sử từ status hoặc tự đặt timestamp. Product
active/published_at/archived redirect thiếu first_public history bị chặn mutation.

Rollback source có kiểm soát; không reset sequence, xóa audit/outbox/SKU/history hoặc
Down migrations nền. Data đã commit dùng recovery/roll-forward; task không apply
production. Receiver cache thật/SSR/canonical/sitemap/feed thuộc P3 và chưa tồn tại;
test receiver giả không là bằng chứng cache thật. Khi tích hợp phải có receiver và
xử lý invalidation pending/dead trước mở storefront. Order usage adapter thật sẽ
được nối cùng Sales P2; P1 kiểm contract injection và stock movement thật.

## Verification

Lệnh Linux chạy trong runner riêng `teapremium-p105-g3`, bind đúng worktree này,
SDK10.0.403, Node22.22.2, PostgreSQL17 Testcontainers, runtime tp_app, ffmpeg.
Artifacts ignored ở `artifacts/p1-05`; không dùng DB/container production.
Trạng thái và exact totals cuối: xem `p1-05-g3-evidence.md` sau khi verification xong.
