import { expect, test, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import type { Auth } from "../../src/lib/api";
import type { AttributeRow, TypeRow } from "../../src/lib/catalog-api";
import type { Product, ProductImage, Variant } from "../../src/lib/products-api";

const route = "/catalog/products";
const uid = (n: number) => `00000000-0000-7000-8000-${String(n).padStart(12, "0")}`;
const productId = uid(1), defaultId = uid(2), typeId = uid(3), secondType = uid(4), categoryId = uid(5), materialId = uid(6), artisanId = uid(7);
const volume = uid(8), certified = uid(9), size = uid(10), color = uid(11), choice = uid(12), axis = uid(13);
const photo = uid(14), otherPhoto = uid(15), mediaId = uid(16), altMediaId = uid(17);
const stamp = "2026-11-20T08:00:00Z";
const version = "product-token+/==";
const childVersion = "variant-token+/==";
const apiRoot = "/api/v1/admin/";
const perms = ["catalog.read", "catalog.write", "catalog.publish", "pricing.write"];
const ready: Auth = { passwordChangeRequired: false, totpRequired: false, user: {
  id: uid(18), email: "product-owner@example.invalid", displayName: "Quản trị thử", isActive: true,
  mustChangePassword: false, totpEnabled: true, roles: ["owner"], rowVersion: "identity-version", permissions: perms
} };
const base: Product = {
  id: productId, name: "Ấm tử sa", slug: "am-tu-sa", skuBase: "AM-XX-0001", productTypeId: typeId, status: "draft", rowVersion: version,
  updatedAt: stamp, shortDescription: "Ấm trà dùng hằng ngày, làm từ đất tử sa với dáng tròn và nắp chắc chắn.",
  descriptionHtml: "<p>Mô tả <strong>in đậm</strong></p>", descriptionJson: null, descriptionWordCount: 4, thinDescription: true,
  materialId, artisanId, categoryIds: [categoryId], primaryCategoryId: categoryId, robotsNoindex: false,
  variants: [{ id: defaultId, sku: "AM-XX-0001", status: "active", priceVnd: null, rowVersion: childVersion }], media: [], attributes: []
};
const type = (id: string, name: string, isActive = true): TypeRow => ({ id, name, isActive, rowVersion: "type-v", createdAt: stamp,
  updatedAt: stamp, code: "type", skuPrefix: "AM", description: null, googleProductCategory: null, sortOrder: 0 });
const attr = (id: string, name: string, dataType: string, extra: Partial<AttributeRow> = {}): AttributeRow => ({
  id, name, dataType, code: "attr", productTypeId: typeId, rowVersion: "attr-v", unit: null, scope: "product", isActive: true,
  isVisible: true, isVariantAxis: false, isRequiredForPublish: false, sortOrder: 0, isFilterable: false, ...extra
});
const attributes = [attr(volume, "Dung tích", "number", { isRequiredForPublish: true, unit: "ml" }), attr(certified, "Có chứng nhận", "boolean"),
  attr(size, "Kích thước", "dimensions", { unit: "cm" }), attr(color, "Màu men", "option"), attr(axis, "Dung tích biến thể", "number", { scope: "variant", isVariantAxis: true })];
const asset = { id: photo, url: "/mock-product-image.svg", mimeType: "image/png", purpose: "catalog", defaultAlt: "Ảnh ấm tử sa", urlExpiresAt: null };

type Call = { method: string; path: string; query: URLSearchParams; body: Record<string, unknown> | null };
type Failure = { status: number; code: string; errors?: Record<string, string[]> };

async function fixture(page: Page, options: { product?: Partial<Product>; user?: Partial<Auth["user"]>; auth?: Partial<Auth>; signedFailure?: boolean } = {}) {
  let product = { ...structuredClone(base), ...options.product };
  const more = Array.from({ length: 21 }, (_, index) => ({ ...base, id: uid(100 + index), name: `Sản phẩm ${index + 2}`, skuBase: `AM-XX-${index + 2}`, status: "active", productTypeId: secondType }));
  const calls: Call[] = [];
  let nonce = 0, nextVersion = 0, signedFailure = options.signedFailure;
  let nextError: Failure | undefined;
  let writeGate: Promise<void> | undefined;
  let delayedSearch: Promise<void> | undefined;
  const signed: { method: string; headers: Record<string, string> }[] = [];
  const writes = () => calls.filter(call => call.method !== "GET");
  const waitForWrite = (path: string) => page.waitForResponse(response => response.request().method() !== "GET" && new URL(response.url()).pathname === apiRoot + path);
  await page.route("**/mock-product-image.svg", request => request.fulfill({ contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" width="36" height="36"><rect width="36" height="36" fill="#6f553c"/></svg>' }));
  await page.route("https://upload.example.invalid/**", async request => {
    signed.push({ method: request.request().method(), headers: await request.request().allHeaders() });
    await request.fulfill({ status: signedFailure ? 403 : 200, headers: { "access-control-allow-origin": "*" } });
    signedFailure = false;
  });
  await page.route("**/api/v1/admin/**", async request => {
    const req = request.request();
    const url = new URL(req.url());
    const path = url.pathname.slice(apiRoot.length);
    const [resource, id, relation, childId, command] = path.split("/");
    const method = req.method();
    const body = req.postDataJSON() as Record<string, unknown> | null;
    const query = url.searchParams;
    const result = (value: unknown, status = 200) => request.fulfill({ json: value, status });
    const list = (items: unknown[]) => result({ items, totalItems: items.length, page: 1, pageSize: 100 });
    calls.push({ path, method, body, query });
    if (path === "auth/me") return result({ ...ready, ...options.auth, user: { ...ready.user, ...options.user } });
    if (path === "auth/csrf") return result({ token: `mock-csrf-${++nonce}` });
    if (method !== "GET") expect(req.headers()["x-csrf-token"]).toBe(`mock-csrf-${nonce}`);
    if (resource === "categories") return list([{ id: categoryId, name: "Trà cụ", status: "visible" }]);
    if (resource === "materials") return list([{ id: materialId, name: "Tử nê", status: "archived" }]);
    if (resource === "artisans") return list([{ id: artisanId, name: "Chu Bân", status: "archived" }]);
    if (resource === "product-types") {
      if (path.endsWith("/options")) return list([{ id: choice, label: "Men trắng", isActive: true }]);
      if (path.endsWith("/attributes")) return list(id === typeId ? attributes : []);
      return list([type(typeId, "Ấm trà"), type(secondType, "Chén trà")]);
    }
    if (resource === "media") {
      if (method === "GET") return list([asset, { ...asset, id: otherPhoto, defaultAlt: "Ảnh thứ hai" }, { ...asset, id: uid(77), purpose: "evidence", defaultAlt: "Ảnh riêng tư" }]);
      if (path.endsWith("/complete")) return result(asset);
      expect(body?.purpose).toBe("catalog");
      return result({ uploadId: photo, putUrl: "https://upload.example.invalid/pending?X-Mock-Signature=fixture-only",
        headers: { "content-type": "image/png" }, expiresAt: new Date(Date.now() + 60000).toISOString() });
    }
    if (method === "GET" && !id) {
      const q = query.get("q") ?? "";
      if (q === "cũ" && delayedSearch) await delayedSearch;
      const normalized = (value: string) => value.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase();
      const items = [product, ...more].filter(row => normalized(`${row.name} ${row.skuBase}`).includes(normalized(q)) &&
        [["status", row.status], ["type", row.productTypeId], ["material", row.materialId], ["artisan", row.artisanId]].every(([param, value]) =>
          !query.has(param!) || query.get(param!) === value) && (!query.has("category") || row.categoryIds?.includes(query.get("category")!)));
      const index = Number(query.get("page") ?? 1), pageSize = Number(query.get("pageSize") ?? 20);
      expect(pageSize).toBeLessThanOrEqual(100);
      return result({ items: items.slice((index - 1) * pageSize, index * pageSize), page: index, pageSize, totalItems: items.length }).catch(() => {});
    }
    if (method === "GET") {
      const found = id === productId ? product : more.find(row => row.id === id);
      if (!found) return result({ code: "not_found" }, 404);
      if (relation !== "variants") return result(found);
      return result(found.variants?.find(row => row.id === childId));
    }
    if (writeGate) await writeGate;
    if (nextError) { const reply = nextError; nextError = undefined; return result(reply, reply.status); }
    if (!id && method === "POST") {
      product = { ...product, ...body, rowVersion: `product-${++nextVersion}` };
      return result(product, 201);
    }
    if (relation === "variants" && childId) expect(body?.rowVersion).toBe(product.variants?.find(row => row.id === childId)?.rowVersion);
    else if (body?.rowVersion !== product.rowVersion) return result({ code: "concurrency_conflict" }, 409);
    switch (relation) {
      case undefined: product = { ...product, ...body }; break;
      case "variants": {
        const rows = [...(product.variants ?? [])];
        const index = rows.findIndex(row => row.id === childId);
        const updates = { ...body, rowVersion: `variant-${nextVersion + 1}` } as Variant;
        if (childId) rows[index] = { ...rows[index], ...updates };
        else rows.push({ id: uid(70), sku: "AM-XX-0001-V2", status: "active", priceVnd: null, ...updates });
        product.variants = rows;
        break;
      }
      case "media": {
        let rows = [...(product.media ?? [])];
        if (!childId) rows.push({ id: body?.assetId === photo ? mediaId : altMediaId, url: asset.url, sortOrder: rows.length, ...body } as ProductImage);
        else if (childId === "order") rows = (body?.mediaIds as string[]).map((media, index) => ({ ...rows.find(row => row.id === media), sortOrder: index }));
        else if (command === "cover") rows = rows.map(row => ({ ...row, isCover: row.id === childId }));
        else if (method === "DELETE") rows = rows.filter(row => row.id !== childId);
        else rows = rows.map(row => row.id === childId ? { ...row, ...body } : row);
        product.media = rows;
        break;
      }
      case "publish": product = { ...product, status: "active", firstPublishedAt: stamp, descriptionWordCount: 149, thinDescription: true,
        warnings: [{ code: "thin_description_noindex", field: "descriptionHtml", actualWords: 149, recommendedWords: 150 }] }; break;
      case "archive": product = { ...product, status: "archived", archivedRedirectPath: product.firstPublishedAt ? "/danh-muc/tra-cu" : null }; break;
      case "restore-draft": product = { ...product, status: "draft", archivedRedirectPath: null }; break;
      default: return result({ code: "not_found" }, 404);
    }
    product.rowVersion = `product-${++nextVersion}`;
    return result(product);
  });
  return { calls, writes, waitForWrite, signed, reject: (value: Failure) => { nextError = value; },
    modify: (value: Partial<Product>) => { product = { ...product, ...value }; },
    hold: () => { let release!: () => void; writeGate = new Promise(resolve => { release = resolve; }); return () => { release(); writeGate = undefined; }; },
    holdSearch: () => { let release!: () => void; delayedSearch = new Promise(resolve => { release = resolve; }); return () => { release(); delayedSearch = undefined; }; }
  };
}

const tab = (page: Page, name: string) => page.getByRole("tab", { name, exact: true });
const button = (page: Page, name: string) => page.getByRole("button", { name, exact: true });
const editPage = `${route}/${productId}`;
const productPath = `products/${productId}`;
const save = (page: Page) => button(page, "Lưu thay đổi");

test("P1-12 G4 F2 initial failure, repeated 503, recovery and cancel preserve attribute ownership", async ({ page }) => {
  const api = await fixture(page);
  const definitionsPath = `**/product-types/${typeId}/attributes?*`;
  let unavailable = true;
  await page.route(definitionsPath, request => unavailable ? request.fulfill({ status: 503, json: {} }) : request.fallback());
  await page.goto(editPage);
  await tab(page, "Thuộc tính").click();
  await expect(page.getByText("Chưa tải được định nghĩa thuộc tính.", { exact: true })).toBeVisible();
  await expect(button(page, "Chỉnh sửa thuộc tính")).toBeDisabled();
  unavailable = false;
  await button(page, "Tải lại thuộc tính").click();
  await button(page, "Chỉnh sửa thuộc tính").click();
  const input = page.getByRole("textbox", { name: /Dung tích \(ml\)/ });
  await input.fill("17");
  unavailable = true;
  const failed = page.waitForResponse(response => response.url().includes("/attributes?") && response.status() === 503);
  await page.evaluate(() => window.dispatchEvent(new Event("visibilitychange")));
  await failed;
  await expect(page.getByText(/Thay đổi đang nhập được giữ lại/)).toBeVisible();
  await expect(input).toHaveValue("17");
  await expect(button(page, "Hủy")).toBeEnabled();
  await expect(save(page)).toBeDisabled();
  const second = page.waitForResponse(response => response.url().includes("/attributes?") && response.status() === 503);
  await button(page, "Tải lại thuộc tính").click();
  await second;
  await expect(input).toHaveValue("17");
  unavailable = false;
  await button(page, "Tải lại thuộc tính").click();
  await expect(save(page)).toBeEnabled();
  await save(page).click();
  await expect(button(page, "Chỉnh sửa thuộc tính")).toBeVisible();
  expect(api.writes().at(-1)?.body?.attributes).toEqual([{ attributeId: volume, valueNumber: 17 }]);
  await button(page, "Chỉnh sửa thuộc tính").click();
  await input.fill("18");
  unavailable = true;
  await page.evaluate(() => window.dispatchEvent(new Event("visibilitychange")));
  await expect(page.getByText(/Thay đổi đang nhập được giữ lại/)).toBeVisible();
  page.once("dialog", dialog => dialog.accept());
  await button(page, "Hủy").click();
  await expect(input).toHaveValue("17");
  await tab(page, "SEO").click();
  await expect(button(page, "Chỉnh sửa seo")).toBeEnabled();
});

test("P1-12 G4 F2 changed definitions and options require explicit reconciliation and stay scoped to product", async ({ page }) => {
  const api = await fixture(page);
  let change = false;
  await page.route(`**/product-types/${typeId}/attributes?*`, request => change
    ? request.fulfill({ json: { items: attributes.map(def => def.id === volume ? { ...def, unit: "l" } : def), totalItems: attributes.length } })
    : request.fallback());
  await page.route("**/attributes/*/options?*", request => change
    ? request.fulfill({ json: { items: [{ id: choice, label: "Men trắng", isActive: false }], totalItems: 1 } })
    : request.fallback());
  await page.goto(editPage);
  await tab(page, "Thuộc tính").click();
  await button(page, "Chỉnh sửa thuộc tính").click();
  await page.getByRole("textbox", { name: /Dung tích \(ml\)/ }).fill("17");
  await page.getByLabel("Màu men", { exact: true }).selectOption(choice);
  await page.getByLabel("Có chứng nhận", { exact: true }).selectOption("false");
  change = true;
  const refreshed = page.waitForResponse(response => response.url().includes("/options?"));
  await page.evaluate(() => window.dispatchEvent(new Event("visibilitychange")));
  await refreshed;
  await expect(page.getByRole("main").getByRole("alert")).toContainText("Dung tích · 17");
  await expect(page.getByRole("main").getByRole("alert")).toContainText("Màu men");
  await expect(save(page)).toBeDisabled();
  await expect(page.getByLabel("Có chứng nhận", { exact: true })).toHaveValue("false");
  page.once("dialog", dialog => dialog.accept());
  await button(page, "Bỏ thay đổi không tương thích").click();
  await expect(page.getByRole("textbox", { name: /Dung tích \(l\)/ })).toHaveValue("");
  await tab(page, "SEO").click();
  await expect(tab(page, "Thuộc tính")).toHaveAttribute("aria-selected", "true");
  await expect(page.getByLabel("Có chứng nhận", { exact: true })).toHaveValue("false");
  await expect(save(page)).toBeEnabled();
  await expect(button(page, "Hủy")).toBeEnabled();
  await save(page).click();
  await expect(button(page, "Chỉnh sửa thuộc tính")).toBeVisible();
  expect(api.writes().at(-1)?.body?.attributes).toEqual([{ attributeId: certified, valueBool: false }]);
  await page.goto(`${route}/${uid(100)}`);
  await tab(page, "Thuộc tính").click();
  await expect(page.getByText("Loại sản phẩm chưa có thuộc tính cho mục này.")).toBeVisible();
  await expect(page.getByRole("textbox", { name: /Dung tích/ })).toHaveCount(0);
});

test("P1-12 G4 F2 discarding the last incompatible edit releases ownership", async ({ page }) => {
  const api = await fixture(page);
  let changed = false;
  await page.route(`**/product-types/${typeId}/attributes?*`, request => changed
    ? request.fulfill({ json: { items: attributes.map(def => def.id === volume ? { ...def, unit: "l" } : def), totalItems: attributes.length } })
    : request.fallback());
  await page.goto(editPage);
  await tab(page, "Thuộc tính").click();
  await button(page, "Chỉnh sửa thuộc tính").click();
  await page.getByRole("textbox", { name: /Dung tích \(ml\)/ }).fill("17");
  changed = true;
  const refreshed = page.waitForResponse(response => response.url().includes("/attributes?"));
  await page.evaluate(() => window.dispatchEvent(new Event("visibilitychange")));
  await refreshed;
  await expect(page.getByRole("main").getByRole("alert")).toContainText("Dung tích · 17");
  page.once("dialog", dialog => dialog.accept());
  await button(page, "Bỏ thay đổi không tương thích").click();
  await expect(button(page, "Chỉnh sửa thuộc tính")).toBeEnabled();
  await expect(save(page)).toHaveCount(0);
  await expect(button(page, "Công khai")).toBeEnabled();
  await expect(page.getByRole("textbox", { name: /Dung tích \(l\)/ })).toHaveValue("");
  await tab(page, "SEO").click();
  await expect(tab(page, "SEO")).toHaveAttribute("aria-selected", "true");
  expect(api.writes()).toHaveLength(0);
});

test("P1-12 G4 F1 paste, undo/redo and failed save keep the rich draft and editor instance", async ({ page }) => {
  const api = await fixture(page);
  await page.goto(editPage);
  const editor = page.getByRole("textbox", { name: "Mô tả chi tiết", exact: true });
  await expect(editor).toHaveText("Mô tả in đậm");
  const originalEditor = await editor.elementHandle();
  await button(page, "Chỉnh sửa thông tin").click();
  await editor.click();
  await page.keyboard.press("End");
  await editor.evaluate(element => {
    const clipboardData = new DataTransfer();
    clipboardData.setData("text/html", '<p><strong>Dán đậm</strong><a href="javascript:void(0)">Liên kết</a><script>unsafe</script></p>');
    element.dispatchEvent(new ClipboardEvent("paste", { clipboardData, bubbles: true, cancelable: true }));
  });
  await expect(editor).toContainText("Dán đậm");
  await expect(editor.locator('script,a[href^="javascript:"]')).toHaveCount(0);
  await page.keyboard.press("Control+z");
  await expect(editor).toHaveText("Mô tả in đậm");
  await expect(save(page)).toBeDisabled();
  await page.keyboard.press("Control+y");
  await expect(editor).toContainText("Dán đậm");
  api.reject({ status: 409, code: "slug_exists" });
  const release = api.hold();
  const response = api.waitForWrite(productPath);
  await save(page).click();
  await expect(editor).toHaveAttribute("contenteditable", "false");
  release();
  expect((await response).status()).toBe(409);
  await expect(editor).toHaveAttribute("contenteditable", "true");
  await expect(editor).toContainText("Dán đậm");
  await save(page).click();
  await expect(button(page, "Chỉnh sửa thông tin")).toBeVisible();
  expect(await editor.evaluate((element, original) => element === original, originalEditor)).toBe(true);
  expect(api.writes()).toHaveLength(2);
});

test("P1-12 server list, reference filters, pagination and stale search response", async ({ page }) => {
  const api = await fixture(page);
  await page.goto(route);
  await expect(page.getByRole("table", { name: "Danh sách sản phẩm" })).toBeVisible();
  await expect(page.getByText("AM-XX-0001", { exact: true })).toBeVisible();
  await button(page, "Trang sau").click();
  await expect(page.getByText("Trang 2 / 2 · 22 mục")).toBeVisible();
  await page.getByLabel("Loại sản phẩm", { exact: true }).selectOption(typeId);
  await page.getByLabel("Danh mục", { exact: true }).selectOption(categoryId);
  await page.getByLabel("Chất liệu", { exact: true }).selectOption(materialId);
  await page.getByLabel("Nghệ nhân", { exact: true }).selectOption(artisanId);
  await page.getByLabel("Trạng thái", { exact: true }).selectOption("draft");
  await expect(page.getByText("Trang 1 / 1 · 1 mục")).toBeVisible();
  const release = api.holdSearch();
  const search = page.getByRole("searchbox", { name: "Tìm tên hoặc SKU" });
  await search.fill("cũ");
  await expect.poll(() => api.calls.some(call => call.query.get("q") === "cũ")).toBe(true);
  await search.fill("am tu sa");
  await expect(page.getByText("AM-XX-0001", { exact: true })).toBeVisible();
  release();
  await expect(page.getByText("AM-XX-0001", { exact: true })).toBeVisible();
  const query = api.calls.filter(call => call.path === "products").at(-1)!.query;
  expect(Object.fromEntries(query)).toMatchObject({ q: "am tu sa", page: "1", pageSize: "20", type: typeId, category: categoryId, material: materialId, artisan: artisanId, status: "draft" });
});

test("P1-12 draft create handles duplicate slug without GET new or duplicate write", async ({ page }) => {
  const api = await fixture(page);
  await page.goto(`${route}/new`);
  await page.getByLabel("Loại sản phẩm", { exact: true }).selectOption(typeId);
  await page.getByLabel("Tên sản phẩm", { exact: true }).fill("Ấm mới");
  await page.getByLabel("Slug", { exact: true }).fill("am-moi");
  api.reject({ status: 409, code: "slug_exists" });
  await button(page, "Tạo bản nháp").click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("Đường dẫn đã tồn tại");
  await expect(page.getByLabel("Tên sản phẩm", { exact: true })).toHaveValue("Ấm mới");
  await page.getByLabel("Slug", { exact: true }).fill("am-moi-2");
  await button(page, "Tạo bản nháp").click();
  await expect(page).toHaveURL(new RegExp(`${productId}$`));
  await expect(page.getByRole("heading", { name: "Ấm mới" })).toBeVisible();
  expect(api.writes().at(-1)?.body).toEqual({ name: "Ấm mới", slug: "am-moi-2", productTypeId: typeId });
  expect(api.calls.some(call => call.path === "products/new")).toBe(false);
});

test("P1-12 edit type confirmation, rich content, references and typed attributes", async ({ page }) => {
  const api = await fixture(page);
  await page.goto(editPage);
  await expect(page.locator("#descriptionHtml strong")).toHaveText("in đậm");
  await button(page, "Chỉnh sửa thông tin").click();
  await page.getByLabel("Loại sản phẩm", { exact: true }).selectOption(secondType);
  await expect(save(page)).toBeDisabled();
  await page.getByRole("checkbox", { name: /Xác nhận đổi loại sản phẩm/ }).check();
  await save(page).click();
  await expect(button(page, "Chỉnh sửa thông tin")).toBeVisible();
  expect(api.writes().at(-1)?.body).toEqual({ productTypeId: secondType, confirmTypeChange: true, rowVersion: version });
  await button(page, "Chỉnh sửa thông tin").click();
  await page.getByLabel("Loại sản phẩm", { exact: true }).selectOption(typeId);
  await page.getByRole("checkbox", { name: /Xác nhận đổi loại sản phẩm/ }).check();
  await page.getByRole("textbox", { name: "Mô tả chi tiết", exact: true }).fill("Mô tả được chỉnh sửa");
  await page.getByRole("textbox", { name: "Mô tả chi tiết", exact: true }).press("Control+a");
  await page.getByRole("group", { name: "Định dạng mô tả chi tiết" }).getByRole("button", { name: "Đậm" }).click();
  await page.getByLabel("Tên phụ", { exact: true }).fill("Tên phụ đã sửa");
  await save(page).click();
  await expect(button(page, "Chỉnh sửa thông tin")).toBeVisible();
  await expect(page.locator("#descriptionHtml strong")).toHaveText("Mô tả được chỉnh sửa");
  expect(api.writes().at(-1)?.body).toMatchObject({ subtitle: "Tên phụ đã sửa", descriptionHtml: "<p><strong>Mô tả được chỉnh sửa</strong></p>" });
  expect(api.writes().at(-1)?.body).not.toHaveProperty("materialId");
  expect(api.writes().at(-1)?.body).not.toHaveProperty("artisanId");
  await tab(page, "Thuộc tính").click();
  await button(page, "Chỉnh sửa thuộc tính").click();
  await page.getByLabel(/Dung tích \(ml\)/).fill("0");
  await page.getByLabel("Có chứng nhận", { exact: true }).selectOption("false");
  await page.getByLabel("Màu men", { exact: true }).selectOption(choice);
  await button(page, "Nhập Kích thước").click();
  await page.getByLabel("Chiều rộng", { exact: true }).fill("2,5");
  await save(page).click();
  await expect(button(page, "Chỉnh sửa thuộc tính")).toBeVisible();
  expect(api.writes().at(-1)?.body?.attributes).toEqual(expect.arrayContaining([
    { attributeId: volume, valueNumber: 0 }, { attributeId: certified, valueBool: false }, { attributeId: color, optionId: choice }, { attributeId: size, valueJson: { w: 2.5 } }
  ]));
});

test("P1-12 variant create, price range, inactive NULL, stable SKU and child version", async ({ page }) => {
  const api = await fixture(page);
  await page.goto(editPage);
  await tab(page, "Biến thể & giá").click();
  await expect(page.getByText("AM-XX-0001 · Hoạt động ·")).toBeVisible();
  await button(page, "Thêm biến thể").click();
  await page.getByLabel("Tên biến thể").fill("Ấm lớn");
  await page.getByLabel("Giá niêm yết (VND)", { exact: true }).fill("10000000001");
  await button(page, "Tạo biến thể").click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("10.000.000.000");
  expect(api.writes()).toHaveLength(0);
  await page.getByLabel("Giá niêm yết (VND)", { exact: true }).fill("3.050.000");
  await page.getByRole("textbox", { name: /Dung tích biến thể/ }).fill("245");
  await button(page, "Tạo biến thể").click();
  await expect(page.getByRole("heading", { name: "Ấm lớn" })).toBeVisible();
  expect(api.writes().at(-1)?.body).toMatchObject({ rowVersion: version, priceVnd: 3050000 });
  expect(api.writes().at(-1)?.body).not.toHaveProperty("sku");
  await button(page, "Chỉnh sửa biến thể AM-XX-0001-V2").click();
  await page.getByLabel("Giá niêm yết (VND)", { exact: true }).fill("");
  await page.getByLabel("Trạng thái biến thể", { exact: true }).selectOption("inactive");
  await button(page, "Lưu biến thể").click();
  await expect(page.getByText("AM-XX-0001-V2 · Ngừng bán ·")).toBeVisible();
  expect(api.writes().at(-1)?.body).toMatchObject({ rowVersion: "variant-1", priceVnd: null, status: "inactive" });
});

test("P1-12 catalog writer omits price and SKU from the write payload", async ({ page }) => {
  const api = await fixture(page, { user: { permissions: ["catalog.read", "catalog.write"], roles: ["content"] } });
  await page.goto(editPage);
  await tab(page, "Biến thể & giá").click();
  await button(page, "Chỉnh sửa biến thể AM-XX-0001").click();
  await expect(page.getByLabel("Giá niêm yết (VND)", { exact: true })).toHaveCount(0);
  await expect(page.getByLabel("SKU biến thể", { exact: true })).toBeDisabled();
  await page.getByLabel("Tên biến thể").fill("Tên không sửa giá");
  await button(page, "Lưu biến thể").click();
  await expect(page.getByRole("heading", { name: "Tên không sửa giá" })).toBeVisible();
  expect(api.writes().at(-1)?.body).toEqual({ name: "Tên không sửa giá", rowVersion: childVersion });
  await expect(button(page, "Công khai")).toHaveCount(0);
});

test("G4-F1 restricted staff can edit other fields, reverse classification and create unique variants", async ({ page }) => {
  const permissions = ["catalog.read", "catalog.write", "inventory.read"];
  const api = await fixture(page, { user: { permissions, roles: ["content"] }, product: { variants: [
    { ...base.variants![0], isUniquePiece: false },
    { id: uid(170), sku: "AM-UNIQUE-01", rowVersion: "unique-v", status: "active", isUniquePiece: true }
  ] } });
  await page.goto(editPage);
  await tab(page, "Biến thể & giá").click();
  await button(page, "Chỉnh sửa biến thể AM-XX-0001").click();
  const unique = page.getByRole("checkbox", { name: "Hàng độc bản", exact: true });
  await expect(unique).toBeDisabled();
  await expect(unique).not.toBeChecked();
  await expect(unique).toHaveAccessibleDescription("Cần quyền đọc và ghi kho để chuyển biến thể này thành hàng độc bản.");
  await page.getByLabel("Tên biến thể").fill("Giữ phân loại");
  await button(page, "Lưu biến thể").click();
  await expect(page.getByRole("heading", { name: "Giữ phân loại" })).toBeVisible();
  expect(api.writes().at(-1)?.body).toEqual({ name: "Giữ phân loại", rowVersion: childVersion });

  await button(page, "Chỉnh sửa biến thể AM-UNIQUE-01").click();
  await expect(unique).toBeEnabled();
  await expect(unique).toBeChecked();
  await unique.uncheck();
  await button(page, "Lưu biến thể").click();
  await expect(unique).toHaveCount(0);
  expect(api.writes().at(-1)?.body).toEqual({ isUniquePiece: false, rowVersion: "unique-v" });

  await button(page, "Thêm biến thể").click();
  await expect(unique).toBeEnabled();
  await unique.check();
  await button(page, "Tạo biến thể").click();
  await expect(unique).toHaveCount(0);
  expect(api.writes().at(-1)?.body).toMatchObject({ isUniquePiece: true });
});

test("G4-F1 staff with catalog and inventory permissions can classify an existing variant", async ({ page }) => {
  const permissions = ["catalog.read", "catalog.write", "inventory.read", "inventory.write"];
  const api = await fixture(page, { user: { permissions, roles: ["content", "sales"] } });
  await page.goto(editPage);
  await tab(page, "Biến thể & giá").click();
  await button(page, "Chỉnh sửa biến thể AM-XX-0001").click();
  const unique = page.getByRole("checkbox", { name: "Hàng độc bản", exact: true });
  await expect(unique).toBeEnabled();
  await unique.check();
  await button(page, "Lưu biến thể").click();
  await expect(unique).toHaveCount(0);
  expect(api.writes().at(-1)?.body).toEqual({ isUniquePiece: true, rowVersion: childVersion });
});

test("P1-12 read-only list/editor and direct create or denied routes do not start mutations", async ({ page, context }) => {
  const api = await fixture(page, { user: { permissions: ["catalog.read"], roles: ["sales"] } });
  await page.goto(route);
  await expect(page.getByRole("link", { name: "Tạo sản phẩm" })).toHaveCount(0);
  await page.getByRole("link", { name: "Xem Ấm tử sa", exact: true }).click();
  await tab(page, "Biến thể & giá").click();
  await expect(button(page, "Thêm biến thể")).toHaveCount(0);
  await expect(button(page, "Công khai")).toHaveCount(0);
  await page.goto(`${route}/new`);
  await expect(page.getByRole("heading", { name: "Bạn chưa có quyền tạo sản phẩm" })).toBeVisible();
  expect(api.writes()).toHaveLength(0);
  expect(api.calls.some(call => call.path.includes("/variants"))).toBe(false);
  const denied = await context.newPage();
  const guard = await fixture(denied, { user: { permissions: [] } });
  await denied.goto(editPage);
  await expect(denied.getByRole("heading", { name: "Bạn chưa có quyền truy cập" })).toBeVisible();
  expect(guard.calls.some(call => call.path.startsWith("products"))).toBe(false);
  const gated = await context.newPage();
  const onboarding = await fixture(gated, { auth: { passwordChangeRequired: true } });
  await gated.goto(editPage);
  await expect(gated).toHaveURL(/change-password$/);
  expect(onboarding.calls.some(call => call.path.startsWith("products"))).toBe(false);
});

test("P1-12 upload retry, complete and attach before cover, ALT, variant, reorder and detach", async ({ page }) => {
  const api = await fixture(page, { signedFailure: true, product: { media: [{ id: altMediaId, assetId: otherPhoto, altText: "Ảnh thứ hai", sortOrder: 0 }] } });
  await page.goto(editPage);
  await tab(page, "Ảnh").click();
  await button(page, "Thêm ảnh").click();
  await button(page, "Chọn từ thư viện ảnh").click();
  await expect(page.getByText(/Ảnh riêng tư/)).toHaveCount(0);
  await expect(page.getByRole("button", { name: /Ảnh ấm tử sa/ })).toBeVisible();
  await page.getByLabel("Tệp ảnh mới", { exact: true }).setInputFiles({ name: "anh.png", mimeType: "image/png", buffer: Buffer.from("synthetic image") });
  await button(page, "Tải ảnh lên").click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("Không tải được ảnh");
  expect(api.calls.some(call => call.path.endsWith("/complete"))).toBe(false);
  await button(page, "Tải ảnh lên").click();
  await expect(page.getByText(/Ảnh đã sẵn sàng/)).toBeVisible();
  expect(api.signed).toHaveLength(2);
  expect(api.signed[1].headers["content-type"]).toBe("image/png");
  expect(api.signed[1].headers["x-csrf-token"]).toBeUndefined();
  expect(api.signed[1].headers.cookie).toBeUndefined();
  await page.getByLabel("Văn bản thay thế (ALT)").fill("Ảnh bìa mặt trước");
  await page.getByLabel("Ảnh cho biến thể").selectOption(defaultId);
  await button(page, "Gắn ảnh").click();
  const image = page.getByRole("listitem").filter({ hasText: "Ảnh bìa mặt trước" });
  await expect(image).toContainText("Biến thể AM-XX-0001");
  expect(api.writes().slice(-2).map(call => call.path)).toEqual([`media/upload-intents/${photo}/complete`, `${productPath}/media`]);
  await image.getByRole("button", { name: "Đặt làm ảnh bìa" }).click();
  await expect(image.getByText("Ảnh bìa", { exact: true })).toBeVisible();
  await button(page, "Đưa ảnh 2 lên").click();
  await expect.poll(() => api.writes().at(-1)?.body?.mediaIds).toEqual([mediaId, altMediaId]);
  await expect(button(page, "Đưa ảnh 1 xuống")).toBeEnabled();
  await image.getByRole("button", { name: "Sửa ALT và biến thể" }).click();
  await page.getByLabel("Văn bản thay thế (ALT)").fill("ALT cập nhật");
  await page.getByLabel("Ảnh cho biến thể").selectOption("");
  await button(page, "Lưu ảnh").click();
  await expect(page.getByText("ALT cập nhật", { exact: true })).toBeVisible();
  expect(api.writes().at(-1)?.body).toMatchObject({ rowVersion: "product-3", variantId: null });
  page.once("dialog", dialog => dialog.accept());
  await page.getByRole("listitem").filter({ hasText: "ALT cập nhật" }).getByRole("button", { name: "Gỡ ảnh" }).click();
  await expect(page.getByText("ALT cập nhật", { exact: true })).toHaveCount(0);
  expect(api.writes().at(-1)).toMatchObject({ method: "DELETE", path: `${productPath}/media/${mediaId}` });
});

test("P1-12 all publish errors, explicit SEO fill, thin warning, archive and restore", async ({ page }) => {
  const api = await fixture(page);
  await page.goto(editPage);
  api.reject({ status: 422, code: "publish_requirements_not_met", errors: { "media.cover.altText": ["cover_alt_missing"],
    [`attributes[${volume}]`]: ["required_attribute_missing"], variants: ["variant_axis_duplicate"], new_rule: ["unsupported_code"] } });
  await button(page, "Công khai").click();
  const error = page.getByRole("alert").filter({ hasText: "Sản phẩm chưa đủ điều kiện công khai" });
  await expect(error).toContainText("Thiếu ảnh bìa có ALT hợp lệ");
  await expect(error).toContainText("Thiếu giá trị thuộc tính bắt buộc");
  await expect(error).toContainText("tổ hợp trục trùng nhau");
  await expect(error).toContainText("unsupported_code");
  await error.getByRole("button", { name: "Xem Ảnh" }).click();
  await expect(page.getByRole("heading", { name: "Ảnh sản phẩm" })).toBeFocused();
  await tab(page, "SEO").click();
  await button(page, "Chỉnh sửa seo").click();
  await button(page, "Điền từ tên sản phẩm").click();
  await button(page, "Điền từ mô tả ngắn").click();
  await expect(page.getByLabel("Tiêu đề SEO", { exact: true })).toHaveValue("Ấm tử sa");
  await page.getByLabel("Từ khóa trọng tâm", { exact: true }).fill("ấm tử sa");
  await save(page).click();
  await expect(button(page, "Công khai")).toBeEnabled();
  expect(api.writes().at(-1)?.body).not.toHaveProperty("robotsNoindex");
  await button(page, "Công khai").click();
  await expect(page.getByText("Đang công khai", { exact: true })).toBeVisible();
  await expect(page.getByText(/Mô tả đã lưu có 149 từ/).first()).toBeVisible();
  await expect(page.getByRole("checkbox", { name: "Noindex thủ công" })).not.toBeChecked();
  await button(page, "Lưu trữ").click();
  await page.getByLabel("Đường dẫn chuyển hướng", { exact: true }).fill("/khong-hop-le");
  api.reject({ status: 400, code: "validation_failed", errors: { redirectTo: ["Đích chưa công khai."] } });
  await button(page, "Xác nhận lưu trữ").click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("Đích chưa công khai.");
  await page.getByLabel("Đường dẫn chuyển hướng", { exact: true }).fill("");
  await button(page, "Xác nhận lưu trữ").click();
  await expect(page.getByText(/Đường dẫn chuyển hướng · \/danh-muc\/tra-cu/)).toBeVisible();
  expect(api.writes().at(-1)?.body).not.toHaveProperty("redirectTo");
  page.once("dialog", dialog => dialog.accept());
  await button(page, "Khôi phục bản nháp").click();
  await expect(page.getByText("Bản nháp", { exact: true })).toBeVisible();
});

test("P1-12 slow and stale save preserves dirty form, blocks tab, link and repeat, then reloads explicitly", async ({ page }) => {
  const api = await fixture(page);
  await page.goto(editPage);
  await button(page, "Chỉnh sửa thông tin").click();
  const field = page.getByLabel("Tên phụ", { exact: true });
  await field.fill("Đang sửa");
  await tab(page, "SEO").click();
  await expect(tab(page, "Thông tin")).toHaveAttribute("aria-selected", "true");
  await expect(button(page, "Công khai")).toBeDisabled();
  page.once("dialog", dialog => dialog.dismiss());
  await page.getByRole("link", { name: /Danh sách sản phẩm/ }).click();
  await expect(page).toHaveURL(new RegExp(`${productId}$`));
  api.modify({ subtitle: "Nội dung từ phiên khác", rowVersion: "remote-2" });
  const refetch = page.waitForResponse(response => response.request().method() === "GET" && response.url().endsWith(productPath));
  await page.evaluate(() => window.dispatchEvent(new Event("visibilitychange")));
  expect((await refetch).status()).toBe(200);
  await expect(field).toHaveValue("Đang sửa");
  const release = api.hold();
  const request = api.waitForWrite(productPath);
  await save(page).click();
  await expect(button(page, "Đang xử lý…")).toBeDisabled();
  await expect(field).toHaveValue("Đang sửa");
  release();
  expect((await request).status()).toBe(409);
  await expect(field).toHaveValue("Đang sửa");
  await expect(save(page)).toBeDisabled();
  expect(api.writes()).toHaveLength(1);
  page.once("dialog", dialog => dialog.accept());
  await button(page, "Bỏ thay đổi và tải phiên bản mới").click();
  await expect(field).toHaveValue("Nội dung từ phiên khác");
  await button(page, "Chỉnh sửa thông tin").click();
  await field.fill("Sau đối chiếu");
  await save(page).click();
  await expect(button(page, "Chỉnh sửa thông tin")).toBeVisible();
  expect(api.writes().at(-1)?.body?.rowVersion).toBe("remote-2");
});

test("P1-12 SKU conflict and permission failure keep the edit usable and missing product is explicit", async ({ page }) => {
  const api = await fixture(page);
  await page.goto(editPage);
  await tab(page, "Biến thể & giá").click();
  await button(page, "Chỉnh sửa biến thể AM-XX-0001").click();
  await page.getByLabel("SKU biến thể", { exact: true }).fill("MANUAL-01");
  api.reject({ status: 409, code: "sku_exists" });
  await button(page, "Lưu biến thể").click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("SKU này đã tồn tại");
  await expect(page.getByLabel("SKU biến thể", { exact: true })).toHaveValue("MANUAL-01");
  await expect(button(page, "Lưu biến thể")).toBeEnabled();
  api.reject({ status: 403, code: "forbidden" });
  await button(page, "Lưu biến thể").click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("Bạn chưa được cấp quyền");
  expect(api.writes()).toHaveLength(2);
  await expect(page.getByLabel("SKU biến thể", { exact: true })).toHaveValue("MANUAL-01");
  page.once("dialog", dialog => dialog.accept());
  await button(page, "Hủy").click();
  await page.goto(`${route}/${uid(999)}`);
  await expect(page.getByRole("heading", { name: "Không tìm thấy sản phẩm" })).toBeVisible();
});

test("P1-12 keyboard tab activation and 390 px layout", async ({ page }) => {
  await fixture(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(editPage);
  await tab(page, "Thông tin").focus();
  await page.keyboard.press("ArrowRight");
  await expect(tab(page, "Thuộc tính")).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(tab(page, "Thuộc tính")).toHaveAttribute("aria-selected", "true");
  await expect(tab(page, "Thuộc tính")).toBeFocused();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  const scan = await new AxeBuilder({ page }).include(".products-admin").analyze();
  expect(scan.violations).toEqual([]);
});
