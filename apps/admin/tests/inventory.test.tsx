import assert from "node:assert/strict";
import { test } from "node:test";
import { QueryClient } from "@tanstack/react-query";
import { AdminApi, ApiError, isConcurrencyConflict } from "../src/lib/api";
import {
  InventoryApi,
  MAX_VARIANT_HYDRATE_PAGES,
  hydrateVariantLabels
} from "../src/lib/inventory-api";
import {
  buildAdjustmentPayload,
  buildCreateReceiptPayload,
  buildEditReceiptPayload,
  canApplyOpeningImport,
  countUnicodeRunes,
  extractLineErrors,
  formatCostVnd,
  hasStaleStocktakeError,
  inventoryPermissions,
  isInventoryBusinessError,
  isInvalidStatusTransition,
  isUuid,
  openingApplyError,
  openingApplyResult,
  openingDryRunSuccess,
  openingImportInitialState,
  openingSelectFile,
  parseCostVnd,
  parseCountedQty,
  resolvePostIdempotencyKey,
  stocktakeDelta,
  validateMovementDateRange
} from "../src/lib/inventory-forms";

const NativeRequest = globalThis.Request;
globalThis.Request = class extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    super(typeof input === "string" ? new URL(input, "https://admin.example.invalid") : input, init);
  }
};

const variantA = "00000000-0000-7000-8000-000000000101";
const variantB = "00000000-0000-7000-8000-000000000102";
const docId = "00000000-0000-7000-8000-000000000201";
const version = "inv-ver-1";

test("P1-13-U01 derives all inventory capabilities strictly from permissions never roles", () => {
  const readOnly = inventoryPermissions(["inventory.read"]);
  assert.equal(readOnly.canRead, true);
  assert.equal(readOnly.canWrite, false);
  assert.equal(readOnly.canApprove, false);
  assert.equal(readOnly.canReadCosts, false);
  assert.equal(readOnly.canReadCatalog, false);
  assert.equal(readOnly.canViewAlerts, false);
  assert.equal(readOnly.canViewMovements, false);
  assert.equal(readOnly.canUseReceipts, false);
  assert.equal(readOnly.canAdjust, false);
  assert.equal(readOnly.canIncreaseAdjustment, false);
  assert.equal(readOnly.canUseStocktakes, false);
  assert.equal(readOnly.canApproveStocktake, false);
  assert.equal(readOnly.canOpenImport, false);

  const sales = inventoryPermissions(["inventory.read", "inventory.write", "catalog.read"]);
  assert.equal(sales.canViewAlerts, true);
  assert.equal(sales.canViewMovements, true);
  assert.equal(sales.canUseReceipts, true);
  assert.equal(sales.canAdjust, true);
  assert.equal(sales.canIncreaseAdjustment, false);
  assert.equal(sales.canApproveStocktake, false);
  assert.equal(sales.canOpenImport, false);
  assert.equal(sales.canReadCosts, false);
  assert.equal(sales.canReadCatalog, true);

  const ownerNoCosts = inventoryPermissions(["inventory.read", "inventory.write", "inventory.approve"]);
  assert.equal(ownerNoCosts.canIncreaseAdjustment, true);
  assert.equal(ownerNoCosts.canApproveStocktake, true);
  assert.equal(ownerNoCosts.canOpenImport, true);
  assert.equal(ownerNoCosts.canReadCosts, false);

  const costsOnly = inventoryPermissions(["inventory.read", "costs.read"]);
  assert.equal(costsOnly.canReadCosts, true);
  assert.equal(costsOnly.canWrite, false);
});

test("P1-13-U02 parses non-negative integer VND cost and formats zero vs missing", () => {
  assert.equal(parseCostVnd("0"), 0);
  assert.equal(parseCostVnd("150000"), 150_000);
  assert.equal(parseCostVnd("1.500.000"), 1_500_000);
  assert.equal(parseCostVnd(String(Number.MAX_SAFE_INTEGER)), Number.MAX_SAFE_INTEGER);
  for (const bad of ["", "  ", "-1", "1,5", "12.34", "3e4", "0x10", "9007199254740992", "9007199254740993", "abc"]) {
    assert.throws(() => parseCostVnd(bad), bad);
  }
  assert.equal(formatCostVnd(0), "0 đ");
  assert.equal(formatCostVnd("0"), "0 đ");
  assert.equal(formatCostVnd(150_000), "150.000 đ");
  assert.equal(formatCostVnd(null), "—");
  assert.equal(formatCostVnd(undefined), "—");
});

