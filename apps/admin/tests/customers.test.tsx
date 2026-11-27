import assert from "node:assert/strict";
import { test } from "node:test";
import { AdminApi, ApiError, isConcurrencyConflict } from "../src/lib/api";
import {
  AdministrativeGeoClient,
  isAdministrativeDataNotReady,
  selectWardsForProvince
} from "../src/lib/administrative-geo";
import { CustomersApi, type CustomerAddress, type CustomerDetail, type CustomerInteraction } from "../src/lib/customers-api";
import {
  buildCorrectIdentityPayload,
  buildCreateAddressPayload,
  buildCreateCustomerPayload,
  buildCreateInteractionPayload,
  buildPatchAddressPayload,
  buildPatchCustomerPayload,
  buildPatchInteractionPayload,
  buildSafeZaloUrl,
  canApplyCustomerImport,
  canCompleteFollowUp,
  canEditInteraction,
  countImportWarnings,
  customerImportApplyResult,
  customerImportDryRunSuccess,
  customerImportInitialState,
  customerImportSelectFile,
  customerPermissions,
  extractExistingCustomerId,
  formatPhoneDisplay,
  formatVnd,
  isImmutableCustomer,
  manualInteractionKinds,
  nextVnDate,
  normalizeVnMobilePhone,
  parseMinLtvVnd,
  toLocalPhone,
  validateDeliveredDateRange,
  validateFollowUpResult,
  validateTagForm
} from "../src/lib/customers-forms";

const NativeRequest = globalThis.Request;
globalThis.Request = class extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    super(typeof input === "string" ? new URL(input, "https://admin.example.invalid") : input, init);
  }
};

const custA = "00000000-0000-7000-8000-000000001001";
const custB = "00000000-0000-7000-8000-000000001002";
const addrA = "00000000-0000-7000-8000-000000002001";
const ixA = "00000000-0000-7000-8000-000000003001";
const tagA = "00000000-0000-7000-8000-000000004001";
const orderA = "00000000-0000-7000-8000-000000005001";
const stamp = "2026-11-26T03:00:00Z";

function sampleDetail(overrides: Partial<CustomerDetail> = {}): CustomerDetail {
  return {
    id: custA,
    code: "KH001001",
    fullName: "Nguyễn Minh Quân",
    phoneE164: "+84912345678",
    email: "quan@example.invalid",
    zaloName: "Quân Trà",
    customerGroup: "retail",
    isVip: false,
    origin: "admin",
    acquisitionSource: "zalo_personal",
    referredByCustomerId: null,
    contactStatus: "active",
    noteSummary: "Thích ấm tử sa nhỏ",
    mergedIntoCustomerId: null,
    erasedAt: null,
    createdAt: stamp,
    updatedAt: stamp,
    rowVersion: "cust-v1",
    stats: {
      hadLegacyPurchase: false,
      deliveredOrdersCount: 2,
      lifetimeValueVnd: 3_500_000,
      firstDeliveredAt: "2026-10-01T08:00:00Z",
      lastDeliveredAt: "2026-11-20T08:00:00Z",
      lastContactedAt: "2026-11-25T08:00:00Z"
    },
    tags: [{ id: tagA, name: "Tử sa", color: "#214f40" }],
    addresses: [],
    interests: [],
    consents: [],
    accountSummary: {
      hasAccount: false,
      status: null,
      phoneVerifiedAt: null,
      emailVerifiedAt: null,
      historyUnlockedAt: null,
      lastLoginAt: null
    },
    ...overrides
  };
}

