"use client";

import { useMemo, useState } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import type { ColumnDef, PaginationState } from "@tanstack/react-table";
import {
  InventoryApi,
  inventoryReceiptsKey,
  type ReceiptSummary
} from "../../lib/inventory-api";
import { formatVnDateTime, receiptStatusLabels } from "../../lib/inventory-forms";
import { DataTable, paginationParams } from "../data-table";
import { useAdmin } from "../providers";

export function ReceiptsPanel() {
  const { api: admin } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);
  const [status, setStatus] = useState("");
  const [pagination, setPagination] = useState<PaginationState>({ pageIndex: 0, pageSize: 20 });

  const params = { ...paginationParams(pagination), status: status || undefined };
  const query = useQuery({
    queryKey: [...inventoryReceiptsKey, "list", params],
    queryFn: ({ signal }) => api.listReceipts(params, signal)
  });

  const columns = useMemo<ColumnDef<ReceiptSummary>[]>(() => [
    { id: "code", header: "Mã phiếu", cell: ({ row }) => <strong>{row.original.code}</strong> },
    {
      id: "status",
      header: "Trạng thái",
      cell: ({ row }) => (
        <span className={`inventory-badge ${row.original.status === "posted" ? "is-ok" : row.original.status === "cancelled" ? "is-muted" : "is-warning"}`}>
          {receiptStatusLabels[row.original.status] ?? row.original.status}
        </span>
      )
    },
    { id: "supplierName", header: "Nhà cung cấp", cell: ({ row }) => row.original.supplierName || "—" },
    { id: "receivedAt", header: "Ngày nhận (VN)", cell: ({ row }) => formatVnDateTime(row.original.receivedAt) },
    { id: "postedAt", header: "Ngày chốt (VN)", cell: ({ row }) => formatVnDateTime(row.original.postedAt) },
    {
      id: "actions",
      header: "Thao tác",
      cell: ({ row }) => (
        <Link className="button secondary" href={`/inventory/receipts/${row.original.id}`} aria-label={`Chi tiết phiếu nhập ${row.original.code}`}>
          Chi tiết
        </Link>
      )
    }
  ], []);

  return (
    <section className="inventory-panel" aria-labelledby="inventory-receipts-heading">
      <div className="inventory-section-header">
        <div>
          <h2 id="inventory-receipts-heading">Phiếu nhập kho</h2>
          <p className="hint">Tạo phiếu nhập nháp, rà soát dòng hàng và chốt nhập vào sổ kho.</p>
        </div>
        <Link className="button" href="/inventory/receipts/new">Tạo phiếu nhập</Link>
      </div>

      <div className="inventory-filters">
        <label className="product-field">
          Trạng thái phiếu nhập
          <select
            aria-label="Trạng thái phiếu nhập"
            value={status}
            onChange={event => {
              setStatus(event.target.value);
              setPagination(prev => ({ ...prev, pageIndex: 0 }));
            }}
          >
            <option value="">Tất cả trạng thái</option>
            <option value="draft">Bản nháp</option>
            <option value="posted">Đã chốt</option>
            <option value="cancelled">Đã hủy</option>
          </select>
        </label>
      </div>

      <DataTable<ReceiptSummary>
        caption="Danh sách phiếu nhập kho"
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
  );
}
