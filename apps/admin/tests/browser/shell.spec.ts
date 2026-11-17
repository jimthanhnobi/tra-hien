import { test, expect, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import type { Auth } from "../../src/lib/api";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { DataTable } from "../../src/components/data-table";

const ready: Auth = { passwordChangeRequired: false, totpRequired: false, user: {
  id: "00000000-0000-7000-8000-000000000001", email: "synthetic@example.invalid", displayName: "Minh An", isActive: true,
  mustChangePassword: false, totpEnabled: true, roles: ["owner"], rowVersion: "fixture",
  permissions: ["catalog.read", "inventory.read", "orders.read", "customers.read", "marketing.write", "content.write", "finance.read", "settings.write", "staff.manage"]
} };

async function iam(page: Page, initial: Auth | null = null) {
  let session = initial;
  let loginResponse = ready;
  let loginError: number | undefined;
  let meError: number | undefined;
  let csrfVersion = 0;
  const calls: { path: string; body: unknown; csrf: string | undefined }[] = [];
  await page.route("**/api/**", async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname.replace("/api/v1/admin/auth/", "");
    const body: unknown = request.postDataJSON();
    calls.push({ path, body, csrf: request.headers()["x-csrf-token"] });
    const json = (value: unknown, status = 200) => route.fulfill({ status, contentType: "application/json", body: JSON.stringify(value), headers: { "Cache-Control": "no-store" } });
    if (path === "csrf") return json({ token: `csrf-${++csrfVersion}` });
    if (request.method() === "POST") expect(request.headers()["x-csrf-token"]).toBe(`csrf-${csrfVersion}`);
    if (path === "me") return meError ? json({ code: "forbidden" }, meError) : session ? json(session) : json({ code: "unauthenticated" }, 401);
    if (path === "login") {
      if (loginError) return json(loginError === 429 ? { code: "rate_limited" } : { code: "validation_failed", errors: { Email: ["Email không hợp lệ."] } }, loginError);
      session = loginResponse; return json(session);
    }
    if (path === "logout" || path === "change-password" || path === "totp/verify") { session = null; return route.fulfill({ status: 204 }); }
    if (path === "totp/setup") return json({ sharedKey: "SYNTHETICKEYONLY", provisioningUri: "otpauth://totp/Fixture?secret=JBSWY3DPEHPK3PXP&issuer=Fixture" });
    throw new Error(`Unsupported endpoint: ${path}`);
  });
  return { calls, loginAs: (value: Auth) => { loginResponse = value; }, rejectLogin: (status: number) => { loginError = status; },
    expire: () => { session = null; }, deny: () => { meError = 403; } };
}

async function fillLogin(page: Page, totp = "") {
  await page.getByLabel("Email công việc").fill(ready.user.email);
  await page.getByLabel("Mật khẩu", { exact: true }).fill("SyntheticOnly!12");
  if (totp) await page.getByLabel("Mã xác thực TOTP").fill(totp);
  await page.getByRole("button", { name: "Đăng nhập", exact: true }).click();
}

test("T01/T02/T03/T07 anonymous -> login with real fields and optional TOTP -> refreshed me/CSRF", async ({ page }) => {
  const backend = await iam(page); await page.goto("/orders");
  await expect(page).toHaveURL(/\/login$/);
  await fillLogin(page, "123456"); await expect(page.getByRole("heading", { name: "Chào Minh An," })).toBeVisible();
  expect(backend.calls.find(x => x.path === "login")?.body).toEqual({ email: ready.user.email, password: "SyntheticOnly!12", totpCode: "123456" });
  const at = backend.calls.findIndex(x => x.path === "login");
  expect(backend.calls.slice(at + 1).map(x => x.path)).toContain("csrf");
  expect(backend.calls.slice(at + 1).map(x => x.path)).toContain("me");
});

test("T04/T06 first password onboarding -> empty 204 -> login; direct URLs cannot bypass", async ({ page }) => {
  const backend = await iam(page); backend.loginAs({ ...ready, passwordChangeRequired: true, totpRequired: true, user: { ...ready.user, permissions: [] } });
  await page.goto("/login"); await fillLogin(page);
  await expect(page).toHaveURL(/change-password$/); await page.goto("/orders"); await expect(page).toHaveURL(/change-password$/);
  await page.getByLabel("Mật khẩu hiện tại", { exact: true }).fill("SyntheticOnly!12");
  await page.getByLabel("Mật khẩu mới", { exact: true }).fill("NewSynthetic!123");
  await page.getByLabel("Xác nhận mật khẩu mới", { exact: true }).fill("NewSynthetic!123");
  await page.getByRole("button", { name: "Lưu mật khẩu mới" }).click();
  await expect(page).toHaveURL(/login$/); await expect(page.getByRole("status")).toContainText("Đã đổi mật khẩu");
  expect(backend.calls.find(x => x.path === "change-password")?.body).toEqual({ currentPassword: "SyntheticOnly!12", newPassword: "NewSynthetic!123" });
});

