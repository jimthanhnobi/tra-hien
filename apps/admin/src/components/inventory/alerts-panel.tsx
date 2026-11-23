"use client";

import { useMemo } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, RefreshCw } from "lucide-react";
import {
  InventoryApi,
  cacheVariantLabels,
  inventoryAlertsKey,
  toVariantLabel
} from "../../lib/inventory-api";
import { formatInt, formatSignedDelta, formatVnDateTime } from "../../lib/inventory-forms";
import { useAdmin } from "../providers";
import { FormError, Pending } from "../ui";

export function AlertsPanel() {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);
  const query = useQuery({
    queryKey: [...inventoryAlertsKey, 50],
    queryFn: async ({ signal }) => {
      const data = await api.alerts(50, signal);
      if (!signal.aborted) {
        cacheVariantLabels(queries, [
          ...data.lowStockItems.map(toVariantLabel),
          ...data.mismatches.map(toVariantLabel)
        ]);
      }
      return data;
    }
  });

  return (
    <section className="inventory-alerts-panel" aria-labelledby="inventory-alerts-heading">
      <div className="inventory-section-header">
        <div>
          <h2 id="inventory-alerts-heading">Cảnh báo kho</h2>
          {query.data && <p className="hint">Kiểm tra lúc {formatVnDateTime(query.data.checkedAt)}</p>}
        </div>
        <button
          type="button"
          className="button secondary"
          disabled={query.isFetching}
          onClick={() => void query.refetch()}
        >
          <RefreshCw size={16} aria-hidden="true" /> {query.isFetching ? "Đang tải…" : "Làm mới cảnh báo"}
        </button>
      </div>

      {query.isPending ? (
        <Pending>Đang tải cảnh báo kho…</Pending>
      ) : query.isError ? (
        <div>
          <FormError error={query.error} />
          <button type="button" className="button secondary" onClick={() => void query.refetch()}>
            Thử tải lại cảnh báo
          </button>
        </div>
      ) : (
        <>
          <div className="inventory-summary-grid">
            <div className="inventory-stat-card">
              <span>Biến thể tồn thấp</span>
              <strong>{formatInt(query.data.lowStockTotal)}</strong>
            </div>
            <div className="inventory-stat-card">
              <span>Lệch đối soát sổ kho</span>
              <strong>{formatInt(query.data.mismatchTotal)}</strong>
            </div>
            <div className={`inventory-stat-card ${query.data.deadAlertOutboxCount > 0 ? "is-danger" : ""}`}>
              <span>Cảnh báo lỗi hàng đợi</span>
              <strong>{formatInt(query.data.deadAlertOutboxCount)}</strong>
            </div>
          </div>

          {query.data.deadAlertOutboxCount > 0 && (
            <div className="alert" role="alert">
              <AlertTriangle size={19} aria-hidden="true" />
              <div>
                <p>
                  Có {formatInt(query.data.deadAlertOutboxCount)} thông điệp cảnh báo kho trong hàng đợi gặp lỗi xử lý (dead outbox).
                  Cần kiểm tra vận hành để tránh bỏ sót cảnh báo tồn thấp.
                </p>
              </div>
            </div>
          )}

          {query.data.lowStockTotal === 0 && query.data.mismatchTotal === 0 && query.data.deadAlertOutboxCount === 0 && (
            <p className="inventory-muted-note">Hiện không có cảnh báo tồn thấp hoặc lệch đối soát sổ kho.</p>
          )}

          {query.data.lowStockItems.length > 0 && (
            <div className="inventory-alert-group">
              <h3>Tồn thấp ({formatInt(query.data.lowStockTotal)})</h3>
              <ul className="inventory-alert-list">
                {query.data.lowStockItems.map(item => (
                  <li key={item.variantId} className="inventory-alert-item">
                    <div>
                      <strong>{item.sku}</strong> — {item.productName}{item.variantName ? ` · ${item.variantName}` : ""}
                      <p className="hint">
                        Khả dụng: {formatInt(item.available)} / Ngưỡng: {formatInt(item.lowStockThreshold)} · Tồn: {formatInt(item.onHand)} · Đang giữ: {formatInt(item.reserved)}
                      </p>
                    </div>
                    <div className="inventory-inline-actions">
                      <Link className="button secondary" href={`/inventory/${item.variantId}/movements`}>Sổ kho</Link>
                      <Link className="button secondary" href={`/inventory/receipts/new?variantId=${item.variantId}`}>Nhập thêm</Link>
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          )}

          {query.data.mismatches.length > 0 && (
            <div className="inventory-alert-group">
              <h3>Lệch đối soát sổ kho ({formatInt(query.data.mismatchTotal)})</h3>
              <ul className="inventory-alert-list">
                {query.data.mismatches.map(item => (
                  <li key={item.variantId} className="inventory-alert-item is-warning">
                    <div>
                      <strong>{item.sku}</strong> — {item.productName}{item.variantName ? ` · ${item.variantName}` : ""}
                      {!item.hasStockLevel && <span className="inventory-badge is-danger">Thiếu dòng tồn kho</span>}
                      <p className="hint">
                        Tồn hiện tại: {formatInt(item.onHand)} · Sổ kho: {formatInt(item.ledgerQuantity)} · Chênh lệch: {formatSignedDelta(item.delta)}
                      </p>
                    </div>
                    <div className="inventory-inline-actions">
                      <Link className="button secondary" href={`/inventory/${item.variantId}/movements`}>Xem sổ kho</Link>
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </>
      )}
    </section>
  );
}