test("P2-14-U01 derives customer capabilities strictly from permissions plus owner check for identity fields", () => {
  // Even with owner role, read-only permissions grant ZERO write capabilities.
  const readOnly = customerPermissions(["customers.read"], ["owner"]);
  assert.equal(readOnly.canRead, true);
  assert.equal(readOnly.canWrite, false);
  assert.equal(readOnly.canCreate, false);
  assert.equal(readOnly.canImport, false);
  assert.equal(readOnly.canManageTags, false);
  assert.equal(readOnly.canManageAddresses, false);
  assert.equal(readOnly.canCorrectPhone, false);
  assert.equal(readOnly.canCorrectOwnerIdentity, false);

  // Sales staff with read+write can correct phone, manage tags/addresses/import, but NOT owner identity fields.
  const sales = customerPermissions(["customers.read", "customers.write"], ["sales"]);
  assert.equal(sales.canWrite, true);
  assert.equal(sales.canCreate, true);
  assert.equal(sales.canImport, true);
  assert.equal(sales.canManageTags, true);
  assert.equal(sales.canManageAddresses, true);
  assert.equal(sales.canCorrectPhone, true);
  assert.equal(sales.canCorrectOwnerIdentity, false);

  // Owner with read+write gets full correct-identity capability (BR-CRM-04).
  const owner = customerPermissions(["customers.read", "customers.write"], ["owner"]);
  assert.equal(owner.canCorrectOwnerIdentity, true);

  const none = customerPermissions([], ["owner"]);
  assert.equal(none.canRead, false);
  assert.equal(none.canWrite, false);
});

test("P2-14-U02 normalizes VN mobile phones, formats display and generates safe Zalo URLs only", () => {
  assert.deepEqual(normalizeVnMobilePhone("0912 345 678"), { valid: true, e164: "+84912345678" });
  assert.deepEqual(normalizeVnMobilePhone("(038)-123.4567"), { valid: true, e164: "+84381234567" });
  assert.deepEqual(normalizeVnMobilePhone("84881234567"), { valid: true, e164: "+84881234567" });
  assert.deepEqual(normalizeVnMobilePhone("+84771234567"), { valid: true, e164: "+84771234567" });
  assert.deepEqual(normalizeVnMobilePhone("   "), { valid: true, e164: null });

  for (const invalid of ["02438251234", "0123456789", "+84000000000", "091234", "abc", "0912345678\0"]) {
    assert.equal(normalizeVnMobilePhone(invalid).valid, false, `Expected invalid phone: ${invalid}`);
  }

  assert.equal(toLocalPhone("+84912345678"), "0912345678");
  assert.equal(formatPhoneDisplay("+84912345678"), "0912 345 678");
  assert.equal(formatPhoneDisplay(null), "—");
  assert.equal(formatVnd(3_500_000), "3.500.000 đ");

  assert.equal(buildSafeZaloUrl("+84912345678"), "https://zalo.me/0912345678");
  assert.equal(buildSafeZaloUrl("+84000000000"), null);
  assert.equal(buildSafeZaloUrl("javascript:alert(1)"), null);
  assert.equal(buildSafeZaloUrl(null), null);
});

