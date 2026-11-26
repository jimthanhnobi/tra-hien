# P2-04: Interactions + hẹn chăm sóc + "Cần liên hệ hôm nay" (BR-CRM-06/07)

> **Immutable Implementation Record ([WORKFLOW.md](../WORKFLOW.md) §2.2):** Hồ sơ này ghi nhận phạm vi, baseline, traceability, quyết định đã duyệt, hợp đồng dùng chung, danh mục tài sản thay đổi và bằng chứng trước freeze cho `P2-04`. Mọi **Dynamic Task Status** (manifest SHA-256 của candidate này, log kiểm chứng gắn manifest sau freeze, verdict G4, đóng findings, published commit SHA, URL PR, kết quả 4 job GitHub Actions và trạng thái merge) được lưu riêng ngoài tệp tracked tại báo cáo bàn giao, PR body và `artifacts/`.

- **Tier:** `A` — xử lý trực tiếp văn bản tự do chứa PII/nội dung trao đổi khách hàng (`crm.interactions.content`, `follow_up_result` và thông tin liên hệ trên hàng đợi hẹn chăm sóc), phân quyền Admin (`customers.read` / `customers.write` vs `content` `403`, `AC-P2-09`), bảo vệ CSRF, đồng bộ khóa dòng `crm.customers` / `crm.interactions` và kiểm soát audit fail-closed che PII (`02-architecture.md` §5.4).
- **Approved BASE & Context:** `origin/main` SHA `137dca869891ab7b7c0bcd6e7ebd8d347bbf40fe` (đã gồm merge PR `#44` `P2-01`, PR `#45` `P2-02`, PR `#46` `P2-05`, PR `#47` `P2-13`, PR `#48` Knowledge Recovery, PR `#49`/`#50` Workflow V2, PR `#51` `P2-03` `feec578` và PR `#52` `P2-07` `137dca8`), branch `feature/p2-04-interactions-followups`, worktree `D:\tra-hien-p2-04`.
- **Readiness & Decision References:**
  - **Full G1 Audited Spec SHA:** `cadce575597738bf40ad7c45689482de5a5fbc3d` ([spec-inventory.md](../analysis/spec-inventory.md), [spec-gaps.md](../analysis/spec-gaps.md)); G2 nền tại [conformance-review.md](../architecture/conformance-review.md), [task-readiness.md](task-readiness.md) (mốc `B01` `dadc4435e8f115faaeb9e8511e56e09d5d517a8a`), kế thừa [p2-01-implementation.md](p2-01-implementation.md), [p2-02-implementation.md](p2-02-implementation.md) và [p2-03-implementation.md](p2-03-implementation.md).
  - **Kiểm diff `docs/spec/` trên `137dca8`:** từ `85218d1` (`P2-01`) tới `137dca8` (`origin/main`), `docs/spec/` chỉ có 1 commit `20031d4` (`P2-07`, amend §7–§9 Payments/Refunds); phần CRM (`crm.interactions`, `BR-CRM-06/07`, `05-api.md` §4.5) **không đổi**. `P2-04` thực hiện **0 chỉnh sửa `docs/spec/*`**.
  - **Quyết định `OWNER_APPROVED` (`2026-11-26`, `Clarification only` — ghi nhận tại [ADR-P2-01-phase2-contracts.md](../decisions/ADR-P2-01-phase2-contracts.md) §4.5):**
    - `GAP-P204-01 = A`: chỉ cho phép 6 `kind` thủ công (`inquiry`, `consultation`, `quote`, `follow_up`, `complaint`, `note`) và cấm `campaignId` trên `POST`/`PATCH .../interactions` (`kind = 'campaign_message'` → `400`); **cấm tuyệt đối mọi `PATCH`** trên dòng `kind == 'campaign_message'` sẵn có (`409 campaign_interaction_immutable`, không bypass kể cả khi chỉ sửa `content`/`orderId`/`followUpAt`), vẫn cho `GET` đủ 7 `kind` kèm `campaignId` và `POST .../complete-follow-up` nếu `follow_up_at != null`.
    - `ASSUMPTION-P204-01 = A`: `GET /api/v1/admin/follow-ups?due=overdue|today|week` dùng `BusinessCalendar` (`Asia/Ho_Chi_Minh`, UTC+7): `overdue` (`< clock.UtcNow`), `today` mặc định (`< ToUtc(vnToday.AddDays(1), 00:00)`), `week` (`< ToUtc(vnToday.AddDays(7), 00:00)`), sắp cố định `follow_up_at ASC, id ASC`, loại khách `erased`/`merged`, trả `isOverdue` và `customerContactStatus`.
    - `ASSUMPTION-P204-02 = A` (`BR-CRM-07`): luôn khóa dòng khách hàng cha `SELECT *, xmin FROM crm.customers WHERE id = @customerId FOR NO KEY UPDATE` trước khi ghi/sửa tương tác; `POST` (`kind != 'note'`) nâng theo `GREATEST(last_contacted_at, occurred_at)` (`kind == 'note'` không chạm `last_contacted_at`/`updated_at`); `PATCH` đổi `kind` hoặc `occurredAt` tính lại `MAX(occurred_at) WHERE customer_id = @id AND kind <> 'note'` (gồm cả `campaign_message`) trong cùng transaction dưới khóa cha; `complete-follow-up` khóa dòng interaction `FOR UPDATE` (tránh đóng trùng `409 follow_up_already_completed`) và không đổi `last_contacted_at`.
    - `ASSUMPTION-P204-03 = A`: chặn ghi trên khách `erased` (`409 customer_erased`) và `merged` (`409 customer_merged`), loại khỏi `follow-ups`, giữ `GET .../interactions` `200`; cho phép ghi tương tác dịch vụ trên khách `do_not_contact` và **bảo toàn tuyệt đối `contact_status = 'do_not_contact'`**; kiểm `orderId` tồn tại và thuộc `{id}` qua `ICustomerOrderQueries` (`400 order_not_found`/`order_customer_mismatch`) + savepoint bắt `23503 fk_interactions_order`; kiểm nghiêm ngặt `occurredAt <= clock.UtcNow` (không clock skew dương, bắt buộc offset UTC `Z`/`±HH:MM`, tỉnh lược trên `POST` mặc định `clock.UtcNow`); giữ `GET /api/v1/admin/customers/{id}/campaigns` ở `P5-02` (không tạo stub).
  - **Các mục chưa duyệt giữ nguyên trạng thái trong [ADR-P2-01-phase2-contracts.md](../decisions/ADR-P2-01-phase2-contracts.md):** `O5` (`IMPLEMENTED_BUT_NOT_CONFIRMED`), `O6a`/`O6b`/`O7` cho `P2-06`/`P2-08`/`P2-12` (`PROPOSED`/`UNRESOLVED`), `G27` diff, `G24`, `Q1`–`Q8`.

