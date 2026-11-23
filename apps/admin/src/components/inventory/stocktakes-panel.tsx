"use client";

import { useMemo, useRef, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import type { ColumnDef, PaginationState } from "@tanstack/react-table";
import {
  InventoryApi,
  inventoryStocktakeKey,
  inventoryStocktakesKey,
  type StocktakeSummary
} from "../../lib/inventory-api";
import {
  cleanNullableText,
  createStocktakeSchema,
  formatVnDateTime,
  stocktakeStatusLabels
} from "../../lib/inventory-forms";
import { DataTable, paginationParams } from "../data-table";
import { useAdmin } from "../providers";
import { FormError } from "../ui";

export function StocktakesPanel() {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);
  const router = useRouter();

  const [status, setStatus] = useState("");
  const [pagination, setPagination] = useState<PaginationState>({ pageIndex: 0, pageSize: 20 });
  const [note, setNote] = useState("");
  const [noteError, setNoteError] = useState("");
  const [createError, setCreateError] = useState<unknown>();
  const [creating, setCreating] = useState(false);
  const busy = useRef(false);

  const params = { ...paginationParams(pagination), status: status || undefined };
  const query = useQuery({
    queryKey: [...inventoryStocktakesKey, "list", params],
    queryFn: ({ signal }) => api.listStocktakes(params, signal)
  });

  async function handleCreate(event: React.FormEvent) {
    event.preventDefault();
    if (busy.current) return;
    setNoteError("");
    setCreateError(undefined);
    const parsed = createStocktakeSchema.safeParse({ note });
    if (!parsed.success) {
      setNoteError(parsed.error.issues[0]?.message ?? "Ghi chú không hợp lệ.");
      return;
    }
    busy.current = true;
    setCreating(true);
    try {
      const created = await api.createStocktake(cleanNullableText(note));
      queries.setQueryData(inventoryStocktakeKey(created.document.id), created);
      void queries.invalidateQueries({ queryKey: [...inventoryStocktakesKey, "list"] });
      router.push(`/inventory/stocktakes/${created.document.id}`);
    } catch (error) {
      setCreateError(error);
    } finally {
      busy.current = false;
      setCreating(false);
    }
  }

  const columns = useMemo<ColumnDef<StocktakeSummary>[]>(() => [
    { id: "code", header: "Mã phiên", cell: ({ row }) => <strong>{row.original.code}</strong> },
    {
      id: "status",
      header: "Trạng thái",
      cell: ({ row }) => (
        <span className={`inventory-badge ${row.original.status === "approved" ? "is-ok" : row.original.status === "cancelled" ? "is-muted" : "is-warning"}`}>
          {stocktakeStatusLabels[row.original.status] ?? row.original.status}
        </span>
      )
    },
    { id: "note", header: "Ghi chú", cell: ({ row }) => row.original.note || "—" },
    { id: "createdAt", header: "Tạo lúc (VN)", cell: ({ row }) => formatVnDateTime(row.original.createdAt) },
    { id: "submittedAt", header: "Gửi duyệt", cell: ({ row }) => formatVnDateTime(row.original.submittedAt) },
    { id: "approvedAt", header: "Duyệt lúc", cell: ({ row }) => formatVnDateTime(row.original.approvedAt) },
    {
      id: "actions",
      header: "Thao tác",
      cell: ({ row }) => (
        <Link className="button secondary" href={`/inventory/stocktakes/${row.original.id}`} aria-label={`Mở phiên kiểm kê ${row.original.code}`}>
          Mở phiên
        </Link>
      )
    }
  ], []);

  return (
    <div className="inventory-stack">
      <section className="catalog-form inventory-form-card" aria-labelledby="create-stocktake-heading">
        <h2 id="create-stocktake-heading">Tạo phiên kiểm kê mới</h2>
        <form onSubmit={handleCreate} noValidate>
          <div className="product-field">
            <label htmlFor="stocktake-note">Ghi chú phiên kiểm kê (tùy chọn)</label>
            <input
              id="stocktake-note"
              type="text"
              maxLength={2000}
              value={note}
              disabled={creating}
              aria-invalid={Boolean(noteError)}
              onChange={event => setNote(event.target.value)}
              placeholder="Ví dụ: Kiểm kê cuối tuần khu kệ ấm tử sa…"
            />
            {noteError && <p className="field-error">{noteError}</p>}
          </div>
          <FormError error={createError} />
          <div className="catalog-actions">
            <button type="submit" className="button" disabled={creating}>
              {creating ? "Đang tạo…" : "Tạo phiên kiểm kê"}
            </button>
          </div>
        </form>
      </section>

      <section className="inventory-panel" aria-labelledby="stocktakes-list-heading">
        <div className="inventory-section-header">
          <div>
            <h2 id="stocktakes-list-heading">Danh sách phiên kiểm kê</h2>
            <p className="hint">Đếm số lượng thực tế, gửi duyệt và áp chênh lệch vào tồn kho hiện tại.</p>
          </div>
        </div>

        <div className="inventory-filters">
          <label className="product-field">
            Trạng thái kiểm kê
            <select
              aria-label="Trạng thái kiểm kê"
              value={status}
              onChange={event => {
                setStatus(event.target.value);
                setPagination(prev => ({ ...prev, pageIndex: 0 }));
              }}
            >
              <option value="">Tất cả trạng thái</option>
              <option value="counting">Đang đếm</option>
              <option value="submitted">Chờ duyệt</option>
              <option value="approved">Đã duyệt</option>
              <option value="cancelled">Đã hủy</option>
            </select>
          </label>
        </div>

        <DataTable<StocktakeSummary>
          caption="Danh sách phiên kiểm kê"
          data={query.data?.items ?? []}
          columns={columns}
          totalItems={Number(query.data?.totalItems ?? 0)}
          pagination={pagination}
          onPaginationChange={setPagination}
          loading={query.isPending}
          error={query.error}
          onRetry={() => void query.refetch()}
          getRowId={row => row.id}
        />
      </section>
    </div>
  );
}