test("P2-14-U03 validates create, patch and correct-identity payloads (BR-CRM-01, BR-CRM-04)", () => {
  const missingContact = buildCreateCustomerPayload({
    fullName: "Khách thiếu liên hệ",
    phone: "",
    zaloName: "   ",
    email: "",
    customerGroup: "retail",
    isVip: false,
    acquisitionSource: "unknown",
    contactStatus: "active",
    hadLegacyPurchase: false,
    noteSummary: "",
    tagIds: []
  });
  assert.ok(missingContact.errors?.contact);

  const validCreate = buildCreateCustomerPayload({
    fullName: "  Trần Thu Hà  ",
    phone: "0912.345.678",
    zaloName: "Hà Trà",
    email: "ha@example.invalid",
    customerGroup: "wholesale",
    isVip: true,
    acquisitionSource: "zalo_personal",
    contactStatus: "active",
    hadLegacyPurchase: true,
    noteSummary: "Khách sỉ",
    tagIds: [tagA]
  });
  assert.equal(validCreate.errors, undefined);
  assert.equal(validCreate.payload?.phone, "+84912345678");
  assert.equal(validCreate.payload?.fullName, "Trần Thu Hà");

  // Zalo-only customer cannot clear zaloName on PATCH.
  const zaloOnly = sampleDetail({ phoneE164: null, zaloName: "Khách Zalo" });
  const clearZalo = buildPatchCustomerPayload(zaloOnly, {
    fullName: zaloOnly.fullName,
    zaloName: "",
    email: "",
    customerGroup: "retail",
    isVip: false,
    contactStatus: "active",
    noteSummary: ""
  });
  assert.ok(clearZalo.errors?.zaloName);

  // Correct-identity for sales NEVER includes owner-only fields even if changed in draft.
  const detail = sampleDetail();
  const salesCorrection = buildCorrectIdentityPayload(
    detail,
    {
      phone: "0987654321",
      origin: "legacy_import",
      acquisitionSource: "legacy",
      hadLegacyPurchase: true,
      reason: "Khách đổi số điện thoại mới"
    },
    false
  );
  assert.equal(salesCorrection.errors, undefined);
  assert.deepEqual(salesCorrection.payload, {
    rowVersion: "cust-v1",
    reason: "Khách đổi số điện thoại mới",
    phone: "+84987654321"
  });
  assert.equal("origin" in salesCorrection.payload!, false);
  assert.equal("acquisitionSource" in salesCorrection.payload!, false);
  assert.equal("hadLegacyPurchase" in salesCorrection.payload!, false);

  // Owner changing only origin/legacy without changing phone omits phone cleanly.
  const ownerCorrection = buildCorrectIdentityPayload(
    detail,
    {
      phone: "0912345678",
      origin: "legacy_import",
      acquisitionSource: "legacy",
      hadLegacyPurchase: true,
      reason: "Xác nhận khách cũ từ sổ tay"
    },
    true
  );
  assert.equal(ownerCorrection.errors, undefined);
  assert.deepEqual(ownerCorrection.payload, {
    rowVersion: "cust-v1",
    reason: "Xác nhận khách cũ từ sổ tay",
    origin: "legacy_import",
    acquisitionSource: "legacy",
    hadLegacyPurchase: true
  });
  assert.equal("phone" in ownerCorrection.payload!, false);

  // Immutable checks
  assert.equal(isImmutableCustomer(sampleDetail({ contactStatus: "erased" })), true);
  assert.equal(isImmutableCustomer(sampleDetail({ mergedIntoCustomerId: custB })), true);
  assert.equal(isImmutableCustomer(sampleDetail()), false);
});

test("P2-14-U04 validates address forms and omits unchanged location on PATCH for historical locations", () => {
  const created = buildCreateAddressPayload(
    {
      label: "Nhà riêng",
      receiverName: "Nguyễn Minh Quân",
      receiverPhone: "0912 345 678",
      provinceCode: "01",
      wardCode: "00001",
      wardNameText: "",
      addressLine: "Số 12 Tràng Tiền",
      isDefault: true
    },
    "cust-v1"
  );
  assert.equal(created.errors, undefined);
  assert.equal(created.payload?.receiverPhone, "+84912345678");
  assert.equal(created.payload?.wardCode, "00001");
  assert.equal(created.payload?.wardNameText, null);

  // Fallback wardNameText allowed when wardCode is empty.
  const textFallback = buildCreateAddressPayload({
    label: "",
    receiverName: "Nguyễn Minh Quân",
    receiverPhone: "0912345678",
    provinceCode: "01",
    wardCode: "",
    wardNameText: "Phường Hoàn Kiếm",
    addressLine: "Số 12 Tràng Tiền",
    isDefault: false
  });
  assert.equal(textFallback.errors, undefined);
  assert.equal(textFallback.payload?.wardCode, null);
  assert.equal(textFallback.payload?.wardNameText, "Phường Hoàn Kiếm");

  // Editing receiverName on an inactive historical location omits provinceCode/wardCode on PATCH.
  const inactiveAddr: CustomerAddress = {
    id: addrA,
    customerId: custA,
    label: "Nhà cũ",
    receiverName: "Tên cũ",
    receiverPhone: "+84912345678",
    provinceCode: "99",
    provinceName: "Tỉnh Cũ",
    wardCode: "99001",
    wardName: "Phường Cũ",
    wardNameText: null,
    addressLine: "Đường Cũ",
    isDefault: false,
    isLocationActive: false,
    createdAt: stamp,
    updatedAt: stamp
  };
  const patchUnchangedLocation = buildPatchAddressPayload(
    inactiveAddr,
    {
      label: "Nhà cũ",
      receiverName: "Tên mới",
      receiverPhone: "0912345678",
      provinceCode: "99",
      wardCode: "99001",
      wardNameText: "",
      addressLine: "Đường Cũ",
      isDefault: true
    },
    "cust-v1"
  );
  assert.equal(patchUnchangedLocation.errors, undefined);
  assert.deepEqual(patchUnchangedLocation.payload, {
    rowVersion: "cust-v1",
    receiverName: "Tên mới",
    isDefault: true
  });
  assert.equal("provinceCode" in patchUnchangedLocation.payload!, false);
  assert.equal("wardCode" in patchUnchangedLocation.payload!, false);
});

