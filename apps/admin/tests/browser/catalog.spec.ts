import { test, expect, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import type { components } from "@teapremium/api-client";
import type { Auth } from "../../src/lib/api";
import type { CatalogRow, TypeRow, AttributeRow, OptionRow } from "../../src/lib/catalog-api";

const id = "00000000-0000-7000-8000-000000000011";
const productA = "00000000-0000-7000-8000-000000000021";
const productB = "00000000-0000-7000-8000-000000000022";
const mediaId = "00000000-0000-7000-8000-000000000031";
const version = "opaque-version+/==";
const ready: Auth = { passwordChangeRequired: false, totpRequired: false, user: {
  id: "staff-fixture", email: "synthetic@example.invalid", displayName: "Minh An", isActive: true,
  mustChangePassword: false, totpEnabled: true, roles: ["owner"], rowVersion: "fixture",
  permissions: ["catalog.read", "catalog.write", "catalog.publish"]
} };
const taxonomy = (name: string, extra: Partial<CatalogRow> = {}): CatalogRow => ({
  id, rowVersion: version, name, slug: "thu-nghiem", status: "draft", parentId: null, sortOrder: 0,
  kind: null, skuCode: null, summary: null, title: null, descriptionHtml: null, descriptionJson: null,
  bioHtml: null, bioJson: null, bodyHtml: null, bodyJson: null, coverMediaId: null, portraitMediaId: null,
  focusKeyword: null, seoTitle: null, seoDescription: null, robotsNoindex: false, firstPublishedAt: null,
  resolvedSeoTitle: name, resolvedSeoDescription: "Mô tả từ hệ thống", ...extra
});
const typeRow: TypeRow = { id, rowVersion: version, code: "am", name: "Ấm trà", skuPrefix: "AM", description: null, googleProductCategory: null, sortOrder: 0, isActive: true, createdAt: "2026-11-20T00:00:00Z", updatedAt: "2026-11-20T00:00:00Z" };
const attrRow: AttributeRow = { id: "attr", productTypeId: id, rowVersion: version, code: "color", name: "Màu sắc", dataType: "option", scope: "variant", unit: null, isVariantAxis: true, isRequiredForPublish: true, isFilterable: true, isVisible: true, isActive: true, sortOrder: 0 };
const optionRow: OptionRow = { id: "option", attributeId: "attr", rowVersion: version, code: "red", label: "Đỏ", isActive: true, sortOrder: 0 };
const productRows: components["schemas"]["ProductData"][] = Array.from({ length: 24 }, (_, index) => ({
  id: index === 0 ? productA : index === 1 ? productB : `00000000-0000-7000-8000-${String(index + 23).padStart(12, "0")}`,
  name: index === 0 ? "Ấm tử sa" : index === 1 ? "Chén men ngọc" : `Sản phẩm ${index + 1}`,
  skuBase: `AM-XX-${String(index + 1).padStart(5, "0")}`, status: ["draft", "active", "archived"][index % 3],
  variants: [{ sku: `VARIANT-${index + 1}` }], media: index === 0 ? [{ isCover: true, url: "/synthetic-cover.svg", altText: "Ấm tử sa thật" }] : []
}));
type Call = { method: string; path: string; body: Record<string, unknown> | null; csrf?: string; search: string };

async function backend(page: Page, options: { permissions?: string[]; empty?: boolean; products?: boolean; delay?: number } = {}) {
  await page.route("**/synthetic-cover.svg", route => route.fulfill({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" width="56" height="56"><rect width="56" height="56" fill="#e4dbcc"/><circle cx="28" cy="28" r="16" fill="#75543c"/></svg>' }));
  const calls: Call[] = []; let csrf = 0; let nextError: { status: number; code: string; method: string } | undefined; let expired = false;
  let putGate: Promise<void> | undefined;
  const records: Record<string, CatalogRow[]> = {
    materials: [taxonomy("Tử nê", { kind: "clay", skuCode: "TN", descriptionHtml: "<p>Nội dung cũ <strong>in đậm</strong></p>", descriptionJson: '{"type":"doc"}' })],
    artisans: [taxonomy("Chu Bân", { bioHtml: "<p>Tiểu sử cũ</p>" })],
    collections: [taxonomy("Trà cụ mùa thu", { status: "hidden" })],
    categories: [taxonomy("Trà cụ", { status: "visible" }), taxonomy("Ấm", { id: "parent", parentId: id, status: "hidden" }), taxonomy("Ấm nhỏ", { id: "child", parentId: "parent", status: "visible" })]
  };
  let types = [typeRow], attrs = [attrRow], opts = [optionRow];
  let relations: components["schemas"]["CollectionProductList"] = { id, rowVersion: version, items: options.products ? [{ productId: productA, sortOrder: 3 }, { productId: productB, sortOrder: 7 }] : [] };
  await page.route("**/api/**", async route => {
    const request = route.request(), path = new URL(request.url()).pathname, method = request.method();
    const body = request.postDataJSON() as Record<string, unknown> | null;
    calls.push({ path, method, body, csrf: request.headers()["x-csrf-token"], search: new URL(request.url()).search });
    const json = (value: unknown, status = 200) => route.fulfill({ status, contentType: "application/json", body: JSON.stringify(value) });
    if (path.endsWith("/auth/me")) return expired ? json({ code: "unauthenticated" }, 401) : json({ ...ready, user: { ...ready.user, permissions: options.permissions ?? ready.user.permissions } });
    if (path.endsWith("/auth/csrf")) return json({ token: `csrf-${++csrf}` });
    if (method !== "GET") expect(request.headers()["x-csrf-token"]).toBe(`csrf-${csrf}`);
    if (method === "PUT" && putGate) await putGate;
    if (nextError && method === nextError.method) {
      const error = nextError; nextError = undefined; if (error.status === 401) expired = true;
      return json({ code: error.code, errors: error.code === "validation_failed" ? { Name: ["Tên bị từ chối bởi máy chủ."] } : {} }, error.status);
    }
    const parts = path.replace("/api/v1/admin/", "").split("/"); const [resource, entityId] = parts;
    const query = new URL(request.url()).searchParams;
    const list = (items: unknown[]) => json({ items: options.empty ? [] : items, page: Number(query.get("page") ?? 1), pageSize: Number(query.get("pageSize") ?? 20), totalItems: options.empty ? 0 : items.length });
    if (resource === "media") return list([
      { id: mediaId, purpose: "catalog", mimeType: "image/webp", defaultAlt: "Chân dung công khai", url: "https://images.example.invalid/portrait.webp", urlExpiresAt: null },
      { id: "private", purpose: "evidence", mimeType: "image/webp", defaultAlt: "Bằng chứng riêng tư", urlExpiresAt: "2026-11-21T00:00:00Z" }
    ]);
    if (resource === "product-types") {
      const items = parts.includes("options") ? opts : parts.includes("attributes") ? attrs : types;
      const detail = parts.length % 2 === 0;
      if (method === "GET") return detail ? json(items[0]) : list(items);
      const updated = { ...items[0], ...body, rowVersion: "reference-new-version" };
      if (parts.includes("options")) opts = [updated as OptionRow]; else if (parts.includes("attributes")) attrs = [updated as AttributeRow]; else types = [updated as TypeRow];
      return json(updated, method === "POST" ? 201 : 200);
    }
    if (resource === "products") {
      expect(method).toBe("GET");
      if (entityId) return json(productRows.find(x => x.id === entityId));
      const q = query.get("q")?.toLowerCase() || "", status = query.get("status");
      const filtered = productRows.filter(x => (!q || `${x.name} ${x.skuBase} ${x.variants?.[0].sku}`.toLowerCase().includes(q)) && (!status || x.status === status));
      const pageNo = Number(query.get("page") || 1), size = Number(query.get("pageSize") || 20);
      expect(size).toBe(20); expect(query.has("stock")).toBe(false);
      return json({ items: filtered.slice((pageNo - 1) * size, pageNo * size), page: pageNo, pageSize: size, totalItems: filtered.length });
    }
    if (!records[resource]) throw new Error(`Unimplemented API called: ${method} ${path}`);
    if (parts[2] === "products") {
      if (method === "PUT" && (body?.rowVersion !== relations.rowVersion || body.rowVersion !== records.collections[0].rowVersion)) return json({ code: "concurrency_conflict" }, 409);
      if (method === "PUT") { relations = { id, rowVersion: "relation-new-version", items: body!.items as typeof relations.items }; records.collections[0].rowVersion = relations.rowVersion; }
      return json(relations);
    }
    if (parts[2] === "publish") {
      records[resource][0] = { ...records[resource][0], status: "published", rowVersion: "published-version" };
      return json({ id, status: "published", rowVersion: "published-version", warnings: [{ code: "content_below_recommended_words", field: resource === "artisans" ? "bioHtml" : "descriptionHtml", actualWords: 3, recommendedWords: 400 }] });
    }
    if (method === "GET") {
      if (options.delay) await new Promise(resolve => setTimeout(resolve, options.delay));
      if (entityId) return records[resource].some(x => x.id === entityId) ? json(records[resource].find(x => x.id === entityId)) : json({ code: "not_found" }, 404);
      const q = query.get("q"), status = query.get("status");
      return list(records[resource].filter(x => (!q || x.name.includes(q)) && (!status || x.status === status)));
    }
    if (method === "PATCH" && body?.rowVersion !== records[resource][0].rowVersion) return json({ code: "concurrency_conflict" }, 409);
    const updated = { ...records[resource][0], ...body, rowVersion: "saved-version" };
    records[resource][0] = updated;
    if (resource === "collections") relations.rowVersion = updated.rowVersion;
    return json(updated, method === "POST" ? 201 : 200);
  });
  return { calls, reject: (status: number, code: string, method = "PATCH") => { nextError = { status, code, method }; },
    holdPut: () => { let release!: () => void; putGate = new Promise<void>(resolve => { release = resolve; }); return () => { release(); putGate = undefined; }; },
    changeCollection: (values: Partial<CatalogRow>) => { records.collections[0] = { ...records.collections[0], ...values }; relations.rowVersion = records.collections[0].rowVersion; }
  };
}

test("P1-11 product types list/edit uses generated contract and CSRF", async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog");
  await page.getByRole("button", { name: "Chỉnh sửa am", exact: true }).click();
  await page.getByLabel("Tên", { exact: true }).fill("Ấm pha trà");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
  const call = server.calls.find(x => x.method === "PATCH")!;
  expect(call.body).toMatchObject({ name: "Ấm pha trà", rowVersion: version, skuPrefix: "AM", isActive: true }); expect(call.csrf).toBeTruthy();
});

test("P1-11 create type/attribute/option validates fields before POST", async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog/product-types");
  await page.getByRole("button", { name: "Thêm mới", exact: true }).click();
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByLabel("Tên", { exact: true })).toHaveAttribute("aria-invalid", "true");
  expect(server.calls.filter(x => x.method === "POST")).toHaveLength(0);
  await page.getByLabel("Tên", { exact: true }).fill("Chén trà"); await page.getByLabel("Mã", { exact: true }).fill("chen");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
  expect(server.calls.find(x => x.method === "POST")?.body).not.toHaveProperty("rowVersion");
  await page.getByRole("button", { name: "Thuộc tính chen", exact: true }).click();
  await page.getByRole("button", { name: "Thêm mới", exact: true }).click();
  await page.getByLabel("Tên", { exact: true }).fill("Men"); await page.getByLabel("Mã", { exact: true }).fill("glaze");
  await page.getByLabel("Kiểu dữ liệu (dataType)").selectOption("option");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await page.getByRole("button", { name: "Lựa chọn glaze", exact: true }).click();
  await page.getByRole("button", { name: "Thêm mới", exact: true }).click();
  await page.getByLabel("Nhãn lựa chọn").fill("Men ngọc"); await page.getByLabel("Mã", { exact: true }).fill("jade");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByRole("cell", { name: "Men ngọc", exact: true })).toBeVisible();
  expect(server.calls.filter(x => x.method === "POST")).toHaveLength(3);
});

