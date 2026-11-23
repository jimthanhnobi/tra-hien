import { expect, test, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import type { Auth } from "../../src/lib/api";
import type {
  AlertsView,
  MovementRow,
  OpeningResult,
  ProductTypeView,
  ReceiptSummary,
  ReceiptView,
  StockRow,
  StocktakeSummary,
  StocktakeView
} from "../../src/lib/inventory-api";

const uid = (n: number) => `00000000-0000-7000-8000-${String(n).padStart(12, "0")}`;
const varA = uid(101);
const varB = uid(102);
const varC = uid(103);
const prodA = uid(201);
const prodB = uid(202);
const typeTeapot = uid(301);
const typeCup = uid(302);
const receiptDraftId = uid(401);
const receiptCancelId = uid(402);
const stocktakeId = uid(501);
const stocktakeSubmitCancelId = uid(502);
const actorId = uid(901);
const stamp = "2026-11-23T03:00:00Z";
const apiRoot = "/api/v1/admin/";

const ownerPerms = ["inventory.read", "inventory.write", "inventory.approve", "costs.read", "catalog.read"];
const salesPerms = ["inventory.read", "inventory.write", "catalog.read"];
const readOnlyPerms = ["inventory.read"];

function makeAuth(permissions: string[], roles: string[] = ["owner"]): Auth {
  return {
    passwordChangeRequired: false,
    totpRequired: false,
    user: {
      id: actorId,
      email: "inventory-test@example.invalid",
      displayName: "Nhân viên kho thử",
      isActive: true,
      mustChangePassword: false,
      totpEnabled: true,
      roles,
      rowVersion: "staff-v1",
      permissions
    }
  };
}

const sampleTypes: ProductTypeView[] = [
  { id: typeTeapot, code: "AM", name: "Ấm trà", isActive: true, sortOrder: 0, skuPrefix: "AM", description: null, googleProductCategory: null, rowVersion: "t1", createdAt: stamp, updatedAt: stamp },
  { id: typeCup, code: "CH", name: "Chén trà", isActive: true, sortOrder: 1, skuPrefix: "CH", description: null, googleProductCategory: null, rowVersion: "t2", createdAt: stamp, updatedAt: stamp }
];

function baseStockRows(): StockRow[] {
  return [
    { variantId: varA, productId: prodA, productTypeId: typeTeapot, sku: "AM-TD-0001", productName: "Ấm tử sa Thạch Biều", variantName: "180ml", availability: "in_stock", onHand: 5, reserved: 1, available: 4, lowStock: false, avgCostVnd: 850_000 },
    { variantId: varB, productId: prodB, productTypeId: typeCup, sku: "CH-MT-0002", productName: "Chén men tro", variantName: "60ml", availability: "in_stock", onHand: 2, reserved: 0, available: 2, lowStock: true, avgCostVnd: 0 },
    { variantId: varC, productId: prodB, productTypeId: typeCup, sku: "CH-MT-0003", productName: "Chén độc bản", variantName: null, availability: "out_of_stock", onHand: 0, reserved: 0, available: 0, lowStock: true, avgCostVnd: null }
  ];
}

function baseAlerts(): AlertsView {
  return {
    checkedAt: stamp,
    lowStockTotal: 1,
    lowStockItems: [
      { variantId: varB, productId: prodB, sku: "CH-MT-0002", productName: "Chén men tro", variantName: "60ml", onHand: 2, reserved: 0, available: 2, lowStockThreshold: 3 }
    ],
    mismatchTotal: 1,
    mismatches: [
      { variantId: varC, productId: prodB, sku: "CH-MT-0003", productName: "Chén độc bản", variantName: null, onHand: 0, ledgerQuantity: 1, delta: -1, hasStockLevel: false }
    ],
    deadAlertOutboxCount: 2
  };
}

interface RecordedCall {
  method: string;
  path: string;
  query: URLSearchParams;
  headers: Record<string, string>;
  body: Record<string, unknown> | null;
}

interface ForcedError {
  status: number;
  code: string;
  errors?: Record<string, string[]>;
}

async function setupFixture(
  page: Page,
  options: {
    permissions?: string[];
    roles?: string[];
    stockRows?: StockRow[];
    alerts?: AlertsView;
    receipts?: Record<string, ReceiptView>;
    stocktakes?: Record<string, StocktakeView>;
    movements?: Record<string, MovementRow[]>;
  } = {}
) {
  const perms = options.permissions ?? ownerPerms;
  const canWrite = perms.includes("inventory.write");
  const canReadCosts = perms.includes("costs.read");
  const stockRows = structuredClone(options.stockRows ?? baseStockRows());
  const alertsData = structuredClone(options.alerts ?? baseAlerts());

  const receipts: Record<string, ReceiptView> = structuredClone(options.receipts ?? {
    [receiptDraftId]: {
      document: {
        id: receiptDraftId,
        code: "PN-261123-1",
        status: "draft",
        supplierName: "Xưởng gốm Phù Lãng",
        note: "Đợt nhập thử",
        receivedAt: "2026-11-23T02:00:00Z",
        createdAt: stamp,
        createdBy: actorId,
        postedAt: null,
        postedBy: null,
        rowVersion: "rcpt-v1"
      },
      lines: [
        { variantId: varA, quantity: 3, ...(canReadCosts ? { unitCostVnd: 800_000 } : {}) }
      ]
    },
    [receiptCancelId]: {
      document: {
        id: receiptCancelId,
        code: "PN-261123-2",
        status: "draft",
        supplierName: "Xưởng trà Thái Nguyên",
        note: null,
        receivedAt: null,
        createdAt: stamp,
        createdBy: actorId,
        postedAt: null,
        postedBy: null,
        rowVersion: "rcpt-cancel-v1"
      },
      lines: [
        { variantId: varB, quantity: 5, ...(canReadCosts ? { unitCostVnd: 50_000 } : {}) }
      ]
    }
  });

  const stocktakes: Record<string, StocktakeView> = structuredClone(options.stocktakes ?? {
    [stocktakeId]: {
      document: {
        id: stocktakeId,
        code: "KK-261123-1",
        status: "counting",
        note: "Kiểm kê định kỳ",
        createdAt: stamp,
        createdBy: actorId,
        submittedAt: null,
        approvedAt: null,
        approvedBy: null,
        rowVersion: "st-v1"
      },
      lines: []
    },
    [stocktakeSubmitCancelId]: {
      document: {
        id: stocktakeSubmitCancelId,
        code: "KK-261123-2",
        status: "submitted",
        note: "Phiên chờ duyệt",
        createdAt: stamp,
        createdBy: actorId,
        submittedAt: stamp,
        approvedAt: null,
        approvedBy: null,
        rowVersion: "st-sub-v1"
      },
      lines: [
        { variantId: varA, countedQty: 7, systemQtyAtCount: 5, countedAt: stamp, countedBy: actorId },
        { variantId: varB, countedQty: 0, systemQtyAtCount: 2, countedAt: stamp, countedBy: actorId },
        { variantId: varC, countedQty: 2, systemQtyAtCount: 0, countedAt: stamp, countedBy: actorId }
      ]
    }
  });

  const movementsByVariant: Record<string, MovementRow[]> = structuredClone(options.movements ?? {
    [varA]: [
      { id: uid(601), movementType: "receipt", quantity: 3, onHandAfter: 5, unitCostVnd: canReadCosts ? 800_000 : undefined, refType: "receipt", refId: receiptDraftId, reason: null, actorType: "staff", actorId, occurredAt: "2026-11-22T08:00:00Z", createdAt: "2026-11-22T08:00:00Z" },
      { id: uid(602), movementType: "opening", quantity: 2, onHandAfter: 2, unitCostVnd: canReadCosts ? 0 : undefined, refType: "opening", refId: null, reason: "Tồn đầu kỳ", actorType: "staff", actorId, occurredAt: "2026-11-20T08:00:00Z", createdAt: "2026-11-20T08:00:00Z" }
    ]
  });

  let csrfNonce = 0;
  let versionSeq = 10;
  let authenticated = true;
  let nextError: ForcedError | undefined;
  let nextOpeningHandler: ((dryRun: boolean, headers: Record<string, string>) => { status: number; json: unknown }) | undefined;
  const calls: RecordedCall[] = [];

  await page.route("**/api/v1/admin/**", async route => {
    const req = route.request();
    const url = new URL(req.url());
    const path = url.pathname.slice(apiRoot.length);
    const method = req.method();
    const headers = req.headers();
    let body: Record<string, unknown> | null = null;
    if (method !== "GET" && (headers["content-type"] ?? "").includes("application/json")) {
      try { body = req.postDataJSON() as Record<string, unknown>; } catch { body = null; }
    }
    const fulfill = (json: unknown, status = 200) => route.fulfill({ status, json });
    calls.push({ method, path, query: url.searchParams, headers, body });

    if (path === "auth/me") {
      if (!authenticated) return fulfill({ code: "unauthenticated" }, 401);
      return fulfill(makeAuth(perms, options.roles ?? ["owner"]));
    }
    if (path === "auth/csrf") {
      return fulfill({ token: `mock-csrf-${++csrfNonce}` });
    }
    if (!authenticated) return fulfill({ code: "unauthenticated" }, 401);

    if (method !== "GET") {
      expect(headers["x-csrf-token"]).toBe(`mock-csrf-${csrfNonce}`);
    }

    if (nextError && method !== "GET") {
      const err = nextError;
      nextError = undefined;
      if (err.status === 401) authenticated = false;
      return fulfill({ code: err.code, errors: err.errors }, err.status);
    }

    if (path === "product-types") {
      return fulfill({ items: sampleTypes, page: 1, pageSize: 100, totalItems: sampleTypes.length });
    }

    if (path === "inventory" && method === "GET") {
      const q = (url.searchParams.get("q") ?? "").toLowerCase();
      const status = url.searchParams.get("status") ?? "";
      const type = url.searchParams.get("type") ?? "";
      const pageNum = Number(url.searchParams.get("page") ?? 1);
      const pageSize = Number(url.searchParams.get("pageSize") ?? 20);

      const filtered = stockRows
        .filter(row => !q || `${row.sku} ${row.productName} ${row.variantName ?? ""}`.toLowerCase().includes(q))
        .filter(row => !type || row.productTypeId === type)
        .filter(row => {
          if (!status) return true;
          if (status === "in") return row.availability === "in_stock";
          if (status === "out") return row.availability === "out_of_stock";
          if (status === "low") return Boolean(row.lowStock);
          return true;
        })
        .map(row => {
          const out: StockRow = {
            variantId: row.variantId,
            productId: row.productId,
            productTypeId: row.productTypeId,
            sku: row.sku,
            productName: row.productName,
            variantName: row.variantName,
            availability: row.availability
          };
          if (canWrite) {
            out.onHand = row.onHand;
            out.reserved = row.reserved;
            out.available = row.available;
            out.lowStock = row.lowStock;
          }
          if (canReadCosts) {
            out.avgCostVnd = row.avgCostVnd;
          }
          return out;
        });

      return fulfill({
        items: filtered.slice((pageNum - 1) * pageSize, pageNum * pageSize),
        page: pageNum,
        pageSize,
        totalItems: filtered.length
      });
    }

    if (path === "inventory/alerts" && method === "GET") {
      return fulfill(alertsData);
    }

    const movMatch = /^inventory\/([^/]+)\/movements$/.exec(path);
    if (movMatch && method === "GET") {
      const vid = movMatch[1];
      const list = movementsByVariant[vid];
      if (!list) return fulfill({ code: "not_found" }, 404);
      const mType = url.searchParams.get("type") ?? "";
      const pageNum = Number(url.searchParams.get("page") ?? 1);
      const pageSize = Number(url.searchParams.get("pageSize") ?? 20);
      const filtered = list
        .filter(row => !mType || row.movementType === mType)
        .map(row => canReadCosts ? row : { ...row, unitCostVnd: undefined });
      return fulfill({
        items: filtered.slice((pageNum - 1) * pageSize, pageNum * pageSize),
        page: pageNum,
        pageSize,
        totalItems: filtered.length
      });
    }

    if (path === "inventory/opening-import" && method === "POST") {
      const dryRun = url.searchParams.get("dryRun") === "true";
      if (nextOpeningHandler) {
        const handler = nextOpeningHandler;
        nextOpeningHandler = undefined;
        const res = handler(dryRun, headers);
        return fulfill(res.json, res.status);
      }
      if (dryRun) {
        const res: OpeningResult = {
          dryRun: true,
          committed: false,
          validRows: 1,
          invalidRows: 0,
          previewToken: "preview-token-valid-1",
          rows: [{ row: 2, sku: "AM-TD-0001", variantId: varA, errors: [] }]
        };
        return fulfill(res);
      }
      expect(headers["x-inventory-preview-token"]).toBeTruthy();
      const res: OpeningResult = {
        dryRun: false,
        committed: true,
        validRows: 1,
        invalidRows: 0,
        rows: [{ row: 2, sku: "AM-TD-0001", variantId: varA, errors: [] }]
      };
      return fulfill(res);
    }

    if (path === "inventory/adjustments" && method === "POST") {
      const vid = String(body?.variantId ?? "");
      const delta = Number(body?.quantityDelta ?? 0);
      return fulfill({ id: uid(701), variantId: vid, quantityDelta: delta, onHandAfter: Math.max(0, 5 + delta) });
    }

    if (path === "receipts" && method === "GET") {
      const status = url.searchParams.get("status") ?? "";
      const items: ReceiptSummary[] = Object.values(receipts)
        .map(r => r.document)
        .filter(d => !status || d.status === status);
      return fulfill({ items, page: 1, pageSize: 20, totalItems: items.length });
    }

    if (path === "receipts" && method === "POST") {
      const id = uid(450 + versionSeq++);
      const lines = ((body?.lines as Array<{ variantId: string; quantity: number; unitCostVnd: number }>) ?? []).map(l => ({
        variantId: l.variantId,
        quantity: l.quantity,
        ...(canReadCosts ? { unitCostVnd: l.unitCostVnd } : {})
      }));
      const created: ReceiptView = {
        document: {
          id,
          code: `PN-261123-${versionSeq}`,
          status: "draft",
          supplierName: (body?.supplierName as string | null) ?? null,
          note: (body?.note as string | null) ?? null,
          receivedAt: (body?.receivedAt as string | null) ?? null,
          createdAt: stamp,
          createdBy: actorId,
          postedAt: null,
          postedBy: null,
          rowVersion: `rcpt-v${versionSeq}`
        },
        lines
      };
      receipts[id] = created;
      return fulfill(created, 201);
    }

    const rcptMatch = /^receipts\/([^/]+)(?:\/(post|cancel))?$/.exec(path);
    if (rcptMatch) {
      const [, id, action] = rcptMatch;
      const doc = receipts[id];
      if (!doc) return fulfill({ code: "not_found" }, 404);
      if (method === "GET") return fulfill(doc);
      if (body?.rowVersion !== doc.document.rowVersion) {
        return fulfill({ code: "concurrency_conflict" }, 409);
      }
      if (method === "PATCH") {
        const nextLines = ((body?.lines as Array<{ variantId: string; quantity: number; unitCostVnd?: number }>) ?? []).map(l => ({
          variantId: l.variantId,
          quantity: l.quantity,
          ...(canReadCosts ? { unitCostVnd: l.unitCostVnd ?? 0 } : {})
        }));
        doc.document = {
          ...doc.document,
          supplierName: (body?.supplierName as string | null) ?? null,
          note: (body?.note as string | null) ?? null,
          receivedAt: (body?.receivedAt as string | null) ?? null,
          rowVersion: `rcpt-v${++versionSeq}`
        };
        doc.lines = nextLines;
        return fulfill(doc);
      }
      if (action === "post") {
        expect(headers["idempotency-key"]).toBeTruthy();
        doc.document = { ...doc.document, status: "posted", postedAt: stamp, postedBy: actorId, rowVersion: `rcpt-v${++versionSeq}` };
        return fulfill({ id: doc.document.id, status: "posted", rowVersion: doc.document.rowVersion });
      }
      if (action === "cancel") {
        doc.document = { ...doc.document, status: "cancelled", rowVersion: `rcpt-v${++versionSeq}` };
        return fulfill(doc);
      }
    }

    if (path === "stocktakes" && method === "GET") {
      const status = url.searchParams.get("status") ?? "";
      const items: StocktakeSummary[] = Object.values(stocktakes)
        .map(s => s.document)
        .filter(d => !status || d.status === status);
      return fulfill({ items, page: 1, pageSize: 20, totalItems: items.length });
    }

    if (path === "stocktakes" && method === "POST") {
      const id = uid(550 + versionSeq++);
      const created: StocktakeView = {
        document: {
          id,
          code: `KK-261123-${versionSeq}`,
          status: "counting",
          note: (body?.note as string | null) ?? null,
          createdAt: stamp,
          createdBy: actorId,
          submittedAt: null,
          approvedAt: null,
          approvedBy: null,
          rowVersion: `st-v${versionSeq}`
        },
        lines: []
      };
      stocktakes[id] = created;
      return fulfill(created, 201);
    }

    const stCountMatch = /^stocktakes\/([^/]+)\/lines\/([^/]+)$/.exec(path);
    if (stCountMatch && method === "PUT") {
      const [, id, vid] = stCountMatch;
      const doc = stocktakes[id];
      if (!doc) return fulfill({ code: "not_found" }, 404);
      if (body?.rowVersion !== doc.document.rowVersion) return fulfill({ code: "concurrency_conflict" }, 409);
      const stock = stockRows.find(s => s.variantId === vid);
      const sysQty = Number(stock?.onHand ?? 0);
      const line = { variantId: vid, countedQty: Number(body?.countedQty ?? 0), systemQtyAtCount: sysQty, countedAt: stamp, countedBy: actorId };
      doc.lines = [...doc.lines.filter(l => l.variantId !== vid), line];
      doc.document = { ...doc.document, rowVersion: `st-v${++versionSeq}` };
      return fulfill(doc);
    }

    const stMatch = /^stocktakes\/([^/]+)(?:\/(submit|approve|cancel))?$/.exec(path);
    if (stMatch) {
      const [, id, action] = stMatch;
      const doc = stocktakes[id];
      if (!doc) return fulfill({ code: "not_found" }, 404);
      if (method === "GET") return fulfill(doc);
      if (body?.rowVersion !== doc.document.rowVersion) return fulfill({ code: "concurrency_conflict" }, 409);
      if (action === "submit") {
        doc.document = { ...doc.document, status: "submitted", submittedAt: stamp, rowVersion: `st-v${++versionSeq}` };
      } else if (action === "approve") {
        doc.document = { ...doc.document, status: "approved", approvedAt: stamp, approvedBy: actorId, rowVersion: `st-v${++versionSeq}` };
      } else if (action === "cancel") {
        doc.document = { ...doc.document, status: "cancelled", rowVersion: `st-v${++versionSeq}` };
      }
      return fulfill(doc);
    }

    return fulfill({ code: "not_found" }, 404);
  });

  return {
    calls,
    receipts,
    stocktakes,
    rejectNext(err: ForcedError) { nextError = err; },
    setOpeningNext(handler: (dryRun: boolean, headers: Record<string, string>) => { status: number; json: unknown }) {
      nextOpeningHandler = handler;
    },
    mutateReceiptRemote(id: string, patch: Partial<ReceiptView["document"]>) {
      receipts[id].document = { ...receipts[id].document, ...patch };
    },
    mutateStocktakeRemote(id: string, patch: Partial<StocktakeView["document"]>) {
      stocktakes[id].document = { ...stocktakes[id].document, ...patch };
    }
  };
}

test("P1-13-B01 inventory.read-only staff sees binary availability only and fires 0 forbidden requests on direct URLs", async ({ page }) => {
  const fx = await setupFixture(page, { permissions: readOnlyPerms, roles: ["owner"] });
  await page.goto("/inventory");

  await expect(page.getByRole("heading", { name: "Danh sách tồn kho" })).toBeVisible();
  await expect(page.getByText("AM-TD-0001")).toBeVisible();
  await expect(page.getByText("Còn hàng").first()).toBeVisible();
  await expect(page.getByText("Hết hàng").first()).toBeVisible();

  // Hidden write/cost/catalog/alert controls.
  await expect(page.getByRole("heading", { name: "Cảnh báo kho" })).toHaveCount(0);
  await expect(page.getByLabel("Loại sản phẩm")).toHaveCount(0);
  await expect(page.getByLabel("Trạng thái tồn kho").locator("option[value='low']")).toHaveCount(0);
  await expect(page.getByRole("columnheader", { name: "Tồn kho" })).toHaveCount(0);
  await expect(page.getByRole("columnheader", { name: "Giá vốn BQ" })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Phiếu nhập" })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Điều chỉnh" })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Kiểm kê" })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Tồn đầu kỳ" })).toHaveCount(0);

  // Direct navigation to forbidden sub-routes renders 403 and sends 0 forbidden API calls.
  for (const forbiddenUrl of [
    `/inventory/${varA}/movements`,
    "/inventory/receipts",
    "/inventory/adjustments",
    "/inventory/stocktakes",
    "/inventory/opening-import"
  ]) {
    await page.goto(forbiddenUrl);
    await expect(page.getByRole("heading", { name: "Bạn chưa có quyền truy cập" })).toBeVisible();
  }

  const forbiddenPaths = fx.calls.filter(c =>
    c.path === "product-types"
    || c.path === "inventory/alerts"
    || c.path.includes("/movements")
    || c.path.startsWith("receipts")
    || c.path.startsWith("stocktakes")
    || c.path.includes("opening-import")
    || c.path.includes("adjustments")
  );
  expect(forbiddenPaths).toHaveLength(0);
});