test("P2-14-U05 enforces manual interaction kinds, campaign_message immutability and follow-up rules (GAP-P204-01 = A)", () => {
  assert.equal(manualInteractionKinds.length, 6);
  assert.equal((manualInteractionKinds as readonly string[]).includes("campaign_message"), false);

  const campaignIx: CustomerInteraction = {
    id: ixA,
    customerId: custA,
    staffId: custB,
    staffDisplayName: "Hệ thống",
    channel: "zalo_personal",
    kind: "campaign_message",
    content: "Mời khách xem ấm mới",
    occurredAt: "2026-11-25T02:00:00Z",
    orderId: null,
    orderCode: null,
    campaignId: tagA,
    followUpAt: "2026-11-26T02:00:00Z",
    followUpDoneAt: null,
    followUpResult: null,
    createdAt: "2026-11-25T02:00:00Z"
  };

  // campaign_message is NOT editable via PATCH, but pending follow-up CAN be completed!
  assert.equal(canEditInteraction(campaignIx), false);
  assert.equal(canCompleteFollowUp(campaignIx), true);
  assert.equal(canCompleteFollowUp({ ...campaignIx, followUpDoneAt: stamp }), false);
  assert.equal(canEditInteraction({ kind: "consultation" }), true);

  const nowMs = Date.parse("2026-11-26T10:00:00+07:00");
  const futureOccurred = buildCreateInteractionPayload(
    {
      channel: "zalo_personal",
      kind: "consultation",
      content: "Tư vấn ấm",
      occurredAt: "2026-11-26T10:30",
      followUpAt: "",
      orderId: ""
    },
    nowMs
  );
  assert.ok(futureOccurred.errors?.occurredAt);

  const badFollowUp = buildCreateInteractionPayload(
    {
      channel: "phone",
      kind: "follow_up",
      content: "Gọi hỏi thăm",
      occurredAt: "2026-11-26T09:00",
      followUpAt: "2026-11-26T08:59",
      orderId: ""
    },
    nowMs
  );
  assert.ok(badFollowUp.errors?.followUpAt);

  const validIx = buildCreateInteractionPayload(
    {
      channel: "zalo_personal",
      kind: "quote",
      content: "Báo giá ấm Thạch Biều",
      occurredAt: "2026-11-26T09:00",
      followUpAt: "2026-11-27T09:00",
      orderId: orderA
    },
    nowMs
  );
  assert.equal(validIx.errors, undefined);
  assert.equal(validIx.payload?.occurredAt, "2026-11-26T09:00:00+07:00");
  assert.equal(validIx.payload?.followUpAt, "2026-11-27T09:00:00+07:00");

  // Patching a manual interaction with completed follow-up omits followUpAt so backend does not reject 409.
  const doneIx: CustomerInteraction = {
    ...campaignIx,
    kind: "consultation",
    campaignId: null,
    followUpDoneAt: "2026-11-26T03:00:00Z",
    followUpResult: "Khách đã chốt"
  };
  const patchedDone = buildPatchInteractionPayload(
    doneIx,
    {
      channel: "phone",
      kind: "consultation",
      content: "Cập nhật nội dung",
      occurredAt: "2026-11-25T09:00",
      followUpAt: "2026-11-28T09:00",
      orderId: ""
    },
    nowMs
  );
  assert.equal(patchedDone.errors, undefined);
  assert.equal("followUpAt" in patchedDone.payload!, false);

  assert.ok(validateFollowUpResult("   "));
  assert.equal(validateFollowUpResult("Khách hẹn tuần sau"), null);
});