for (const resource of ["categories", "collections", "materials", "artisans"] as const) test(`P1-11 create ${resource}: safe default, automatic slug, no publication bypass`, async ({ page }) => {
  const server = await backend(page); await page.goto(`/catalog/${resource}`); await page.getByRole("button", { name: "Thêm mới", exact: true }).click();
  await page.getByLabel("Tên", { exact: true }).fill("Tên mới");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
  const body = server.calls.find(x => x.method === "POST")!.body!;
  expect(body.name).toBe("Tên mới"); expect(body.status).toBe(resource === "materials" || resource === "artisans" ? "draft" : "hidden");
  expect(body).not.toHaveProperty("slug"); expect(body).not.toHaveProperty("rowVersion");
});

test("P1-11 attribute axis/unit validation and options edit", async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog/product-types");
  await page.getByRole("button", { name: "Thuộc tính am", exact: true }).click();
  await expect(page.getByRole("cell", { name: /option \/ variant/ })).toBeVisible();
  await page.getByRole("button", { name: "Chỉnh sửa color" }).click();
  await page.getByLabel("Đơn vị", { exact: true }).fill("ml");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Đơn vị chỉ dùng cho số hoặc kích thước.")).toBeVisible();
  expect(server.calls.filter(x => x.method === "PATCH")).toHaveLength(0);
  await page.getByLabel("Đơn vị", { exact: true }).fill("");
  await page.getByLabel("Phạm vi (scope)").selectOption("product");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Trục cần phạm vi biến thể và kiểu văn bản, số hoặc lựa chọn.")).toBeVisible();
  await page.getByLabel("Phạm vi (scope)").selectOption("variant");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
  await page.getByRole("button", { name: "Lựa chọn color" }).click();
  await page.getByRole("button", { name: "Chỉnh sửa red" }).click();
  await page.getByLabel("Nhãn lựa chọn").fill("Đỏ nâu");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByRole("cell", { name: "Đỏ nâu", exact: true })).toBeVisible();
  expect(server.calls.filter(x => x.method === "PATCH").at(-1)?.body).toMatchObject({ label: "Đỏ nâu", rowVersion: version });
});