---

## 1. Phạm vi & Traceability

### 1.1 Nguồn đặc tả và ánh xạ mã định danh

- **Task ID:** `P2-04` ([07-roadmap.md](../spec/07-roadmap.md) dòng `89`, phụ thuộc `P2-02` đã merge).
- **Spec references (đối chiếu trực tiếp trên `137dca8`):**
  - [00-README.md](../spec/00-README.md) §1–§3 (`UTC timestamptz`, `Asia/Ho_Chi_Minh`, UUID v7, `DEC-01`, `DEC-14`..`DEC-17`, `DEC-30`).
  - [01-requirements.md](../spec/01-requirements.md) (`FR-CRM-03`, `FR-CRM-05`, `FR-CRM-06`, `FR-PLT-01`, `NFR-01`, `NFR-04`, `NFR-07`, `NFR-08`, `NFR-09`, `NFR-12`).
  - [02-architecture.md](../spec/02-architecture.md) §3 (ranh giới module), §5.1 (`IClock`/`BusinessCalendar`), §5.2 (`IUnitOfWork`), §5.4 (`[Audited]`/`AuditValuePolicy`), §5.9 (`ProblemDetails`/log không PII), §6–§7 (`AdminCookie`, CSRF, phân quyền).
  - [03-database.md](../spec/03-database.md) §1, §2, §3.5, §3.9, §4 (`tp_app`/`tp_readonly`).
  - [schema.sql](../spec/schema.sql) dòng `655-673` (`crm.interactions`, `ix_interactions_customer`, `ix_interactions_follow_up`, `ck_interactions_*`), `fk_interactions_order`.
  - [04-business-rules.md](../spec/04-business-rules.md) dòng `4` (`IClock`), `249` (`BR-CRM-05`), `250` (`BR-CRM-06`), `251` (`BR-CRM-07`), `258` (`BR-CRM-10`), `259` (`BR-CRM-11`), `268` (`BR-MKT-03`), `272` (`BR-MKT-07`).
  - [05-api.md](../spec/05-api.md) §1, §4.5 dòng `208-210`.
  - [07-roadmap.md](../spec/07-roadmap.md) dòng `89` (`P2-04`), `114` (`AC-P2-09`).