test("P2-14-U06 enforces legacy customer import dry-run -> apply state machine and warning counters (BR-CRM-03, O4)", () => {
  const badExt = customerImportSelectFile(new File(["x"], "customers.txt", { type: "text/plain" }));
  assert.equal(badExt.file, null);
  assert.ok(badExt.fileError);

  const oversized = new File([new Uint8Array(5 * 1024 * 1024 + 1)], "huge.csv", { type: "text/csv" });
  assert.ok(customerImportSelectFile(oversized).fileError);

  const file = new File(["full_name,phone\nA,0912345678\n"], "legacy.csv", { type: "text/csv" });
  let state = customerImportInitialState();
  assert.equal(canApplyCustomerImport(state), false);

  state = customerImportSelectFile(file);
  assert.equal(state.fileError, null);
  assert.equal(canApplyCustomerImport(state), false);

  // Dry-run with errors locks Apply.
  state = customerImportDryRunSuccess(state, {
    summary: {
      dryRun: true,
      committed: false,
      totalRows: 2,
      readyCount: 1,
      importedCount: 0,
      skippedCount: 0,
      duplicateInFileCount: 0,
      existingPhoneSkippedCount: 0,
      errorCount: 1,
      createdTagsCount: 0,
      createdInteractionsCount: 0,
      newTags: ["Khách cũ"]
    },
    rows: [
      {
        row: 2,
        status: "would_import",
        reasonCode: "would_import",
        customerId: null,
        customerCode: null,
        normalizedPhone: null,
        existingCustomerId: null,
        duplicateOfRow: null,
        createNoteInteraction: false,
        tags: ["Khách cũ"],
        warnings: ["zalo_only_reimport_risk", "possible_duplicate_zalo_customer"],
        errors: []
      },
      {
        row: 3,
        status: "error",
        reasonCode: "missing_contact",
        customerId: null,
        customerCode: null,
        normalizedPhone: null,
        existingCustomerId: null,
        duplicateOfRow: null,
        createNoteInteraction: false,
        tags: [],
        warnings: [],
        errors: [{ column: "phone", code: "missing_contact", message: "Thiếu liên hệ" }]
      }
    ]
  });
  assert.equal(canApplyCustomerImport(state), false);
  assert.deepEqual(countImportWarnings(state.result), {
    zaloOnlyCount: 1,
    possibleDuplicateZaloCount: 1
  });

  // Clean dry-run unlocks Apply.
  state = customerImportDryRunSuccess(state, {
    summary: {
      dryRun: true,
      committed: false,
      totalRows: 2,
      readyCount: 1,
      importedCount: 0,
      skippedCount: 1,
      duplicateInFileCount: 0,
      existingPhoneSkippedCount: 1,
      errorCount: 0,
      createdTagsCount: 0,
      createdInteractionsCount: 0,
      newTags: ["Khách cũ"]
    },
    rows: []
  });
  assert.equal(canApplyCustomerImport(state), true);

  // HTTP 200 committed=false resets previewed and keeps applied=false.
  const uncommitted = customerImportApplyResult(state, {
    summary: {
      dryRun: false,
      committed: false,
      totalRows: 2,
      readyCount: 1,
      importedCount: 0,
      skippedCount: 0,
      duplicateInFileCount: 0,
      existingPhoneSkippedCount: 0,
      errorCount: 1,
      createdTagsCount: 0,
      createdInteractionsCount: 0,
      newTags: []
    },
    rows: []
  });
  assert.equal(uncommitted.applied, false);
  assert.equal(canApplyCustomerImport(uncommitted), false);

  // HTTP 200 committed=true marks applied=true.
  const committed = customerImportApplyResult(state, {
    summary: {
      dryRun: false,
      committed: true,
      totalRows: 2,
      readyCount: 1,
      importedCount: 1,
      skippedCount: 1,
      duplicateInFileCount: 0,
      existingPhoneSkippedCount: 1,
      errorCount: 0,
      createdTagsCount: 1,
      createdInteractionsCount: 1,
      newTags: ["Khách cũ"]
    },
    rows: []
  });
  assert.equal(committed.applied, true);
  assert.equal(canApplyCustomerImport(committed), false);
});

