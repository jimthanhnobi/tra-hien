import { test, expect } from "@playwright/test";
import { createHmac } from "node:crypto";
import { readFile } from "node:fs/promises";

// Prevent automatic failure snapshots from retaining ephemeral enrollment material.
test.afterEach(async ({ context }) => { await context.close(); });

function totp(secret: string, at: number) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const char of secret.toUpperCase().replaceAll(" ", "")) bits += alphabet.indexOf(char).toString(2).padStart(5, "0");
  const key = Buffer.from(bits.match(/.{8}/g)!.map(x => parseInt(x, 2)));
  const counter = Buffer.alloc(8); counter.writeBigUInt64BE(BigInt(Math.floor(at / 30_000)));
  const hmac = createHmac("sha1", key).update(counter).digest(); const offset = hmac[19] & 15;
  return ((hmac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).toString().padStart(6, "0");
}

test("real P0-08 Production + PG17 + TLS: password onboarding, TOTP, Secure cookie, revocation, logout", async ({ page, context }) => {
  if (!process.env.ADMIN_IAM_FIXTURE) throw new Error("ADMIN_IAM_FIXTURE must point to credentials for a fresh isolated test DB");
  const fixture = JSON.parse(await readFile(process.env.ADMIN_IAM_FIXTURE, "utf8")) as { email: string; password: string; newPassword: string };
  // Never enable trace/video/screenshots: this test handles ephemeral enrollment secrets.
  const endpoints = new Set<string>();
  page.on("request", request => { if (new URL(request.url()).pathname.startsWith("/api/")) endpoints.add(`${request.method()} ${new URL(request.url()).pathname}`); });
  const login = async (password: string, code?: string) => {
    await page.getByLabel("Email công việc").fill(fixture.email);
    await page.getByLabel("Mật khẩu", { exact: true }).fill(password);
    if (code) await page.getByLabel("Mã xác thực TOTP").fill(code);
    await page.getByRole("button", { name: "Đăng nhập", exact: true }).click();
  };
  await page.goto("/orders"); await expect(page).toHaveURL(/login$/);
  await login(fixture.password); await expect(page).toHaveURL(/change-password$/);
  const restricted = await page.evaluate(async () => (await fetch("/api/v1/admin/staff/", { credentials: "include", cache: "no-store" })).status);
  expect(restricted).toBe(403);
  await page.getByLabel("Mật khẩu hiện tại", { exact: true }).fill(fixture.password);
  await page.getByLabel("Mật khẩu mới", { exact: true }).fill(fixture.newPassword);
  await page.getByLabel("Xác nhận mật khẩu mới", { exact: true }).fill(fixture.newPassword);
  const changed = page.waitForResponse(response => response.url().endsWith("/change-password"));
  await page.getByRole("button", { name: "Lưu mật khẩu mới" }).click(); expect((await changed).status()).toBe(204);
  await expect(page).toHaveURL(/login$/);
  await login(fixture.newPassword); await expect(page).toHaveURL(/totp-setup$/);
  await page.getByRole("button", { name: "Bắt đầu thiết lập" }).click(); await page.getByText("Không quét được mã QR?").click();
  const secret = (await page.locator(".provisioning code").textContent())!;
  const now = Date.now();
  // Avoid expiring a code while filling the form, without fixed long sleeps.
  const wait = 30_000 - now % 30_000;
  if (wait < 5000) await new Promise(resolve => setTimeout(resolve, wait + 1000));
  const verified = page.waitForResponse(response => response.url().endsWith("/totp/verify"));
  await page.getByLabel("Mã xác thực 6 số").fill(totp(secret, Date.now()));
  await page.getByRole("button", { name: "Xác minh và hoàn tất" }).click(); expect((await verified).status()).toBe(204);
  await expect(page).toHaveURL(/login$/);
  // TOTP replay protection requires the next time step after enrollment.
  const nextStep = 30_000 - Date.now() % 30_000 + 1000;
  await new Promise(resolve => setTimeout(resolve, nextStep));
  await login(fixture.newPassword, totp(secret, Date.now()));
  await expect(page.getByRole("heading", { name: "Chào Owner," })).toBeVisible();
  const cookie = (await context.cookies()).find(cookie => cookie.name === "__Host-tp_admin");
  expect({ secure: cookie?.secure, httpOnly: cookie?.httpOnly, sameSite: cookie?.sameSite, path: cookie?.path })
    .toEqual({ secure: true, httpOnly: true, sameSite: "Strict", path: "/" });
  expect(await page.evaluate(() => document.cookie.includes("__Host-tp_admin"))).toBe(false);
  const server = await page.evaluate(async () => {
    const response = await fetch("/", { cache: "no-store", credentials: "include" });
    return { html: await response.text(), cache: response.headers.get("Cache-Control") };
  });
  expect(server.cache).toContain("no-store");
  for (const value of [fixture.password, fixture.newPassword, fixture.email, secret]) expect(server.html.includes(value)).toBe(false);
  expect(await page.evaluate(async () => (await fetch("/api/v1/admin/staff/", { credentials: "include", cache: "no-store" })).status)).toBe(200);
  const missingCsrf = await page.evaluate(async () => (await fetch("/api/v1/admin/auth/logout", { method: "POST", credentials: "include" })).status); expect(missingCsrf).toBe(400);
  await page.getByRole("button", { name: /Tài khoản Owner/ }).click(); await page.getByRole("menuitem", { name: "Đăng xuất" }).click();
  await page.getByRole("button", { name: "Xác nhận đăng xuất" }).click(); await expect(page).toHaveURL(/login$/);
  expect(await page.evaluate(async () => (await fetch("/api/v1/admin/auth/me", { credentials: "include", cache: "no-store" })).status)).toBe(401);
  expect([...endpoints].sort()).toEqual([
    "GET /api/v1/admin/auth/csrf", "GET /api/v1/admin/auth/me", "POST /api/v1/admin/auth/change-password", "POST /api/v1/admin/auth/login",
    "POST /api/v1/admin/auth/logout", "POST /api/v1/admin/auth/totp/setup", "POST /api/v1/admin/auth/totp/verify", "GET /api/v1/admin/staff/"
  ].sort());
});