test("P1-11 category full hierarchy, invalid parents excluded, visibility metadata", async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog/categories");
  await expect(page.getByRole("cell", { name: "Trà cụ / Ấm / Ấm nhỏ", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Chỉnh sửa Trà cụ", exact: true }).click();
  const parents = page.getByLabel("Danh mục cha"); await expect(parents.getByRole("option")).toHaveCount(1);
  await page.getByLabel("Trạng thái", { exact: true }).selectOption("hidden");
  await page.getByLabel("Thứ tự", { exact: true }).fill("8");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
  expect(server.calls.find(x => x.method === "PATCH")?.body).toMatchObject({ parentId: null, status: "hidden", sortOrder: 8, slug: "thu-nghiem", rowVersion: version });
});

for (const resource of ["materials", "artisans"] as const) test(`P1-11 ${resource}: form, safe content, publication confirmation and warnings`, async ({ page }) => {
  const server = await backend(page); await page.goto(`/catalog/${resource}`);
  await page.getByRole("button", { name: resource === "materials" ? "Chỉnh sửa Tử nê" : "Chỉnh sửa Chu Bân" }).click();
  await expect(page.getByLabel("Trạng thái", { exact: true }).getByRole("option", { name: "Đã xuất bản" })).toHaveCount(0);
  const contentLabel = resource === "materials" ? "Mô tả" : "Tiểu sử";
  await page.getByLabel(contentLabel, { exact: true }).fill('<script>alert("x")</script>');
  await expect(page.getByRole("button", { name: "Xuất bản", exact: true })).toBeDisabled();
  await page.getByLabel("Tiêu đề SEO", { exact: true }).fill("Tiêu đề mới");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
  expect(server.calls.find(x => x.method === "PATCH")?.body?.[resource === "materials" ? "descriptionHtml" : "bioHtml"]).toContain("&lt;script&gt;");
  await page.getByRole("button", { name: resource === "materials" ? "Chỉnh sửa Tử nê" : "Chỉnh sửa Chu Bân" }).click();
  await page.getByRole("button", { name: "Xuất bản", exact: true }).click();
  await expect(page.getByRole("dialog")).toBeVisible();
  expect(server.calls.filter(x => x.path.endsWith("/publish"))).toHaveLength(0);
  await page.keyboard.press("Escape"); await expect(page.getByRole("dialog")).toHaveCount(0);
  await page.getByRole("button", { name: "Xuất bản", exact: true }).click();
  await page.getByRole("button", { name: "Xác nhận xuất bản", exact: true }).click();
  await expect(page.getByText("Đã xuất bản thành công.")).toBeVisible();
  await expect(page.getByText(/Nội dung có 3 từ; khuyến nghị 400 từ/)).toBeVisible();
  expect(server.calls.find(x => x.path.endsWith("/publish"))?.body).toEqual({ rowVersion: "saved-version" });
  await expect(page.getByLabel("Không lập chỉ mục tìm kiếm", { exact: true })).not.toBeChecked();
});

test("P1-11 unchanged rich content and JSON are omitted; public-only media picker", async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog/artisans"); await page.getByRole("button", { name: "Chỉnh sửa Chu Bân" }).click();
  await page.getByRole("button", { name: "Chọn từ thư viện ảnh" }).click();
  await expect(page.getByRole("button", { name: /Chân dung công khai/ })).toBeVisible();
  await expect(page.getByText("Bằng chứng riêng tư")).toHaveCount(0);
  await page.getByRole("button", { name: /Chân dung công khai/ }).click();
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
  const body = server.calls.find(x => x.method === "PATCH")!.body!;
  expect(body.portraitMediaId).toBe(mediaId); expect(body).not.toHaveProperty("bioHtml"); expect(body).not.toHaveProperty("bioJson");
});

test("P1-11 collections reorder existing real IDs atomically with relation version", async ({ page }) => {
  const server = await backend(page, { products: true }); await page.goto("/catalog/collections");
  await page.getByRole("button", { name: "Chỉnh sửa Trà cụ mùa thu" }).click();
  await page.getByRole("button", { name: `Đưa sản phẩm ${productB} lên`, exact: true }).click();
  await page.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click();
  await expect.poll(() => server.calls.filter(x => x.method === "PUT").length).toBe(1);
  expect(server.calls.find(x => x.method === "PUT")?.body).toEqual({ rowVersion: version, items: [{ productId: productB, sortOrder: 0 }, { productId: productA, sortOrder: 1 }] });
  await expect(page.getByText("Đã lưu danh sách sản phẩm.")).toBeVisible();
  expect(server.calls.some(x => x.path === "/api/v1/admin/products")).toBe(false);
});

test("P1-11 collection stale relation version keeps proposed order, blocks replay", async ({ page }) => {
  const server = await backend(page, { products: true }); await page.goto("/catalog/collections"); await page.getByRole("button", { name: "Chỉnh sửa Trà cụ mùa thu" }).click();
  await page.getByRole("button", { name: `Đưa sản phẩm ${productB} lên`, exact: true }).click(); server.reject(409, "concurrency_conflict", "PUT");
  await page.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click();
  await expect(page.getByText(/Thứ tự có xung đột/)).toBeVisible();
  await expect(page.getByRole("button", { name: "Lưu danh sách sản phẩm" })).toBeDisabled();
  await expect(page.getByRole("region", { name: "Sản phẩm trong bộ sưu tập" }).getByRole("listitem").first()).toContainText(productB);
  expect(server.calls.filter(x => x.method === "PUT")).toHaveLength(1);
});

test("P1-11 empty collection offers real product discovery without automatic queries", async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog/collections"); await page.getByRole("button", { name: "Chỉnh sửa Trà cụ mùa thu" }).click();
  await expect(page.getByText("Bộ sưu tập chưa có sản phẩm.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Lưu danh sách sản phẩm" })).toHaveCount(0);
  expect(server.calls.some(x => x.path === "/api/v1/admin/products")).toBe(false);
});

