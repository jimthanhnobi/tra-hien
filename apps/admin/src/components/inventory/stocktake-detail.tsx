"use client";

import { useMemo, useRef, useState } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle } from "lucide-react";
import { ApiError, isConcurrencyConflict } from "../../lib/api";
import {
  InventoryApi,
  inventoryAlertsKey,
  inventoryStockKey,
  inventoryStocktakeKey,
  inventoryStocktakesKey,
  type StocktakeView
} from "../../lib/inventory-api";
import {
  extractLineErrors,
  formatInt,
  formatSignedDelta,
  formatVnDateTime,
  hasStaleStocktakeError,
  isInvalidStatusTransition,
  parseCountedQty,
  stocktakeDelta,
  stocktakeStatusLabels,
  type InventoryPermissions
} from "../../lib/inventory-forms";
import { useProductNavigationGuard } from "../products/product-navigation";
import { useAdmin } from "../providers";
import { Confirmation, Feedback, FormError, Pending } from "../ui";
import { VariantIdentity, VariantPicker, useHydratedVariantLabels } from "./variant-picker";

export function StocktakeDetail({ id, perms }: { id: string; perms: InventoryPermissions }) {
  const { api: admin } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);
  const [reloadNonce, setReloadNonce] = useState(0);

  const query = useQuery({
    queryKey: inventoryStocktakeKey(id),
    queryFn: ({ signal }) => api.getStocktake(id, signal),
    refetchOnWindowFocus: false
  });

  if (query.isPending) return <Pending>Đang tải phiên kiểm kê…</Pending>;
  if (query.isError) {
    if (query.error instanceof ApiError && query.error.status === 404) {
      return (
        <section className="empty-state">
          <h2>Không tìm thấy phiên kiểm kê</h2>
          <p>Phiên kiểm kê không tồn tại hoặc đường dẫn không hợp lệ.</p>
          <Link className="button" href="/inventory/stocktakes">Về danh sách kiểm kê</Link>
        </section>
      );
    }
    return (
      <div>
        <FormError error={query.error} />
        <button type="button" className="button secondary" onClick={() => void query.refetch()}>Thử tải lại</button>
      </div>
    );
  }

  return (
    <StocktakeDetailBody
      key={`${query.data.document.id}:${reloadNonce}`}
      initialDoc={query.data}
      perms={perms}
      onReloadLatest={async () => {
        const result = await query.refetch();
        if (result.data) setReloadNonce(n => n + 1);
      }}
    />
  );
}