### 1.2 Trong phạm vi / Ngoài phạm vi

- **Trong phạm vi (`P2-04`):**
  1. Luật miền `TeaPremium.Domain.Crm.InteractionRules`: tập kênh/loại đọc & ghi thủ công, kiểm độ dài Unicode scalar (`Rune`) `1..4000` (từ chối `\0` và surrogate đơn lẻ), kiểm `occurred_at <= now` & `follow_up_at > occurred_at`, quy tắc `CountsTowardLastContacted`/`MaxLastContacted`.
  2. Năm endpoint Admin API (`05-api.md:208-210`):
     - `GET /api/v1/admin/customers/{id}/interactions` (`customers.read`).
     - `POST /api/v1/admin/customers/{id}/interactions` (`customers.write` + `RequireCsrfHeader`).
     - `PATCH /api/v1/admin/customers/{id}/interactions/{iid}` (`customers.write` + `RequireCsrfHeader`).
     - `POST /api/v1/admin/customers/{id}/interactions/{iid}/complete-follow-up` (`customers.write` + `RequireCsrfHeader`).
     - `GET /api/v1/admin/follow-ups?due=today|overdue|week` (`customers.read`).
  3. Mở rộng contract đọc chéo module `ICustomerOrderQueries` (`FindOrderReferenceAsync`, `ResolveOrderCodesAsync` trong `Infrastructure.Orders`) và thêm port chỉ-đọc `IStaffDirectory` (`EfStaffDirectory` trong `Infrastructure.Iam`, đăng ký tại `IamRegistration.cs`) bảo toàn tuyệt đối ranh giới module `CrmModuleBoundaryRule`.
  4. Tái sử dụng nguyên trạng cấu hình `[Audited]` trên `Interaction`, nhánh `or Interaction` tại `AuditSaveChangesInterceptor.cs:67` và nhánh `AuditValuePolicy("crm.interactions")` tại `AuditValuePolicy.cs:112-119` đã được `P2-03` merge trên `137dca8` (không trùng lặp, không làm thoái lui).
  5. Tái sinh `packages/api-client/openapi.json` và `packages/api-client/src/generated/schema.ts` qua `./scripts/verify-linux.ps1 -Mode api -WriteBack`.
- **Ngoài phạm vi:**
  - `GET /api/v1/admin/customers/{id}/campaigns` → `P5-02` (không tạo stub).
  - `kind = 'campaign_message'` tự sinh khi đánh dấu `"Đã gửi"` → `P5-03`; job `QualifyReferrals` → `P5-05`.
  - Gộp khách & ẩn danh khi xóa khách (`BR-CRM-05`, `BR-CRM-11`, bàn giao `R-P204-01` ẩn danh `follow_up_result`) → `P2-12`.
  - Giao diện Admin UI khách hàng 360° & hàng đợi hẹn chăm sóc (`apps/admin`) → `P2-14` (`0` thay đổi trong `apps/*`).

---

## 2. Hợp đồng API, Thiết kế & Shared Contracts

### 2.1 Năm endpoint và quy tắc lỗi

