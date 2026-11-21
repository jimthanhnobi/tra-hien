import { expect, test, type Page } from "@playwright/test";
import { createHmac, randomUUID } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import type { Product } from "../../src/lib/products-api";

type Fixture = { email: string; password: string; newPassword: string; totpSecret?: string };
const route = "/catalog/products";
const button = (page: Page, name: string) => page.getByRole("button", { name, exact: true });
const tab = (page: Page, name: string) => page.getByRole("tab", { name, exact: true });

// Synthetic keys and credentials never enter test attachments or traces.
test.afterEach(async ({ context }) => { await context.close(); });

function totp(secret: string) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  const bits = [...secret.toUpperCase().replaceAll(" ", "")].map(value => alphabet.indexOf(value).toString(2).padStart(5, "0")).join("");
  const key = Buffer.from(bits.match(/.{8}/g)!.map(value => parseInt(value, 2)));
  const counter = Buffer.alloc(8); counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const hash = createHmac("sha1", key).update(counter).digest();
  return ((hash.readUInt32BE(hash.at(-1)! & 15) & 0x7fffffff) % 1000000).toString().padStart(6, "0");
}

async function api(page: Page, path: string, method = "GET", body?: unknown) {
  return page.evaluate(async ({ path, method, body }) => {
    const headers: Record<string, string> = { "Content-Type": "application/json" };
    if (method !== "GET") {
      const csrf = await fetch("/api/v1/admin/auth/csrf", { credentials: "include", cache: "no-store" });
      headers["X-CSRF-TOKEN"] = (await csrf.json()).token;
    }
    const response = await fetch(`/api/v1/admin/${path}`, { method, headers, credentials: "include", cache: "no-store",
      body: body === undefined ? undefined : JSON.stringify(body) });
    const text = await response.text();
    return { status: response.status, data: text ? JSON.parse(text) : null };
  }, { path, method, body });
}

async function login(page: Page, email: string, password: string, secret?: string) {
  await page.goto("/login");
  await page.getByLabel("Email công việc").fill(email);
  await page.getByLabel("Mật khẩu", { exact: true }).fill(password);
  if (secret) await page.getByLabel("Mã xác thực TOTP").fill(totp(secret));
  await button(page, "Đăng nhập").click();
}

async function ownerSession(page: Page, fixture: Fixture, fixturePath: string) {
  if (!fixture.totpSecret) {
    await login(page, fixture.email, fixture.password);
    await expect(page).toHaveURL(/change-password$/);
    await page.getByLabel("Mật khẩu hiện tại", { exact: true }).fill(fixture.password);
    await page.getByLabel("Mật khẩu mới", { exact: true }).fill(fixture.newPassword);
    await page.getByLabel("Xác nhận mật khẩu mới", { exact: true }).fill(fixture.newPassword);
    await button(page, "Lưu mật khẩu mới").click();
    await expect(page).toHaveURL(/login$/);
    await login(page, fixture.email, fixture.newPassword);
    await expect(page).toHaveURL(/totp-setup$/);
    await button(page, "Bắt đầu thiết lập").click();
    await page.getByText("Không quét được mã QR?").click();
    fixture.totpSecret = (await page.locator(".provisioning code").textContent())!;
    if (30000 - Date.now() % 30000 < 6000) await new Promise(resolve => setTimeout(resolve, 6500));
    await page.getByLabel("Mã xác thực 6 số").fill(totp(fixture.totpSecret));
    await button(page, "Xác minh và hoàn tất").click();
    await expect(page).toHaveURL(/login$/);
    await writeFile(fixturePath, JSON.stringify(fixture));
    await new Promise(resolve => setTimeout(resolve, 30000 - Date.now() % 30000 + 1000));
  }
  await login(page, fixture.email, fixture.newPassword, fixture.totpSecret);
  await expect(page).toHaveURL(/:9444\/$/);
  await expect(page.getByRole("heading", { name: /^Chào .+,$/ })).toBeVisible();
}