for (const [name, permissions, write, publish] of [
  ["read only", ["catalog.read"], false, false], ["no publish", ["catalog.read", "catalog.write"], true, false], ["publish only", ["catalog.read", "catalog.publish"], false, true]
] as const) test(`P1-11 permissions ${name}: role never grants controls`, async ({ page }) => {
  await backend(page, { permissions: [...permissions] }); await page.goto("/catalog/materials");
  await expect(page.getByRole("button", { name: "Thêm mới", exact: true })).toHaveCount(write ? 1 : 0);
  await page.getByRole("button", { name: write ? "Chỉnh sửa Tử nê" : "Xem Tử nê" }).click();
  if (write) await expect(page.getByLabel("Tên", { exact: true })).toBeEnabled(); else await expect(page.getByLabel("Tên", { exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Lưu thay đổi", exact: true })).toHaveCount(write ? 1 : 0);
  await expect(page.getByRole("button", { name: "Xuất bản", exact: true })).toHaveCount(publish ? 1 : 0);
});

test("P1-11 no catalog.read denies nested route and sends no catalog requests", async ({ page }) => {
  const server = await backend(page, { permissions: ["catalog.write", "catalog.publish"] }); await page.goto("/catalog/categories");
  await expect(page.getByRole("heading", { name: "Bạn chưa có quyền truy cập" })).toBeVisible();
  expect(server.calls.filter(x => !x.path.includes("/auth/"))).toHaveLength(0);
});

for (const [status, code, text] of [
  [400, "validation_failed", "Tên bị từ chối bởi máy chủ."], [400, "csrf_invalid", "Phiên bảo vệ đã thay đổi"],
  [403, "forbidden", "Bạn chưa được cấp quyền"], [404, "not_found", "Không tìm thấy dữ liệu"], [409, "concurrency_conflict", "Dữ liệu có xung đột"]
] as const) test(`P1-11 mutation ${status}/${code}: retains unsaved values and never retries`, async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog/materials"); await page.getByRole("button", { name: "Chỉnh sửa Tử nê" }).click();
  await page.getByLabel("Tên", { exact: true }).fill("Tên chưa lưu"); server.reject(status, code);
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByRole("alert").filter({ hasText: text }).first()).toBeVisible();
  await expect(page.getByLabel("Tên", { exact: true })).toHaveValue("Tên chưa lưu");
  expect(server.calls.filter(x => x.method === "PATCH")).toHaveLength(1);
  if (status === 400 && code === "validation_failed") await expect(page.getByLabel("Tên", { exact: true })).toHaveAttribute("aria-invalid", "true");
  if (status === 409) {
    await expect(page.getByRole("button", { name: "Lưu thay đổi", exact: true })).toBeDisabled();
    page.once("dialog", dialog => dialog.accept()); await page.getByRole("button", { name: "Tải bản mới", exact: true }).click();
    await expect(page.getByLabel("Tên", { exact: true })).toHaveValue("Tử nê");
    await expect(page.getByRole("button", { name: "Lưu thay đổi", exact: true })).toBeEnabled();
  }
});

test("P1-11 401 mutation invalidates identity and returns to login", async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog/materials"); await page.getByRole("button", { name: "Chỉnh sửa Tử nê" }).click();
  server.reject(401, "unauthenticated"); await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page).toHaveURL(/\/login$/); await expect(page.getByLabel("Email công việc")).toBeVisible();
  expect(server.calls.filter(x => x.method === "PATCH")).toHaveLength(1);
});

test("P1-11 publish 422 keeps draft, displays backend conditions", async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog/artisans"); await page.getByRole("button", { name: "Chỉnh sửa Chu Bân" }).click();
  server.reject(422, "publish_requirements_not_met", "POST"); await page.getByRole("button", { name: "Xuất bản", exact: true }).click();
  await page.getByRole("button", { name: "Xác nhận xuất bản" }).click();
  await expect(page.getByRole("dialog").getByRole("alert")).toContainText("Chưa thể thực hiện thao tác");
  await expect(page.getByLabel("Trạng thái", { exact: true })).toHaveValue("draft");
});

test("P1-11 publish stale version prevents stale resubmission until explicit reload", async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog/artisans"); await page.getByRole("button", { name: "Chỉnh sửa Chu Bân" }).click();
  server.reject(409, "concurrency_conflict", "POST"); await page.getByRole("button", { name: "Xuất bản", exact: true }).click();
  await page.getByRole("button", { name: "Xác nhận xuất bản" }).click();
  await expect(page.getByRole("dialog").getByRole("alert")).toContainText("Dữ liệu có xung đột");
  await page.getByRole("button", { name: "Xác nhận xuất bản" }).click();
  expect(server.calls.filter(x => x.path.endsWith("/publish"))).toHaveLength(1);
  await page.keyboard.press("Escape"); await expect(page.getByRole("button", { name: "Xuất bản", exact: true })).toBeDisabled();
  await page.getByRole("button", { name: "Tải bản mới để xuất bản" }).click();
  await expect(page.getByRole("button", { name: "Xuất bản", exact: true })).toBeEnabled();
});

test("P1-11 loading, empty, server error retry and search/filter params", async ({ page }) => {
  const server = await backend(page, { empty: true, delay: 250 }); await page.goto("/catalog/materials");
  await expect(page.getByText("Đang tải danh sách…")).toBeVisible();
  await expect(page.getByRole("cell", { name: "Chưa có dữ liệu để hiển thị." })).toBeVisible();
  await page.getByLabel("Tìm theo tên").fill("Tử nê"); await page.getByLabel("Lọc trạng thái").selectOption("draft");
  await expect.poll(() => server.calls.some(x => x.search.includes("status=draft") && decodeURIComponent(x.search).includes("Tử"))).toBe(true);
  server.reject(403, "forbidden", "GET"); await page.getByLabel("Tìm theo tên").fill("Không có");
  await expect(page.getByRole("alert").filter({ hasText: "chưa được cấp quyền" })).toBeVisible();
  await page.getByRole("button", { name: "Thử tải lại" }).click();
  await expect(page.getByRole("cell", { name: "Chưa có dữ liệu để hiển thị." })).toBeVisible();
});

for (const width of [390, 1440]) test(`P1-11 catalog keyboard, a11y, responsive ${width}`, async ({ page }, testInfo) => {
  await page.setViewportSize({ width, height: 900 }); await backend(page); await page.goto("/catalog/materials");
  const nav = page.getByRole("navigation", { name: "Quản lý catalog" });
  await expect(nav.getByRole("link", { name: "Nghệ nhân", exact: true })).toBeVisible();
  await nav.getByRole("link", { name: "Nghệ nhân", exact: true }).focus(); await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/catalog\/artisans$/); await page.getByRole("button", { name: "Chỉnh sửa Chu Bân" }).click();
  await page.getByLabel("Tên", { exact: true }).focus(); await expect(page.getByLabel("Tên", { exact: true })).toBeFocused();
  await page.keyboard.press("Tab"); await expect(page.getByLabel("Đường dẫn (slug)", { exact: true })).toBeFocused();
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath(`catalog-artisan-${width}.png`), fullPage: true });
});

for (const [code, resource, field, original, corrected] of [
  ["slug_exists", "materials", "Đường dẫn (slug)", "trung-slug", "slug-moi"],
  ["sku_code_exists", "materials", "Mã SKU chất liệu", "TN", "TS"],
  ["product_type_code_exists", "product-types", "Mã", "am", "chen"],
  ["future_domain_conflict", "materials", "Đường dẫn (slug)", "trung-slug", "slug-moi"]
] as const) test(`G4 409 ${code}: new form can correct and resubmit without GET new or replay`, async ({ page }) => {
  const server = await backend(page); await page.goto(`/catalog/${resource}`); await page.getByRole("button", { name: "Thêm mới", exact: true }).click();
  await page.getByLabel("Tên", { exact: true }).fill("Tên giữ nguyên");
  await page.getByLabel(field, { exact: true }).fill(original);
  server.reject(409, code, "POST"); await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByRole("alert").filter({ hasText: code === "future_domain_conflict" ? /xung đột/ : /tồn tại/ }).first()).toBeVisible();
  await expect(page.getByRole("button", { name: "Lưu thay đổi", exact: true })).toBeEnabled({ timeout: 1500 });
  await expect(page.getByLabel("Tên", { exact: true })).toHaveValue("Tên giữ nguyên");
  await expect(page.getByRole("button", { name: "Tải bản mới", exact: true })).toHaveCount(0);
  expect(server.calls.filter(x => x.method === "POST")).toHaveLength(1);
  await page.getByLabel(field, { exact: true }).fill(corrected);
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
  expect(server.calls.filter(x => x.method === "POST")).toHaveLength(2);
  expect(server.calls.some(x => x.method === "GET" && x.path.endsWith("/new"))).toBe(false);
});