| Method & Path (`/api/v1/...`) | Quyền & Bảo vệ | Request / Query | Response | Mã lỗi chính |
|---|---|---|---|---|
| `GET admin/customers/{id}/interactions` | `AdminSecurity.Ready`, `customers.read` | `page=1`, `pageSize=20` (`1..100`), sort cố định `occurredAt DESC, id DESC` | `200 OK` `CatalogPage<CustomerInteractionView>` | `400`, `401`, `403`, `404 not_found` |
| `POST admin/customers/{id}/interactions` | `AdminSecurity.Ready`, `customers.write`, `RequireCsrfHeader` | Strict JSON `CreateCustomerInteractionRequest` (`channel`, `kind` ∈ 6 loại thủ công, `content`, `occurredAt?`, `orderId?`, `followUpAt?`; cấm `campaignId`, cấm duplicate/unknown keys) | `201 Created` `CustomerInteractionView` + `Location` | `400 validation_failed` (`invalid_channel`, `invalid_interaction_kind`, `invalid_content`, `occurred_at_in_future`, `follow_up_before_or_equal_occurred_at`, `order_not_found`, `order_customer_mismatch`, `csrf_invalid`), `401`, `403`, `404`, `409 customer_erased` / `customer_merged`, `415` |
| `PATCH admin/customers/{id}/interactions/{iid}` | `AdminSecurity.Ready`, `customers.write`, `RequireCsrfHeader` | Strict partial JSON `PatchCustomerInteractionRequest` (`≥ 1` khóa trong `{channel, kind, content, occurredAt, orderId, followUpAt}`; `null` chỉ hợp lệ cho `orderId`/`followUpAt`) | `200 OK` `CustomerInteractionView` | `400 validation_failed` (`empty_patch`, `unknown_field`, `invalid_interaction_kind`, `occurred_at_in_future`, `follow_up_before_or_equal_occurred_at`, `order_not_found`, `order_customer_mismatch`, `csrf_invalid`), `401`, `403`, `404`, `409 customer_erased` / `customer_merged` / `campaign_interaction_immutable` / `follow_up_already_completed`, `415` |
| `POST admin/customers/{id}/interactions/{iid}/complete-follow-up` | `AdminSecurity.Ready`, `customers.write`, `RequireCsrfHeader` | Strict JSON `CompleteFollowUpRequest` (`result` trimmed `1..4000` Unicode `Rune`, không `\0`) | `200 OK` `CustomerInteractionView` | `400 validation_failed` (`follow_up_result_required`, `invalid_follow_up_result`, `csrf_invalid`), `401`, `403`, `404`, `409 customer_erased` / `customer_merged` / `follow_up_not_scheduled` / `follow_up_already_completed`, `415` |
| `GET admin/follow-ups` | `AdminSecurity.Ready`, `customers.read` | `due=today\|overdue\|week` (tùy chọn, mặc định `today`; từ chối trùng tham số `due`), `page=1`, `pageSize=20` (`1..100`), sort cố định `followUpAt ASC, id ASC` | `200 OK` `CatalogPage<FollowUpItemView>` | `400 validation_failed` (`invalid_due_filter`), `401`, `403` |

### 2.2 Thứ tự khóa, Đồng thời & Ranh giới Module

- **Kiểm soát định dạng thời gian & độ phân giải microsecond PostgreSQL (`InteractionTimestampParser`):**
  - Mọi `occurredAt`/`followUpAt` dạng chuỗi phải khớp regex ISO-8601 nghiêm ngặt `\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:[Zz]|[+-]\d{2}:\d{2})\z` và vượt qua `DateTimeOffset.TryParse(..., RoundtripKind)` — từ chối chuỗi RFC 2822 (ví dụ `"Tue, 24 Nov 2026 10:00:00 +07:00"`), dấu cách thay `'T'`, hoặc thiếu offset UTC.
  - Kiểm `occurredAt <= clock.UtcNow` được chạy **trước** ở đầy đủ độ phân giải tick (`+1 tick` vẫn bị từ chối `400 occurred_at_in_future`, không có clock skew dương); sau đó chuẩn hóa `occurredAt` (gồm cả mặc định `clock.UtcNow` khi tỉnh lược trên `POST`), `followUpAt`, `createdAt`, `followUpDoneAt` về đúng độ phân giải microsecond của PostgreSQL `timestamptz` (`TruncateToMicroseconds`) **trước** khi kiểm `follow_up_at > occurred_at` và trước khi lưu xuống DB (`crm.interactions` và `crm.customers.last_contacted_at`) để bất biến luôn nhất quán tuyệt đối sau vòng đọc/ghi PostgreSQL.
- **Thứ tự khóa nghiêm ngặt chống deadlock:** Mọi thao tác ghi (`POST`, `PATCH`, `complete-follow-up`) đều chạy trong transaction `IUnitOfWork` và khóa theo đúng thứ tự:
  1. `SELECT *, xmin FROM crm.customers WHERE id = @customerId FOR NO KEY UPDATE` (kiểm tra tồn tại + `erased`/`merged`, tuần tự hóa mọi cập nhật `last_contacted_at`).
  2. Trên `PATCH` và `complete-follow-up`: `SELECT * FROM crm.interactions WHERE id = @iid AND customer_id = @customerId FOR UPDATE` (kiểm cross-customer `404`, immutability của `campaign_message`, và chống đua đóng hẹn trùng).