test("P2-14-U07 maps CRM ApiError codes, preserves ApiError.data and validates list filters/tags", () => {
  const dupPhone = new ApiError(409, {
    code: "customer_phone_exists",
    data: { existingCustomerId: custB }
  });
  assert.equal(dupPhone.code, "customer_phone_exists");
  assert.equal(extractExistingCustomerId(dupPhone), custB);
  assert.equal(dupPhone.fields.phone?.[0], dupPhone.message);

  assert.equal(
    extractExistingCustomerId(new ApiError(409, { code: "customer_phone_exists", data: { existingCustomerId: "not-uuid" } })),
    null
  );
  assert.equal(isConcurrencyConflict(new ApiError(409, { code: "concurrency_conflict" })), true);
  assert.equal(isConcurrencyConflict(dupPhone), false);

  const notReady = new ApiError(422, { code: "administrative_data_not_ready" });
  assert.equal(isAdministrativeDataNotReady(notReady), true);
  assert.match(notReady.message, /địa giới hành chính/);

  assert.match(new ApiError(409, { code: "account_exists" }).message, /tài khoản web/);
  assert.match(new ApiError(409, { code: "customer_erased" }).message, /xóa dữ liệu/);
  assert.match(new ApiError(409, { code: "customer_merged" }).message, /gộp/);
  assert.match(new ApiError(409, { code: "campaign_interaction_immutable" }).message, /chiến dịch/);
  assert.match(new ApiError(409, { code: "follow_up_already_completed" }).message, /hoàn thành/);
  assert.match(new ApiError(409, { code: "tag_name_exists" }).message, /nhãn/);

  assert.equal(parseMinLtvVnd(""), undefined);
  assert.equal(parseMinLtvVnd("1.500.000"), 1_500_000);
  assert.throws(() => parseMinLtvVnd("-100"));
  assert.equal(validateDeliveredDateRange("2026-11-01", "2026-11-26"), null);
  assert.equal(validateDeliveredDateRange("2026-11-20", "2026-11-20"), null);
  assert.ok(validateDeliveredDateRange("2026-11-26", "2026-11-01"));
  assert.ok(validateDeliveredDateRange("", "9999-12-31"));

  // P1-F1: inclusive VN end date D -> exclusive backend lastDeliveredBefore = D+1 across all calendar boundaries.
  assert.equal(nextVnDate("2026-11-20"), "2026-11-21");
  assert.equal(nextVnDate("2026-11-30"), "2026-12-01");
  assert.equal(nextVnDate("2026-12-31"), "2027-01-01");
  assert.equal(nextVnDate("2024-02-28"), "2024-02-29");
  assert.equal(nextVnDate("2024-02-29"), "2024-03-01");
  assert.equal(nextVnDate("2026-02-28"), "2026-03-01");
  assert.throws(() => nextVnDate("2026-02-30"));
  assert.throws(() => nextVnDate("9999-12-31"));

  assert.deepEqual(validateTagForm("  VIP Trà  ", "#214f40").payload, { name: "VIP Trà", color: "#214f40" });
  assert.ok(validateTagForm("", "#214f40").error);
  assert.ok(validateTagForm("VIP", "red").error);
});