test("G4 409 existing unknown domain conflict keeps original version and allows manual correction", async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog/materials"); await page.getByRole("button", { name: "Chỉnh sửa Tử nê" }).click();
  await page.getByLabel("Tên", { exact: true }).fill("Tên chưa lưu"); server.reject(409, "unknown_domain_conflict");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByRole("alert").filter({ hasText: /xung đột/ }).first()).toBeVisible();
  await expect(page.getByRole("button", { name: "Lưu thay đổi", exact: true })).toBeEnabled({ timeout: 1500 });
  await page.getByLabel("Tên", { exact: true }).fill("Tên sửa lại"); await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
  const patches = server.calls.filter(x => x.method === "PATCH"); expect(patches).toHaveLength(2);
  expect(patches.map(x => x.body?.rowVersion)).toEqual([version, version]);
});

test("G4 collection dirty metadata visibly blocks reorder without PUT or PATCH", async ({ page }) => {
  const server = await backend(page, { products: true }); await page.goto("/catalog/collections"); await page.getByRole("button", { name: "Chỉnh sửa Trà cụ mùa thu" }).click();
  await page.getByLabel("Tên", { exact: true }).fill("Tên chưa lưu");
  await expect(page.getByText("Hãy lưu hoặc hủy thay đổi thông tin trước khi sắp xếp sản phẩm.")).toBeVisible({ timeout: 1500 });
  await expect(page.getByRole("button", { name: "Lưu danh sách sản phẩm" })).toBeDisabled();
  await expect(page.getByRole("button", { name: `Đưa sản phẩm ${productB} lên` })).toBeDisabled();
  expect(server.calls.filter(x => ["PUT", "PATCH"].includes(x.method))).toHaveLength(0);
  await expect(page.getByLabel("Tên", { exact: true })).toHaveValue("Tên chưa lưu");
});

for (const result of ["success", "failure", "stale"] as const) test(`G4 collection delayed PUT ${result}: metadata locked, values survive, no unintended PATCH`, async ({ page }) => {
  const server = await backend(page, { products: true }); await page.goto("/catalog/collections"); await page.getByRole("button", { name: "Chỉnh sửa Trà cụ mùa thu" }).click();
  const name = page.getByLabel("Tên", { exact: true });
  // A DOM marker distinguishes the existing input from a rowVersion-key remount.
  await name.evaluate(input => input.setAttribute("data-g4-original-input", "true"));
  await page.getByRole("button", { name: `Đưa sản phẩm ${productB} lên` }).click();
  if (result !== "success") server.reject(result === "stale" ? 409 : 403, result === "stale" ? "concurrency_conflict" : "forbidden", "PUT");
  const release = server.holdPut();
  try {
    await page.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click();
    await expect.poll(() => server.calls.filter(x => x.method === "PUT").length).toBe(1);
    await expect(name).toBeDisabled({ timeout: 1500 });
    await expect(page.getByText("Đang lưu danh sách sản phẩm. Vui lòng chờ trước khi sửa thông tin.")).toBeVisible();
    // Attempt keyboard editing while the network response is held.
    await name.click({ force: true }); await page.keyboard.type("Không được ghi");
    await expect(name).toHaveValue("Trà cụ mùa thu");
    await expect(page.getByRole("button", { name: "Lưu thay đổi", exact: true })).toBeDisabled();
    await page.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click({ force: true });
    expect(server.calls.filter(x => x.method === "PUT")).toHaveLength(1);
  } finally { release(); }
  if (result === "success") await expect(page.getByText("Đã lưu danh sách sản phẩm.")).toBeVisible();
  else await expect(page.getByRole("alert").filter({ hasText: result === "stale" ? /Thứ tự có xung đột/ : /chưa được cấp quyền/ }).first()).toBeVisible();
  await expect(name).toHaveValue("Trà cụ mùa thu");
  await expect(name).toHaveAttribute("data-g4-original-input", "true");
  expect(server.calls.filter(x => x.method === "PATCH")).toHaveLength(0);
  await expect(page.getByLabel("Mô tả SEO", { exact: true })).toHaveValue("");
  if (result === "success") {
    await expect(name).toBeEnabled();
    await name.fill("Tên tiếp tục soạn");
    expect(server.calls.filter(x => x.method === "GET" && x.path === `/api/v1/admin/collections/${id}`)).toHaveLength(1);
    await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
    await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
    const patches = server.calls.filter(x => x.method === "PATCH"); expect(patches).toHaveLength(1);
    expect(patches[0].body).toMatchObject({ name: "Tên tiếp tục soạn", rowVersion: "relation-new-version", seoDescription: null });
    expect(patches[0].body).not.toHaveProperty("items");
  } else {
    expect(server.calls.filter(x => x.method === "PATCH")).toHaveLength(0);
    await expect(name).toBeDisabled();
    await expect(page.getByRole("button", { name: "Hủy thay đổi sản phẩm" })).toBeEnabled();
    if (result === "failure") await expect(page.getByRole("button", { name: "Lưu danh sách sản phẩm" })).toBeEnabled();
    if (result === "stale") {
      page.once("dialog", dialog => dialog.accept()); await page.getByRole("button", { name: "Tải lại thứ tự" }).click();
      await expect(page.getByText(/Thứ tự có xung đột/)).toHaveCount(0);
    }
  }
});

for (const code of ["attribute_code_exists", "option_code_exists"] as const) test(`G4 409 ${code}: scoped reference uniqueness can be corrected`, async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog/product-types"); await page.getByRole("button", { name: "Thuộc tính am", exact: true }).click();
  if (code === "option_code_exists") await page.getByRole("button", { name: "Lựa chọn color", exact: true }).click();
  await page.getByRole("button", { name: "Thêm mới", exact: true }).click();
  await page.getByLabel(code === "option_code_exists" ? "Nhãn lựa chọn" : "Tên", { exact: true }).fill("Tên mới");
  await page.getByLabel("Mã", { exact: true }).fill("red"); server.reject(409, code, "POST");
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByLabel("Mã", { exact: true })).toHaveAttribute("aria-invalid", "true");
  await expect(page.getByRole("button", { name: "Lưu thay đổi", exact: true })).toBeEnabled();
  await page.getByLabel("Mã", { exact: true }).fill("brown"); await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
  expect(server.calls.filter(x => x.method === "POST")).toHaveLength(2);
  expect(server.calls.some(x => x.method === "GET" && x.path.endsWith("/new"))).toBe(false);
});

