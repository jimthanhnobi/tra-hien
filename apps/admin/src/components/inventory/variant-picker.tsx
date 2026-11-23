"use client";

import { useEffect, useMemo, useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { useQuery } from "@tanstack/react-query";
import { Check, Copy, X } from "lucide-react";
import {
  InventoryApi,
  cacheVariantLabels,
  hydrateVariantLabels,
  inventoryKey,
  readCachedVariantLabels,
  toVariantLabel,
  variantLabelCache,
  type StockRow,
  type VariantLabel
} from "../../lib/inventory-api";
import { availabilityLabels, formatInt, isUuid, shortVariantId } from "../../lib/inventory-forms";
import { useAdmin } from "../providers";
import { FormError, Pending } from "../ui";

export function useHydratedVariantLabels(variantIds: readonly string[], enabled = true): Record<string, VariantLabel> {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);
  const normalizedIds = useMemo(
    () => [...new Set(variantIds.filter(isUuid).map(id => id.toLowerCase()))].sort(),
    [variantIds]
  );
  const query = useQuery({
    queryKey: [...inventoryKey, "variant-hydrate", normalizedIds],
    queryFn: ({ signal }) => hydrateVariantLabels(api, queries, normalizedIds, signal),
    enabled: enabled && normalizedIds.length > 0,
    ...variantLabelCache
  });
  return useMemo(() => {
    const fromCache = readCachedVariantLabels(queries, normalizedIds);
    return query.data ? { ...fromCache, ...query.data } : fromCache;
  }, [queries, normalizedIds, query.data]);
}

export function VariantIdentity({
  variantId,
  label
}: {
  variantId: string;
  label?: Pick<VariantLabel, "sku" | "productName" | "variantName">;
}) {
  const [copied, setCopied] = useState(false);
  if (label) {
    return (
      <div className="inventory-variant-id">
        <strong>{label.sku}</strong>
        <span>{label.productName}{label.variantName ? ` · ${label.variantName}` : ""}</span>
      </div>
    );
  }
  return (
    <div className="inventory-variant-id">
      <code title={variantId}>{shortVariantId(variantId)}</code>
      <button
        type="button"
        className="inventory-copy-button"
        aria-label={`Sao chép mã biến thể ${variantId}`}
        onClick={async () => {
          try {
            await navigator.clipboard?.writeText(variantId);
            setCopied(true);
            window.setTimeout(() => setCopied(false), 1800);
          } catch {
            setCopied(false);
          }
        }}
      >
        {copied ? <><Check size={14} aria-hidden="true" /> Đã sao chép</> : <><Copy size={14} aria-hidden="true" /> Sao chép mã</>}
      </button>
    </div>
  );
}

interface VariantPickerProps {
  open: boolean;
  mode?: "single" | "multi";
  excludedIds?: readonly string[];
  canWrite: boolean;
  onSelect: (items: StockRow[]) => void;
  onClose: () => void;
  title?: string;
}

export function VariantPicker(props: VariantPickerProps) {
  // Unmount dialog state whenever closed so search, pagination, and selections never leak across reopens.
  if (!props.open) return null;
  return <VariantPickerDialog {...props} />;
}

