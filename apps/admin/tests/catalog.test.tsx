import assert from "node:assert/strict";
import test from "node:test";
import { JSDOM } from "jsdom";
import { QueryClient } from "@tanstack/react-query";
import { AdminApi, ApiError, isConcurrencyConflict } from "../src/lib/api";
import { CatalogApi, productDetailOptions, productSearchOptions, type CatalogRow, type TypeRow } from "../src/lib/catalog-api";
import { defaults, formSchema, referenceFields, taxonomyFields, validParent, paragraphHtml, plainText, categoryPath } from "../src/lib/catalog-forms";

const NativeRequest = globalThis.Request;
globalThis.Request = class extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) { super(typeof input === "string" ? new URL(input, "https://admin.example.invalid") : input, init); }
};
const row = { id: "one", rowVersion: "opaque-do-not-decode", name: "Ấm", parentId: null, status: "hidden", slug: "am" } as CatalogRow;

test("FR-CAT-08 discovery uses generated product paths, Unicode/SKU query, bounded pages and cancellation", async () => {
  const requests: Request[] = [];
  const controller = new AbortController();
  const api = new CatalogApi(new AdminApi((async (request: Request) => {
    requests.push(request);
    return Response.json(request.url.includes("/products/") ? { id: "synthetic", name: "Ấm tử sa", skuBase: "AM-XX-00001" } : { items: [], page: 2, pageSize: 20, totalItems: 21 });
  }) as typeof fetch));
  const page = await api.searchProducts({ page: 2, pageSize: 20, q: "Ấm VARIANT-001", status: "archived" }, controller.signal);
  assert.equal(page.totalItems, 21);
  const url = new URL(requests[0].url);
  assert.equal(url.pathname, "/api/v1/admin/products"); assert.equal(url.searchParams.get("q"), "Ấm VARIANT-001");
  assert.equal(url.searchParams.get("status"), "archived"); assert.equal(url.searchParams.get("pageSize"), "20");
  assert.equal(url.searchParams.get("page"), "2"); assert.equal(url.searchParams.has("stock"), false);
  assert.equal(requests[0].credentials, "include");
  assert.equal((await api.product("synthetic", controller.signal)).skuBase, "AM-XX-00001");
  assert.equal(new URL(requests[1].url).pathname, "/api/v1/admin/products/synthetic");
  controller.abort(); assert.equal(requests[0].signal.aborted, true); assert.equal(requests[1].signal.aborted, true);
});

test("F3 search and detail QueryClient entries contain only display and paging data", async () => {
  const raw = {
    id: "synthetic", name: "Ấm", status: "draft", skuBase: "AM-001", descriptionHtml: "<p>Not needed</p>",
    variants: [{ priceVnd: 120000, lowStockThreshold: 5, sku: "VARIANT-001" }],
    media: [{ isCover: true, url: "/cover.jpg", altText: "Ấm", id: "image", sortOrder: 4 }]
  };
  const api = new CatalogApi(new AdminApi((async (request: Request) => Response.json(
    request.url.includes("/products/") ? raw : { items: [raw], page: 2, pageSize: 20, totalItems: 21 }
  )) as typeof fetch));
  const cache = new QueryClient();
  const expected = { id: "synthetic", name: "Ấm", status: "draft", skuBase: "AM-001", cover: { url: "/cover.jpg", altText: "Ấm" } };
  const search = productSearchOptions(api, cache, "Ấm", "", 2);
  const detail = productDetailOptions(api, raw.id);
  try {
    await cache.fetchQuery(search);
    assert.deepEqual(cache.getQueryData(search.queryKey), { items: [expected], page: 2, pageSize: 20, totalItems: 21 });
    assert.deepEqual(cache.getQueryData(detail.queryKey), expected);
    cache.removeQueries({ queryKey: detail.queryKey });
    await cache.fetchQuery(detail);
    assert.deepEqual(cache.getQueryData(detail.queryKey), expected);
  } finally { cache.clear(); }
});

