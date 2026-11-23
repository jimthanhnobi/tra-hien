"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import type { ColumnDef, PaginationState } from "@tanstack/react-table";
import {
  InventoryApi,
  cacheVariantLabels,
  inventoryProductTypesKey,
  inventoryStockKey,
  toVariantLabel,
  type StockRow
} from "../../lib/inventory-api";
import {
  availabilityLabels,
  formatCostVnd,
  formatInt,
  type InventoryPermissions
} from "../../lib/inventory-forms";
import { DataTable, paginationParams } from "../data-table";
import { useAdmin } from "../providers";
import { FormError } from "../ui";
import { AlertsPanel } from "./alerts-panel";

export function StockListPanel({ perms }: { perms: InventoryPermissions }) {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);

  const [search, setSearch] = useState("");
  const [q, setQ] = useState("");
  const [typeId, setTypeId] = useState("");
  const [status, setStatus] = useState("");
  const [pagination, setPagination] = useState<PaginationState>({ pageIndex: 0, pageSize: 20 });

  const searchPending = search.trim() !== q;
  useEffect(() => {
    if (!searchPending) return;
    const timer = window.setTimeout(() => {
      setQ(search.trim());
      setPagination(previous => ({ ...previous, pageIndex: 0 }));
    }, 250);
    return () => window.clearTimeout(timer);
  }, [search, searchPending]);

  // Never send status=low without inventory.write to prevent 403 loops.
  const effectiveStatus = status === "low" && !perms.canWrite ? "" : status;
  const effectiveType = perms.canReadCatalog ? typeId : "";

  const productTypes = useQuery({
    queryKey: inventoryProductTypesKey,
    queryFn: ({ signal }) => api.listProductTypes(signal),
    enabled: perms.canReadCatalog,
    refetchOnWindowFocus: false
  });

  const params = {
    ...paginationParams(pagination),
    q: q || undefined,
    status: effectiveStatus || undefined,
    type: effectiveType || undefined
  };

  const stock = useQuery({
    queryKey: [...inventoryStockKey, params],
    queryFn: async ({ signal }) => {
      const result = await api.listStock(params, signal);
      if (!signal.aborted) cacheVariantLabels(queries, result.items.map(toVariantLabel));
      return result;
    }
  });

  const typeMap = useMemo(() => {
    const map = new Map<string, string>();
    for (const item of productTypes.data ?? []) map.set(item.id, item.name);
    return map;
  }, [productTypes.data]);

  const columns = useMemo<ColumnDef<StockRow>[]>(() => {
    const base: ColumnDef<StockRow>[] = [
      {
        id: "variant",
        header: "Biến thể",
        cell: ({ row: { original: item } }) => (
          <div className="inventory-variant-id">
            <strong>{item.sku}</strong>
            <span>{item.productName}{item.variantName ? ` · ${item.variantName}` : ""}</span>
          </div>
        )
      }
    ];
    if (perms.canReadCatalog) {
      base.push({
        id: "type",
        header: "Loại sản phẩm",
        cell: ({ row }) => typeMap.get(row.original.productTypeId) ?? "—"
      });
    }
    base.push({
      id: "availability",
      header: "Trạng thái",
      cell: ({ row: { original: item } }) => (
        <div className="inventory-badges">
          <span className={`inventory-badge ${item.availability === "in_stock" ? "is-ok" : "is-muted"}`}>
            {availabilityLabels[item.availability] ?? item.availability}
          </span>
          {perms.canWrite && item.lowStock && <span className="inventory-badge is-warning">Tồn thấp</span>}
        </div>
      )
    });
    if (perms.canWrite) {
      base.push(
        { id: "onHand", header: "Tồn kho", cell: ({ row }) => formatInt(row.original.onHand) },
        { id: "reserved", header: "Đang giữ", cell: ({ row }) => formatInt(row.original.reserved) },
        { id: "available", header: "Khả dụng", cell: ({ row }) => formatInt(row.original.available) }
      );
    }
    if (perms.canReadCosts) {
      base.push({
        id: "avgCostVnd",
        header: "Giá vốn BQ",
        cell: ({ row }) => formatCostVnd(row.original.avgCostVnd)
      });
    }
    if (perms.canViewMovements) {
      base.push({
        id: "actions",
        header: "Thao tác",
        cell: ({ row }) => (
          <Link
            className="button secondary"
            href={`/inventory/${row.original.variantId}/movements`}
            aria-label={`Xem sổ kho ${row.original.sku}`}
          >
            Sổ kho
          </Link>
        )
      });
    }
    return base;
  }, [perms, typeMap]);

  return (
    <div className="inventory-stack">
      {perms.canViewAlerts && <AlertsPanel />}

      <section className="inventory-panel" aria-labelledby="inventory-stock-heading">
        <div className="inventory-section-header">
          <div>
            <h2 id="inventory-stock-heading">Danh sách tồn kho</h2>
            <p className="hint">
              {perms.canWrite
                ? "Theo dõi tồn kho, giữ hàng và khả dụng theo từng biến thể."
                : "Hiển thị trạng thái còn hàng / hết hàng theo quyền được cấp."}
            </p>
          </div>
          {perms.canUseReceipts && (
            <Link className="button" href="/inventory/receipts/new">Tạo phiếu nhập</Link>
          )}
        </div>

        <div className="inventory-filters">
          <label className="product-field">
            Tìm SKU hoặc tên sản phẩm
            <input
              type="search"
              maxLength={200}
              value={search}
              onChange={event => setSearch(event.target.value)}
              placeholder="Nhập SKU hoặc tên…"
            />
          </label>
          {perms.canReadCatalog && (
            <label className="product-field">
              Loại sản phẩm
              <select
                aria-label="Loại sản phẩm"
                value={typeId}
                onChange={event => {
                  setTypeId(event.target.value);
                  setPagination(prev => ({ ...prev, pageIndex: 0 }));
                }}
              >
                <option value="">Tất cả loại</option>
                {(productTypes.data ?? []).map(item => (
                  <option key={item.id} value={item.id}>{item.name}</option>
                ))}
              </select>
            </label>
          )}
          <label className="product-field">
            Trạng thái tồn kho
            <select
              aria-label="Trạng thái tồn kho"
              value={effectiveStatus}
              onChange={event => {
                setStatus(event.target.value);
                setPagination(prev => ({ ...prev, pageIndex: 0 }));
              }}
            >
              <option value="">Tất cả trạng thái</option>
              <option value="in">Còn hàng</option>
              <option value="out">Hết hàng</option>
              {perms.canWrite && <option value="low">Tồn thấp</option>}
            </select>
          </label>
        </div>

        {perms.canReadCatalog && productTypes.isError && (
          <div>
            <FormError error={productTypes.error} />
            <button type="button" className="button secondary" onClick={() => void productTypes.refetch()}>
              Tải lại loại sản phẩm
            </button>
          </div>
        )}

        <DataTable<StockRow>
          caption="Danh sách tồn kho theo biến thể"
          data={stock.data?.items ?? []}
          columns={columns}
          totalItems={Number(stock.data?.totalItems ?? 0)}
          pagination={pagination}
          onPaginationChange={setPagination}
          loading={searchPending || stock.isPending}
          error={stock.error}
          onRetry={() => void stock.refetch()}
          getRowId={row => row.variantId}
        />
      </section>
    </div>
  );
}
