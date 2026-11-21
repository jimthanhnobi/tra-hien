import assert from "node:assert/strict";
import { test } from "node:test";
import type { AttributeRow } from "../src/lib/catalog-api";
import { AdminApi, ApiError, isConcurrencyConflict } from "../src/lib/api";
import { attributeValue, buildAttributes, initialAttributeValue, nullable, parseDimensions, parseVnd, publishIssues, readDimensions, thinDescription, variantPayload } from "../src/lib/product-forms";
import { ProductsApi } from "../src/lib/products-api";

const NativeRequest = globalThis.Request;
globalThis.Request = class extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) { super(typeof input === "string" ? new URL(input, "https://admin.example.invalid") : input, init); }
};

const productId = "00000000-0000-7000-8000-000000000001";
const variantId = "00000000-0000-7000-8000-000000000002";
const mediaId = "00000000-0000-7000-8000-000000000003";
const root = "/api/v1/admin/products";
const version = "opaque+/==";
const def = (type: string, isActive = true): AttributeRow => ({ id: type, code: type, productTypeId: productId, name: type, dataType: type,
  scope: "product", isActive, sortOrder: 0, isFilterable: false, isVariantAxis: false, isVisible: true, isRequiredForPublish: false, rowVersion: version, unit: null });

test("P1-12 shapes changed attributes without losing typed zero, false or historical values", () => {
  const original = [{ attributeId: "inactive", optionId: "legacy" }, { attributeId: "text", valueText: "Giữ nguyên" }];
  const definitions = [def("inactive", false), def("text"), def("number"), def("boolean"), def("option"), def("dimensions")];
  assert.deepEqual(buildAttributes(original, definitions, { inactive: "unexpected", number: "0", boolean: "false", option: "choice", dimensions: { d: "0", h: null, w: "" } }), [
    ...original, { attributeId: "number", valueNumber: 0 }, { attributeId: "boolean", valueBool: false },
    { attributeId: "option", optionId: "choice" }, { attributeId: "dimensions", valueJson: { d: 0, h: null } }
  ]);
  assert.deepEqual(buildAttributes(original, definitions, { text: "" }), [original[0]]);
  assert.equal(initialAttributeValue(def("boolean"), { attributeId: "boolean", valueBool: false }), "false");
  assert.deepEqual(readDimensions('{"w":0,"h":null}'), { w: "0", h: null });
  assert.deepEqual(parseDimensions({ w: "1,25", h: "" }), { w: 1.25 });
  assert.deepEqual(attributeValue(def("dimensions"), {}), { attributeId: "dimensions", valueJson: {} });
  assert.throws(() => attributeValue(def("number"), "0.001"));
  assert.throws(() => parseDimensions({ w: "Infinity" }));
  assert.equal(nullable(" \n "), null);
  assert.equal(nullable(" Tên có dấu "), " Tên có dấu ");
});

test("P1-12 BR-CAT-15 and BR-PRC-03 keep VND integer and remove unauthorized price/SKU", () => {
  assert.equal(parseVnd(""), null);
  assert.equal(parseVnd("10.000.000.000"), 10_000_000_000);
  assert.equal(parseVnd("1"), 1);
  for (const invalid of ["0", "-1", "1,5", "0x10", "3e4", "12.34", "10000000001", "9007199254740993"]) assert.throws(() => parseVnd(invalid), invalid);
  const body = { rowVersion: "wrong", priceVnd: null, sku: "MANUAL", name: "Ấm" };
  assert.deepEqual(variantPayload(version, body, false, false), { rowVersion: version, name: "Ấm" });
  assert.deepEqual(variantPayload(version, body, true, false), { rowVersion: version, name: "Ấm", priceVnd: null });
  assert.deepEqual(variantPayload(version, body, false, true), { rowVersion: version, name: "Ấm", sku: "MANUAL" });
});