- **Tính lại `last_contacted_at` (`BR-CRM-07`):**
  - `POST`: `kind == 'note'` không chạm `customer.LastContactedAt` / `customer.UpdatedAt`; các loại khác áp `GREATEST(last_contacted_at, occurred_at)` và bảo toàn tuyệt đối `contact_status`.
  - `PATCH`: chỉ khi `kind` hoặc `occurredAt` thay đổi mới tính lại `MAX(occurred_at)` trên toàn bộ các dòng `id != @iid AND kind <> 'note'` (gồm cả `campaign_message`) kết hợp dòng đang sửa trong cùng transaction dưới khóa cha.
  - `complete-follow-up`: chỉ cập nhật `follow_up_done_at` và `follow_up_result`, không sửa `last_contacted_at`.
- **Savepoint chống đua FK:** `EfInteractionStore.CreateAsync` và `UpdateAsync` bọc `SaveChangesAsync` trong savepoint, bắt `PostgresException` `23503` trên `fk_interactions_order` → rollback savepoint và trả `400 validation_failed` (`order_not_found`).
- **Tương thích `CrmModuleBoundaryRule` & `R-P201-03`:**
  - `TeaPremium.Infrastructure.Crm` (`EfInteractionStore`, `EfInteractionQueries`, `CrmRegistration`) chỉ phụ thuộc vào `Customer`/`Interaction` (schema `crm`) và các port Application (`IStaffDirectory`, `ICustomerOrderQueries`).
  - `EfStaffDirectory` nằm tại `TeaPremium.Infrastructure.Iam` và đăng ký trong `IamRegistration.cs`, dùng tra cứu từ điển left-lookup (`staffDisplayName: string?`) để không bao giờ làm rơi dòng tương tác hệ thống (`R-P201-03`).
  - `EfCustomerOrderQueries` nằm tại `TeaPremium.Infrastructure.Orders` (đăng ký trong `OrdersRegistration.cs`).

---

## 3. Danh mục tài sản thay đổi (24 files)

- **Migration / Recovery:** `none` (`0` migration mới, `0` thay đổi `AppDbContextModelSnapshot`, `0` thay đổi `docs/spec/*`, `0` thay đổi `apps/*`, `0` thay đổi `artifacts/p2-04/pre-integration/*`). Bảng `crm.interactions`, các CHECK/FK/index và quyền `tp_app`/`tp_readonly` đã có từ `P2-01`; khôi phục bằng cách revert commit code + OpenAPI/client.
- **Tệp mới (`14` files):**
  1. `backend/src/TeaPremium.Domain/Crm/InteractionRules.cs`
  2. `backend/src/TeaPremium.Application/Iam/IStaffDirectory.cs`
  3. `backend/src/TeaPremium.Application/Crm/InteractionContracts.cs`
  4. `backend/src/TeaPremium.Application/Crm/InteractionValidators.cs`
  5. `backend/src/TeaPremium.Application/Crm/InteractionHandlers.cs`
  6. `backend/src/TeaPremium.Infrastructure/Iam/EfStaffDirectory.cs`
  7. `backend/src/TeaPremium.Infrastructure/Crm/EfInteractionStore.cs`
  8. `backend/src/TeaPremium.Infrastructure/Crm/EfInteractionQueries.cs`
  9. `backend/src/TeaPremium.Api/Crm/InteractionEndpoints.cs`
  10. `backend/src/TeaPremium.Api/Crm/InteractionOpenApi.cs`
  11. `backend/tests/TeaPremium.UnitTests/InteractionRulesAndCalendarTests.cs`
  12. `backend/tests/TeaPremium.UnitTests/InteractionValidatorsTests.cs`
  13. `backend/tests/TeaPremium.IntegrationTests/CustomerInteractionsAndFollowUpsApiTests.cs`
  14. `docs/plans/p2-04-implementation.md`