test("P1-13-U03 builds receipt PATCH payload omitting hidden existing cost and requiring new line cost", () => {
  const editSales = buildEditReceiptPayload(
    version,
    {
      supplierName: "  Xưởng Bát Tràng  ",
      receivedAt: "2026-11-23T09:30",
      note: "Đợt 1",
      lines: [
        { variantId: variantA, quantity: "3", unitCostVnd: "", isExisting: true },
        { variantId: variantB, quantity: "2", unitCostVnd: "0", isExisting: false }
      ]
    },
    false
  );
  assert.equal(editSales.errors, undefined);
  assert.deepEqual(editSales.payload, {
    rowVersion: version,
    supplierName: "Xưởng Bát Tràng",
    receivedAt: "2026-11-23T09:30:00+07:00",
    note: "Đợt 1",
    lines: [
      { variantId: variantA, quantity: 3 },
      { variantId: variantB, quantity: 2, unitCostVnd: 0 }
    ]
  });
  assert.equal("unitCostVnd" in editSales.payload!.lines![0], false);
  assert.notEqual(editSales.payload?.lines, null);

  // Missing cost on a newly added line must fail even when costs.read is absent.
  const missingNewCost = buildEditReceiptPayload(
    version,
    {
      supplierName: "",
      receivedAt: "",
      note: "",
      lines: [{ variantId: variantB, quantity: "1", unitCostVnd: "", isExisting: false }]
    },
    false
  );
  assert.ok(missingNewCost.errors?.lineFields[variantB]?.unitCostVnd);

  // Create mode always requires unitCostVnd >= 0 and rejects duplicate variantId.
  const duplicate = buildCreateReceiptPayload({
    supplierName: "",
    receivedAt: "",
    note: "",
    lines: [
      { variantId: variantA, quantity: "1", unitCostVnd: "100000", isExisting: false },
      { variantId: variantA, quantity: "2", unitCostVnd: "100000", isExisting: false }
    ]
  });
  assert.ok(duplicate.errors?.lines);
});

test("P1-13-U04 validates adjustment and damage rules (BR-INV-08)", () => {
  // Unicode runes: 3 emoji surrogate pairs count as 3 runes, 2 Vietnamese chars fail.
  assert.equal(countUnicodeRunes("🍵🍵🍵"), 3);
  assert.equal(countUnicodeRunes("vỡ"), 2);

  const shortReason = buildAdjustmentPayload(
    { variantId: variantA, type: "damage", direction: "decrease", quantity: "1", reason: "  vỡ  " },
    true
  );
  assert.ok(shortReason.errors?.reason);

  const emojiReason = buildAdjustmentPayload(
    { variantId: variantA, type: "damage", direction: "decrease", quantity: "2", reason: "🍵🍵🍵" },
    false
  );
  assert.deepEqual(emojiReason.payload, { variantId: variantA, type: "damage", quantityDelta: -2, reason: "🍵🍵🍵" });

  const damageIncrease = buildAdjustmentPayload(
    { variantId: variantA, type: "damage", direction: "increase", quantity: "1", reason: "Lý do hợp lệ" },
    true
  );
  assert.ok(damageIncrease.errors?.direction);

  const increaseWithoutApprove = buildAdjustmentPayload(
    { variantId: variantA, type: "adjustment", direction: "increase", quantity: "3", reason: "Tìm thấy thêm trong kho" },
    false
  );
  assert.ok(increaseWithoutApprove.errors?.direction);

  const increaseWithApprove = buildAdjustmentPayload(
    { variantId: variantA, type: "adjustment", direction: "increase", quantity: "3", reason: "Tìm thấy thêm trong kho" },
    true
  );
  assert.deepEqual(increaseWithApprove.payload, {
    variantId: variantA,
    type: "adjustment",
    quantityDelta: 3,
    reason: "Tìm thấy thêm trong kho"
  });

  const zeroQty = buildAdjustmentPayload(
    { variantId: variantA, type: "adjustment", direction: "decrease", quantity: "0", reason: "Lý do hợp lệ" },
    true
  );
  assert.ok(zeroQty.errors?.quantity);
});

