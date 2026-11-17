import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { QueryObserver } from "@tanstack/react-query";
import { AdminApi, ApiError, type Auth } from "../src/lib/api";
import { visibleNavigation, destination } from "../src/lib/navigation";
import { AdminProvider, createAdminRuntime, authKey } from "../src/components/providers";
import { DataTable, paginationParams } from "../src/components/data-table";

const auth: Auth = { passwordChangeRequired: false, totpRequired: false, user: {
  id: "staff-a", email: "test@example.invalid", displayName: "Nhân viên thử", isActive: true,
  mustChangePassword: false, totpEnabled: true, roles: ["owner", "content"], permissions: ["catalog.read", "content.write"], rowVersion: "1"
} };
const NativeRequest = globalThis.Request;
globalThis.Request = class extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    super(typeof input === "string" ? new URL(input, "https://admin.example.invalid") : input, init);
  }
};
function mockApi(reply?: (request: Request) => Response | Promise<Response>) {
  const requests: Request[] = [];
  const api = new AdminApi((async (request: Request) => {
    requests.push(request);
    return reply ? reply(request) : new Response(JSON.stringify(request.url.endsWith("/csrf") ? { token: "csrf-test" } : auth), { headers: { "Content-Type": "application/json" } });
  }) as typeof fetch);
  return { api, requests };
}

test("T02/T03/T06/T17 real generated contracts, CSRF and bodyless 204", async () => {
  const { api, requests } = mockApi(request => {
    if (request.url.endsWith("/csrf")) return Response.json({ token: "fresh" });
    return new Response(null, { status: 204 });
  });
  await api.login({ email: "test@example.invalid", password: "SyntheticOnly!12", totpCode: "123456" });
  await api.changePassword({ currentPassword: "SyntheticOnly!12", newPassword: "OtherSynthetic!12" });
  await api.verifyTotp({ code: "234567" });
  await api.logout();
  assert.equal(requests.length, 8);
  assert.deepEqual(requests.map(r => new URL(r.url).pathname), ["csrf", "login", "csrf", "change-password", "csrf", "totp/verify", "csrf", "logout"].map(x => `/api/v1/admin/auth/${x}`));
  const body = await requests[1].json();
  assert.deepEqual(body, { email: "test@example.invalid", password: "SyntheticOnly!12", totpCode: "123456" });
  for (const request of requests) {
    assert.equal(request.credentials, "include"); assert.equal(request.cache, "no-store");
    if (request.method === "POST") assert.equal(request.headers.get("X-CSRF-TOKEN"), "fresh");
  }
});

test("T07/T11 authentication transitions cancel old requests and clear all identity caches", async () => {
  const { api, requests } = mockApi();
  const runtime = createAdminRuntime(api);
  const observer = new QueryObserver(runtime.queries, { queryKey: authKey, enabled: false });
  const observed: unknown[] = [];
  const stop = observer.subscribe(result => observed.push(result.data));
  runtime.queries.setQueryData(["private-orders"], { customer: "private fixture" });
  runtime.queries.setMutationDefaults(["change"], { gcTime: Infinity });
  await runtime.authenticated();
  assert.equal(runtime.queries.getQueryData(["private-orders"]), undefined);
  assert.deepEqual(runtime.queries.getQueryData(authKey), auth);
  assert.ok(observed.some(value => value === auth || JSON.stringify(value) === JSON.stringify(auth)));
  assert.ok(requests[0].url.endsWith("/csrf")); assert.ok(requests[1].url.endsWith("/me"));
  let aborted = false;
  const pending = runtime.queries.fetchQuery({ queryKey: ["pending-secret"], queryFn: ({ signal }) => new Promise(resolve => {
    signal.addEventListener("abort", () => { aborted = true; resolve("late-secret"); });
  }) }).catch(() => {});
  await runtime.signedOut(); await pending;
  assert.equal(aborted, true);
  assert.equal(runtime.queries.getQueryData(["pending-secret"]), undefined);
  assert.equal(runtime.queries.getQueryData(authKey), null);
  assert.equal(runtime.queries.getMutationCache().getAll().length, 0);
  stop(); runtime.queries.clear();
});