test("P1-13-B02 sales staff sees quantities and alerts without costs or approve actions", async ({ page }) => {
  await setupFixture(page, { permissions: salesPerms, roles: ["sales"] });
  await page.goto("/inventory");

  await expect(page.getByRole("heading", { name: "Cảnh báo kho" })).toBeVisible();
  await expect(page.getByRole("columnheader", { name: "Tồn kho" })).toBeVisible();
  await expect(page.getByRole("columnheader", { name: "Giá vốn BQ" })).toHaveCount(0);
  await expect(page.getByLabel("Trạng thái tồn kho").locator("option[value='low']")).toHaveCount(1);
  await expect(page.getByRole("link", { name: "Tồn đầu kỳ" })).toHaveCount(0);

  await page.goto(`/inventory/${varA}/movements`);
  await expect(page.getByRole("heading", { name: "Sổ kho biến thể" })).toBeVisible();
  await expect(page.getByRole("columnheader", { name: "Đơn giá vốn" })).toHaveCount(0);

  await page.goto("/inventory/adjustments");
  await expect(page.getByLabel("Chiều điều chỉnh").locator("option[value='increase']")).toHaveCount(0);
  await expect(page.getByText(/inventory\.approve/)).toBeVisible();

  await page.goto(`/inventory/stocktakes/${stocktakeSubmitCancelId}`);
  await expect(page.getByRole("button", { name: "Duyệt kiểm kê" })).toHaveCount(0);
  await expect(page.getByText(/Chờ quản lý có quyền/)).toBeVisible();

  await page.goto("/inventory/opening-import");
  await expect(page.getByRole("heading", { name: "Bạn chưa có quyền truy cập" })).toBeVisible();
});