test("P2-14-U08 verifies CustomersApi CSRF/multipart/204 and AdministrativeGeoClient allowlist without weakening AdminApi", async () => {
  let csrf = 0;
  const calls: Array<{ method: string; path: string; csrf: string | null; credentials: string }> = [];

  const admin = new AdminApi((async (request: Request) => {
    const url = new URL(request.url);
    if (url.pathname === "/api/v1/admin/auth/csrf") {
      return Response.json({ token: `csrf-${++csrf}` });
    }
    calls.push({
      method: request.method,
      path: url.pathname + url.search,
      csrf: request.headers.get("X-CSRF-TOKEN"),
      credentials: request.credentials
    });

    if (url.pathname === "/api/v1/store/provinces") {
      return Response.json({ items: [{ code: "01", name: "Thành phố Hà Nội", sortOrder: 1 }] });
    }
    if (url.pathname === "/api/v1/store/provinces/01/wards") {
      return Response.json({
        provinceCode: "01",
        provinceName: "Thành phố Hà Nội",
        items: [{ code: "00001", provinceCode: "01", name: "Phường Phúc Xá" }]
      });
    }
    if (url.pathname === "/api/v1/store/provinces/99/wards") {
      return Response.json({ code: "administrative_data_not_ready" }, { status: 422 });
    }
    if (url.pathname === "/api/v1/admin/customers/import") {
      const form = await request.formData();
      assert.ok(form.get("file") instanceof File);
      return Response.json({
        summary: {
          dryRun: true,
          committed: false,
          totalRows: 1,
          readyCount: 1,
          importedCount: 0,
          skippedCount: 0,
          duplicateInFileCount: 0,
          existingPhoneSkippedCount: 0,
          errorCount: 0,
          createdTagsCount: 0,
          createdInteractionsCount: 0,
          newTags: []
        },
        rows: []
      });
    }
    if (request.method === "DELETE") {
      return new Response(null, { status: 204 });
    }
    return Response.json(sampleDetail());
  }) as typeof fetch);

  const api = new CustomersApi(admin);
  const geo = new AdministrativeGeoClient(admin);

  // Public geo endpoints work cleanly without CSRF and with credentials=omit.
  const provinces = await geo.listProvinces();
  assert.equal(provinces.items[0].code, "01");
  const wards = await geo.listWards("01");
  assert.equal(wards.items[0].code, "00001");
  await assert.rejects(geo.listWards("99"), (err: unknown) => isAdministrativeDataNotReady(err));

  // Invalid province codes, non-allowlisted reference paths, and store paths on AdminApi.client fail closed with 0 fetches.
  const beforeGuard = calls.length;
  await assert.rejects(geo.listWards("bad"), ApiError);
  const rawGeo = admin.createAdministrativeGeoClient();
  await assert.rejects(
    rawGeo.GET("/api/v1/store/products" as never),
    /Unsupported reference API path/
  );
  await assert.rejects(
    rawGeo.GET("/api/v1/admin/customers" as never),
    /Unsupported reference API path/
  );
  await assert.rejects(
    admin.client.GET("/api/v1/store/provinces"),
    /Unsupported admin API path/
  );
  await assert.rejects(api.getCustomer("not-a-uuid"), ApiError);
  assert.equal(calls.length, beforeGuard);

  // Admin CRM mutations include fresh CSRF token and handle 204/multipart cleanly.
  const csv = new File(["full_name,phone\nA,0912345678\n"], "old.csv", { type: "text/csv" });
  await api.importCustomers(csv, true);
  await api.deleteAddress(custA, addrA, "cust-v1");

  assert.equal(csrf, 2);
  assert.equal(calls[0].path, "/api/v1/store/provinces");
  assert.equal(calls[0].credentials, "omit");
  assert.equal(calls[0].csrf, null);

  const importCall = calls.find(c => c.path.startsWith("/api/v1/admin/customers/import"));
  assert.equal(importCall?.csrf, "csrf-1");
  const delCall = calls.find(c => c.method === "DELETE");
  assert.equal(delCall?.csrf, "csrf-2");
});

