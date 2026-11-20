import { test, expect, type Page } from "@playwright/test";
import { createHmac, randomUUID } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import type { components } from "@teapremium/api-client";

type Product = components["schemas"]["ProductData"];
type Relation = components["schemas"]["CollectionProductList"];
type Fixture = { email: string; password: string; newPassword: string; totpSecret?: string };
// Enrollment material must never appear in failure DOM snapshots or traces.
test.afterEach(async ({ context }) => { await context.close(); });
function totp(secret: string) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  const bits = [...secret.toUpperCase().replaceAll(" ", "")].map(x => alphabet.indexOf(x).toString(2).padStart(5, "0")).join("");
  const key = Buffer.from(bits.match(/.{8}/g)!.map(x => parseInt(x, 2)));
  const counter = Buffer.alloc(8); counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30_000)));
  const hash = createHmac("sha1", key).update(counter).digest(); const offset = hash[19] & 15;
  return ((hash.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).toString().padStart(6, "0");
}
async function api(page: Page, path: string, method = "GET", body?: unknown, csrf = true) {
  return page.evaluate(async ({ path, method, body, csrf }) => {
    const headers: Record<string, string> = { "Content-Type": "application/json" };
    if (method !== "GET" && csrf) {
      const response = await fetch("/api/v1/admin/auth/csrf", { credentials: "include", cache: "no-store" });
      headers["X-CSRF-TOKEN"] = (await response.json()).token;
    }
    const response = await fetch(`/api/v1/admin/${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), credentials: "include", cache: "no-store" });
    const text = await response.text();
    return { status: response.status, data: text ? JSON.parse(text) : null };
  }, { path, method, body, csrf });
}
async function login(page: Page, fixture: Fixture, password: string, secret?: string) {
  await page.goto("/login"); await page.getByLabel("Email công việc").fill(fixture.email);
  await page.getByLabel("Mật khẩu", { exact: true }).fill(password);
  if (secret) await page.getByLabel("Mã xác thực TOTP").fill(totp(secret));
  await page.getByRole("button", { name: "Đăng nhập", exact: true }).click();
}

test("real P1-11: PG17 + Production IAM + trusted TLS + collection picker persistence/concurrency/permissions/CSRF", async ({ page, context, browser }) => {
  const file = process.env.ADMIN_CATALOG_FIXTURE;
  if (!file) throw new Error("ADMIN_CATALOG_FIXTURE must identify fresh synthetic credentials for the disposable stack on port 9443");
  const fixture: Fixture = JSON.parse(await readFile(file, "utf8"));
  if (!fixture.email.endsWith("@example.invalid")) throw new Error("Only synthetic fixture accounts are allowed");
  if (!fixture.totpSecret) {
    await login(page, fixture, fixture.password); await expect(page).toHaveURL(/change-password$/);
    await page.getByLabel("Mật khẩu hiện tại", { exact: true }).fill(fixture.password);
    await page.getByLabel("Mật khẩu mới", { exact: true }).fill(fixture.newPassword);
    await page.getByLabel("Xác nhận mật khẩu mới", { exact: true }).fill(fixture.newPassword);
    await page.getByRole("button", { name: "Lưu mật khẩu mới" }).click(); await expect(page).toHaveURL(/login$/);
    await login(page, fixture, fixture.newPassword); await expect(page).toHaveURL(/totp-setup$/);
    await page.getByRole("button", { name: "Bắt đầu thiết lập" }).click(); await page.getByText("Không quét được mã QR?").click();
    fixture.totpSecret = (await page.locator(".provisioning code").textContent())!;
    if (30_000 - Date.now() % 30_000 < 5000) await new Promise(resolve => setTimeout(resolve, 6000));
    await page.getByLabel("Mã xác thực 6 số").fill(totp(fixture.totpSecret));
    await page.getByRole("button", { name: "Xác minh và hoàn tất" }).click(); await expect(page).toHaveURL(/login$/);
    await writeFile(file, JSON.stringify(fixture));
    await new Promise(resolve => setTimeout(resolve, 30_000 - Date.now() % 30_000 + 1000));
  }
  await login(page, fixture, fixture.newPassword, fixture.totpSecret); await expect(page).not.toHaveURL(/login$/);
  const cookie = (await context.cookies()).find(x => x.name === "__Host-tp_admin");
  expect(!!cookie && cookie.secure && cookie.httpOnly && cookie.sameSite === "Strict").toBe(true);
  expect((await api(page, "auth/me")).status).toBe(200);
  const suffix = randomUUID().replaceAll("-", "").slice(0, 10);
  const type = await api(page, "product-types", "POST", { code: `test${suffix}`, name: `Loại giả ${suffix}`, skuPrefix: "TE" }); expect(type.status).toBe(201);
  const products: Product[] = [];
  for (const name of [`Ấm thử ${suffix}`, `Chén thử ${suffix}`]) {
    const result = await api(page, "products", "POST", { productTypeId: type.data.id, name }); expect(result.status).toBe(201); products.push(result.data);
  }
  for (const q of [products[0].name!, products[0].skuBase!, products[0].variants![0].sku!]) {
    const search = await api(page, `products?page=1&pageSize=20&q=${encodeURIComponent(q)}`);
    expect(search.status).toBe(200); expect(search.data.items.some((x: Product) => x.id === products[0].id)).toBe(true);
  }
  const name = `Bộ thử ${suffix}`;
  const created = await api(page, "collections", "POST", { name, status: "hidden" }); expect(created.status).toBe(201);
  const id: string = created.data.id, path = `collections/${id}/products`;
  await page.goto("/catalog/collections"); await page.getByRole("button", { name: `Chỉnh sửa ${name}`, exact: true }).click();
  const region = page.getByRole("region", { name: "Sản phẩm trong bộ sưu tập" });
  const add = async (product: Product, by: string) => {
    await region.getByRole("button", { name: "Thêm sản phẩm", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Thêm sản phẩm" });
    await dialog.getByRole("searchbox").fill(by);
    await dialog.getByRole("checkbox", { name: `Chọn ${product.name}, SKU ${product.skuBase}`, exact: true }).check();
    await dialog.getByRole("button", { name: "Thêm 1 sản phẩm đã chọn" }).click();
  };
  const save = async () => {
    const response = page.waitForResponse(r => r.request().method() === "PUT" && r.url().endsWith(path));
    await region.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click(); const result = await response;
    expect(result.status()).toBe(200); return await result.json() as Relation;
  };
  const nameField = page.getByLabel("Tên", { exact: true });
  await add(products[0], products[0].name!);
  await expect(nameField).toBeDisabled();
  page.once("dialog", dialog => dialog.accept());
  await region.getByRole("button", { name: "Hủy thay đổi sản phẩm" }).click();
  await expect(nameField).toBeEnabled();
  expect((await api(page, path)).data.items).toEqual([]);

  const updatedName = `${name} đã sửa`;
  await nameField.fill(updatedName);
  await expect(region.getByRole("button", { name: "Thêm sản phẩm", exact: true })).toBeDisabled();
  const patchResponse = page.waitForResponse(r => r.request().method() === "PATCH" && r.url().endsWith(`collections/${id}`));
  await page.getByRole("button", { name: "Lưu thay đổi", exact: true }).click();
  const patch = await patchResponse;
  expect(patch.status()).toBe(200);
  const versionAfterPatch = (await patch.json()).rowVersion;
  expect(versionAfterPatch).not.toBe(created.data.rowVersion);
  await add(products[0], products[0].name!);
  const putRequest = page.waitForRequest(r => r.method() === "PUT" && r.url().endsWith(path));
  const first = await save();
  expect((await putRequest).postDataJSON().rowVersion).toBe(versionAfterPatch);
  expect(first.rowVersion).not.toBe(versionAfterPatch);
  await expect(nameField).toBeEnabled();
  await page.reload(); await page.getByRole("button", { name: `Chỉnh sửa ${updatedName}`, exact: true }).click();
  await expect(region.getByText(products[0].name!, { exact: true })).toBeVisible();
  expect((await api(page, path)).data.items.map((x: Relation["items"][number]) => x.productId)).toEqual([products[0].id]);
  await add(products[1], products[1].skuBase!); const second = await save(); expect(second.rowVersion !== first.rowVersion).toBe(true);
  await region.getByRole("button", { name: `Đưa sản phẩm ${products[1].id} lên` }).click(); const third = await save();
  expect(third.items.map(x => x.productId)).toEqual([products[1].id, products[0].id]);
  await region.getByRole("button", { name: `Bỏ sản phẩm ${products[0].id} khỏi bộ sưu tập` }).click(); const fourth = await save();
  expect(fourth.items).toEqual([{ productId: products[1].id, sortOrder: 0 }]); expect((await api(page, `products/${products[0].id}`)).status).toBe(200);
  // Real competing API write makes the UI snapshot stale; no mocked responses.
  const remote = await api(page, `collections/${id}`, "PATCH", { name: `${name} khác`, rowVersion: fourth.rowVersion }); expect(remote.status).toBe(200);
  await add(products[0], products[0].skuBase!);
  const staleResponse = page.waitForResponse(r => r.request().method() === "PUT" && r.url().endsWith(path));
  await region.getByRole("button", { name: "Lưu danh sách sản phẩm" }).click(); expect((await staleResponse).status()).toBe(409);
  await expect(region.getByText(/Thứ tự có xung đột/)).toBeVisible(); await expect(region.getByText(products[0].name!, { exact: true })).toBeVisible();
  expect((await api(page, path)).data.items).toEqual(fourth.items);
  const csrfFailure = await api(page, path, "PUT", { rowVersion: remote.data.rowVersion, items: [] }, false);
  expect(csrfFailure.status).toBe(400); expect(csrfFailure.data.code).toBe("csrf_invalid");
  const salesPassword = `T!${randomUUID()}aA1`, salesNext = `T!${randomUUID()}bB2`;
  const salesEmail = `sales-${suffix}@example.invalid`;
  expect((await api(page, "staff/", "POST", { email: salesEmail, displayName: "Nhân viên giả", initialPassword: salesPassword, roles: ["sales"] })).status).toBe(200);
  const salesContext = await browser.newContext();
  try {
    const salesPage = await salesContext.newPage();
    await login(salesPage, { email: salesEmail, password: salesPassword, newPassword: salesNext }, salesPassword);
    await expect(salesPage).toHaveURL(/change-password$/);
    expect((await api(salesPage, "auth/change-password", "POST", { currentPassword: salesPassword, newPassword: salesNext })).status).toBe(204);
    await login(salesPage, { email: salesEmail, password: salesPassword, newPassword: salesNext }, salesNext);
    await expect(salesPage).not.toHaveURL(/login$/);
    expect((await api(salesPage, path)).status).toBe(200);
    expect((await api(salesPage, path, "PUT", { rowVersion: remote.data.rowVersion, items: [] })).status).toBe(403);
    await salesPage.goto("/catalog/collections"); await salesPage.getByRole("button", { name: `Xem ${name} khác`, exact: true }).click();
    await expect(salesPage.getByRole("button", { name: "Thêm sản phẩm", exact: true })).toHaveCount(0);
  } finally { await salesContext.close(); }
  expect((await api(page, path)).data.items).toEqual(fourth.items);
});