test("FR-CAT-08 empty full-list replacement preserves opaque token and fresh CSRF", async () => {
  let token = 0; const writes: Request[] = [];
  const api = new CatalogApi(new AdminApi((async (request: Request) => {
    if (request.url.endsWith("/csrf")) return Response.json({ token: `token-${++token}` });
    writes.push(request); return Response.json({ id: "collection", rowVersion: "returned-version", items: [] });
  }) as typeof fetch));
  const result = await api.orderProducts("collection", "opaque+/==", []);
  assert.equal(result.rowVersion, "returned-version"); assert.deepEqual(await writes[0].json(), { rowVersion: "opaque+/==", items: [] });
  assert.equal(writes[0].method, "PUT"); assert.equal(writes[0].headers.get("X-CSRF-TOKEN"), "token-1");
});

test("G4 409 classifier requires both status and concurrency_conflict code", () => {
  assert.equal(isConcurrencyConflict(new ApiError(409, { code: "concurrency_conflict" })), true);
  for (const error of [new ApiError(400, { code: "concurrency_conflict" }), new Error("concurrency_conflict"), new ApiError(409, {}), new ApiError(409, { code: "unknown" }), { status: 409, code: "concurrency_conflict" }]) assert.equal(isConcurrencyConflict(error), false);
});

for (const [code, field] of [["slug_exists", "slug"], ["sku_code_exists", "skuCode"], ["product_type_code_exists", "code"], ["attribute_code_exists", "code"], ["option_code_exists", "code"], ["slug_allocation_exhausted", "slug"]] as const) test(`G4 ${code}: useful field errors without stale classification or mutation replay`, async () => {
  let writes = 0;
  const api = new CatalogApi(new AdminApi((async (r: Request) => {
    if (r.url.endsWith("/csrf")) return Response.json({ token: "test" });
    writes++; return writes === 1 ? Response.json({ code }, { status: 409 }) : Response.json(row);
  }) as typeof fetch));
  await assert.rejects(api.save("materials", { name: "Giữ lại", slug: "trung" }), (error: unknown) => error instanceof ApiError && !isConcurrencyConflict(error) && !!error.fields[field]?.[0]);
  assert.equal(writes, 1);
  await api.save("materials", { name: "Giữ lại", slug: "moi" }); assert.equal(writes, 2);
});

test("G4 domain structure conflicts are actionable errors without a stale reload requirement", () => {
  for (const code of ["attribute_in_use", "attribute_has_options", "attribute_not_option", "publication_history_unverified"]) {
    const error = new ApiError(409, { code }); assert.equal(isConcurrencyConflict(error), false); assert.ok(error.message); assert.ok(!error.message.includes("phiên bản"));
  }
});

