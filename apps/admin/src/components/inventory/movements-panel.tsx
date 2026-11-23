"use client";

import { useMemo, useState } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import type { ColumnDef, PaginationState } from "@tanstack/react-table";
import { ApiError } from "../../lib/api";
import {
  InventoryApi,
  inventoryMovementsKey,
  type MovementRow
} from "../../lib/inventory-api";
import {
  formatCostVnd,
  formatInt,
  formatSignedDelta,
  formatVnDateTime,
  isUuid,
  movementTypeLabels,
  movementTypes,
  shortVariantId,
  validateMovementDateRange,
  type InventoryPermissions
} from "../../lib/inventory-forms";
import { DataTable, paginationParams } from "../data-table";
import { useAdmin } from "../providers";
import { VariantIdentity, useHydratedVariantLabels } from "./variant-picker";

export function MovementsPanel({ variantId, perms }: { variantId: string; perms: InventoryPermissions }) {
  const { api: admin } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);
  const [type, setType] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [pagination, setPagination] = useState<PaginationState>({ pageIndex: 0, pageSize: 20 });

  const rangeError = validateMovementDateRange(from, to);
  const variantIds = useMemo(() => [variantId], [variantId]);
  const labels = useHydratedVariantLabels(variantIds, perms.canRead);
  const label = labels[variantId.toLowerCase()];

  const params = {
    ...paginationParams(pagination),
    type: type || undefined,
    from: from.trim() || undefined,
    to: to.trim() || undefined
  };

  const query = useQuery({
    queryKey: [...inventoryMovementsKey(variantId), params],
    queryFn: ({ signal }) => api.listMovements(variantId, params, signal),
    enabled: !rangeError
  });

  const columns = useMemo<ColumnDef<MovementRow>[]>(() => {
    const list: ColumnDef<MovementRow>[] = [
      { id: "occurredAt", header: "Thời điểm (VN)", cell: ({ row }) => formatVnDateTime(row.original.occurredAt) },
      {
        id: "movementType",
        header: "Loại biến động",
        cell: ({ row }) => movementTypeLabels[row.original.movementType] ?? row.original.movementType
      },
      {
        id: "quantity",
        header: "Biến động",
        cell: ({ row }) => (
          <strong className={Number(row.original.quantity) >= 0 ? "inventory-delta-pos" : "inventory-delta-neg"}>
            {formatSignedDelta(row.original.quantity)}
          </strong>
        )
      },
      { id: "onHandAfter", header: "Tồn sau", cell: ({ row }) => formatInt(row.original.onHandAfter) }
    ];
    if (perms.canReadCosts) {
      list.push({
        id: "unitCostVnd",
        header: "Đơn giá vốn",
        cell: ({ row }) => formatCostVnd(row.original.unitCostVnd)
      });
    }
    list.push(
      {
        id: "ref",
        header: "Chứng từ tham chiếu",
        cell: ({ row: { original: item } }) => {
          if (item.refType === "receipt" && item.refId && isUuid(item.refId)) {
            return <Link className="product-link-button" href={`/inventory/receipts/${item.refId}`}>Phiếu nhập {shortVariantId(item.refId)}</Link>;
          }
          if (item.refType === "stocktake" && item.refId && isUuid(item.refId)) {
            return <Link className="product-link-button" href={`/inventory/stocktakes/${item.refId}`}>Kiểm kê {shortVariantId(item.refId)}</Link>;
          }
          if (item.refType && item.refId) return `${item.refType} · ${shortVariantId(item.refId)}`;
          return item.refType || "—";
        }
      },
      { id: "reason", header: "Lý do / Ghi chú", cell: ({ row }) => row.original.reason || "—" }
    );
    return list;
  }, [perms.canReadCosts]);

  if (query.isError && query.error instanceof ApiError && query.error.status === 404) {
    return (
      <section className="empty-state">
        <h2>Không tìm thấy biến thể</h2>
        <p>Biến thể yêu cầu không tồn tại hoặc đã bị gỡ bỏ khỏi danh mục.</p>
        <Link className="button" href="/inventory">Quay về tồn kho</Link>
      </section>
    );
  }

  return (
    <section className="inventory-panel" aria-labelledby="inventory-movements-heading">
      <div className="inventory-section-header">
        <div>
          <Link className="product-link-button" href="/inventory">← Danh sách tồn kho</Link>
          <h2 id="inventory-movements-heading">Sổ kho biến thể</h2>
          <VariantIdentity variantId={variantId} label={label} />
        </div>
      </div>

      <div className="inventory-filters">
        <label className="product-field">
          Loại biến động
          <select
            aria-label="Loại biến động"
            value={type}
            onChange={event => {
              setType(event.target.value);
              setPagination(prev => ({ ...prev, pageIndex: 0 }));
            }}
          >
            <option value="">Tất cả loại</option>
            {movementTypes.map(key => (
              <option key={key} value={key}>{movementTypeLabels[key]}</option>
            ))}
          </select>
        </label>
        <label className="product-field">
          Từ ngày (giờ VN)
          <input
            type="date"
            aria-label="Từ ngày"
            value={from}
            onChange={event => {
              setFrom(event.target.value);
              setPagination(prev => ({ ...prev, pageIndex: 0 }));
            }}
          />
        </label>
        <label className="product-field">
          Đến trước ngày (giờ VN)
          <input
            type="date"
            aria-label="Đến trước ngày"
            value={to}
            onChange={event => {
              setTo(event.target.value);
              setPagination(prev => ({ ...prev, pageIndex: 0 }));
            }}
          />
        </label>
        {(type || from || to) && (
          <button
            type="button"
            className="button secondary"
            onClick={() => {
              setType("");
              setFrom("");
              setTo("");
              setPagination(prev => ({ ...prev, pageIndex: 0 }));
            }}
          >
            Xóa bộ lọc
          </button>
        )}
      </div>

      {rangeError && <p className="field-error" role="alert">{rangeError}</p>}

      {!rangeError && (
        <DataTable<MovementRow>
          caption="Sổ kho biến động theo biến thể"
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
      )}
    </section>
  );
}