test("P2-14-U09 protects ward selection against out-of-order responses, AbortSignal cancellation, empty lists and 500 errors", async () => {
  // selectWardsForProvince returns items ONLY when response.provinceCode strictly matches selectedProvinceCode.
  const hanoiWards = {
    provinceCode: "01",
    provinceName: "Thành phố Hà Nội",
    items: [{ code: "00001", provinceCode: "01", name: "Phường Phúc Xá" }]
  };
  const hcmWards = {
    provinceCode: "79",
    provinceName: "Thành phố Hồ Chí Minh",
    items: [{ code: "26734", provinceCode: "79", name: "Phường Tân Định" }]
  };
  assert.deepEqual(selectWardsForProvince(hanoiWards, "01"), hanoiWards.items);
  assert.deepEqual(selectWardsForProvince(hanoiWards, "79"), []);
  assert.deepEqual(selectWardsForProvince(hanoiWards, ""), []);
  assert.deepEqual(selectWardsForProvince(undefined, "01"), []);

  // Out-of-order resolution + AbortSignal cancellation:
  // Request A ("01") starts first and resolves AFTER Request B ("79"); active province is "79".
  let resolveA!: (res: Response) => void;
  let onFetchAEntered: (() => void) | null = null;
  let abortedA = false;
  const admin = new AdminApi((async (request: Request) => {
    const url = new URL(request.url);
    if (url.pathname === "/api/v1/store/provinces") {
      return Response.json({ items: [] });
    }
    if (url.pathname === "/api/v1/store/provinces/01/wards") {
      if (request.signal.aborted) {
        abortedA = true;
        throw new DOMException("Aborted", "AbortError");
      }
      return await new Promise<Response>((resolve, reject) => {
        resolveA = resolve;
        request.signal.addEventListener("abort", () => {
          abortedA = true;
          reject(new DOMException("Aborted", "AbortError"));
        }, { once: true });
        onFetchAEntered?.();
        onFetchAEntered = null;
      });
    }
    if (url.pathname === "/api/v1/store/provinces/79/wards") {
      return Response.json(hcmWards);
    }
    if (url.pathname === "/api/v1/store/provinces/50/wards") {
      return Response.json({ detail: "Lỗi máy chủ tạm thời" }, { status: 500 });
    }
    return Response.json({ code: "not_found" }, { status: 404 });
  }) as typeof fetch);

  const geo = new AdministrativeGeoClient(admin);

  // Empty province list succeeds cleanly with items = [].
  assert.deepEqual((await geo.listProvinces()).items, []);

  // 500 error maps cleanly to ApiError(500).
  await assert.rejects(geo.listWards("50"), (err: unknown) => err instanceof ApiError && err.status === 500);

  // Out-of-order simulation WITHOUT abort (verifying selectWardsForProvince guard) + WITH abort:
  let selectedProvince = "01";
  let displayedWards: typeof hcmWards.items = [];
  const fetchAStarted = new Promise<void>(resolve => { onFetchAEntered = resolve; });
  const reqA = geo.listWards("01").then(data => {
    const next = selectWardsForProvince(data, selectedProvince);
    if (data.provinceCode === selectedProvince) displayedWards = next;
  });
  await fetchAStarted;
  selectedProvince = "79";
  const dataB = await geo.listWards("79");
  displayedWards = selectWardsForProvince(dataB, selectedProvince);
  assert.deepEqual(displayedWards, hcmWards.items);

  // Now resolve delayed Request A after Request B already rendered: displayedWards must stay HCM!
  resolveA(Response.json(hanoiWards));
  await reqA;
  assert.deepEqual(displayedWards, hcmWards.items);

  // Also verify AbortController aborts in-flight Request A cleanly once fetch is entered.
  const ctrl = new AbortController();
  const abortEntered = new Promise<void>(resolve => { onFetchAEntered = resolve; });
  const abortableReq = geo.listWards("01", ctrl.signal);
  await abortEntered;
  ctrl.abort();
  await assert.rejects(abortableReq);
  assert.equal(abortedA, true);
});