test("P1-11 generated endpoint matrix: all mutations keep credentials, fresh CSRF and opaque versions", async () => {
  const calls: Request[] = [];
  const api = new CatalogApi(new AdminApi((async (r: Request) => {
    calls.push(r);
    if (r.url.endsWith("/csrf")) return Response.json({ token: `fresh-${calls.length}` });
    if (r.method === "GET" && !r.url.includes("/products")) return Response.json({ items: [], totalItems: 0 });
    return Response.json({ ...row, items: [], warnings: [] });
  }) as typeof fetch));
  for (const resource of ["categories", "collections", "materials", "artisans"] as const) {
    await api.list(resource, { page: 2, pageSize: 10, q: "Ấm", status: "hidden" });
    await api.get(resource, "one"); await api.save(resource, { name: "Ấm" });
    await api.save(resource, { name: "Ấm", rowVersion: row.rowVersion }, "one");
  }
  for (const resource of ["materials", "artisans"] as const) await api.publish(resource, "one", row.rowVersion);
  await api.products("one"); await api.orderProducts("one", row.rowVersion, [{ productId: "existing-product", sortOrder: 0 }]);
  await api.references({ kind: "types" }, { page: 1, pageSize: 20 });
  await api.references({ kind: "attributes", typeId: "one" }, { page: 1, pageSize: 20 });
  await api.references({ kind: "options", typeId: "one", attributeId: "a" }, { page: 1, pageSize: 20 });
  await api.reference({ kind: "types" }, "one");
  await api.reference({ kind: "attributes", typeId: "one" }, "a");
  await api.reference({ kind: "options", typeId: "one", attributeId: "a" }, "o");
  const reference: TypeRow = { id: row.id, rowVersion: row.rowVersion, code: "am", name: "Ấm", skuPrefix: null, description: null, googleProductCategory: null, isActive: true, sortOrder: 0, createdAt: "2026-11-20T00:00:00Z", updatedAt: "2026-11-20T00:00:00Z" };
  for (const existing of [undefined, reference]) {
    await api.saveType({ code: "am", name: "Ấm" }, existing);
    await api.saveAttribute("one", { code: "volume", name: "Dung tích", dataType: "number" }, existing);
    await api.saveOption("one", "a", { code: "red", label: "Đỏ" }, existing);
  }
  await api.media(1, "Ảnh");
  assert.equal(calls.filter(r => ["POST", "PATCH", "PUT"].includes(r.method)).length, 17);
  for (let i = 0; i < calls.length; i++) {
    const request = calls[i]; assert.equal(request.credentials, "include"); assert.equal(request.cache, "no-store");
    if (["POST", "PATCH", "PUT"].includes(request.method)) {
      assert.ok(calls[i - 1].url.endsWith("/auth/csrf"));
      assert.equal(request.headers.get("X-CSRF-TOKEN"), `fresh-${i}`);
      const body = await request.json();
      if (request.method !== "POST" || request.url.endsWith("/publish")) assert.equal(body.rowVersion, row.rowVersion);
    }
  }
  assert.ok(calls.some(r => r.url.includes("q=%E1%BA%A4m")));
  assert.equal(calls.some(r => new URL(r.url).pathname === "/api/v1/admin/products"), false);
});

for (const [status, code] of [[400, "validation_failed"], [400, "csrf_invalid"], [401, "unauthenticated"], [403, "forbidden"], [404, "not_found"], [409, "concurrency_conflict"], [422, "publish_requirements_not_met"]] as const) {
  test(`P1-11 ${status}/${code}: no retry, no mutation replay, errors and session invalidation`, async () => {
    let sent = 0, expired = 0;
    const admin = new AdminApi((async (r: Request) => {
      if (r.url.endsWith("/csrf")) return Response.json({ token: "test" });
      sent++; return Response.json({ code, errors: { Name: ["Tên chưa hợp lệ."] } }, { status });
    }) as typeof fetch);
    admin.onUnauthorized = () => { expired++; };
    await assert.rejects(new CatalogApi(admin).save("materials", { name: "A", rowVersion: "opaque" }, "one"), (e: unknown) => e instanceof ApiError && e.status === status && e.code === code && e.fields.Name[0] === "Tên chưa hợp lệ.");
    assert.equal(sent, 1); assert.equal(expired, status === 401 ? 1 : 0);
  });
}

test("P1-11 CSRF failure prevents writing, no cached-token fallback", async () => {
  const calls: string[] = [];
  const api = new CatalogApi(new AdminApi((async (r: Request) => { calls.push(r.method); return Response.json({ code: "csrf_invalid" }, { status: 400 }); }) as typeof fetch));
  await assert.rejects(api.publish("artisans", "id", "opaque"), ApiError);
  assert.deepEqual(calls, ["GET"]);
});