test("T08 401, changed identity and changed permissions remove module data; no retry on errors", async () => {
  let next: Auth | null = auth;
  const { api, requests } = mockApi(() => next ? Response.json(next) : Response.json({ code: "unauthenticated" }, { status: 401 }));
  const runtime = createAdminRuntime(api);
  runtime.queries.setQueryData(authKey, auth);
  runtime.queries.setQueryData(["module"], "old-identity");
  next = { ...auth, user: { ...auth.user, id: "staff-b" } };
  await runtime.readSession(); assert.equal(runtime.queries.getQueryData(["module"]), undefined);
  runtime.queries.setQueryData(["module"], "stale-permissions");
  next = { ...auth, user: { ...auth.user, permissions: [] } };
  await runtime.readSession(); assert.equal(runtime.queries.getQueryData(["module"]), undefined);
  next = null; runtime.queries.setQueryData(["module"], "old-session");
  assert.equal(await runtime.readSession(), null); assert.equal(runtime.queries.getQueryData(["module"]), undefined);
  assert.equal(requests.length, 3);
  const observer = new QueryObserver(runtime.queries, { queryKey: ["denied"], queryFn: async () => { throw new ApiError(403, {}); } });
  const stop = observer.subscribe(() => {});
  await observer.refetch();
  assert.equal(observer.getCurrentResult().failureCount, 1); stop(); runtime.queries.clear();
});

test("T09/T12 ProblemDetails extension fields and generic credential errors", async () => {
  const { api } = mockApi(() => Response.json({ code: "validation_failed", errors: { NewPassword: ["Mật khẩu chưa hợp lệ."] } }, { status: 400 }));
  await assert.rejects(api.me(), (error: unknown) => error instanceof ApiError && error.fields.NewPassword[0] === "Mật khẩu chưa hợp lệ.");
  assert.match(new ApiError(429, {}).message, /chờ một phút/);
  assert.match(new ApiError(401, { code: "invalid_credentials" }).message, /Thông tin đăng nhập hoặc mã xác thực/);
});

test("T10 permission navigation uses union returned by backend, never role inference", () => {
  const items = visibleNavigation(auth.user.permissions);
  assert.deepEqual(items.map(x => x.path), ["/", "/catalog", "/content"]);
  assert.deepEqual(visibleNavigation([]).map(x => x.path), ["/"]);
  assert.equal(destination(null), "/login");
  assert.equal(destination({ ...auth, passwordChangeRequired: true, totpRequired: true }), "/change-password");
  assert.equal(destination({ ...auth, totpRequired: true }), "/totp-setup");
});

test("T14 table server pagination maps 0-based to 1-based and empty state spans columns", () => {
  assert.deepEqual(paginationParams({ pageIndex: 2, pageSize: 20 }), { page: 3, pageSize: 20 });
  const html = renderToStaticMarkup(<DataTable<{ id: string }> caption="Danh sách thử nghiệm" data={[]} columns={[{ accessorKey: "id", header: "Mã" }]}
    totalItems={0} pagination={{ pageIndex: 0, pageSize: 20 }} onPaginationChange={() => {}} getRowId={row => row.id} />);
  assert.match(html, /scope="col"/); assert.match(html, /Chưa có dữ liệu/); assert.match(html, /aria-label="Số mục mỗi trang"/);
});

test("T16 SSR contains only neutral loading markup; no identity, credentials or provisioning", () => {
  // Full App Router response is also checked in the browser suite.
  const html = renderToStaticMarkup(<AdminProvider><p>Đang kiểm tra phiên làm việc…</p></AdminProvider>);
  for (const secret of [auth.user.id, auth.user.email, "password", "provisioningUri", "sharedKey", "SyntheticOnly"]) assert.ok(!html.includes(secret));
});