function VariantPickerDialog({
  mode = "single",
  excludedIds = [],
  canWrite,
  onSelect,
  onClose,
  title = "Chọn biến thể"
}: VariantPickerProps) {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);
  const [search, setSearch] = useState("");
  const [q, setQ] = useState("");
  const [page, setPage] = useState(1);
  const [selectedMap, setSelectedMap] = useState<Record<string, StockRow>>({});
  const excluded = useMemo(() => new Set(excludedIds.map(id => id.toLowerCase())), [excludedIds]);

  function resetAndClose() {
    setSelectedMap({});
    setSearch("");
    setQ("");
    setPage(1);
    onClose();
  }

  const searchPending = search.trim() !== q;
  useEffect(() => {
    if (!searchPending) return;
    const timer = window.setTimeout(() => {
      setQ(search.trim());
      setPage(1);
    }, 250);
    return () => window.clearTimeout(timer);
  }, [search, searchPending]);

  const query = useQuery({
    queryKey: [...inventoryKey, "picker", q, page],
    queryFn: async ({ signal }) => {
      const result = await api.listStock({ q: q || undefined, page, pageSize: 20 }, signal);
      if (!signal.aborted) cacheVariantLabels(queries, result.items.map(toVariantLabel));
      return result;
    },
    refetchOnWindowFocus: false,
    refetchOnReconnect: false
  });

  const chosenList = Object.values(selectedMap).filter(item => !excluded.has(item.variantId.toLowerCase()));
  const totalItems = Number(query.data?.totalItems ?? 0);

  return (
    <Dialog.Root open onOpenChange={value => { if (!value) resetAndClose(); }}>
      <Dialog.Portal>
        <Dialog.Overlay className="overlay" />
        <Dialog.Content className="dialog catalog catalog-product-dialog inventory-picker-dialog">
          <Dialog.Title>{title}</Dialog.Title>
          <Dialog.Description>
            Tìm biến thể theo SKU hoặc tên sản phẩm. Không hiển thị giá vốn tại hộp thoại này.
          </Dialog.Description>
          <div className="inventory-filters">
            <label className="product-field">
              Tìm theo SKU hoặc tên sản phẩm
              <input
                autoFocus
                type="search"
                maxLength={200}
                value={search}
                onChange={event => setSearch(event.target.value)}
                placeholder="Nhập SKU hoặc tên…"
              />
            </label>
          </div>
          {mode === "multi" && <p role="status">Đã chọn {chosenList.length} biến thể.</p>}
          <div className="catalog-product-scroll">
            {searchPending || query.isPending ? (
              <Pending>Đang tìm biến thể…</Pending>
            ) : query.isError ? (
              <div>
                <FormError error={query.error} />
                <button type="button" className="button secondary" onClick={() => void query.refetch()}>
                  Thử tìm lại
                </button>
              </div>
            ) : (
              <>
                {!query.data.items.length && <p role="status">Không tìm thấy biến thể phù hợp.</p>}
                <ul className="catalog-product-results">
                  {query.data.items.map(item => {
                    const key = item.variantId.toLowerCase();
                    const isExcluded = excluded.has(key);
                    const isChecked = Boolean(selectedMap[key]);
                    return (
                      <li key={item.variantId} className="inventory-picker-item">
                        {mode === "multi" ? (
                          <label className="catalog-product-choice">
                            <input
                              type="checkbox"
                              aria-label={`Chọn ${item.sku} - ${item.productName}`}
                              checked={isExcluded || isChecked}
                              disabled={isExcluded}
                              onChange={event => {
                                setSelectedMap(prev => {
                                  const next = { ...prev };
                                  if (event.target.checked) next[key] = item;
                                  else delete next[key];
                                  return next;
                                });
                              }}
                            />
                            <div className="inventory-picker-meta">
                              <strong>{item.sku}</strong>
                              <span>{item.productName}{item.variantName ? ` · ${item.variantName}` : ""}</span>
                              <small>
                                {canWrite
                                  ? `Tồn: ${formatInt(item.onHand)} · Khả dụng: ${formatInt(item.available)}`
                                  : availabilityLabels[item.availability] ?? item.availability}
                                {isExcluded ? " · Đã có trong danh sách" : ""}
                              </small>
                            </div>
                          </label>
                        ) : (
                          <div className="inventory-picker-single">
                            <div className="inventory-picker-meta">
                              <strong>{item.sku}</strong>
                              <span>{item.productName}{item.variantName ? ` · ${item.variantName}` : ""}</span>
                              <small>
                                {canWrite
                                  ? `Tồn: ${formatInt(item.onHand)} · Khả dụng: ${formatInt(item.available)}`
                                  : availabilityLabels[item.availability] ?? item.availability}
                              </small>
                            </div>
                            <button
                              type="button"
                              className="button secondary"
                              disabled={isExcluded}
                              onClick={() => {
                                if (excluded.has(key)) return;
                                cacheVariantLabels(queries, [toVariantLabel(item)]);
                                onSelect([item]);
                                resetAndClose();
                              }}
                            >
                              {isExcluded ? "Đã chọn" : "Chọn"}
                            </button>
                          </div>
                        )}
                      </li>
                    );
                  })}
                </ul>
                <div className="catalog-actions">
                  <button type="button" className="button secondary" disabled={page <= 1} onClick={() => setPage(p => p - 1)}>
                    Trang trước
                  </button>
                  <span>Trang {page} · {totalItems} biến thể</span>
                  <button type="button" className="button secondary" disabled={page * 20 >= totalItems} onClick={() => setPage(p => p + 1)}>
                    Trang sau
                  </button>
                </div>
              </>
            )}
          </div>
          <div className="dialog-actions">
            <button type="button" className="button secondary" onClick={resetAndClose}>Đóng</button>
            {mode === "multi" && (
              <button
                type="button"
                className="button"
                disabled={chosenList.length === 0}
                onClick={() => {
                  const validItems = chosenList.filter(item => !excluded.has(item.variantId.toLowerCase()));
                  if (validItems.length > 0) {
                    cacheVariantLabels(queries, validItems.map(toVariantLabel));
                    onSelect(validItems);
                  }
                  resetAndClose();
                }}
              >
                Thêm {chosenList.length} biến thể
              </button>
            )}
          </div>
          <Dialog.Close className="close-button" aria-label="Đóng hộp thoại chọn biến thể">
            <X size={20} />
          </Dialog.Close>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