test("P1-11 attribute/options validation enforces typed scope, units, axis and codes", () => {
  const fields = referenceFields("attributes"); const schema = formSchema(fields, "attributes");
  const values = { ...defaults(fields), code: "volume", name: "Dung tích", dataType: "number", scope: "variant", unit: "ml", isVariantAxis: true };
  assert.equal(schema.safeParse(values).success, true);
  for (const bad of [{ scope: "product" }, { dataType: "boolean", unit: "" }, { dataType: "text" }, { code: "Upper" }, { name: " " }, { sortOrder: 1.5 }]) assert.equal(schema.safeParse({ ...values, ...bad }).success, false);
  assert.equal(schema.safeParse({ ...values, dataType: "text", unit: "" }).success, true);
  const options = referenceFields("options");
  assert.equal(formSchema(options, "options").safeParse({ ...defaults(options), label: "Đỏ", code: "r" }).success, true);
  assert.equal(formSchema(options, "options").safeParse({ ...defaults(options), label: "", code: "a-b" }).success, false);
});

test("P1-11 material/schema/status: SKU XX reserved, no PATCH publication, no invented SEO minimum", () => {
  const fields = taxonomyFields("materials"); const schema = formSchema(fields, "materials");
  const values = { ...defaults(fields), name: "Tử nê" };
  assert.equal(schema.safeParse(values).success, true);
  for (const bad of [{ kind: "invented" }, { skuCode: "XX" }, { skuCode: "lower" }, { status: "published" }, { slug: "Upper Slug" }, { slug: "x".repeat(81) }]) assert.equal(schema.safeParse({ ...values, ...bad }).success, false);
  assert.equal(schema.safeParse({ ...values, skuCode: "TN", robotsNoindex: true }).success, true);
  const categoryFields = taxonomyFields("categories"), category = { ...defaults(categoryFields), name: "Trà", robotsNoindex: true };
  assert.equal(formSchema(categoryFields, "categories").safeParse(category).success, false);
  assert.equal(formSchema(categoryFields, "categories").safeParse({ ...category, noindexReason: "Chưa hoàn thiện" }).success, true);
});

test("P1-11 category tree: max depth, cycles, self and full subtree reparent", () => {
  const rows = [row, { ...row, id: "two", name: "Cấp 2", parentId: "one" }, { ...row, id: "three", name: "Cấp 3", parentId: "two" }, { ...row, id: "other", name: "Khác" }];
  assert.equal(validParent(rows, "new", "two"), true);
  assert.equal(validParent(rows, "new", "three"), false);
  assert.equal(validParent(rows, "one", "one"), false);
  assert.equal(validParent(rows, "one", "two"), false);
  assert.equal(validParent(rows, "one", "other"), false);
  assert.equal(validParent(rows, "two", null), true);
  assert.equal(categoryPath(rows[2], rows), "Ấm / Cấp 2 / Cấp 3");
});

test("P1-11 category tree loads every backend page, fails closed on incomplete pages", async () => {
  const pages: number[] = [];
  const admin = new AdminApi((async (r: Request) => {
    const page = Number(new URL(r.url).searchParams.get("page")); pages.push(page);
    return Response.json({ items: page === 1 ? [row] : [{ ...row, id: "two" }], totalItems: 2 });
  }) as typeof fetch);
  assert.equal((await new CatalogApi(admin).categoryTree()).length, 2); assert.deepEqual(pages, [1, 2]);
  await assert.rejects(new CatalogApi(new AdminApi((async () => Response.json({ items: [], totalItems: 1 })) as typeof fetch)).categoryTree(), ApiError);
});

test("P1-11 plain editor escapes markup; detached parsing excludes scripts/styles", () => {
  assert.equal(paragraphHtml('<img src=x onerror="alert(1)">\n&'), '<p>&lt;img src=x onerror=&quot;alert(1)&quot;&gt;</p><p>&amp;</p>');
  const dom = new JSDOM(); const old = globalThis.DOMParser; globalThis.DOMParser = dom.window.DOMParser;
  try { assert.equal(plainText("<p>Một &amp; hai</p><script>evil</script><style>css</style><p>Ba</p>"), "Một & hai\nBa"); }
  finally { if (old) globalThis.DOMParser = old; else Reflect.deleteProperty(globalThis, "DOMParser"); dom.window.close(); }
});