test("G4 existing stale reload requires discard consent and adopts actual refreshed values/version without remount", async ({ page }) => {
  const server = await backend(page); await page.goto("/catalog/collections"); await page.getByRole("button", { name: "Chỉnh sửa Trà cụ mùa thu" }).click();
  const name = page.getByLabel("Tên", { exact: true }); await name.evaluate(input => input.setAttribute("data-g4-original-input", "true"));
  await name.fill("Tên chưa lưu"); server.changeCollection({ name: "Tên người khác lưu", rowVersion: "remote-version" });
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByRole("button", { name: "Lưu thay đổi", exact: true })).toBeDisabled();
  await expect(name).toHaveValue("Tên chưa lưu");
  const before = server.calls.length;
  page.once("dialog", dialog => dialog.dismiss()); await page.getByRole("button", { name: "Tải bản mới", exact: true }).click();
  await expect(name).toHaveValue("Tên chưa lưu"); expect(server.calls.length).toBe(before);
  page.once("dialog", dialog => dialog.accept()); await page.getByRole("button", { name: "Tải bản mới", exact: true }).click();
  await expect(name).toHaveValue("Tên người khác lưu"); await expect(name).toHaveAttribute("data-g4-original-input", "true");
  await name.fill("Tên lưu sau đối chiếu"); await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
  expect(server.calls.filter(x => x.method === "PATCH").map(x => x.body?.rowVersion)).toEqual([version, "remote-version"]);
});

test("G4 collection mismatch between metadata and relation versions cannot rebase unsaved snapshot", async ({ page }) => {
  const server = await backend(page, { products: true }); await page.goto("/catalog/collections");
  await page.route(`**/collections/${id}/products`, async route => {
    server.changeCollection({ name: "Tên do người khác sửa", rowVersion: "remote-version" }); await route.fallback();
  });
  await page.getByRole("button", { name: "Chỉnh sửa Trà cụ mùa thu" }).click();
  await expect(page.getByText("Thông tin bộ sưu tập đã thay đổi. Tải bản mới trước khi sắp xếp sản phẩm.")).toBeVisible({ timeout: 1500 });
  await expect(page.getByRole("button", { name: `Đưa sản phẩm ${productB} lên` })).toBeDisabled();
  expect(server.calls.filter(x => ["PUT", "PATCH"].includes(x.method))).toHaveLength(0);
});


async function collection(page: Page) {
  await page.goto("/catalog/collections"); await page.getByRole("button", { name: "Chỉnh sửa Trà cụ mùa thu" }).click();
  return page.getByRole("region", { name: "Sản phẩm trong bộ sưu tập" });
}
async function picker(page: Page) {
  await page.getByRole("button", { name: "Thêm sản phẩm", exact: true }).click();
  return page.getByRole("dialog", { name: "Thêm sản phẩm" });
}

test("F1 collection PATCH failure retains draft, pending blocks double submission, then PUT uses returned version", async ({ page }) => {
  const server = await backend(page, { products: true });
  const region = await collection(page);
  const add = region.getByRole("button", { name: "Thêm sản phẩm", exact: true });
  const name = page.getByLabel("Tên", { exact: true });
  const save = page.getByRole("button", { name: "Lưu thay đổi", exact: true });
  await name.evaluate(input => input.setAttribute("data-original", "yes"));
  await name.fill("Tên mới của bộ sưu tập");
  await expect(add).toBeDisabled();
  server.reject(403, "forbidden", "PATCH");
  await save.click();
  await expect(page.getByText(/chưa được cấp quyền/)).toBeVisible();
  await expect(name).toHaveValue("Tên mới của bộ sưu tập");
  await expect(save).toBeEnabled();
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route(`**/collections/${id}`, async route => {
    if (route.request().method() === "PATCH") await gate;
    await route.fallback();
  });
  try {
    const dispatched = page.waitForRequest(request => request.method() === "PATCH");
    await save.click(); await dispatched;
    await expect(name).toBeDisabled();
    await name.evaluate(input => input.closest("form")!.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true })));
    await add.evaluate(button => button.dispatchEvent(new MouseEvent("click", { bubbles: true })));
    await expect(page.getByRole("dialog")).toHaveCount(0);
  } finally { release(); }
  await expect(page.getByText("Đã lưu thông tin.")).toBeVisible();
  await expect(name).toHaveAttribute("data-original", "yes");
  await expect(name).toHaveValue("Tên mới của bộ sưu tập");
  await region.getByRole("button", { name: `Đưa sản phẩm ${productB} lên` }).click();
  await expect(name).toBeDisabled();
  await region.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click();
  await expect(name).toBeEnabled();
  expect(server.calls.filter(x => x.method === "PATCH")).toHaveLength(2);
  expect(server.calls.filter(x => x.method === "PUT").map(x => x.body?.rowVersion)).toEqual(["saved-version"]);
});

test("F2 identical product names have distinct checkbox names and keyboard selects the intended SKU", async ({ page }) => {
  const server = await backend(page);
  await page.route(url => url.pathname === "/api/v1/admin/products", route => route.fulfill({
    json: { items: productRows.slice(0, 2).map(product => ({ ...product, name: "Ấm cùng tên" })), page: 1, pageSize: 20, totalItems: 2 }
  }));
  const region = await collection(page);
  const dialog = await picker(page);
  const first = dialog.getByRole("checkbox", { name: "Chọn Ấm cùng tên, SKU AM-XX-00001", exact: true });
  const second = dialog.getByRole("checkbox", { name: "Chọn Ấm cùng tên, SKU AM-XX-00002", exact: true });
  await second.focus();
  await page.keyboard.press("Space");
  await expect(first).not.toBeChecked();
  await expect(second).toBeChecked();
  await dialog.getByRole("button", { name: "Thêm 1 sản phẩm đã chọn" }).click();
  const name = page.getByLabel("Tên", { exact: true });
  await expect(name).toBeDisabled();
  await name.press("Enter", { timeout: 1500 });
  await expect(name).toHaveValue("Trà cụ mùa thu");
  await region.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click();
  await expect(name).toBeEnabled();
  expect(server.calls.filter(x => x.method === "PATCH")).toHaveLength(0);
  expect(server.calls.find(x => x.method === "PUT")?.body?.items).toEqual([{ productId: productB, sortOrder: 0 }]);
});

