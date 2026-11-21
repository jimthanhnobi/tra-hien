# P1-09 — Phiếu nhập, điều chỉnh và kiểm kê

## Baseline và phạm vi

G0 PASS, G1 TARGETED PASS, G2 GO. Worktree `D:/tra-hien-p1-09`, nhánh `feature/p1-09-inventory-operations`, HEAD và merge base ban đầu `667274cfcc4c7aae790100237ccc2de29a0484ee`, worktree sạch. P1-08 đã merge. [CI push-main](https://github.com/jimthanhnobi/tra-hien/actions/runs/44899900821) PASS đủ bốn job trước G3.

Tái sử dụng G1 tại `cadce575597738bf40ad7c45689482de5a5fbc3d` và G1/G2 P1-08. Từ base P1-08 `b45558f`, spec chỉ thêm quy tắc PATCH phân loại độc bản tại 05 §4.2. Đã đối chiếu 00 V2, 01 FR-INV-01/02/04/05/06 và FR-PLT-01, 02 §5.2–5.5 và §7, 03 §1/§3.3/§4, 04 BR-INV-01/02/05/07–10/12, 05 §1/§4.3, 07 P1-09 và AC-P1-04/05/06/08, DDL sáu bảng Inventory, A01 (G13 và D12), B01 và [kiến trúc Inventory](p1-inventory-architecture.md).

### Quyết định owner ngày 21/11/2026

- BR-INV-10 và 05 §4.3 chưa định nghĩa hai phiên đếm cùng variant. Cho đếm chồng lấn. Khi một phiên khác đã duyệt biến động sau lần đếm, chặn duyệt phiếu sau nếu dòng trùng có delta khác 0. Hủy phiên đó và đếm lại. Zero delta không tạo biến động.
- 05 §4.3 có route hủy mà chưa ghi trạng thái kiểm kê được hủy. Cho hủy cả `counting` và `submitted`.
- FR-INV-02 và 05 §4.3 cho nhân viên nhập giá nhưng cấm trả giá khi thiếu `costs.read`. PATCH được bỏ giá của dòng giữ cùng variant để giữ giá đã lưu. Dòng mới phải có giá. Nhập giá không cấp quyền đọc giá.

## Hợp đồng triển khai

- **Chung.** AdminCookie, StaffReady, `inventory.write` và CSRF với ghi. List phân trang 20 mặc định, tối đa 100, lọc status, ngày tạo giảm dần rồi UUID giảm dần. Detail trả `document` và `lines`. Giá trị thời gian gửi về UTC, `receivedAt` đầu vào có offset và được chuẩn hóa UTC. Text supplier ≤ 200, note/reason ≤ 2.000. Tối đa 500 dòng. ID chứng từ/line dùng UUID v7. Mã `PN-yyMMdd-N` hoặc `KK-yyMMdd-N` theo giờ VN. Hậu tố cấp tăng theo giá trị số lớn nhất, đệm tối thiểu ba chữ số và tiếp tục qua 999. Advisory transaction lock theo ngày và loại chứng từ, UNIQUE DB giữ chốt cuối. Nếu hậu tố đã đạt hoặc vượt miền `long`, trả 409. Không thêm sequence hay migration.
- **Phiếu nhập.** Create draft, supplier/note/receivedAt/lines tùy chọn. PATCH bỏ field thì giữ, null xóa text/time, `lines: []` xóa dòng draft, mảng lines thay danh sách theo variant. Bỏ hoặc null giá giữ nguyên giá của dòng cũ. Dòng mới cần giá VND nguyên không âm, lượng nguyên dương. Cấm trùng variant. Draft hủy hoặc post, posted/cancelled là terminal. Post đòi ít nhất một dòng. Trường giá ở detail/create/edit/cancel chỉ có khi `costs.read`. List và post/replay không có giá hoặc tổng tiền.
- **Idempotency.** `inventory.receipt.post.v1`, actor scope AdminCookie + UUID, header UUID và buffering P0-07. Response allowlist `id,status,rowVersion`, giữ byte khi replay, lưu 48 giờ. Cùng key khác request 409. Phiếu posted vẫn bị chặn bởi version/lifecycle với key khác hoặc hết hạn. Dùng đúng context/transaction của `EfUnitOfWork`, commit idempotency cùng toàn bộ ghi kho.
- **Điều chỉnh.** Lượng khác 0, lý do sau trim có tối thiểu 3 ký tự Unicode. Tăng cần thêm `inventory.approve`, xét quyền trước khi đọc tồn. Damage chỉ nhận lượng âm. Movement dùng giá bình quân hiện hành (giữ NULL/0), không tính lại avg. Reference UUID riêng của lần điều chỉnh, không tạo bảng chứng từ phụ.
- **Kiểm kê.** PUT đếm lại thay cả snapshot lần đếm của variant trong `counting` và cập nhật xmin. Dòng lưu on_hand lúc khóa, số đếm, thời điểm và actor. Tồn thiếu row đọc là 0. Submit cần ít nhất một dòng. Approve cần thêm `inventory.approve`, áp delta trên on_hand hiện tại, báo tất cả dòng lỗi bằng `errors["lines[variantId]"]`, rollback cả phiên nếu một dòng lỗi. Mốc đếm và duyệt được đọc sau khóa variant, có chặn dưới theo mốc liên quan cộng 1 µs khi cần để thứ tự giữ được ở độ chính xác PostgreSQL.
- **Khóa và phiên bản.** Idempotency nếu có → chứng từ → tập Product UUID tăng (`FOR NO KEY UPDATE`) → Variant UUID tăng (`FOR UPDATE`) → Stock UUID tăng (`FOR UPDATE`). Một lần `LockAsync` cho tập tham gia. Lập mọi posting trước stage, một posting mỗi variant. xmin lấy lại sau SaveChanges, kể cả cập nhật chỉ ở dòng. POST/PATCH/PUT sửa hiện hữu yêu cầu opaque `rowVersion`, sai định dạng 400 và stale 409.
- **Audit và outbox.** `AuditSaveChangesInterceptor` ghi thay đổi chứng từ, dòng phiếu nhập và số dư. Dòng kiểm kê có khóa ghép nên audit định danh theo UUID chứng từ, allowlist payload đếm cùng actor/time. Giá vốn, supplier và note bị che khỏi audit chung. `InventoryMutation` stage movement và `StorefrontInvalidate` ở mốc 0 ↔ dương. Không thêm producer alert.

## Nghiệm thu và bàn giao

AC-P1-04 dùng hai phiếu 10 × 100.000 và 10 × 130.000, kỳ vọng 115.000 VND và ledger tổng 20. AC-P1-05 dùng phiếu nhiều dòng chứa hàng độc bản đã có 1, mọi dòng rollback. AC-P1-06 đọc JSON và replay bằng staff thiếu quyền cost. AC-P1-08 xác minh đếm 5 lúc hệ thống 6, biến động damage riêng giảm còn 5, duyệt delta −1 cho 4. Ca kiểm kê chồng lấn kiểm cả chính sách chặn và hủy phiên submitted.

P1-10 dùng cùng ledger và phép so sánh availability để nối producer/consumer alert và đối soát. P1-13 dùng list/detail, token xmin, lỗi từng variant và form nhập giá tách quyền xem giá. Khi main tích hợp P1-14, giải quyết cùng điểm đăng ký API và sinh lại OpenAPI/client trên base được review. Không có rollback DDL. Rollback code giữ dữ liệu chứng từ và ledger, xử lý nghiệp vụ đã ghi sai bằng điều chỉnh có audit.

## Kiểm chứng local

Runner Linux SDK 10.0.403, giới hạn 4 CPU, PostgreSQL 17.11 Testcontainers với DB dùng một lần. Đối chiếu SHA-256 runner/worktree đủ 11 file backend và API client đã đổi. Snapshot đầu vào `9D7685A987BFF0A9B49B5E80DE1FC2117CD3C644D2628E06DD39D06D6DCC1F99`. Log, script và TRX trong `artifacts/p1-09/`. Frontend không có delta. Tái sử dụng frontend CI tại base.

| Lệnh | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warning/error |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS |
| `dotnet test backend/tests/TeaPremium.UnitTests --no-build --no-restore --filter FullyQualifiedName~StockRulesTests --logger trx --results-directory /evidence/unit` | 23/23 PASS |
| `dotnet test backend/tests/TeaPremium.ArchitectureTests --no-build --no-restore --logger trx --results-directory /evidence/architecture` | 34/34 PASS |
| `dotnet tool restore` và `dotnet tool run dotnet-ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS, không model drift. Connection design-time loopback port 1 |
| `corepack pnpm --filter @teapremium/api-client install --frozen-lockfile`, `corepack pnpm generate:api`, `corepack pnpm check:api`, `corepack pnpm --filter @teapremium/api-client typecheck` | PASS |
| `node artifacts/p1-09/contract.mjs` | PASS. Giữ 71 operation và các schema baseline, gồm Pricing và hai multipart import. Thêm 14 operation, tổng 85. Header idempotency bắt buộc, cost optional theo quyền, replay trả 3 field an toàn. |

Focused PG17 **155/155 PASS**, 0 fail/skip. Gồm P1-09, Inventory P1-08 kể cả bulk rebuild, schema parity, Catalog, Pricing, Product Import và P0-07 idempotency. Kiểm tranh khóa qua quan sát backend PostgreSQL, lỗi tại COMMIT và hủy sau flush, rồi kiểm rollback cả receipt, ledger, audit, outbox và replay record.

```sh
dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter 'FullyQualifiedName~InventoryApiTests|FullyQualifiedName~CatalogInventoryParityTests|FullyQualifiedName~SalePrice|FullyQualifiedName~ProductApiTests|FullyQualifiedName~ProductImport|FullyQualifiedName~IdempotencyTests|FullyQualifiedName~CatalogReferenceApiTests' --logger trx --results-directory /evidence/focused
```

Full backend chạy một lượt trên candidate cuối. **1.168/1.168 PASS**, 0 fail, 0 skip. Gồm 209 unit, 34 architecture, 925 integration. Script kiểm đủ ba TRX PASS.

```sh
dotnet test backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory /evidence/full
pwsh -NoProfile -File scripts/check-test-results.ps1 -Directory /evidence/full
```

## Candidate G3 ban đầu

**READY_FOR_LOCAL_G4: YES**, Tier A, cùng worktree. Fetch cuối xác nhận HEAD, merge base và origin/main đều `667274cfcc4c7aae790100237ccc2de29a0484ee`. Index rỗng. [Manifest SHA-256](p1-09-candidate.sha256) ghi 12 file nội dung, gồm 5 tracked sửa và 7 untracked, cộng 1 file metadata manifest. Không phát hiện gap chặn P1-09. Khi tích hợp P1-14 cần xác minh lại base, điểm đăng ký API và hai file sinh.

**NOT RUN ở lần bàn giao ban đầu** gồm G4 độc lập, CI trên candidate local, full Admin E2E và thao tác dữ liệu thật.

## Focused G4 remediation

G4 độc lập ban đầu BLOCK Tier A. Giữ báo cáo lịch sử tại `D:/tra-hien-p1-09-g4-29e88e10/final-review.md`. HEAD, origin/main và merge base ở `667274cfcc4c7aae790100237ccc2de29a0484ee`. Không có base drift tại lúc bắt đầu. Đã kiểm raw SHA-256, raw Git blob và Git blob có filter của 12 file cùng manifest. Fingerprint cũ `29E88E1015B8FF259B6A334359B75E289C98EA388C9FD494C284557FE46777AA`. Bản sao `artifacts/p1-09/remediation/reviewed.tar` đã thử phục hồi đủ 13 file, SHA-256 `0C9A8A8912AD9EA92FFBDD45691F8AA23E5C14EBDF85CDD6BDC777345A123842`.

- **G4-F1 / 05 §1, §4.3, BR-INV-07/08/10.** Contract P1-13 dùng `lines?` có giá trị mảng, gồm cả mảng rỗng theo quy tắc draft. Create line cần giá VND nguyên không âm. PATCH line được bỏ giá hoặc gửi null khi giữ variant đã có, còn variant mới phải gửi giá. Số 0 là giá hợp lệ. Số lượng receipt và điều chỉnh là số nguyên, countedQty là số nguyên không âm, rowVersion vẫn là chuỗi opaque. DTO request tách theo create/PATCH, dùng metadata số strict của Pricing và transformer giới hạn ở schema request P1-09. HTTP tiếp tục từ chối chuỗi số, số lẻ, null ở lines, phần tử null và field không khai báo.
- **G4-F2 / 03 §1.** Owner duyệt bỏ trần 999. Mã cho P1-13 dùng `PN-yyMMdd-N` và `KK-yyMMdd-N`. Phần số được so theo giá trị, đệm tối thiểu ba chữ số, dùng `long` và cộng checked. Giữ khóa transaction theo ngày VN và loại, UNIQUE, mã bất biến và không tái sử dụng mã đã hủy. Rollback giao dịch cho phép lần tạo tiếp theo cấp mã chưa từng commit. Làm rõ đúng một dòng tại 03 §1. Schema, grants, dependency và giao thức khóa Inventory giữ nguyên.
- **Phạm vi kiểm chứng.** Tái sử dụng full G3 1.168/1.168 và bằng chứng G4 độc lập 225/225 PG17, 11/11 probe, 23/23 unit, 34/34 architecture cho phần giữ nguyên. Không lặp full suite vì delta chỉ sửa contract request và cấp mã chứng từ. Chạy lại các endpoint Inventory, kiểm P0-07 idempotency, schema parity và architecture trên candidate sửa. CI tại SHA publish vẫn là gate riêng. Rollback local bằng cách phục hồi file đúng phạm vi từ archive và bỏ các file mới theo manifest.

**Bằng chứng remediation.** Nhật ký và TRX ở `artifacts/p1-09/remediation/`. SHA-256 của `source.tar` đưa vào runner là `379F8C6F4207A620BE69EA988A5E4D6DBC97B93FE98A65FA9A729344F488EA0A`. Đã so byte SHA-256 của 13 file nội dung ngoài handoff với bản trong runner sau khi sinh contract. Sáu file gốc giữ nguyên SHA-256 gồm hai file Application, hai file Persistence và hai file kiểm thử Inventory cũ.

| Lệnh | Kết quả |
|---|---|
| `dotnet restore backend/TeaPremium.sln --locked-mode` | PASS |
| `dotnet build backend/TeaPremium.sln --no-restore -warnaserror` | PASS, 0 warning/error |
| `dotnet format backend/TeaPremium.sln --verify-no-changes --no-restore` | PASS |
| `dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~P109` | PASS 4/4 trên PG17. Một ca schema/HTTP, một ca biên và cấp đồng thời, hai ca ở/vượt giới hạn số. |
| `dotnet test backend/tests/TeaPremium.ArchitectureTests --no-build --no-restore` | PASS 34/34 |
| `dotnet tool run dotnet-ef migrations has-pending-model-changes --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure` | PASS, model khớp migration gần nhất |
| `corepack pnpm --filter @teapremium/api-client install --frozen-lockfile`, `corepack pnpm generate:api`, `corepack pnpm check:api`, `corepack pnpm --filter @teapremium/api-client typecheck` | PASS |
| `node artifacts/p1-09/remediation/check-contract.mjs` | PASS. Cả 85 operation definitions khớp bản đã review, toàn bộ schema của 71 operation nền và các response schema giữ nguyên. Kiểm riêng OpenAPI và TypeScript request P1-09. |

`dotnet test backend/tests/TeaPremium.IntegrationTests --no-build --no-restore --filter 'FullyQualifiedName~InventoryApiTests|FullyQualifiedName~IdempotencyTests|FullyQualifiedName~CatalogInventoryParityTests|FullyQualifiedName~SalePriceContractTests' --logger trx --results-directory /evidence/final-focused` PASS 80/80 trên PostgreSQL 17, gồm 4 ca remediation, các ca giữ giá vốn, replay/idempotency và rollback phiếu nhập.

**READY_FOR_FOCUSED_INDEPENDENT_G4: YES.** Cùng worktree `D:/tra-hien-p1-09`, branch `feature/p1-09-inventory-operations`. HEAD và origin/main ở implementation base, index rỗng. Manifest ghi 14 file nội dung (6 tracked sửa, 8 untracked) và tự nằm ngoài danh sách hash. Phần cấp mã duyệt các mã trùng tiền tố ngày/loại trong lúc giữ advisory lock để tìm hậu tố lớn nhất theo giá trị số.

**NOT RUN.** Full backend trên candidate mới, G4 độc lập sau remediation, CI tại SHA publish và Admin E2E. Không áp dụng dữ liệu production.