- **Tệp chỉnh sửa (`10` files, gồm `2` file API contract tái sinh tự động):**
  15. `backend/src/TeaPremium.Application/Orders/Contracts/ICustomerOrderQueries.cs`
  16. `backend/src/TeaPremium.Infrastructure/Iam/IamRegistration.cs`
  17. `backend/src/TeaPremium.Infrastructure/Orders/EfCustomerOrderQueries.cs`
  18. `backend/src/TeaPremium.Infrastructure/Crm/CrmRegistration.cs`
  19. `backend/src/TeaPremium.Api/Program.cs`
  20. `backend/tests/TeaPremium.IntegrationTests/AuditValuePolicyTests.cs`
  21. `backend/tests/TeaPremium.IntegrationTests/OpenApiTests.cs`
  22. `packages/api-client/openapi.json` *(tái sinh qua `-Mode api -WriteBack`)*
  23. `packages/api-client/src/generated/schema.ts` *(tái sinh qua `-Mode api -WriteBack`)*
  24. `docs/decisions/ADR-P2-01-phase2-contracts.md` *(chỉ ghi nhận `GAP-P204-01 = A` và `ASSUMPTION-P204-01/02/03 = A` là `OWNER_APPROVED 2026-11-26, Clarification only`)*

---

## 4. Ma trận kiểm thử & Bằng chứng trước Freeze (`0` skipped)

### 4.1 Unit Tests (`TeaPremium.UnitTests` — `15` test methods mới)

- **`InteractionRulesAndCalendarTests.cs` (`8` methods):**
  1. `BrCrm06IsValidScheduleAcceptsBackdatedAndCurrentOccurredAtAndRejectsFutureTimestamp`
  2. `BrCrm06IsValidScheduleRequiresFollowUpStrictlyAfterOccurredAt`
  3. `BrCrm06ContentAndFollowUpResultRuneBoundsMatchPostgresCharacterLength`
  4. `BrCrm07LastContactedRulesExcludeNoteAndTakeGreatestTimestamp`
  5. `BrCrm06FollowUpWindowTodayAndOverdueRespectAsiaHoChiMinhDayRolloverAt1700Utc`
  6. `FollowUpWindowWeekComputesSevenVietnamDaysBoundaryAcrossMonthAndYearEnd`
  7. `InteractionTimestampParserRejectsRfc2822SpaceSeparatorAndMissingTimezoneOffset`
  8. `BrCrm06TimestampMicrosecondNormalizationAlignsFollowUpInvariantWhileKeepingStrictFutureTickCheck`
- **`InteractionValidatorsTests.cs` (`7` methods):**
  9. `CreateInteractionValidatorEnforcesChannelManualKindContentAndExplicitUtcOffsets`
  10. `CreateInteractionValidatorEmitsOnlyCleanMachineCodesWhenRequiredFieldsAreNull`
  11. `PatchInteractionValidatorRejectsEmptyPatchUnknownFieldsCampaignMessageAndCompletionFields`
  12. `BrCrm06CompleteFollowUpValidatorRequiresNonBlankTrimmedResultWithoutNulBytes`
  13. `ListFollowUpsValidatorEnforcesDueEnumAndPaginationBounds`
  14. `ListCustomerInteractionsValidatorEnforcesCustomerIdAndPaginationBounds`
  15. `InteractionJsonBindingContractsDisallowDuplicateAndUnmappedProperties`

### 4.2 Integration, Policy & OpenAPI Contract Tests (`TeaPremium.IntegrationTests` — `15` test methods mới + `1` policy theory mới + `1` OpenAPI contract mở rộng)

- **`AuditValuePolicyTests.cs` (`+1` method, bảo toàn nguyên các test `P2-03`):**
  1. `CrmInteractionsSafeFieldsRejectSpoofedPiiValuesAndAllowAllValidChannelsAndKinds`
- **`OpenApiTests.cs` (cập nhật `115 → 120` operations, bảo toàn toàn bộ kiểm chứng `P2-03` và `P2-07`):**
  2. `StoreCatalogPublishesExactlyNineReadOperations` (kiểm 5 endpoint `P2-04`, xác nhận chưa mở `/api/v1/admin/customers/{id}/campaigns`, kiểm enum `due`/`channel`/`kind` 6 ghi / 7 đọc, kiểm `additionalProperties: false`, không lộ `cost`/`profit`/`password`/`securityStamp`).