test("T05/T06 enrollment secret only in setup, local QR, verify 204 then login", async ({ page }, testInfo) => {
  const backend = await iam(page, { ...ready, totpRequired: true, user: { ...ready.user, permissions: [], totpEnabled: false } });
  await page.goto("/"); await expect(page).toHaveURL(/totp-setup$/);
  await page.screenshot({ path: testInfo.outputPath("totp-instructions.png"), fullPage: true });
  await page.getByRole("button", { name: "Bắt đầu thiết lập" }).click();
  await expect(page.getByRole("img", { name: /Mã QR/ })).toHaveAttribute("src", /^data:image\/png;base64,/);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  await page.getByText("Không quét được mã QR?").click(); await expect(page.getByText("SYNTHETICKEYONLY")).toBeVisible();
  await page.getByLabel("Mã xác thực 6 số").fill("234567"); await page.getByRole("button", { name: "Xác minh và hoàn tất" }).click();
  await expect(page).toHaveURL(/login$/); await expect(page.getByText("SYNTHETICKEYONLY")).toHaveCount(0);
  expect(backend.calls.find(x => x.path === "totp/verify")?.body).toEqual({ code: "234567" });
});

test("T08 expired session hides stale content before redirect", async ({ page }) => {
  const backend = await iam(page, ready); await page.goto("/"); await expect(page.getByRole("heading", { name: "Chào Minh An," })).toBeVisible();
  backend.expire(); await page.evaluate(() => window.dispatchEvent(new Event("visibilitychange")));
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(page).toHaveURL(/login$/);
  await expect(page.getByText("Chào Minh An,")).toHaveCount(0);
  expect(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))).toEqual({ local: 0, session: 0 });
});

test("T09/T10 backend permissions filter menus despite owner role, forbidden direct route, unknown route", async ({ page }, testInfo) => {
  await iam(page, { ...ready, user: { ...ready.user, roles: ["owner", "content"], permissions: ["catalog.read", "content.write"] } });
  await page.goto("/"); const nav = page.getByRole("navigation", { name: "Điều hướng chính" });
  await expect(nav.getByRole("link", { name: "Nội dung" })).toBeVisible(); await expect(nav.getByRole("link", { name: "Tài chính" })).toHaveCount(0);
  await page.goto("/finance"); await expect(page.getByRole("heading", { name: "Bạn chưa có quyền truy cập" })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("forbidden-desktop.png"), fullPage: true });
  await page.goto("/unknown"); await expect(page.getByRole("heading", { name: "Không tìm thấy trang" })).toBeVisible();
  await page.goto("/unknown/deep"); await expect(page.getByRole("heading", { name: "Không tìm thấy trang" })).toBeVisible();
});

test("T05 optional enrollment uses backend totpEnabled and survives a focus recheck", async ({ page }) => {
  await iam(page, { ...ready, user: { ...ready.user, roles: ["sales"], totpEnabled: false } });
  await page.goto("/"); await page.getByRole("button", { name: /Tài khoản Minh An/ }).click();
  await page.getByRole("menuitem", { name: "Thiết lập TOTP" }).click(); await expect(page).toHaveURL(/totp-setup$/);
  await page.getByRole("button", { name: "Bắt đầu thiết lập" }).click();
  await expect(page.getByRole("img", { name: /Mã QR/ })).toBeVisible();
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(page.getByRole("img", { name: /Mã QR/ })).toBeVisible();
});

test("T09 API 403 has useful UI without retry loop", async ({ page }) => {
  const backend = await iam(page, ready); backend.deny(); await page.goto("/");
  await expect(page.getByRole("heading", { name: "Bạn chưa có quyền truy cập" })).toBeVisible();
  expect(backend.calls.filter(x => x.path === "me").length).toBe(1);
});

test("T11/T15 account menu keyboard, confirmation Escape and logout", async ({ page }) => {
  await iam(page, ready); await page.goto("/");
  const account = page.getByRole("button", { name: "Tài khoản Minh An" }); await account.focus(); await page.keyboard.press("Enter");
  await page.keyboard.press("ArrowDown"); await expect(page.getByRole("menuitem", { name: "Đăng xuất" })).toBeFocused();
  await page.keyboard.press("Enter"); await expect(page.getByRole("dialog")).toBeVisible();
  await page.keyboard.press("Escape"); await expect(page.getByRole("dialog")).toHaveCount(0);
  await page.getByRole("menuitem", { name: "Đăng xuất" }).click(); await page.getByRole("button", { name: "Xác nhận đăng xuất" }).click();
  await expect(page).toHaveURL(/login$/); await expect(page.getByRole("button", { name: "Tài khoản Minh An" })).toHaveCount(0);
});