test("P1-13-U05 computes stocktake delta and maps lines[variantId] errors (BR-INV-10)", () => {
  assert.equal(parseCountedQty("0"), 0);
  assert.equal(parseCountedQty("15"), 15);
  assert.throws(() => parseCountedQty("-1"));
  assert.throws(() => parseCountedQty("1.5"));

  assert.equal(stocktakeDelta(8, 5), 3);
  assert.equal(stocktakeDelta(2, 5), -3);
  assert.equal(stocktakeDelta("5", "5"), 0);

  const err = new ApiError(409, {
    code: "stocktake_lines_invalid",
    errors: {
      [`lines[${variantA}]`]: ["unique_piece_violation"],
      [`lines[${variantB}]`]: ["stocktake_count_stale", "out_of_stock"]
    }
  });
  const mapped = extractLineErrors(err);
  assert.deepEqual(mapped[variantA].codes, ["unique_piece_violation"]);
  assert.match(mapped[variantA].messages[0], /độc bản/);
  assert.deepEqual(mapped[variantB].codes, ["stocktake_count_stale", "out_of_stock"]);
  assert.equal(hasStaleStocktakeError(err), true);
  assert.equal(hasStaleStocktakeError(new ApiError(422, { code: "out_of_stock" })), false);
});

test("P1-13-U06 enforces opening-import dry-run -> apply state machine (BR-INV-06)", () => {
  const file1 = new File(["sku,quantity,unit_cost_vnd\nA,2,1000\n"], "opening-1.csv", { type: "text/csv" });
  const file2 = new File(["sku,quantity,unit_cost_vnd\nB,1,2000\n"], "opening-2.csv", { type: "text/csv" });
  const now = 1_700_000_000_000;

  let state = openingImportInitialState();
  assert.equal(canApplyOpeningImport(state, now), false);

  state = openingSelectFile(file1);
  // Dry-run with invalid rows keeps apply locked.
  state = openingDryRunSuccess(state, {
    dryRun: true,
    committed: false,
    validRows: 1,
    invalidRows: 1,
    previewToken: "should-not-enable",
    rows: [{ row: 2, sku: "BAD", variantId: null, errors: [{ column: "sku", code: "not_found", message: "Không thấy SKU" }] }]
  }, now);
  assert.equal(state.previewToken, null);
  assert.equal(canApplyOpeningImport(state, now), false);

  // Valid dry-run opens apply for 30 minutes.
  state = openingDryRunSuccess(state, {
    dryRun: true,
    committed: false,
    validRows: 1,
    invalidRows: 0,
    previewToken: "tok-1",
    rows: [{ row: 2, sku: "A", variantId: variantA, errors: [] }]
  }, now);
  assert.equal(canApplyOpeningImport(state, now), true);
  assert.equal(canApplyOpeningImport(state, now + 30 * 60_000), false);

  // Selecting another file immediately discards previewToken and locks apply.
  const switched = openingSelectFile(file2);
  assert.equal(switched.previewToken, null);
  assert.equal(canApplyOpeningImport(switched, now), false);

  // HTTP 200 committed=false preserves row errors and clears previewToken.
  const failedCommit = openingApplyResult(state, {
    dryRun: false,
    committed: false,
    validRows: 0,
    invalidRows: 1,
    rows: [{ row: 2, sku: "A", variantId: variantA, errors: [{ column: "sku", code: "opening_already_recorded", message: "Đã có giao dịch" }] }]
  });
  assert.equal(failedCommit.applied, false);
  assert.equal(failedCommit.previewToken, null);
  assert.equal(failedCommit.result?.rows[0].errors[0].code, "opening_already_recorded");

  // 400 opening_preview_invalid preserves selected file and clears token.
  const expired = openingApplyError(state, new ApiError(400, { code: "opening_preview_invalid" }));
  assert.equal(expired.file, file1);
  assert.equal(expired.previewToken, null);
});