test("P1-13-B03 stock list filters, pagination, and P1 alerts panel with dead outbox and mismatch", async ({ page }) => {
  const extraStock = Array.from({ length: 22 }, (_, i) => ({
    variantId: uid(800 + i),
    productId: prodA,
    productTypeId: typeTeapot,
    sku: `AM-PG-${String(i + 1).padStart(4, "0")}`,
    productName: `Ấm phân trang ${i + 1}`,
    variantName: null,
    availability: "in_stock",
    onHand: 10,
    reserved: 0,
    available: 10,
    lowStock: false,
    avgCostVnd: 100_000
  }));
  const fx = await setupFixture(page, { stockRows: [...baseStockRows(), ...extraStock] });
  await page.goto("/inventory");

  // Alerts panel verifies low stock, missing stock level mismatch, and deadAlertOutboxCount warning.
  await expect(page.getByText(/Có 2 thông điệp cảnh báo kho trong hàng đợi gặp lỗi/)).toBeVisible();
  await expect(page.getByText("Thiếu dòng tồn kho")).toBeVisible();
  await page.getByRole("button", { name: "Làm mới cảnh báo" }).click();
  expect(fx.calls.filter(c => c.path === "inventory/alerts").length).toBeGreaterThanOrEqual(2);

  // Filter by status=low & type.
  await page.getByLabel("Trạng thái tồn kho").selectOption("low");
  await expect(page.getByRole("cell", { name: /CH-MT-0002/ }).first()).toBeVisible();
  await page.getByLabel("Trạng thái tồn kho").selectOption("");
  await page.getByLabel("Loại sản phẩm").selectOption(typeCup);
  await expect(page.getByRole("cell", { name: /CH-MT-0002/ }).first()).toBeVisible();
  await page.getByLabel("Loại sản phẩm").selectOption("");

  // Pagination & search debounce.
  await page.getByRole("button", { name: "Trang sau" }).click();
  await expect(page.getByText(/Trang 2 \//)).toBeVisible();
  await page.getByLabel("Tìm SKU hoặc tên sản phẩm").fill("Thạch Biều");
  await expect(page.getByRole("cell", { name: /AM-TD-0001/ }).first()).toBeVisible();

  await page.getByRole("link", { name: "Xem sổ kho AM-TD-0001" }).click();
  await expect(page).toHaveURL(new RegExp(`/inventory/${varA}/movements$`));
});

test("P1-13-B04 movements filters, cost visibility for owner, date validation, and 404 variant", async ({ page }) => {
  const fx = await setupFixture(page);
  await page.goto(`/inventory/${varA}/movements`);

  await expect(page.getByRole("columnheader", { name: "Đơn giá vốn" })).toBeVisible();
  await expect(page.getByRole("cell", { name: "800.000 đ", exact: true })).toBeVisible();
  await expect(page.getByRole("cell", { name: "0 đ", exact: true })).toBeVisible();

  await page.getByLabel("Loại biến động").selectOption("receipt");
  await expect(page.getByRole("cell", { name: "Tồn đầu kỳ", exact: true })).toHaveCount(0);

  // Invalid date range blocks fetch and shows error.
  await page.getByLabel("Từ ngày").fill("2026-11-23");
  await page.getByLabel("Đến trước ngày").fill("2026-11-23");
  await expect(page.getByText(/Ngày bắt đầu phải nhỏ hơn ngày kết thúc/)).toBeVisible();

  await page.getByLabel("Từ ngày").fill("2026-11-01");
  await page.getByLabel("Đến trước ngày").fill("2026-11-25");
  await expect.poll(() => fx.calls.some(c => c.path === `inventory/${varA}/movements` && c.query.get("from") === "2026-11-01" && c.query.get("to") === "2026-11-25")).toBe(true);

  await page.goto(`/inventory/${uid(999)}/movements`);
  await expect(page.getByRole("heading", { name: "Không tìm thấy biến thể" })).toBeVisible();
});

test("P1-13-B05 opening-import dry-run -> apply, file switch invalidation, committed=false, 400/413/415", async ({ page }) => {
  const fx = await setupFixture(page);
  await page.goto("/inventory/opening-import");
  const fileInput = page.getByLabel("Tệp tồn đầu kỳ (.csv, .xlsx)");
  const dryRunBtn = page.getByRole("button", { name: "Chạy thử (Dry-run)" });
  const applyBtn = page.getByRole("button", { name: "Áp dụng nhập tồn đầu kỳ" });

  // 1) Invalid rows keep Apply disabled.
  await fileInput.setInputFiles({ name: "bad.csv", mimeType: "text/csv", buffer: Buffer.from("sku,quantity,unit_cost_vnd\nBAD,-1,0\n") });
  fx.setOpeningNext(() => ({
    status: 200,
    json: { dryRun: true, committed: false, validRows: 0, invalidRows: 1, rows: [{ row: 2, sku: "BAD", variantId: null, errors: [{ column: "quantity", code: "invalid_quantity", message: "Số lượng không hợp lệ" }] }] }
  }));
  await dryRunBtn.click();
  await expect(page.getByText(/Số lượng không hợp lệ/)).toBeVisible();
  await expect(applyBtn).toBeDisabled();

  // 2) Valid dry-run enables Apply; switching file clears token and disables Apply.
  await fileInput.setInputFiles({ name: "good-1.csv", mimeType: "text/csv", buffer: Buffer.from("sku,quantity,unit_cost_vnd\nAM-TD-0001,5,100000\n") });
  await dryRunBtn.click();
  await expect(applyBtn).toBeEnabled();
  await fileInput.setInputFiles({ name: "good-2.csv", mimeType: "text/csv", buffer: Buffer.from("sku,quantity,unit_cost_vnd\nAM-TD-0001,6,100000\n") });
  await expect(applyBtn).toBeDisabled();

  // 3) 200 committed: false (opening_already_recorded) keeps row errors and clears token.
  await dryRunBtn.click();
  await expect(applyBtn).toBeEnabled();
  fx.setOpeningNext(() => ({
    status: 200,
    json: { dryRun: false, committed: false, validRows: 0, invalidRows: 1, rows: [{ row: 2, sku: "AM-TD-0001", variantId: varA, errors: [{ column: "sku", code: "opening_already_recorded", message: "Biến thể đã có lịch sử kho" }] }] }
  }));
  await applyBtn.click();
  await page.getByRole("dialog").getByRole("button", { name: "Áp dụng nhập tồn đầu kỳ" }).click();
  await expect(page.getByText(/Biến thể đã có lịch sử kho/)).toBeVisible();
  await expect(applyBtn).toBeDisabled();

  // 4) 400 opening_preview_invalid, 413, 415.
  await dryRunBtn.click();
  await expect(applyBtn).toBeEnabled();
  fx.rejectNext({ status: 400, code: "opening_preview_invalid" });
  await applyBtn.click();
  await page.getByRole("dialog").getByRole("button", { name: "Áp dụng nhập tồn đầu kỳ" }).click();
  await expect(page.getByText(/hết hạn 30 phút/)).toBeVisible();
  await expect(applyBtn).toBeDisabled();

  fx.rejectNext({ status: 413, code: "file_limit_exceeded" });
  await dryRunBtn.click();
  await expect(page.getByText(/5 MiB/)).toBeVisible();

  fx.rejectNext({ status: 415, code: "unsupported_format" });
  await dryRunBtn.click();
  await expect(page.getByText(/CSV hoặc XLSX/)).toBeVisible();

  // 5) Successful dry-run -> apply commit.
  await dryRunBtn.click();
  await expect(applyBtn).toBeEnabled();
  await applyBtn.click();
  await page.getByRole("dialog").getByRole("button", { name: "Áp dụng nhập tồn đầu kỳ" }).click();
  await expect(page.getByText(/Đã áp dụng nhập tồn đầu kỳ thành công/)).toBeVisible();
});

test("P1-13-B06 receipt full lifecycle: create draft -> edit -> post retry with identical Idempotency-Key -> cancel", async ({ page }) => {
  const fx = await setupFixture(page);
  await page.goto("/inventory/receipts/new");

  await page.getByLabel("Nhà cung cấp").fill("Xưởng ấm Nghi Hưng");
  await page.getByLabel("Ngày giờ nhận hàng (giờ VN)").fill("2026-11-23T10:15");
  await page.getByRole("button", { name: "Thêm biến thể" }).click();
  await page.getByRole("checkbox", { name: /AM-TD-0001/ }).check();
  await page.getByRole("button", { name: /Thêm 1 biến thể/ }).click();
  await page.locator(`#qty-${varA}`).fill("4");
  await page.locator(`#cost-${varA}`).fill("900000");
  await page.getByRole("button", { name: "Tạo phiếu nhập nháp" }).click();

  await expect(page).toHaveURL(/\/inventory\/receipts\/[0-9a-f-]+$/);
  await page.getByLabel("Ghi chú").fill("Đã kiểm hàng");
  await page.getByRole("button", { name: "Lưu bản nháp" }).click();
  await expect(page.getByText("Đã lưu bản nháp phiếu nhập.")).toBeVisible();

  // First post fails with 502; retry MUST send the exact same Idempotency-Key.
  fx.rejectNext({ status: 502, code: "" });
  await page.getByRole("button", { name: "Chốt phiếu nhập" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Xác nhận chốt phiếu" }).click();
  await expect(page.getByRole("main").getByRole("alert")).toBeVisible();

  await page.getByRole("button", { name: "Chốt phiếu nhập" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Xác nhận chốt phiếu" }).click();
  await expect(page.getByText(/Đã chốt phiếu nhập/)).toBeVisible();
  await expect(page.getByRole("button", { name: "Chốt phiếu nhập" })).toHaveCount(0);

  const postCalls = fx.calls.filter(c => c.path.endsWith("/post"));
  expect(postCalls).toHaveLength(2);
  expect(postCalls[0].headers["idempotency-key"]).toBe(postCalls[1].headers["idempotency-key"]);

  // Cancel another draft receipt.
  await page.goto(`/inventory/receipts/${receiptCancelId}`);
  await page.getByRole("button", { name: "Hủy phiếu nháp" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Xác nhận hủy phiếu" }).click();
  await expect(page.getByText(/Đã hủy phiếu nhập nháp/)).toBeVisible();
  await expect(page.getByRole("button", { name: "Lưu bản nháp" })).toHaveCount(0);
});

test("P1-13-B07 sales without costs.read edits receipt draft omitting existing cost and requiring new line cost", async ({ page }) => {
  const fx = await setupFixture(page, { permissions: salesPerms, roles: ["sales"] });
  await page.goto(`/inventory/receipts/${receiptDraftId}`);

  await expect(page.getByText("800.000 đ")).toHaveCount(0);
  await expect(page.getByText(/Tổng giá trị phiếu nhập/)).toHaveCount(0);

  // Modify existing line quantity without touching blank cost, and add a new variant with cost=0.
  await page.locator(`#qty-${varA}`).fill("6");
  await page.getByRole("button", { name: "Thêm biến thể" }).click();
  await page.getByRole("checkbox", { name: /CH-MT-0002/ }).check();
  await page.getByRole("button", { name: /Thêm 1 biến thể/ }).click();
  await page.locator(`#qty-${varB}`).fill("2");
  await page.locator(`#cost-${varB}`).fill("0");

  await page.getByRole("button", { name: "Lưu bản nháp" }).click();
  await expect(page.getByText("Đã lưu bản nháp phiếu nhập.")).toBeVisible();

  const patchCall = fx.calls.find(c => c.method === "PATCH" && c.path === `receipts/${receiptDraftId}`);
  expect(patchCall).toBeTruthy();
  const lines = patchCall!.body?.lines as Array<Record<string, unknown>>;
  expect(lines).toHaveLength(2);
  expect(lines[0]).toEqual({ variantId: varA, quantity: 6 });
  expect("unitCostVnd" in lines[0]).toBe(false);
  expect(lines[1]).toEqual({ variantId: varB, quantity: 2, unitCostVnd: 0 });
});

test("P1-13-B08 adjustments and damage enforce permissions and preserve form inputs on 409/422 errors", async ({ page }) => {
  const fx = await setupFixture(page, { permissions: ownerPerms });
  await page.goto("/inventory/adjustments");

  await page.getByRole("button", { name: "Chọn biến thể" }).click();
  await page.locator(".inventory-picker-single").filter({ hasText: "AM-TD-0001" }).getByRole("button", { name: "Chọn" }).click();
  await page.getByLabel("Chiều điều chỉnh").selectOption("increase");
  await page.getByLabel("Số lượng (nguyên dương)").fill("3");
  await page.getByLabel(/Lý do điều chỉnh/).fill("Tìm thấy thêm tại kệ phụ");

  // 409 unique_piece_violation preserves every input.
  fx.rejectNext({ status: 409, code: "unique_piece_violation" });
  await page.getByRole("button", { name: "Lưu điều chỉnh" }).click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("độc bản");
  await expect(page.getByLabel("Số lượng (nguyên dương)")).toHaveValue("3");
  await expect(page.getByLabel(/Lý do điều chỉnh/)).toHaveValue("Tìm thấy thêm tại kệ phụ");

  // 422 out_of_stock also preserves every input.
  fx.rejectNext({ status: 422, code: "out_of_stock" });
  await page.getByRole("button", { name: "Lưu điều chỉnh" }).click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("đang giữ");
  await expect(page.getByLabel(/Lý do điều chỉnh/)).toHaveValue("Tìm thấy thêm tại kệ phụ");

  // Retry succeeds for owner increase.
  await page.getByRole("button", { name: "Lưu điều chỉnh" }).click();
  await expect(page.getByText(/Tồn kho sau điều chỉnh/)).toBeVisible();
  await expect(page.getByRole("link", { name: "Xem sổ kho biến thể" })).toBeVisible();

  // F2: sales submits negative damage adjustment and asserts exact POST payload.
  const salesPage = await page.context().newPage();
  try {
    const fxSales = await setupFixture(salesPage, { permissions: salesPerms, roles: ["sales"] });
    await salesPage.goto("/inventory/adjustments");
    await expect(salesPage.getByLabel("Chiều điều chỉnh").locator("option[value='increase']")).toHaveCount(0);
    await salesPage.getByRole("button", { name: "Chọn biến thể" }).click();
    await salesPage.locator(".inventory-picker-single").filter({ hasText: "AM-TD-0001" }).getByRole("button", { name: "Chọn" }).click();
    await salesPage.getByLabel("Loại nghiệp vụ").selectOption("damage");
    await salesPage.getByLabel("Số lượng (nguyên dương)").fill("2");
    await salesPage.getByLabel(/Lý do điều chỉnh/).fill("Vỡ nắp ấm khi vận chuyển");
    await salesPage.getByRole("button", { name: "Lưu điều chỉnh" }).click();
    await expect(salesPage.getByText(/Tồn kho sau điều chỉnh/)).toBeVisible();

    const damageCalls = fxSales.calls.filter(c => c.method === "POST" && c.path === "inventory/adjustments");
    expect(damageCalls).toHaveLength(1);
    expect(damageCalls[0].body).toEqual({
      variantId: varA,
      type: "damage",
      quantityDelta: -2,
      reason: "Vỡ nắp ấm khi vận chuyển"
    });
  } finally {
    await salesPage.close();
  }
});

test("P1-13-B09 stocktake lifecycle: create -> sequential counts advance rowVersion -> submit -> approve -> cancel", async ({ page }) => {
  const fx = await setupFixture(page);
  await page.goto("/inventory/stocktakes");

  await page.getByLabel(/Ghi chú phiên kiểm kê/).fill("Kiểm kê tháng 11");
  await page.getByRole("button", { name: "Tạo phiên kiểm kê" }).click();
  await expect(page).toHaveURL(/\/inventory\/stocktakes\/[0-9a-f-]+$/);

  // Count variant A.
  await page.getByRole("button", { name: "Chọn biến thể" }).click();
  await page.locator(".inventory-picker-single").filter({ hasText: "AM-TD-0001" }).getByRole("button", { name: "Chọn" }).click();
  await page.getByLabel(/Số đếm thực tế/).fill("6");
  await page.getByRole("button", { name: "Lưu số đếm" }).click();
  await expect(page.getByText("Đã lưu số đếm kiểm kê.")).toBeVisible();

  // Count variant B — must send updated rowVersion from count 1.
  await page.getByRole("button", { name: "Chọn biến thể" }).click();
  await page.locator(".inventory-picker-single").filter({ hasText: "CH-MT-0002" }).getByRole("button", { name: "Chọn" }).click();
  await page.getByLabel(/Số đếm thực tế/).fill("1");
  await page.getByRole("button", { name: "Lưu số đếm" }).click();
  await expect(page.getByRole("cell", { name: "+1" })).toBeVisible();
  await expect(page.getByRole("cell", { name: "-1" })).toBeVisible();

  const countCalls = fx.calls.filter(c => c.method === "PUT" && c.path.includes("/lines/"));
  expect(countCalls).toHaveLength(2);
  expect(countCalls[0].body?.rowVersion).not.toBe(countCalls[1].body?.rowVersion);

  // Submit & approve.
  await page.getByRole("button", { name: "Gửi duyệt" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Xác nhận gửi duyệt" }).click();
  await expect(page.getByText(/Đã gửi duyệt phiên kiểm kê/)).toBeVisible();

  await page.getByRole("button", { name: "Duyệt kiểm kê" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Xác nhận duyệt kiểm kê" }).click();
  await expect(page.getByText(/Đã duyệt phiên kiểm kê/)).toBeVisible();

  // Cancel from counting and from submitted.
  await page.goto(`/inventory/stocktakes/${stocktakeId}`);
  await page.getByRole("button", { name: "Hủy phiên" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Xác nhận hủy phiên" }).click();
  await expect(page.getByText(/Đã hủy phiên kiểm kê/)).toBeVisible();

  await page.goto(`/inventory/stocktakes/${stocktakeSubmitCancelId}`);
  await page.getByRole("button", { name: "Hủy phiên" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Xác nhận hủy phiên" }).click();
  await expect(page.getByText(/Đã hủy phiên kiểm kê/)).toBeVisible();
});

test("P1-13-B10 stocktake approve maps per-line 409/422 errors and guides stale count recovery", async ({ page }) => {
  const fx = await setupFixture(page);
  await page.goto(`/inventory/stocktakes/${stocktakeSubmitCancelId}`);

  fx.rejectNext({
    status: 409,
    code: "stocktake_lines_invalid",
    errors: {
      [`lines[${varA}]`]: ["stocktake_count_stale"],
      [`lines[${varB}]`]: ["out_of_stock"],
      [`lines[${varC}]`]: ["unique_piece_violation"]
    }
  });
  await page.getByRole("button", { name: "Duyệt kiểm kê" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Xác nhận duyệt kiểm kê" }).click();

  await expect(page.getByText(/Vui lòng hủy phiên kiểm kê hiện tại và tạo phiên mới để đếm lại/)).toBeVisible();
  await expect(page.getByText(/Hàng độc bản chỉ cho phép tối đa 1/)).toBeVisible();
  await expect(page.getByText(/Tồn kho không đủ hoặc nhỏ hơn số lượng đang giữ/)).toBeVisible();
  // Approve remains usable (not misclassified as concurrency_conflict) and Cancel is ready.
  await expect(page.getByRole("button", { name: "Hủy phiên" })).toBeEnabled();
});

test("P1-13-B11 409 concurrency_conflict preserves unsaved drafts and recovers via explicit reload on receipt & stocktake", async ({ page }) => {
  const fx = await setupFixture(page);

  // 1) Receipt draft concurrency_conflict.
  await page.goto(`/inventory/receipts/${receiptDraftId}`);
  await page.getByLabel("Nhà cung cấp").fill("Nhà cung cấp đang sửa");
  fx.mutateReceiptRemote(receiptDraftId, { supplierName: "Bản mới từ máy khác", rowVersion: "rcpt-remote-2" });

  await page.getByRole("button", { name: "Lưu bản nháp" }).click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("xung đột phiên bản");
  await expect(page.getByLabel("Nhà cung cấp")).toHaveValue("Nhà cung cấp đang sửa");
  await expect(page.getByRole("button", { name: "Lưu bản nháp" })).toBeDisabled();

  await page.getByRole("button", { name: "Bỏ thay đổi và tải phiên bản mới" }).click();
  await expect(page.getByLabel("Nhà cung cấp")).toHaveValue("Bản mới từ máy khác");
  await page.getByLabel("Nhà cung cấp").fill("Lưu sau khi đối chiếu");
  await page.getByRole("button", { name: "Lưu bản nháp" }).click();
  await expect(page.getByText("Đã lưu bản nháp phiếu nhập.")).toBeVisible();

  // 2) Stocktake counting concurrency_conflict.
  await page.goto(`/inventory/stocktakes/${stocktakeId}`);
  await page.getByRole("button", { name: "Chọn biến thể" }).click();
  await page.locator(".inventory-picker-single").filter({ hasText: "AM-TD-0001" }).getByRole("button", { name: "Chọn" }).click();
  await page.getByLabel(/Số đếm thực tế/).fill("9");
  fx.mutateStocktakeRemote(stocktakeId, { rowVersion: "st-remote-2" });

  await page.getByRole("button", { name: "Lưu số đếm" }).click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("xung đột phiên bản");
  await expect(page.getByLabel(/Số đếm thực tế/)).toHaveValue("9");
  await expect(page.getByRole("button", { name: "Lưu số đếm" })).toBeDisabled();

  await page.getByRole("button", { name: "Bỏ thay đổi và tải phiên bản mới" }).click();
  await expect(page.getByRole("button", { name: "Lưu số đếm" })).toBeEnabled();
});

test("P1-13-B12 receipt form preserves lines on 400, 403, 409 and 422 unique_piece_violation without retry loop", async ({ page }) => {
  const fx = await setupFixture(page);
  await page.goto(`/inventory/receipts/${receiptDraftId}`);
  await page.locator(`#qty-${varA}`).fill("7");

  for (const err of [
    { status: 400, code: "validation_failed", errors: { supplierName: ["Tên không hợp lệ"] } },
    { status: 403, code: "forbidden" },
    { status: 409, code: "unique_piece_violation", errors: { [`lines[${varA}]`]: ["unique_piece_violation"] } },
    { status: 422, code: "unique_piece_violation", errors: { [`lines[${varA}]`]: ["unique_piece_violation"] } }
  ]) {
    const beforeWrites = fx.calls.filter(c => c.method === "PATCH").length;
    fx.rejectNext(err);
    await page.getByRole("button", { name: "Lưu bản nháp" }).click();
    await expect(page.locator(`#qty-${varA}`)).toHaveValue("7");
    await expect(page.getByRole("button", { name: "Lưu bản nháp" })).toBeEnabled();
    expect(fx.calls.filter(c => c.method === "PATCH").length).toBe(beforeWrites + 1);
  }
  await expect(page.getByText(/Hàng độc bản chỉ cho phép tối đa 1/)).toBeVisible();
});

test("P1-13-B13 mobile 390x844 and desktop 1440x1000 layout, touch targets >= 44px, and axe-core 0 violations", async ({ page }) => {
  await setupFixture(page);
  await page.setViewportSize({ width: 390, height: 844 });

  const routes: Array<{ url: string; shot: string }> = [
    { url: "/inventory", shot: "mobile-stock.png" },
    { url: `/inventory/${varA}/movements`, shot: "mobile-movements.png" },
    { url: "/inventory/receipts", shot: "mobile-receipts.png" },
    { url: `/inventory/receipts/${receiptDraftId}`, shot: "mobile-receipt-detail.png" },
    { url: "/inventory/adjustments", shot: "mobile-adjustments.png" },
    { url: `/inventory/stocktakes/${stocktakeSubmitCancelId}`, shot: "mobile-stocktake-detail.png" },
    { url: "/inventory/opening-import", shot: "mobile-opening-import.png" }
  ];

  for (const item of routes) {
    await page.goto(item.url);
    await expect(page.locator(".inventory-admin")).toBeVisible();
    const noOverflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);
    expect(noOverflow, `Horizontal overflow on ${item.url}`).toBe(true);

    const smallControls = await page.evaluate(() => {
      const els = Array.from(document.querySelectorAll(".inventory-admin button, .inventory-admin input:not([type='checkbox']), .inventory-admin select"));
      return els
        .filter(el => {
          const rect = el.getBoundingClientRect();
          return rect.width > 0 && rect.height > 0 && rect.height < 44;
        })
        .map(el => ({ tag: el.tagName, text: el.textContent?.trim() ?? "", h: Math.round(el.getBoundingClientRect().height) }));
    });
    expect(smallControls, `Controls < 44px on ${item.url}`).toEqual([]);

    const scan = await new AxeBuilder({ page }).include(".inventory-admin").analyze();
    expect(scan.violations, `Axe violations on ${item.url}`).toEqual([]);

    await page.screenshot({ path: `../../artifacts/p1-13/${item.shot}`, fullPage: false });
  }

  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto("/inventory");
  await expect(page.locator(".inventory-admin")).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  const desktopScan = await new AxeBuilder({ page }).include(".inventory-admin").analyze();
  expect(desktopScan.violations).toEqual([]);
  await page.screenshot({ path: "../../artifacts/p1-13/desktop-stock.png", fullPage: false });
});

test("P1-13-B14 Escape closes dialog, dirty receipt guards leave, and 401 clears inventory cache to /login", async ({ page }) => {
  const fx = await setupFixture(page);
  await page.goto(`/inventory/receipts/${receiptDraftId}`);

  // 1) Escape closes confirmation dialog.
  await page.getByRole("button", { name: "Hủy phiếu nháp" }).click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);

  // 2) Dirty form blocks leaving when user dismisses confirm dialog.
  await page.getByLabel("Ghi chú").fill("Chưa lưu");
  page.once("dialog", dialog => dialog.dismiss());
  await page.getByRole("link", { name: /Danh sách phiếu nhập/ }).click();
  await expect(page).toHaveURL(new RegExp(`/inventory/receipts/${receiptDraftId}$`));
  await expect(page.getByLabel("Ghi chú")).toHaveValue("Chưa lưu");

  // 3) 401 unauthenticated clears session/inventory cache and redirects to /login.
  fx.rejectNext({ status: 401, code: "unauthenticated" });
  await page.getByRole("button", { name: "Lưu bản nháp" }).click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByText("PN-261123-1")).toHaveCount(0);
});

test("P1-13-B15 F1 VariantPicker resets selections/search on close and confirm without re-inserting removed lines", async ({ page }) => {
  const fx = await setupFixture(page);

  // 1) Create receipt: add A -> remove A -> reopen picker -> count is 0, select B -> only B added & posted.
  await page.goto("/inventory/receipts/new");
  await page.getByRole("button", { name: "Thêm biến thể" }).click();
  await page.getByRole("checkbox", { name: /AM-TD-0001/ }).check();
  await page.getByRole("button", { name: "Thêm 1 biến thể" }).click();
  await expect(page.locator(`#qty-${varA}`)).toBeVisible();

  // Remove A from draft.
  await page.getByRole("button", { name: /Xóa dòng AM-TD-0001/ }).click();
  await expect(page.locator(`#qty-${varA}`)).toHaveCount(0);

  // Reopen picker: count MUST be 0 and A MUST be unchecked; check A then close with "Đóng" -> reopen still clean.
  await page.getByRole("button", { name: "Thêm biến thể" }).click();
  await expect(page.getByRole("dialog").getByText("Đã chọn 0 biến thể.")).toBeVisible();
  await expect(page.getByRole("checkbox", { name: /AM-TD-0001/ })).not.toBeChecked();
  await page.getByLabel("Tìm theo SKU hoặc tên sản phẩm").fill("AM-TD");
  await page.getByRole("checkbox", { name: /AM-TD-0001/ }).check();
  await page.getByRole("dialog").getByRole("button", { name: "Đóng", exact: true }).click();

  // Reopen again after close: search and checkbox A MUST be reset; select only B and add.
  await page.getByRole("button", { name: "Thêm biến thể" }).click();
  await expect(page.getByLabel("Tìm theo SKU hoặc tên sản phẩm")).toHaveValue("");
  await expect(page.getByRole("dialog").getByText("Đã chọn 0 biến thể.")).toBeVisible();
  await expect(page.getByRole("checkbox", { name: /AM-TD-0001/ })).not.toBeChecked();
  await page.getByRole("checkbox", { name: /CH-MT-0002/ }).check();
  await page.getByRole("button", { name: "Thêm 1 biến thể" }).click();

  await expect(page.locator(`#qty-${varB}`)).toBeVisible();
  await expect(page.locator(`#qty-${varA}`)).toHaveCount(0);
  await page.locator(`#qty-${varB}`).fill("3");
  await page.locator(`#cost-${varB}`).fill("50000");
  await page.getByRole("button", { name: "Tạo phiếu nhập nháp" }).click();
  await expect(page).toHaveURL(/\/inventory\/receipts\/[0-9a-f-]+$/);

  const createCall = fx.calls.find(c => c.method === "POST" && c.path === "receipts");
  expect(createCall).toBeTruthy();
  expect(createCall!.body?.lines).toEqual([{ variantId: varB, quantity: 3, unitCostVnd: 50_000 }]);

  // 2) Existing draft PATCH: remove B, reopen picker -> count 0, add C -> PATCH sends only C.
  await page.getByRole("button", { name: /Xóa dòng CH-MT-0002/ }).click();
  await page.getByRole("button", { name: "Thêm biến thể" }).click();
  await expect(page.getByRole("dialog").getByText("Đã chọn 0 biến thể.")).toBeVisible();
  await expect(page.getByRole("checkbox", { name: /CH-MT-0002/ })).not.toBeChecked();
  await page.getByRole("checkbox", { name: /CH-MT-0003/ }).check();
  await page.getByRole("button", { name: "Thêm 1 biến thể" }).click();
  await page.locator(`#qty-${varC}`).fill("1");
  await page.locator(`#cost-${varC}`).fill("120000");
  await page.getByRole("button", { name: "Lưu bản nháp" }).click();
  await expect(page.getByText("Đã lưu bản nháp phiếu nhập.")).toBeVisible();

  const patchCall = fx.calls.find(c => c.method === "PATCH" && c.path.startsWith("receipts/"));
  expect(patchCall).toBeTruthy();
  expect(patchCall!.body?.lines).toEqual([{ variantId: varC, quantity: 1, unitCostVnd: 120_000 }]);
});

test("P1-13-B16 F3 direct-URL hydration stops early on page 2 and falls back to short variantId + working copy button beyond 500 variants", async ({ page }) => {
  const bigCatalog: StockRow[] = Array.from({ length: 600 }, (_, i) => ({
    variantId: uid(2000 + i + 1),
    productId: prodA,
    productTypeId: typeTeapot,
    sku: `SKU-CAT-${String(i + 1).padStart(4, "0")}`,
    productName: `Sản phẩm kho ${i + 1}`,
    variantName: null,
    availability: "in_stock",
    onHand: 5,
    reserved: 0,
    available: 5,
    lowStock: false,
    avgCostVnd: 100_000
  }));
  const page2VarId = uid(2150); // index 149 -> page 2 (items 101..200)
  const beyond500VarId = uid(2550); // index 549 -> page 6 (outside 5x100 hydration cap)
  const earlyReceiptId = uid(481);
  const fallbackReceiptId = uid(482);

  await page.addInitScript(() => {
    (window as unknown as { __copiedTexts?: string[] }).__copiedTexts = [];
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: {
        writeText: async (text: string) => {
          (window as unknown as { __copiedTexts: string[] }).__copiedTexts.push(text);
        }
      }
    });
  });

  const fx = await setupFixture(page, {
    stockRows: bigCatalog,
    receipts: {
      [earlyReceiptId]: {
        document: {
          id: earlyReceiptId,
          code: "PN-261123-81",
          status: "draft",
          supplierName: "Xưởng thử trang 2",
          note: null,
          receivedAt: null,
          createdAt: stamp,
          createdBy: actorId,
          postedAt: null,
          postedBy: null,
          rowVersion: "early-v1"
        },
        lines: [{ variantId: page2VarId, quantity: 2, unitCostVnd: 100_000 }]
      },
      [fallbackReceiptId]: {
        document: {
          id: fallbackReceiptId,
          code: "PN-261123-82",
          status: "draft",
          supplierName: "Xưởng thử ngoài 500",
          note: null,
          receivedAt: null,
          createdAt: stamp,
          createdBy: actorId,
          postedAt: null,
          postedBy: null,
          rowVersion: "fb-v1"
        },
        lines: [{ variantId: beyond500VarId, quantity: 1, unitCostVnd: 100_000 }]
      }
    }
  });

  // 1) Direct URL with variant on page 2 stops early after 2 pages (not 5) and renders real SKU.
  await page.goto(`/inventory/receipts/${earlyReceiptId}`);
  await expect(page.getByText("SKU-CAT-0150")).toBeVisible();
  const earlyHydrateCalls = fx.calls.filter(c => c.method === "GET" && c.path === "inventory");
  expect(earlyHydrateCalls).toHaveLength(2);
  expect(earlyHydrateCalls.map(c => c.query.get("page"))).toEqual(["1", "2"]);

  // 2) Direct URL with variant beyond 500 caps at 5 pages, shows short UUID (no fabricated SKU), and Copy works.
  const callsBeforeFallback = fx.calls.filter(c => c.method === "GET" && c.path === "inventory").length;
  await page.goto(`/inventory/receipts/${fallbackReceiptId}`);
  const shortId = `${beyond500VarId.slice(0, 8)}…${beyond500VarId.slice(-4)}`;
  await expect(page.locator("code", { hasText: shortId })).toBeVisible();
  await expect(page.getByText("SKU-CAT-0550")).toHaveCount(0);

  await expect.poll(() => fx.calls.filter(c => c.method === "GET" && c.path === "inventory").length - callsBeforeFallback).toBe(5);
  const copyBtn = page.getByRole("button", { name: `Sao chép mã biến thể ${beyond500VarId}` });
  await copyBtn.click();
  await expect(copyBtn).toContainText("Đã sao chép");
  const copied = await page.evaluate(() => (window as unknown as { __copiedTexts: string[] }).__copiedTexts);
  expect(copied).toEqual([beyond500VarId]);
});