test("FR-CAT-08 empty collection: multi-select, full replacement, persist/reload, returned version", async ({ page }) => {
  const server = await backend(page); const region = await collection(page); const dialog = await picker(page);
  await dialog.getByRole("checkbox", { name: "Chọn Ấm tử sa, SKU AM-XX-00001", exact: true }).check();
  await dialog.getByRole("checkbox", { name: "Chọn Chén men ngọc, SKU AM-XX-00002", exact: true }).check();
  expect(server.calls.filter(x => x.method === "PUT")).toHaveLength(0);
  await dialog.getByRole("button", { name: "Thêm 2 sản phẩm đã chọn" }).click();
  await expect(region.getByText("Ấm tử sa", { exact: true })).toBeVisible(); await expect(region.getByText("AM-XX-00001")).toBeVisible();
  await expect(region.getByRole("img", { name: "Ấm tử sa thật" })).toHaveAttribute("src", /\/synthetic-cover\.svg$/);
  await expect.poll(() => region.getByRole("img", { name: "Ấm tử sa thật" }).evaluate((element: HTMLImageElement) => element.complete && element.naturalWidth > 0)).toBe(true);
  await expect(region.getByText("Bản nháp", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Lưu thay đổi", exact: true })).toBeDisabled();
  await region.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click();
  await expect(region.getByText("Đã lưu danh sách sản phẩm.")).toBeVisible();
  expect(server.calls.find(x => x.method === "PUT")?.body).toEqual({ rowVersion: version, items: [{ productId: productA, sortOrder: 0 }, { productId: productB, sortOrder: 1 }] });
  await page.reload(); await page.getByRole("button", { name: "Chỉnh sửa Trà cụ mùa thu" }).click();
  await expect(region.getByText("Ấm tử sa", { exact: true })).toBeVisible();
  await region.getByRole("button", { name: `Bỏ sản phẩm ${productB} khỏi bộ sưu tập` }).click();
  await region.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click();
  await expect.poll(() => server.calls.filter(x => x.method === "PUT").length).toBe(2);
  expect(server.calls.filter(x => x.method === "PUT")[1].body).toEqual({ rowVersion: "relation-new-version", items: [{ productId: productA, sortOrder: 0 }] });
  expect(server.calls.some(x => x.method === "DELETE" || x.path.includes("/archive"))).toBe(false);
});

test("FR-CAT-08 append preserves existing IDs, duplicates, search name/base/variant SKU, status and pages", async ({ page }) => {
  const server = await backend(page, { products: true }); const region = await collection(page); const dialog = await picker(page);
  await expect(dialog.getByRole("checkbox", { name: "Chọn Ấm tử sa, SKU AM-XX-00001", exact: true })).toBeChecked();
  await expect(dialog.getByRole("checkbox", { name: "Chọn Ấm tử sa, SKU AM-XX-00001", exact: true })).toBeDisabled();
  await dialog.getByRole("checkbox", { name: "Chọn Sản phẩm 3, SKU AM-XX-00003", exact: true }).check();
  await dialog.getByRole("button", { name: "Trang sản phẩm sau" }).click();
  await dialog.getByRole("checkbox", { name: "Chọn Sản phẩm 21, SKU AM-XX-00021", exact: true }).check();
  const search = dialog.getByRole("searchbox"); await search.fill("Ấm tử sa");
  await expect(dialog.getByRole("checkbox")).toHaveCount(1);
  await search.fill("AM-XX-00003"); await expect(dialog.getByRole("checkbox", { name: "Chọn Sản phẩm 3, SKU AM-XX-00003", exact: true })).toBeChecked();
  await search.fill("VARIANT-4"); await expect(dialog.getByRole("checkbox", { name: "Chọn Sản phẩm 4, SKU AM-XX-00004", exact: true })).toBeVisible();
  await search.fill(""); await dialog.getByLabel("Trạng thái sản phẩm").selectOption("archived");
  await expect(dialog.getByRole("checkbox")).toHaveCount(8);
  await expect(dialog.getByRole("checkbox", { name: "Chọn Sản phẩm 3, SKU AM-XX-00003", exact: true })).toBeChecked();
  await dialog.getByRole("button", { name: "Thêm 2 sản phẩm đã chọn" }).click();
  await region.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click();
  await expect.poll(() => server.calls.filter(x => x.method === "PUT").length).toBe(1);
  const items = server.calls.find(x => x.method === "PUT")?.body?.items as { productId: string; sortOrder: number }[];
  expect(items.map(x => x.productId)).toEqual([productA, productB, productRows[2].id, productRows[20].id]);
  expect(items.map(x => x.sortOrder)).toEqual([0, 1, 2, 3]);
  expect(server.calls.filter(x => x.method === "GET" && x.path === `/api/v1/admin/products/${productA}`)).toHaveLength(1);
});

test("FR-CAT-08 cancel picker and relation edits confirms discard and never mutates", async ({ page }) => {
  const server = await backend(page, { products: true }); const region = await collection(page); const dialog = await picker(page);
  await dialog.getByRole("checkbox", { name: "Chọn Sản phẩm 3, SKU AM-XX-00003", exact: true }).check();
  page.once("dialog", d => d.dismiss()); await dialog.getByRole("button", { name: "Hủy chọn" }).click(); await expect(dialog).toBeVisible();
  page.once("dialog", d => d.accept()); await dialog.getByRole("button", { name: "Hủy chọn" }).click(); await expect(dialog).toHaveCount(0);
  await expect(region.getByRole("button", { name: "Thêm sản phẩm", exact: true })).toBeFocused();
  await region.getByRole("button", { name: `Bỏ sản phẩm ${productA} khỏi bộ sưu tập` }).click();
  await expect(page.getByLabel("Tên", { exact: true })).toBeDisabled();
  page.once("dialog", d => d.dismiss()); await region.getByRole("button", { name: "Hủy thay đổi sản phẩm" }).click();
  await expect(region.getByText("Ấm tử sa", { exact: true })).toHaveCount(0);
  page.once("dialog", d => d.accept()); await region.getByRole("button", { name: "Hủy thay đổi sản phẩm" }).click();
  await expect(region.getByText("Ấm tử sa", { exact: true })).toBeVisible();
  await expect(page.getByLabel("Tên", { exact: true })).toBeEnabled();
  expect(server.calls.filter(x => x.method !== "GET")).toHaveLength(0);
});

test("FR-CAT-08 remove last relation sends empty replacement", async ({ page }) => {
  const server = await backend(page, { products: true }); const region = await collection(page);
  for (const productId of [productA, productB]) await region.getByRole("button", { name: `Bỏ sản phẩm ${productId} khỏi bộ sưu tập` }).click();
  await region.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click();
  await expect(region.getByText("Đã lưu danh sách sản phẩm.")).toBeVisible(); expect(server.calls.find(x => x.method === "PUT")?.body?.items).toEqual([]);
});

test("FR-CAT-08 old search response cannot overwrite new results", async ({ page }) => {
  await backend(page); await collection(page);
  let release!: () => void; const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route("**/admin/products?**", async route => {
    const q = new URL(route.request().url()).searchParams.get("q");
    if (q !== "slow") return route.fallback();
    await gate; try { await route.fulfill({ json: { items: [{ ...productRows[0], name: "Kết quả cũ" }], page: 1, pageSize: 20, totalItems: 1 } }); } catch { /* Superseded request was aborted by React Query. */ }
  });
  const dialog = await picker(page); const search = dialog.getByRole("searchbox");
  try {
    const requested = page.waitForRequest(r => new URL(r.url()).searchParams.get("q") === "slow");
    await search.fill("slow"); await requested; await expect(dialog.getByText("Đang tìm sản phẩm…")).toBeVisible();
    await search.fill("AM-XX-00002"); await expect(dialog.getByRole("checkbox", { name: "Chọn Chén men ngọc, SKU AM-XX-00002" })).toBeVisible();
  } finally { release(); }
  await expect(dialog.getByText("Kết quả cũ")).toHaveCount(0); await expect(dialog.getByRole("checkbox")).toHaveCount(1);
});

test("FR-CAT-08 failed lookup keeps relation, retry recovers; discovery error/empty", async ({ page }) => {
  const server = await backend(page, { products: true }); let failed = true;
  await page.route(`**/admin/products/${productA}`, route => failed ? route.fulfill({ status: 404, json: { code: "not_found" } }) : route.fallback());
  const region = await collection(page); await expect(region.getByRole("button", { name: `Tải lại thông tin ${productA}` })).toBeVisible();
  await expect(region.getByText("Sản phẩm chưa tải được thông tin").first()).toBeVisible();
  await expect(region.getByText(productA, { exact: true })).toBeVisible(); failed = false;
  await region.getByRole("button", { name: `Tải lại thông tin ${productA}` }).click(); await expect(region.getByText("Ấm tử sa", { exact: true })).toBeVisible();
  server.reject(403, "forbidden", "GET"); const dialog = await picker(page); await expect(dialog.getByRole("alert")).toBeVisible();
  await dialog.getByRole("button", { name: "Thử tìm lại" }).click(); await expect(dialog.getByRole("checkbox").first()).toBeVisible();
  await dialog.getByRole("searchbox").fill("không có sản phẩm"); await expect(dialog.getByText("Không tìm thấy sản phẩm phù hợp.")).toBeVisible();
  expect(server.calls.filter(x => x.method === "PUT")).toHaveLength(0);
});

for (const [status, code] of [[409, "concurrency_conflict"], [400, "csrf_invalid"], [403, "forbidden"]] as const) test(`FR-CAT-08 add failure ${code} keeps selections, no replay`, async ({ page }) => {
  const server = await backend(page); const region = await collection(page); const dialog = await picker(page);
  await dialog.getByRole("checkbox", { name: "Chọn Ấm tử sa, SKU AM-XX-00001", exact: true }).check(); await dialog.getByRole("button", { name: "Thêm 1 sản phẩm đã chọn" }).click();
  server.reject(status, code, "PUT"); await region.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click();
  await expect(region.getByRole("alert").first()).toBeVisible(); await expect(region.getByText("Ấm tử sa", { exact: true })).toBeVisible();
  expect(server.calls.filter(x => x.method === "PUT")).toHaveLength(1);
  await expect(page.getByRole("button", { name: "Lưu thay đổi", exact: true })).toBeDisabled();
  if (status === 409) { await expect(region.getByRole("button", { name: "Lưu danh sách sản phẩm" })).toBeDisabled(); page.once("dialog", d => d.dismiss()); await region.getByRole("button", { name: "Tải lại thứ tự" }).click(); await expect(region.getByText("Ấm tử sa", { exact: true })).toBeVisible(); }
});

test("FR-CAT-08 session expiry during picker save redirects and clears protected view", async ({ page }) => {
  const server = await backend(page); await collection(page); const dialog = await picker(page);
  await dialog.getByRole("checkbox", { name: "Chọn Ấm tử sa, SKU AM-XX-00001", exact: true }).check(); await dialog.getByRole("button", { name: "Thêm 1 sản phẩm đã chọn" }).click();
  server.reject(401, "unauthenticated", "PUT"); await page.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click();
  await expect(page).toHaveURL(/login/); expect(server.calls.filter(x => x.method === "PUT")).toHaveLength(1);
});

test("FR-CAT-08 read-only displays names without mutation controls", async ({ page }) => {
  const server = await backend(page, { products: true, permissions: ["catalog.read"] });
  await page.goto("/catalog/collections"); await page.getByRole("button", { name: "Xem Trà cụ mùa thu" }).click();
  await expect(page.getByText("Ấm tử sa", { exact: true })).toBeVisible(); await expect(page.getByRole("button", { name: "Thêm sản phẩm", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /Bỏ sản phẩm|L?u th? t?/ })).toHaveCount(0); expect(server.calls.filter(x => x.method !== "GET")).toHaveLength(0);
});

test("FR-CAT-08 keyboard selection/reorder, mobile dialog and axe", async ({ page }) => {
  await backend(page, { products: true }); await page.setViewportSize({ width: 390, height: 844 }); const region = await collection(page); const dialog = await picker(page);
  const checkbox = dialog.getByRole("checkbox", { name: "Chọn Sản phẩm 3, SKU AM-XX-00003", exact: true }); await checkbox.focus(); await page.keyboard.press("Space"); await expect(checkbox).toBeChecked();
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await dialog.screenshot({ path: "../../artifacts/p1-11-integration/product-dialog-mobile.png" });
  await dialog.getByRole("button", { name: "Thêm 1 sản phẩm đã chọn" }).click();
  const move = region.getByRole("button", { name: `Đưa sản phẩm ${productB} lên` }); await move.focus(); await page.keyboard.press("Enter");
  await expect(region.getByRole("listitem").first()).toContainText("Chén men ngọc");
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await region.screenshot({ path: "../../artifacts/p1-11-integration/collection-picker-mobile.png" });
});

test("FR-CAT-08 large collection: at most 20 lookups, 5000 limit, page-boundary reorder preserves full list", async ({ page }) => {
  await backend(page);
  const items = Array.from({ length: 4999 }, (_, index) => ({ productId: `10000000-0000-7000-8000-${String(index).padStart(12, "0")}`, sortOrder: index }));
  let lookups = 0;
  await page.route("**/admin/products/*", route => { lookups++; return route.fulfill({ json: { ...productRows[0], id: route.request().url().split("/").pop() } }); });
  let payload: Record<string, unknown> | undefined;
  await page.route(`**/collections/${id}/products`, async route => {
    if (route.request().method() === "PUT") payload = route.request().postDataJSON();
    return route.fulfill({ json: { id, rowVersion: version, items: payload?.items ?? items } });
  });
  const region = await collection(page); await expect(region.getByRole("listitem")).toHaveCount(20);
  await expect.poll(() => lookups).toBe(20);
  const dialog = await picker(page); await dialog.getByRole("checkbox", { name: "Chọn Ấm tử sa, SKU AM-XX-00001", exact: true }).check();
  await expect(dialog.getByRole("checkbox", { name: "Chọn Chén men ngọc, SKU AM-XX-00002", exact: true })).toBeDisabled();
  await dialog.getByRole("button", { name: "Thêm 1 sản phẩm đã chọn" }).click();
  await expect(region.getByRole("button", { name: "Thêm sản phẩm", exact: true })).toBeDisabled();
  await region.getByRole("button", { name: "Trang đã chọn sau" }).click();
  await region.getByRole("button", { name: `Đưa sản phẩm ${items[20].productId} lên`, exact: true }).click();
  await region.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click();
  await expect.poll(() => payload !== undefined).toBe(true);
  const saved = payload!.items as typeof items;
  expect(saved).toHaveLength(5000); expect(new Set(saved.map(x => x.productId)).size).toBe(5000);
  expect(saved[19]).toEqual({ ...items[20], sortOrder: 19 }); expect(saved[20]).toEqual({ ...items[19], sortOrder: 20 });
  expect(saved[4999]).toEqual({ productId: productA, sortOrder: 4999 });
  expect(lookups).toBeLessThanOrEqual(41);
});