- **`CustomerInteractionsAndFollowUpsApiTests.cs` (`15` methods):**
  3. `BrCrm06CreateAndListCustomerInteractionsSupportsBackdatingPaginationAndStaffResolution`
  4. `BrCrm06CreateAndPatchInteractionRejectFutureOccurredAtInvalidFollowUpOrderAndStrictJsonViolations`
  5. `BrCrm07CreateAndPatchInteractionUpdatesLastContactedAtExceptNoteUsingGreatestAndRecompute`
  6. `BrCrm07ConcurrentPostAndPatchWithSeededCampaignMessageMaintainExactLastContactedAtWithoutLostUpdates`
  7. `InteractionOrderLinkAcceptsCustomerOwnOrderAndRejectsMissingOrOtherCustomerOrder` *(kèm kiểm chứng trực tiếp savepoint `23503 fk_interactions_order`)*
  8. `PatchInteractionUpdatesPartialFieldsAndCancelsPendingFollowUpWhenSetToNull`
  9. `CampaignMessageKindIsRejectedOnManualWriteImmutableOnAnyPatchWhileReadableAndCompletable`
  10. `BrCrm06CompleteFollowUpRequiresResultSetsDoneAtHandlesConcurrencyAndBlocksPostCompletionMutation`
  11. `BrCrm06FollowUpsTodayOverdueAndWeekFilterByVietnamCalendarAndSortOverdueFirst`
  12. `ErasedAndMergedCustomersRejectInteractionMutationsWith409WhileDoNotContactPreservesStatusAndReadsSucceed`
  13. `InteractionCrossCustomerRouteMismatchReturns404WithoutSideEffects`
  14. `AcP209ContentRoleUnauthenticatedUnreadyHttpMissingCsrfAndUnsupportedMediaTypeAreRejectedAcrossAllEndpoints`
  15. `AuditLogsRedactInteractionContentAndFollowUpResultWithZeroRawPiiInDatabaseOrLogs`
  16. `RealTpAppDatabaseRoleExecutesAllInteractionAndFollowUpQueriesAndMutations`
  17. `G4R0RegressionRejectsRfc2822TimestampsEmitsCleanMissingFieldCodesAndNormalizesSubMicrosecondSchedule`

### 4.3 Bằng chứng trước Freeze

- `./scripts/verify-linux.ps1 -Mode api -WriteBack` → `RESULT=PASS`, `coverage=API`, `exit_code=0` (`0` warnings, `0` errors; ghi lại và kiểm SHA-256 `openapi.json` & `schema.ts`).
- `./scripts/verify-linux.ps1 -Filter "FullyQualifiedName~Interaction|FullyQualifiedName~FollowUp|FullyQualifiedName~CompiledArchitectureTests"` → `RESULT=FOCUSED_PASS`, `coverage=FOCUSED_BACKEND`, `exit_code=0` (`100` unit, `38` architecture gồm `CrmModuleBoundaryRule`, `34` integration, `OpenAPI/client are current.`).
- Lượt kiểm chứng cuối gắn `candidate.manifest` (`-Mode full -Manifest artifacts/p2-04/candidate.manifest`, `coverage=FULL_ALL`) và `manifest_sha256` thuộc **Dynamic Task Status**, được ghi tại báo cáo bàn giao G3 và `artifacts/verify-linux/`.

---

## 5. Giới hạn đã biết, Rủi ro còn lại và Bàn giao liên task

1. **`R-P201-03` (`staff_id` không có FK sang `iam.staff_users`):** Đã giảm thiểu bằng tra cứu left-lookup qua `IStaffDirectory` (`staffDisplayName: string?`), có test tích hợp kiểm chứng trực tiếp cho tương thích `P5-05`.
2. **`R-P204-01` (Bàn giao `P2-12` — `EraseCustomer` `BR-CRM-11`):** Khi xóa dữ liệu khách hàng ở `P2-12`, cần ẩn danh đồng thời `crm.interactions.follow_up_result` (và đóng/xóa hẹn mở `follow_up_at` nếu chưa hoàn thành) bên cạnh `content = '[Đã xóa]'` để không còn sót văn bản tự do chứa PII.
3. **Bàn giao `P2-14` / `P5-02` / `P5-03`:** `P2-14` tiêu thụ trực tiếp 5 endpoint và schema TypeScript của `P2-04`; `P5-02` mở `GET /api/v1/admin/customers/{id}/campaigns`; `P5-03` tạo dòng `kind = 'campaign_message'` với `campaign_id` và tương thích hoàn toàn với công thức `MAX(occurred_at) WHERE kind <> 'note'`.
4. **Kiểm tra chưa chạy ở local:** GitHub Actions CI 4 job và `Docker regression builds` chưa chạy tại giai đoạn local `G3` (`NOT_RUN`, sẽ xác minh tại `publish-pr` sau khi qua Independent G4 PASS và có lệnh Owner).