test("real P1-12 PG17, Production IAM and trusted HTTPS, product/attributes/variants/pricing/stale/publish", async ({ page, context, browser }) => {
  const fixturePath = process.env.ADMIN_PRODUCTS_FIXTURE;
  if (!fixturePath) throw new Error("ADMIN_PRODUCTS_FIXTURE must point to fresh synthetic credentials for the isolated HTTPS stack on 9444");
  const fixture: Fixture = JSON.parse(await readFile(fixturePath, "utf8"));
  if (!fixture.email.endsWith("@example.invalid")) throw new Error("The live suite requires synthetic accounts");
  await ownerSession(page, fixture, fixturePath);
  const cookies = (await context.cookies(page.url())).map(({ name, secure, httpOnly, sameSite }) => ({ name, secure, httpOnly, sameSite }));
  expect(cookies).toEqual(expect.arrayContaining([expect.objectContaining({ name: "__Host-tp_admin", secure: true, httpOnly: true, sameSite: "Strict" })]));

  const suffix = randomUUID().replaceAll("-", "").slice(0, 9);
  const type = await api(page, "product-types", "POST", { name: `Ấm thật thử ${suffix}`, code: `test${suffix}`, skuPrefix: "AT" });
  expect(type.status).toBe(201);
  const category = await api(page, "categories", "POST", { name: `Trà cụ thử ${suffix}`, status: "visible" });
  expect(category.status).toBe(201);
  const numeric = await api(page, `product-types/${type.data.id}/attributes`, "POST", { name: "Dung tích thử", code: "dung_tich", dataType: "number", unit: "ml", scope: "product" });
  expect(numeric.status).toBe(201);
  const required = await api(page, `product-types/${type.data.id}/attributes`, "POST", { name: "Xuất xứ thử", code: "xuat_xu", dataType: "text", scope: "product", isRequiredForPublish: true });
  expect(required.status).toBe(201);

  await page.goto(`${route}/new`);
  await page.getByLabel("Loại sản phẩm", { exact: true }).selectOption(type.data.id);
  await page.getByLabel("Tên sản phẩm").fill(`Ấm kiểm ${suffix}`);
  await button(page, "Tạo bản nháp").click();
  await expect(page).toHaveURL(/\/catalog\/products\/[0-9a-f-]{36}$/);
  const productId = new URL(page.url()).pathname.split("/").at(-1)!;
  const productPath = `products/${productId}`;
  const draft: Product = (await api(page, productPath)).data;
  expect(draft.variants).toHaveLength(1);
  const original = draft.variants![0];
  expect(original.sku).toBe(draft.skuBase);
  expect(original.priceVnd).toBeNull();
  const salePrices = await api(page, `variants/${original.id}/sale-prices`);
  expect(salePrices.status).toBe(200);
  expect(salePrices.data).toMatchObject({ rowVersion: original.rowVersion });

  await button(page, "Chỉnh sửa thông tin").click();
  await page.getByLabel("Tên phụ", { exact: true }).fill("Tên phụ thật");
  await page.getByLabel("Danh mục chính", { exact: true }).selectOption(category.data.id);
  await page.getByLabel("Mô tả ngắn", { exact: true }).fill("Ấm trà dáng tròn và nắp chắc chắn cho buổi thưởng trà buổi sáng.");
  const description = page.getByRole("textbox", { name: "Mô tả chi tiết", exact: true });
  await description.fill("Mô tả chi tiết thử nghiệm");
  await description.press("Control+a");
  await page.getByRole("group", { name: "Định dạng mô tả chi tiết" }).getByRole("button", { name: "Đậm", exact: true }).click();
  await button(page, "Lưu thay đổi").click();
  await expect(button(page, "Chỉnh sửa thông tin")).toBeVisible();
  await tab(page, "Thuộc tính").click();
  await button(page, "Chỉnh sửa thuộc tính").click();
  await page.getByLabel("Dung tích thử (ml)", { exact: true }).fill("0");
  await button(page, "Lưu thay đổi").click();
  await expect(button(page, "Chỉnh sửa thuộc tính")).toBeVisible();
  await tab(page, "Biến thể & giá").click();
  await button(page, `Chỉnh sửa biến thể ${original.sku}`).click();
  await page.getByLabel("Giá niêm yết (VND)", { exact: true }).fill("395.000");
  await button(page, "Lưu biến thể").click();
  await expect(button(page, "Thêm biến thể")).toBeEnabled();
  await button(page, "Thêm biến thể").click();
  await page.getByLabel("Tên biến thể", { exact: true }).fill("Biến thể giá NULL");
  await page.getByLabel("Trạng thái biến thể").selectOption("inactive");
  await button(page, "Tạo biến thể").click();
  await expect(page.getByRole("heading", { name: "Biến thể giá NULL" })).toBeVisible();
  await page.reload();
  await expect(page.getByLabel("Tên phụ", { exact: true })).toHaveValue("Tên phụ thật");
  const persisted: Product = (await api(page, productPath)).data;
  expect(persisted).toMatchObject({ primaryCategoryId: category.data.id, categoryIds: [category.data.id], subtitle: "Tên phụ thật" });
  expect(persisted.descriptionHtml).toContain("<strong>Mô tả chi tiết thử nghiệm</strong>");
  expect(persisted.attributes).toEqual(expect.arrayContaining([expect.objectContaining({ valueNumber: 0 })]));
  expect(persisted.variants).toEqual(expect.arrayContaining([
    expect.objectContaining({ sku: original.sku, priceVnd: 395000 }), expect.objectContaining({ name: "Biến thể giá NULL", priceVnd: null, status: "inactive" })
  ]));

  const rejectedImage = await api(page, `${productPath}/media`, "POST", {
    rowVersion: persisted.rowVersion, assetId: randomUUID(), altText: "Ảnh chưa có asset hoàn tất"
  });
  expect(rejectedImage.status).toBe(400);
  expect((await api(page, productPath)).data).toMatchObject({ rowVersion: persisted.rowVersion, media: [] });

  const publishResponse = page.waitForResponse(response => response.request().method() === "POST" && response.url().endsWith(`${productPath}/publish`));
  await button(page, "Công khai").click();
  expect((await publishResponse).status()).toBe(422);
  const publishErrors = page.getByRole("alert").filter({ hasText: /Sản phẩm chưa đủ điều kiện/ });
  await expect(publishErrors).toContainText("Thiếu ảnh bìa có ALT hợp lệ");
  await expect(publishErrors).toContainText("Xuất xứ thử");
  await expect(publishErrors).toContainText("Thiếu giá trị thuộc tính bắt buộc");

  await button(page, "Chỉnh sửa thông tin").click();
  await page.getByLabel("Tên phụ", { exact: true }).fill("Thay đổi cục bộ chưa lưu");
  expect((await api(page, productPath, "PATCH", { rowVersion: persisted.rowVersion, subtitle: "Thay đổi từ phiên khác" })).status).toBe(200);
  const stale = page.waitForResponse(response => response.request().method() === "PATCH" && response.url().endsWith(productPath));
  await button(page, "Lưu thay đổi").click();
  expect((await stale).status()).toBe(409);
  await expect(page.getByLabel("Tên phụ", { exact: true })).toHaveValue("Thay đổi cục bộ chưa lưu");
  expect((await api(page, productPath)).data.subtitle).toBe("Thay đổi từ phiên khác");

  const contentEmail = `content-${suffix}@example.invalid`, initialPassword = `T!${randomUUID()}aA1`, nextPassword = `T!${randomUUID()}bB2`;
  expect((await api(page, "staff/", "POST", { email: contentEmail, displayName: "Biên tập thử", initialPassword, roles: ["content"] })).status).toBe(200);
  const contentContext = await browser.newContext();
  try {
    const content = await contentContext.newPage();
    await login(content, contentEmail, initialPassword);
    await expect(content).toHaveURL(/change-password$/);
    expect((await api(content, "auth/change-password", "POST", { currentPassword: initialPassword, newPassword: nextPassword })).status).toBe(204);
    await login(content, contentEmail, nextPassword);
    await expect(content).toHaveURL(/:9444\/$/);
    await expect(content.getByRole("heading", { name: "Chào Biên tập thử," })).toBeVisible();
    const read = await api(content, productPath);
    expect(read.status).toBe(200);
    expect(JSON.stringify(read.data)).not.toContain("avgCostVnd");
    const child = read.data.variants.find((variant: { id: string }) => variant.id === original.id);
    expect((await api(content, `${productPath}/variants/${original.id}`, "PATCH", { rowVersion: child.rowVersion, priceVnd: 1 })).status).toBe(403);
    await content.goto(`${route}/${productId}`);
    await tab(content, "Biến thể & giá").click();
    await button(content, `Chỉnh sửa biến thể ${original.sku}`).click();
    await expect(content.getByLabel("Giá niêm yết (VND)", { exact: true })).toHaveCount(0);
    await content.getByLabel("Tên biến thể", { exact: true }).fill("Tên biên tập thử");
    const response = content.waitForResponse(result => result.request().method() === "PATCH" && result.url().endsWith(`/variants/${original.id}`));
    await button(content, "Lưu biến thể").click();
    const save = await response;
    expect(save.status()).toBe(200);
    expect(save.request().postDataJSON()).not.toHaveProperty("priceVnd");
    expect((await api(content, `variants/${original.id}/sale-prices`)).status).toBe(403);
    const final: Product = (await api(page, productPath)).data;
    expect(final.variants?.find(variant => variant.id === original.id)).toMatchObject({ sku: original.sku, name: "Tên biên tập thử", priceVnd: 395000 });
    expect(final).toMatchObject({ subtitle: "Thay đổi từ phiên khác", primaryCategoryId: category.data.id, categoryIds: [category.data.id] });
  } finally { await contentContext.close(); }

  await test.step("G4 F1 preserves HTML structures and JSON source through actual writes", async () => {
    const image = process.env.ADMIN_PRODUCTS_TEST_IMAGE_URL ?? "https://media.example.invalid/catalog/fixture.png";
    const figure = `<figure><img src="${image}" alt="Ảnh mẫu" width="56" height="48"><blockquote><p>Dòng trích</p></blockquote><figcaption><p>Chú thích một</p><p>Chú thích hai</p></figcaption></figure>`;
    const table = '<table><tbody><tr><td colspan="2">Một ô</td></tr></tbody></table>';
    async function seed(body: object) {
      const create = await api(page, "products", "POST", { name: `Rich ${randomUUID()}`, productTypeId: type.data.id, ...body });
      expect(create.status).toBe(201);
      return create.data as Product;
    }
    const editor = page.getByRole("textbox", { name: "Mô tả chi tiết", exact: true });
    async function open(product: Product) {
      await page.goto(`${route}/${product.id}`);
      await button(page, "Chỉnh sửa thông tin").click();
    }
    async function save(product: Product) {
      const response = page.waitForResponse(r => r.request().method() === "PATCH" && r.url().endsWith(`products/${product.id}`));
      await button(page, "Lưu thay đổi").click();
      const result = await response;
      expect(result.status()).toBe(200);
      await expect(button(page, "Chỉnh sửa thông tin")).toBeVisible();
      return result.request().postDataJSON();
    }
    const html = await seed({ descriptionHtml: `<p>Dòng đầu</p>${figure}${table}` });
    expect(html.descriptionHtml).toContain(figure);
    await open(html);
    await editor.locator("p").first().click();
    await page.keyboard.press("Home");
    await page.keyboard.insertText("Gốm ");
    await save(html);
    await page.reload();
    await expect(editor.locator("figure figcaption p")).toHaveText(["Chú thích một", "Chú thích hai"]);
    const persisted = (await api(page, `products/${html.id}`)).data as Product;
    expect(persisted.descriptionHtml).toBe(`<p>Gốm Dòng đầu</p>${figure}${table}`);
    expect(persisted.descriptionJson).toBeNull();

    const imported = await page.evaluate(async ({ suffix, typeCode }) => {
      const csrf = await fetch("/api/v1/admin/auth/csrf", { cache: "no-store" });
      const form = new FormData();
      form.set("file", new File([`product_slug,name,product_type_code,description\nfocused-${suffix},Ấm nhập,${typeCode},"<strong>Ấm</strong>\ndòng hai"\n`],
        "fixture.csv", { type: "text/csv" }));
      const response = await fetch("/api/v1/admin/products/import?dryRun=false", {
        method: "POST", headers: { "X-CSRF-TOKEN": (await csrf.json()).token }, body: form
      });
      return { status: response.status, data: await response.json() };
    }, { suffix, typeCode: type.data.code });
    expect(imported.status).toBe(200);
    const importedProduct = (await api(page, `products/${imported.data.groups[0].productId}`)).data as Product;
    expect(importedProduct.descriptionJson).toBeNull();
    await open(importedProduct);
    await editor.locator("p").first().click();
    await page.keyboard.press("Home");
    await page.keyboard.insertText("Gốm ");
    expect(await save(importedProduct)).not.toHaveProperty("descriptionJson");
    await page.reload();
    await expect(editor).toContainText("Gốm <strong>Ấm</strong>");
    expect((await api(page, `products/${importedProduct.id}`)).data.descriptionHtml)
      .toContain("Gốm &lt;strong&gt;Ấm&lt;/strong&gt;<br>dòng hai");

    const original = { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Mô tả gốc" }] }] };
    const json = await seed({ descriptionJson: original });
    await open(json);
    await page.getByLabel("Tên phụ", { exact: true }).fill("Chỉ cập nhật metadata");
    expect(await save(json)).toEqual({ rowVersion: json.rowVersion, subtitle: "Chỉ cập nhật metadata" });
    await button(page, "Chỉnh sửa thông tin").click();
    await editor.click();
    await page.keyboard.press("End");
    await page.keyboard.insertText(" thêm");
    await expect(editor).toContainText("thêm");
    await page.keyboard.press("Control+z");
    await expect(editor).toHaveText("Mô tả gốc");
    await expect(button(page, "Lưu thay đổi")).toBeDisabled();
    await page.getByLabel("Tên phụ", { exact: true }).fill("Hoàn tác rồi cập nhật metadata");
    const patch = await save(json);
    expect(Object.keys(patch).sort()).toEqual(["rowVersion", "subtitle"]);
    expect((await api(page, `products/${json.id}`)).data.descriptionJson).toBe(json.descriptionJson);
    await button(page, "Chỉnh sửa thông tin").click();
    await editor.press("Control+a");
    await page.getByRole("group", { name: "Định dạng mô tả chi tiết" }).getByRole("button", { name: "Đậm", exact: true }).click();
    const formatting = await save(json);
    expect(formatting.descriptionHtml).toBeUndefined();
    expect(formatting.descriptionJson.content[0].content[0].marks).toEqual([{ type: "bold" }]);
    await page.reload();
    await expect(editor.locator("strong")).toHaveText("Mô tả gốc");
    await button(page, "Chỉnh sửa thông tin").click();
    await editor.fill("");
    expect((await save(json)).descriptionJson.content).toEqual([{ type: "paragraph" }]);
    const cleared = (await api(page, `products/${json.id}`)).data;
    expect(cleared.descriptionWordCount).toBe(0);
    expect(JSON.parse(cleared.descriptionJson).type).toBe("doc");

    const unsupported = await seed({ descriptionHtml: '<p>Trước</p><figure><figcaption>Đầu <p>Khối</p> Cuối</figcaption></figure>' });
    await open(unsupported);
    await expect(editor).toHaveAttribute("contenteditable", "false");
    await expect(page.getByText(/chưa thể lưu nguyên vẹn/)).toBeVisible();
    await page.getByLabel("Tên phụ", { exact: true }).fill("Lưu metadata an toàn");
    expect(Object.keys(await save(unsupported)).sort()).toEqual(["rowVersion", "subtitle"]);
    expect((await api(page, `products/${unsupported.id}`)).data.descriptionHtml).toBe(unsupported.descriptionHtml);

    const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
    const cell = (text: string, attrs?: { rowspan: number; colspan: number }) => ({ type: "tableCell", attrs, content: [paragraph(text)] });
    const row = (...cells: ReturnType<typeof cell>[]) => ({ type: "tableRow", content: cells });
    const tableDoc = (...rows: ReturnType<typeof row>[]) => ({ type: "doc", content: [
      paragraph("Đoạn đầu"), { type: "table", content: rows }
    ] });
    const incompatible = [
      tableDoc(row(cell("Trái"), cell("Phải")), row(cell("Chỉ một ô"))),
      tableDoc(row(cell("Ô giữ nguyên", { rowspan: 4, colspan: 1 })))
    ];
    for (const descriptionJson of incompatible) {
      const saved = await seed({ descriptionJson });
      expect(JSON.parse(saved.descriptionJson!)).toEqual(JSON.parse(JSON.stringify(descriptionJson)));
      await open(saved);
      await expect(page.getByText(/chưa thể lưu nguyên vẹn/)).toBeVisible();
      await expect(editor).toHaveAttribute("contenteditable", "false");
      await expect(editor).toContainText("Đoạn đầu");
      await expect(page.getByRole("group", { name: "Định dạng mô tả chi tiết" })).toHaveCount(0);
      await page.getByLabel("Tên phụ", { exact: true }).fill("Chỉ cập nhật tên phụ");
      expect(await save(saved)).toEqual({ rowVersion: saved.rowVersion, subtitle: "Chỉ cập nhật tên phụ" });
      const loaded = (await api(page, `products/${saved.id}`)).data as Product;
      expect(loaded.descriptionJson).toBe(saved.descriptionJson);
      expect(loaded.descriptionHtml).toBe(saved.descriptionHtml);
    }

    const rectangular = await seed({ descriptionJson: tableDoc(
      row(cell("Trái"), cell("Phải")), row(cell("Trái hai"), cell("Phải hai"))
    ) });
    await open(rectangular);
    await expect(editor).toHaveAttribute("contenteditable", "true");
    await editor.locator("p").first().click();
    await page.keyboard.press("Home");
    await page.keyboard.insertText("Sửa ");
    const rectangularPatch = await save(rectangular);
    expect(rectangularPatch).not.toHaveProperty("descriptionHtml");
    const reloaded = (await api(page, `products/${rectangular.id}`)).data as Product;
    expect(reloaded.descriptionHtml).toContain("<p>Sửa Đoạn đầu</p>");
    const beforeTable = JSON.parse(rectangular.descriptionJson!).content[1];
    const afterTable = JSON.parse(reloaded.descriptionJson!).content[1];
    for (const [index, priorRow] of beforeTable.content.entries()) {
      expect(afterTable.content[index].content.map((entry: { content: unknown }) => entry.content))
        .toEqual(priorRow.content.map((entry: { content: unknown }) => entry.content));
    }
    expect(afterTable.content).toHaveLength(2);
  });
});