for (const status of [400, 429]) test(`T12 ProblemDetails ${status} shown accessibly`, async ({ page }) => {
  const backend = await iam(page); backend.rejectLogin(status); await page.goto("/login"); await fillLogin(page);
  await expect(page.getByRole("alert").first()).toContainText(status === 429 ? "chờ một phút" : "Thông tin chưa hợp lệ");
  if (status === 400) { await expect(page.getByLabel("Email công việc")).toHaveAttribute("aria-invalid", "true"); }
});

test("T13 validation, visibility and in-flight duplicate-submit protection", async ({ page }) => {
  const backend = await iam(page); await page.goto("/login");
  await page.getByRole("button", { name: "Đăng nhập", exact: true }).click();
  await expect(page.getByLabel("Email công việc")).toBeFocused(); expect(backend.calls.some(x => x.path === "login")).toBe(false);
  await page.getByLabel("Email công việc").fill(ready.user.email); await page.getByLabel("Mật khẩu", { exact: true }).fill("SyntheticOnly!12");
  await page.getByRole("button", { name: "Hiện mật khẩu", exact: true }).click(); await expect(page.getByLabel("Mật khẩu", { exact: true })).toHaveAttribute("type", "text");
  await page.route("**/api/v1/admin/auth/login", async route => { await new Promise(resolve => setTimeout(resolve, 400)); await route.fallback(); });
  await page.getByRole("button", { name: "Đăng nhập", exact: true }).click(); await expect(page.getByRole("button", { name: "Đang đăng nhập…" })).toBeDisabled();
  await page.getByLabel("Mật khẩu", { exact: true }).press("Enter");
  await expect(page.getByRole("heading", { name: "Chào Minh An," })).toBeVisible(); expect(backend.calls.filter(x => x.path === "login")).toHaveLength(1);
});

for (const viewport of [{ width: 1440, height: 1000 }, { width: 768, height: 1024 }, { width: 390, height: 844 }]) {
  test(`T15 UI ${viewport.width}: overflow, axe, sidebar/drawer and screenshots`, async ({ page }, testInfo) => {
    await page.setViewportSize(viewport); await iam(page, ready); await page.goto("/");
    await expect(page.getByRole("heading", { name: "Chào Minh An," })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const findings = await new AxeBuilder({ page }).analyze(); expect(findings.violations).toEqual([]);
    await page.screenshot({ path: testInfo.outputPath(`shell-${viewport.width}.png`), fullPage: true });
    if (viewport.width < 801) {
      const trigger = page.getByRole("button", { name: "Mở điều hướng" }); await trigger.click(); await expect(page.getByRole("dialog")).toBeVisible();
      await page.screenshot({ path: testInfo.outputPath(`drawer-${viewport.width}.png`), fullPage: true });
      await page.keyboard.press("Escape"); await expect(page.getByRole("dialog")).toHaveCount(0); await expect(trigger).toBeFocused();
    } else {
      await page.getByRole("button", { name: "Thu gọn thanh điều hướng" }).click();
      await expect(page.getByRole("link", { name: "Tài chính", exact: true }).first()).toHaveAccessibleName("Tài chính");
    }
  });
}

test("T15/T16 login mobile accessibility, SSR and no-cache headers", async ({ page, request }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 }); await iam(page); await page.goto("/login");
  await expect(page.getByLabel("Email công việc")).toBeVisible();
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("login-mobile.png"), fullPage: true });
  const response = await request.get("/login"); const html = await response.text();
  expect(response.headers()["cache-control"]).toContain("no-store");
  expect(response.headers()["x-robots-tag"]).toContain("noindex");
  for (const value of [ready.user.email, "SYNTHETICKEYONLY", "SyntheticOnly!12", "otpauth://"]) expect(html).not.toContain(value);
});

test("T14 table empty fixture fits a narrow viewport with scoped scrolling", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 }); await iam(page); await page.goto("/login");
  await expect(page.getByLabel("Email công việc")).toBeVisible();
  const markup = renderToStaticMarkup(createElement(DataTable<{ id: string }>, {
    caption: "Bảng thử nghiệm component — không phải dữ liệu nghiệp vụ", data: [],
    columns: [{ accessorKey: "id", header: "Mã định danh" }, { id: "name", header: "Tên hiển thị" }, { id: "actions", header: "Thao tác" }],
    totalItems: 0, getRowId: row => row.id, pagination: { pageIndex: 0, pageSize: 20 }, onPaginationChange: () => {}
  }));
  // Isolated static component fixture in the test DOM; retains real app CSS in head.
  // Pagination interactions are tested with RTL, without adding any production route.
  await page.evaluate(html => { document.body.innerHTML = `<main style="padding:16px"><h1>Bảng thử nghiệm</h1>${html}</main>`; }, markup);
  await expect(page.getByRole("cell", { name: "Chưa có dữ liệu để hiển thị." })).toBeVisible();
  await expect(page.getByRole("button", { name: "Trang sau" })).toBeDisabled();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("table-empty-mobile.png"), fullPage: true });
});