test("P1-13-U07 InventoryApi sends CSRF, multipart preview token, Idempotency-Key and bounds variant hydration", async () => {
  let csrf = 0;
  const calls: Array<{ method: string; url: string; csrf: string | null; preview: string | null; idem: string | null; contentType: string | null }> = [];
  const api = new InventoryApi(new AdminApi((async (request: Request) => {
    const url = new URL(request.url);
    assert.equal(request.credentials, "include");
    assert.equal(request.cache, "no-store");
    if (url.pathname.endsWith("/auth/csrf")) return Response.json({ token: `csrf-${++csrf}` });
    calls.push({
      method: request.method,
      url: url.pathname + url.search,
      csrf: request.headers.get("X-CSRF-TOKEN"),
      preview: request.headers.get("X-Inventory-Preview-Token"),
      idem: request.headers.get("Idempotency-Key"),
      contentType: request.headers.get("content-type")
    });
    if (url.pathname.endsWith("/opening-import")) {
      const form = await request.formData();
      assert.ok(form.get("file") instanceof File);
      return Response.json({ dryRun: url.searchParams.get("dryRun") === "true", committed: true, validRows: 1, invalidRows: 0, rows: [] });
    }
    if (url.pathname.endsWith("/post")) return Response.json({ id: docId, status: "posted", rowVersion: "v2" });
    if (url.pathname.includes("/stocktakes/") && request.method === "PUT") {
      return Response.json({ document: { id: docId, code: "KK-1", status: "counting", createdAt: "2026-11-23T00:00:00Z", createdBy: variantA, note: null, rowVersion: "v2", submittedAt: null, approvedAt: null, approvedBy: null }, lines: [] });
    }
    if (url.pathname === "/api/v1/admin/inventory") {
      const page = Number(url.searchParams.get("page") ?? 1);
      return Response.json({
        items: [{ variantId: `00000000-0000-7000-8000-${String(page).padStart(12, "0")}`, productId: docId, productTypeId: docId, sku: `SKU-${page}`, productName: `SP ${page}`, variantName: null, availability: "in_stock" }],
        page,
        pageSize: 100,
        totalItems: 2000
      });
    }
    return Response.json({ document: { id: docId, code: "PN-1", status: "draft", createdAt: "2026-11-23T00:00:00Z", createdBy: variantA, note: null, postedAt: null, postedBy: null, receivedAt: null, rowVersion: "v2", supplierName: null }, lines: [] });
  }) as typeof fetch));

  const csv = new File(["sku,quantity,unit_cost_vnd\nA,1,100\n"], "open.csv", { type: "text/csv" });
  await api.openingImport(csv, { dryRun: false, previewToken: "pv-123" });
  await api.editReceipt(docId, { rowVersion: version, lines: [] });
  await api.postReceipt(docId, version, "idem-uuid-1");
  await api.countStocktakeLine(docId, variantA, version, 4);

  assert.equal(csrf, 4);
  assert.equal(calls[0].method, "POST");
  assert.equal(calls[0].url, "/api/v1/admin/inventory/opening-import?dryRun=false");
  assert.equal(calls[0].csrf, "csrf-1");
  assert.equal(calls[0].preview, "pv-123");
  assert.match(calls[0].contentType ?? "", /multipart\/form-data/);

  assert.equal(calls[1].method, "PATCH");
  assert.equal(calls[1].csrf, "csrf-2");
  assert.equal(calls[2].method, "POST");
  assert.equal(calls[2].idem, "idem-uuid-1");
  assert.equal(calls[2].csrf, "csrf-3");
  assert.equal(calls[3].method, "PUT");
  assert.equal(calls[3].csrf, "csrf-4");

  // GAP-P1-13-01 Option A: direct-URL hydration stops early once all requested variantIds are found,
  // and caps at MAX_VARIANT_HYDRATE_PAGES (5) without fabricating labels when a variant is missing.
  const queries = new QueryClient();
  try {
    const page1Id = "00000000-0000-7000-8000-000000000001";
    const page2Id = "00000000-0000-7000-8000-000000000002";
    const beforeEarly = calls.length;
    const earlyResolved = await hydrateVariantLabels(api, queries, [page1Id, page2Id]);
    assert.equal(calls.length - beforeEarly, 2);
    assert.equal(earlyResolved[page1Id]?.sku, "SKU-1");
    assert.equal(earlyResolved[page2Id]?.sku, "SKU-2");

    // Second call for already-cached IDs fires 0 requests.
    const beforeCached = calls.length;
    const cachedResolved = await hydrateVariantLabels(api, queries, [page1Id, page2Id]);
    assert.equal(calls.length - beforeCached, 0);
    assert.equal(cachedResolved[page2Id]?.sku, "SKU-2");

    const beforeMissing = calls.length;
    const resolved = await hydrateVariantLabels(api, queries, [variantB]);
    assert.equal(calls.length - beforeMissing, MAX_VARIANT_HYDRATE_PAGES);
    assert.equal(resolved[variantB], undefined);
  } finally {
    queries.clear();
  }
});