function StocktakeDetailBody({
  initialDoc,
  perms,
  onReloadLatest
}: {
  initialDoc: StocktakeView;
  perms: InventoryPermissions;
  onReloadLatest: () => Promise<void>;
}) {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);

  const [doc, setDoc] = useState<StocktakeView>(initialDoc);
  const [selectedVariantId, setSelectedVariantId] = useState("");
  const [countedQty, setCountedQty] = useState("");
  const [qtyError, setQtyError] = useState("");
  const [pickerOpen, setPickerOpen] = useState(false);
  const [apiError, setApiError] = useState<unknown>();
  const [feedback, setFeedback] = useState("");
  const [staleConflict, setStaleConflict] = useState(false);
  const [pendingAction, setPendingAction] = useState<"count" | "submit" | "approve" | "cancel" | "reload" | null>(null);
  const busy = useRef(false);

  const status = doc.document.status;
  const isCounting = status === "counting";
  const isSubmitted = status === "submitted";
  const canCancel = isCounting || isSubmitted;
  const lineErrors = useMemo(() => extractLineErrors(apiError), [apiError]);
  const hasStaleLines = hasStaleStocktakeError(apiError);
  const statusChanged = isInvalidStatusTransition(apiError);

  const allVariantIds = useMemo(
    () => [...doc.lines.map(l => l.variantId), ...(selectedVariantId ? [selectedVariantId] : [])],
    [doc.lines, selectedVariantId]
  );
  const labels = useHydratedVariantLabels(allVariantIds, perms.canRead);

  const countDirty = isCounting && Boolean(selectedVariantId || countedQty.trim());
  useProductNavigationGuard(countDirty, pendingAction !== null);

  async function handleSaveCount(event: React.FormEvent) {
    event.preventDefault();
    if (busy.current || staleConflict || !isCounting) return;
    setQtyError("");
    setApiError(undefined);
    setFeedback("");

    if (!selectedVariantId) {
      setQtyError("Vui lòng chọn biến thể để nhập số đếm.");
      return;
    }
    let parsedQty: number;
    try {
      parsedQty = parseCountedQty(countedQty);
    } catch (e) {
      setQtyError(e instanceof Error ? e.message : "Số đếm không hợp lệ.");
      return;
    }

    // Serialize count writes per session so rowVersion advances cleanly without self-conflict (RISK-01).
    busy.current = true;
    setPendingAction("count");
    try {
      const updated = await api.countStocktakeLine(doc.document.id, selectedVariantId, doc.document.rowVersion, parsedQty);
      setDoc(updated);
      queries.setQueryData(inventoryStocktakeKey(updated.document.id), updated);
      setSelectedVariantId("");
      setCountedQty("");
      setFeedback("Đã lưu số đếm kiểm kê.");
    } catch (error) {
      setApiError(error);
      if (isConcurrencyConflict(error)) setStaleConflict(true);
    } finally {
      busy.current = false;
      setPendingAction(null);
    }
  }

  async function handleTransition(action: "submit" | "approve" | "cancel") {
    if (busy.current || staleConflict) return;
    setApiError(undefined);
    setFeedback("");
    busy.current = true;
    setPendingAction(action);
    try {
      const updated = action === "submit"
        ? await api.submitStocktake(doc.document.id, doc.document.rowVersion)
        : action === "approve"
          ? await api.approveStocktake(doc.document.id, doc.document.rowVersion)
          : await api.cancelStocktake(doc.document.id, doc.document.rowVersion);
      setDoc(updated);
      queries.setQueryData(inventoryStocktakeKey(updated.document.id), updated);
      void queries.invalidateQueries({ queryKey: [...inventoryStocktakesKey, "list"] });
      if (action === "approve") {
        void queries.invalidateQueries({ queryKey: inventoryStockKey });
        void queries.invalidateQueries({ queryKey: inventoryAlertsKey });
      }
      setFeedback(
        action === "submit"
          ? `Đã gửi duyệt phiên kiểm kê ${updated.document.code}.`
          : action === "approve"
            ? `Đã duyệt phiên kiểm kê ${updated.document.code} và áp chênh lệch vào tồn kho.`
            : `Đã hủy phiên kiểm kê ${updated.document.code}.`
      );
    } catch (error) {
      setApiError(error);
      if (isConcurrencyConflict(error)) setStaleConflict(true);
    } finally {
      busy.current = false;
      setPendingAction(null);
    }
  }

  async function handleReload() {
    if (busy.current) return;
    busy.current = true;
    setPendingAction("reload");
    try {
      await onReloadLatest();
    } finally {
      busy.current = false;
      setPendingAction(null);
    }
  }

  return (
    <section className="inventory-panel" aria-labelledby="stocktake-detail-heading">
      <div className="inventory-section-header">
        <div>
          <Link className="product-link-button" href="/inventory/stocktakes">← Danh sách kiểm kê</Link>
          <h2 id="stocktake-detail-heading">Phiên kiểm kê {doc.document.code}</h2>
          <div className="inventory-badges">
            <span className={`inventory-badge ${status === "approved" ? "is-ok" : status === "cancelled" ? "is-muted" : "is-warning"}`}>
              {stocktakeStatusLabels[status] ?? status}
            </span>
            <span className="hint">Tạo lúc {formatVnDateTime(doc.document.createdAt)}</span>
            {doc.document.submittedAt && <span className="hint">Gửi duyệt: {formatVnDateTime(doc.document.submittedAt)}</span>}
            {doc.document.approvedAt && <span className="hint">Duyệt lúc: {formatVnDateTime(doc.document.approvedAt)}</span>}
          </div>
          {doc.document.note && <p className="hint">Ghi chú: {doc.document.note}</p>}
        </div>
      </div>

      {feedback && <Feedback>{feedback}</Feedback>}
      <FormError error={apiError} />

      {hasStaleLines && (
        <div className="alert" role="alert">
          <AlertTriangle size={19} aria-hidden="true" />
          <div>
            <p>
              Một hoặc nhiều dòng có số đếm đã lỗi thời vì có phiên kiểm kê khác duyệt trước.
              Vui lòng hủy phiên kiểm kê hiện tại và tạo phiên mới để đếm lại.
            </p>
          </div>
        </div>
      )}

      {(staleConflict || statusChanged) && (
        <div className="inventory-conflict-bar" role="status">
          <p>
            {staleConflict
              ? "Phiên kiểm kê đã thay đổi ở phiên làm việc khác. Số liệu bạn đang nhập vẫn được giữ nguyên trên màn hình."
              : "Trạng thái phiên kiểm kê đã thay đổi. Hãy tải lại phiên bản mới nhất."}
          </p>
          <button
            type="button"
            className="button secondary"
            disabled={pendingAction !== null}
            onClick={() => void handleReload()}
          >
            {pendingAction === "reload" ? "Đang tải…" : "Bỏ thay đổi và tải phiên bản mới"}
          </button>
        </div>
      )}

      {isCounting && (
        <form className="catalog-form inventory-form-card" onSubmit={handleSaveCount} noValidate>
          <h3>Ghi nhận số đếm biến thể</h3>
          <fieldset className="catalog-fieldset" disabled={pendingAction !== null || staleConflict}>
            <div className="product-field">
              <span className="inventory-field-label">Biến thể cần đếm</span>
              <div className="inventory-selected-box">
                {selectedVariantId ? (
                  <VariantIdentity variantId={selectedVariantId} label={labels[selectedVariantId.toLowerCase()]} />
                ) : (
                  <span className="hint">Chưa chọn biến thể.</span>
                )}
                <button type="button" className="button secondary" onClick={() => setPickerOpen(true)}>
                  {selectedVariantId ? "Đổi biến thể" : "Chọn biến thể"}
                </button>
              </div>
            </div>

            <div className="product-field">
              <label htmlFor="stocktake-counted-qty">Số đếm thực tế (nguyên ≥ 0)</label>
              <input
                id="stocktake-counted-qty"
                type="text"
                inputMode="numeric"
                value={countedQty}
                aria-invalid={Boolean(qtyError)}
                onChange={event => setCountedQty(event.target.value)}
                placeholder="Nhập số lượng đếm được…"
              />
              {qtyError && <p className="field-error" role="alert">{qtyError}</p>}
            </div>
          </fieldset>

          <div className="catalog-actions">
            <button type="submit" className="button" disabled={pendingAction !== null || staleConflict}>
              {pendingAction === "count" ? "Đang lưu…" : "Lưu số đếm"}
            </button>
          </div>
        </form>
      )}

      <div className="inventory-lines-section">
        <h3>Các dòng đã đếm ({doc.lines.length})</h3>
        <div className="table-scroll" role="region" aria-label="Bảng các dòng kiểm kê" tabIndex={0}>
          <table>
            <caption>Danh sách biến thể đã đếm trong phiên {doc.document.code}</caption>
            <thead>
              <tr>
                <th scope="col">Biến thể</th>
                <th scope="col">Tồn hệ thống lúc đếm</th>
                <th scope="col">Thực đếm</th>
                <th scope="col">Chênh lệch</th>
                <th scope="col">Thời điểm đếm (VN)</th>
                {isCounting && <th scope="col">Thao tác</th>}
              </tr>
            </thead>
            <tbody>
              {doc.lines.length === 0 ? (
                <tr><td colSpan={isCounting ? 6 : 5} className="table-empty">Chưa có biến thể nào được đếm.</td></tr>
              ) : (
                doc.lines.map(line => {
                  const key = line.variantId.toLowerCase();
                  const delta = stocktakeDelta(line.countedQty, line.systemQtyAtCount);
                  const lineErr = lineErrors[key];
                  return (
                    <tr key={line.variantId} className={lineErr ? "inventory-row-error" : undefined}>
                      <td>
                        <VariantIdentity variantId={line.variantId} label={labels[key]} />
                        {lineErr && (
                          <div className="inventory-line-alert" role="alert">
                            {lineErr.messages.map((msg, i) => <p key={i}>{msg}</p>)}
                          </div>
                        )}
                      </td>
                      <td>{formatInt(line.systemQtyAtCount)}</td>
                      <td><strong>{formatInt(line.countedQty)}</strong></td>
                      <td>
                        <strong className={delta > 0 ? "inventory-delta-pos" : delta < 0 ? "inventory-delta-neg" : undefined}>
                          {formatSignedDelta(delta)}
                        </strong>
                      </td>
                      <td>{formatVnDateTime(line.countedAt)}</td>
                      {isCounting && (
                        <td>
                          <button
                            type="button"
                            className="button secondary"
                            disabled={pendingAction !== null || staleConflict}
                            onClick={() => {
                              setSelectedVariantId(line.variantId);
                              setCountedQty(String(line.countedQty));
                              setQtyError("");
                            }}
                          >
                            Đếm lại
                          </button>
                        </td>
                      )}
                    </tr>
                  );
                })
              )}
            </tbody>
          </table>
        </div>
      </div>

      {(isCounting || isSubmitted) && (
        <div className="catalog-actions inventory-form-actions">
          {isCounting && (
            <Confirmation
              title={`Gửi duyệt phiên kiểm kê ${doc.document.code}?`}
              confirmLabel="Xác nhận gửi duyệt"
              pending={pendingAction === "submit"}
              trigger={
                <button
                  type="button"
                  className="button"
                  disabled={pendingAction !== null || staleConflict || doc.lines.length === 0}
                >
                  Gửi duyệt
                </button>
              }
              onConfirm={async () => {
                await handleTransition("submit");
              }}
            >
              Sau khi gửi duyệt, bạn không thể chỉnh sửa thêm số đếm trên phiên này.
            </Confirmation>
          )}

          {isSubmitted && perms.canApproveStocktake && (
            <Confirmation
              title={`Duyệt phiên kiểm kê ${doc.document.code}?`}
              confirmLabel="Xác nhận duyệt kiểm kê"
              pending={pendingAction === "approve"}
              trigger={
                <button
                  type="button"
                  className="button"
                  disabled={pendingAction !== null || staleConflict}
                >
                  Duyệt kiểm kê
                </button>
              }
              onConfirm={async () => {
                await handleTransition("approve");
              }}
            >
              Hệ thống sẽ áp toàn bộ chênh lệch kiểm kê vào tồn kho hiện tại và ghi bút toán sổ kho. Thao tác này không thể hoàn tác.
            </Confirmation>
          )}

          {isSubmitted && !perms.canApproveStocktake && (
            <p className="inventory-muted-note" role="status">
              Chờ quản lý có quyền <code>inventory.approve</code> duyệt phiên kiểm kê này.
            </p>
          )}

          {canCancel && (
            <Confirmation
              title={`Hủy phiên kiểm kê ${doc.document.code}?`}
              confirmLabel="Xác nhận hủy phiên"
              pending={pendingAction === "cancel"}
              trigger={
                <button
                  type="button"
                  className="button secondary"
                  disabled={pendingAction !== null || staleConflict}
                >
                  Hủy phiên
                </button>
              }
              onConfirm={async () => {
                await handleTransition("cancel");
              }}
            >
              Phiên kiểm kê sẽ chuyển sang trạng thái Đã hủy và không làm thay đổi tồn kho.
            </Confirmation>
          )}
        </div>
      )}

      <VariantPicker
        open={pickerOpen}
        mode="single"
        canWrite={perms.canWrite}
        title="Chọn biến thể kiểm kê"
        onClose={() => setPickerOpen(false)}
        onSelect={items => {
          if (items[0]) {
            setSelectedVariantId(items[0].variantId);
            const existing = doc.lines.find(l => l.variantId.toLowerCase() === items[0].variantId.toLowerCase());
            setCountedQty(existing ? String(existing.countedQty) : "");
            setQtyError("");
          }
        }}
      />
    </section>
  );
}
