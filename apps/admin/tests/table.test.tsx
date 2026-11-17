import assert from "node:assert/strict";
import test from "node:test";
import { JSDOM } from "jsdom";
import { useState } from "react";
import { DataTable, paginationParams } from "../src/components/data-table";
import { ApiError } from "../src/lib/api";

test("T14/T15 typed table changes server pagination, size resets, empty/error/loading accessible", async () => {
  const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "https://admin.example.invalid" });
  const descriptors = new Map<string, PropertyDescriptor | undefined>();
  for (const [name, value] of Object.entries({ window: dom.window, document: dom.window.document, navigator: dom.window.navigator,
    HTMLElement: dom.window.HTMLElement, MutationObserver: dom.window.MutationObserver, getComputedStyle: dom.window.getComputedStyle })) {
    descriptors.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
  }
  const { render, cleanup } = await import("@testing-library/react");
  const { default: userEvent } = await import("@testing-library/user-event");
  const user = userEvent.setup({ document: dom.window.document });
  const requested: { page: number; pageSize: number }[] = [];
  function Fixture() {
    const [pagination, setPagination] = useState({ pageIndex: 0, pageSize: 20 });
    return <DataTable caption="Bảng thử nghiệm" data={[]} columns={[{ accessorKey: "id", header: "Mã" }]}
      totalItems={41} getRowId={(row: { id: string }) => row.id} pagination={pagination} onPaginationChange={updater => {
        setPagination(old => { const next = typeof updater === "function" ? updater(old) : updater; requested.push(paginationParams(next)); return next; });
      }} />;
  }
  try {
    const view = render(<Fixture />);
    assert.equal(view.getByRole("button", { name: "Trang trước" }).hasAttribute("disabled"), true);
    await user.click(view.getByRole("button", { name: "Trang sau" }));
    assert.deepEqual(requested.at(-1), { page: 2, pageSize: 20 });
    assert.match(view.getByRole("status").textContent!, /Trang 2/);
    await user.selectOptions(view.getByLabelText("Số mục mỗi trang"), "50");
    assert.deepEqual(requested.at(-1), { page: 1, pageSize: 50 });
    assert.equal(view.getByRole("button", { name: "Trang sau" }).hasAttribute("disabled"), true);
    assert.ok(view.getByRole("region", { name: "Bảng thử nghiệm" }).hasAttribute("tabindex"));
    cleanup();
    const props = { caption: "Bảng", data: [] as { id: string }[], columns: [{ accessorKey: "id", header: "Mã" }], totalItems: 0,
      pagination: { pageIndex: 0, pageSize: 20 }, onPaginationChange: () => {}, getRowId: (row: { id: string }) => row.id };
    const empty = render(<DataTable {...props} />); assert.ok(empty.getByRole("cell", { name: "Chưa có dữ liệu để hiển thị." })); cleanup();
    const loading = render(<DataTable {...props} loading />); assert.match(loading.getByRole("status").textContent!, /Đang tải/); cleanup();
    let retries = 0;
    const error = render(<DataTable {...props} error={new ApiError(403, {})} onRetry={() => { retries++; }} />);
    assert.match(error.getByRole("alert").textContent!, /chưa được cấp quyền/); await user.click(error.getByRole("button", { name: "Thử tải lại" })); assert.equal(retries, 1);
  } finally {
    cleanup(); dom.window.close();
    for (const [name, descriptor] of descriptors) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor); else Reflect.deleteProperty(globalThis, name);
    }
  }
});