test("P1-13-U08 preserves Idempotency-Key for same (receiptId, rowVersion) and rotates on version change", () => {
  let seq = 0;
  const gen = () => `00000000-0000-7000-8000-00000000090${++seq}`;
  const first = resolvePostIdempotencyKey(null, docId, "v1", gen);
  const retrySame = resolvePostIdempotencyKey(first, docId, "v1", gen);
  assert.equal(retrySame.key, first.key);
  const afterEdit = resolvePostIdempotencyKey(retrySame, docId, "v2", gen);
  assert.notEqual(afterEdit.key, first.key);
  const otherReceipt = resolvePostIdempotencyKey(afterEdit, variantA, "v2", gen);
  assert.notEqual(otherReceipt.key, afterEdit.key);
});

test("P1-13-U09 separates 409 concurrency_conflict from 409/422 inventory business errors (GAP-P1-13-02)", () => {
  const conflict = new ApiError(409, { code: "concurrency_conflict" });
  assert.equal(isConcurrencyConflict(conflict), true);
  assert.equal(isInventoryBusinessError(conflict), false);

  for (const status of [409, 422]) {
    for (const code of ["unique_piece_violation", "out_of_stock", "stocktake_count_stale", "invalid_status_transition", "receipt_lines_invalid", "stocktake_lines_invalid"]) {
      const err = new ApiError(status, { code, errors: { [`lines[${variantA}]`]: [code] } });
      assert.equal(isConcurrencyConflict(err), false, `${status} ${code} must not be concurrency_conflict`);
      assert.equal(isInventoryBusinessError(err), true, `${status} ${code} must be inventory business error`);
      assert.deepEqual(err.fields[`lines[${variantA}]`], [code]);
    }
  }
  assert.equal(isInvalidStatusTransition(new ApiError(409, { code: "invalid_status_transition" })), true);
  assert.equal(isInvalidStatusTransition(new ApiError(422, { code: "invalid_status_transition" })), true);
  assert.equal(isInvalidStatusTransition(new ApiError(409, { code: "out_of_stock" })), false);
  assert.match(new ApiError(415, { code: "unsupported_format" }).message, /CSV hoặc XLSX/);
});

test("P1-13-U10 validates movement VN date ranges and blocks invalid UUIDs before fetch", async () => {
  assert.equal(validateMovementDateRange("", ""), null);
  assert.equal(validateMovementDateRange("2026-11-01", "2026-11-02"), null);
  assert.ok(validateMovementDateRange("2026-11-02", "2026-11-02"));
  assert.ok(validateMovementDateRange("2026-11-05", "2026-11-02"));
  assert.ok(validateMovementDateRange("2026-02-30", ""));
  assert.ok(validateMovementDateRange("bad-date", ""));

  assert.equal(isUuid(variantA), true);
  assert.equal(isUuid("../bad"), false);

  let fetched = 0;
  const api = new InventoryApi(new AdminApi((async () => {
    fetched++;
    return Response.json({});
  }) as typeof fetch));
  await assert.rejects(api.listMovements("not-a-uuid", {}), ApiError);
  await assert.rejects(api.getReceipt("not-a-uuid"), ApiError);
  await assert.rejects(api.getStocktake("not-a-uuid"), ApiError);
  await assert.rejects(api.countStocktakeLine(docId, "bad", version, 1), ApiError);
  assert.equal(fetched, 0);
});