test("P1-12 publish maps every code, including unknown fields, and keeps thin separate from manual noindex", () => {
  const p = { variants: [{ id: variantId, sku: "AM-XX-0001" }], robotsNoindex: true, thinDescription: false };
  const issues = publishIssues({ "media.cover.altText": ["cover_alt_missing"], "attributes[number]": ["required_attribute_missing"],
    [`variants[${variantId}].priceVnd`]: ["variant_price_missing", "another_code"], "future.path": ["future_rule"] }, p, [def("number")]);
  assert.deepEqual(issues.map(issue => issue.tab), ["images", "attributes", "variants", "variants", undefined]);
  assert.equal(issues[2].label, "AM-XX-0001");
  assert.match(issues[4].message, /future_rule/);
  assert.equal(thinDescription(p), false);
  assert.equal(thinDescription({ robotsNoindex: false, thinDescription: true }), true);
  assert.equal(isConcurrencyConflict(new ApiError(409, { code: "redirect_conflict" })), false);
  assert.equal(isConcurrencyConflict(new ApiError(409, { code: "concurrency_conflict" })), true);
});

test("P1-12 generated mutations bind actual paths, forward opaque versions and acquire a fresh CSRF token", async () => {
  let csrf = 0;
  const received: [string, string, unknown][] = [];
  const api = new ProductsApi(new AdminApi((async (request: Request) => {
    const path = new URL(request.url).pathname;
    if (path.endsWith("/csrf")) return Response.json({ token: `fresh-${++csrf}` });
    assert.equal(request.headers.get("X-CSRF-TOKEN"), `fresh-${csrf}`);
    assert.equal(request.credentials, "include");
    received.push([request.method, path, await request.json()]);
    return Response.json({ id: productId, rowVersion: `server-${csrf}` });
  }) as typeof fetch));
  assert.equal((await api.patch(productId, { rowVersion: version, descriptionHtml: null })).rowVersion, "server-1");
  await api.saveVariant(productId, variantId, { rowVersion: "variant-token", priceVnd: null });
  await api.removeImage(productId, mediaId, "media-aggregate-token");
  await api.transition(productId, "archive", { rowVersion: "archive-version" });
  assert.deepEqual(received, [
    ["PATCH", `${root}/${productId}`, { rowVersion: version, descriptionHtml: null }],
    ["PATCH", `${root}/${productId}/variants/${variantId}`, { rowVersion: "variant-token", priceVnd: null }],
    ["DELETE", `${root}/${productId}/media/${mediaId}`, { rowVersion: "media-aggregate-token" }],
    ["POST", `${root}/${productId}/archive`, { rowVersion: "archive-version" }]
  ]);
  assert.equal(csrf, 4);
  await assert.rejects(api.patch("../bad", { rowVersion: version }), ApiError);
  assert.equal(csrf, 4);
});

test("P1-12 signed photo upload excludes Admin credentials and skips completion on storage failure", async () => {
  const calls: string[] = [];
  const api = new ProductsApi(new AdminApi((async (request: Request) => {
    const path = new URL(request.url).pathname;
    calls.push(path);
    if (path.endsWith("/csrf")) return Response.json({ token: "admin-only" });
    if (path.endsWith("/complete")) return Response.json({ id: mediaId, purpose: "catalog", mimeType: "image/png" });
    return Response.json({ putUrl: "https://upload.example.invalid/pending?test=signed", headers: { "content-type": "image/png", "x-csrf-token": "exclude" },
      uploadId: mediaId, expiresAt: new Date(Date.now() + 60_000).toISOString() });
  }) as typeof fetch));
  const file = new File(["synthetic"], "fixture.png", { type: "image/png" });
  await assert.rejects(api.upload(file, undefined, (async () => new Response(null, { status: 403 })) as typeof fetch), /Không tải được ảnh/);
  assert.equal(calls.some(path => path.endsWith("/complete")), false);
  const result = await api.upload(file, undefined, (async (_url: URL, init: RequestInit) => {
    assert.equal(init.method, "PUT"); assert.equal(init.credentials, "omit"); assert.equal(init.redirect, "error");
    assert.equal(new Headers(init.headers).get("x-csrf-token"), null);
    assert.equal(new Headers(init.headers).get("content-type"), "image/png"); assert.equal(init.body, file);
    return new Response(null, { status: 200 });
  }) as typeof fetch);
  assert.equal(result.id, mediaId);
  assert(calls.at(-1)?.endsWith("/complete"));
});
