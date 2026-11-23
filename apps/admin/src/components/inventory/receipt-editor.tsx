"use client";

import { useMemo, useRef, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { ApiError, isConcurrencyConflict } from "../../lib/api";
import {
  InventoryApi,
  inventoryAlertsKey,
  inventoryReceiptKey,
  inventoryReceiptsKey,
  inventoryStockKey,
  type ReceiptView
} from "../../lib/inventory-api";
import {
  buildCreateReceiptPayload,
  buildEditReceiptPayload,
  extractLineErrors,
  formatCostVnd,
  formatInt,
  formatVnDateTime,
  isInvalidStatusTransition,
  isUuid,
  receiptStatusLabels,
  resolvePostIdempotencyKey,
  toVnDateTimeLocal,
  type InventoryPermissions,
  type PostIdempotencyState,
  type ReceiptFormDraft,
  type ReceiptValidationErrors
} from "../../lib/inventory-forms";
import { useProductNavigationGuard } from "../products/product-navigation";
import { useAdmin } from "../providers";
import { Confirmation, Feedback, FormError, Pending } from "../ui";
import { VariantIdentity, VariantPicker, useHydratedVariantLabels } from "./variant-picker";

function receiptToDraft(doc: ReceiptView, canReadCosts: boolean): ReceiptFormDraft {
  return {
    supplierName: doc.document.supplierName ?? "",
    receivedAt: toVnDateTimeLocal(doc.document.receivedAt),
    note: doc.document.note ?? "",
    lines: doc.lines.map(line => ({
      variantId: line.variantId,
      quantity: String(line.quantity),
      unitCostVnd: canReadCosts && line.unitCostVnd !== null && line.unitCostVnd !== undefined ? String(line.unitCostVnd) : "",
      isExisting: true
    }))
  };
}

function draftsEqual(a: ReceiptFormDraft, b: ReceiptFormDraft): boolean {
  if (a.supplierName !== b.supplierName || a.receivedAt !== b.receivedAt || a.note !== b.note) return false;
  if (a.lines.length !== b.lines.length) return false;
  return a.lines.every((line, i) => {
    const other = b.lines[i];
    return line.variantId === other.variantId
      && line.quantity === other.quantity
      && line.unitCostVnd === other.unitCostVnd
      && line.isExisting === other.isExisting;
  });
}

export function ReceiptCreate({ perms, initialVariantId }: { perms: InventoryPermissions; initialVariantId?: string }) {
  const initialDraft: ReceiptFormDraft = {
    supplierName: "",
    receivedAt: "",
    note: "",
    lines: initialVariantId && isUuid(initialVariantId)
      ? [{ variantId: initialVariantId, quantity: "1", unitCostVnd: "", isExisting: false }]
      : []
  };
  return <ReceiptEditorBody mode="create" perms={perms} initialDraft={initialDraft} />;
}

export function ReceiptDetail({ id, perms }: { id: string; perms: InventoryPermissions }) {
  const { api: admin } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);
  const [reloadNonce, setReloadNonce] = useState(0);
  const query = useQuery({
    queryKey: inventoryReceiptKey(id),
    queryFn: ({ signal }) => api.getReceipt(id, signal),
    refetchOnWindowFocus: false
  });

  if (query.isPending) return <Pending>Đang tải phiếu nhập…</Pending>;
  if (query.isError) {
    if (query.error instanceof ApiError && query.error.status === 404) {
      return (
        <section className="empty-state">
          <h2>Không tìm thấy phiếu nhập</h2>
          <p>Phiếu nhập không tồn tại hoặc đường dẫn không hợp lệ.</p>
          <Link className="button" href="/inventory/receipts">Về danh sách phiếu nhập</Link>
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
    <ReceiptEditorBody
      key={`${query.data.document.id}:${reloadNonce}`}
      mode="detail"
      perms={perms}
      initialDoc={query.data}
      initialDraft={receiptToDraft(query.data, perms.canReadCosts)}
      onReloadLatest={async () => {
        const result = await query.refetch();
        if (result.data) setReloadNonce(n => n + 1);
      }}
    />
  );
}

function ReceiptEditorBody({
  mode,
  perms,
  initialDoc,
  initialDraft,
  onReloadLatest
}: {
  mode: "create" | "detail";
  perms: InventoryPermissions;
  initialDoc?: ReceiptView;
  initialDraft: ReceiptFormDraft;
  onReloadLatest?: () => Promise<void>;
}) {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);
  const router = useRouter();

  const [doc, setDoc] = useState<ReceiptView | undefined>(initialDoc);
  const [baseline, setBaseline] = useState<ReceiptFormDraft>(initialDraft);
  const [draft, setDraft] = useState<ReceiptFormDraft>(initialDraft);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<ReceiptValidationErrors>({ lineFields: {} });
  const [apiError, setApiError] = useState<unknown>();
  const [feedback, setFeedback] = useState("");
  const [staleConflict, setStaleConflict] = useState(false);
  const [pendingAction, setPendingAction] = useState<"save" | "post" | "cancel" | "reload" | null>(null);
  const busy = useRef(false);
  const postIdemRef = useRef<PostIdempotencyState | null>(null);

  const isDraft = mode === "create" || doc?.document.status === "draft";
  const dirty = isDraft && !draftsEqual(draft, baseline);
  const lineErrors = useMemo(() => extractLineErrors(apiError), [apiError]);
  const statusChanged = isInvalidStatusTransition(apiError);

  const variantIds = useMemo(() => draft.lines.map(l => l.variantId), [draft.lines]);
  const labels = useHydratedVariantLabels(variantIds, perms.canRead);

  useProductNavigationGuard(dirty, pendingAction !== null);

  async function handleSave(event: React.FormEvent) {
    event.preventDefault();
    if (busy.current || staleConflict) return;
    setFeedback("");
    setApiError(undefined);

    if (mode === "create") {
      const built = buildCreateReceiptPayload(draft);
      if (built.errors || !built.payload) {
        setFieldErrors(built.errors ?? { lineFields: {} });
        return;
      }
      setFieldErrors({ lineFields: {} });
      busy.current = true;
      setPendingAction("save");
      try {
        const created = await api.createReceipt(built.payload);
        queries.setQueryData(inventoryReceiptKey(created.document.id), created);
        void queries.invalidateQueries({ queryKey: [...inventoryReceiptsKey, "list"] });
        router.replace(`/inventory/receipts/${created.document.id}`);
      } catch (error) {
        setApiError(error);
      } finally {
        busy.current = false;
        setPendingAction(null);
      }
      return;
    }

    if (!doc) return;
    const built = buildEditReceiptPayload(doc.document.rowVersion, draft, perms.canReadCosts);
    if (built.errors || !built.payload) {
      setFieldErrors(built.errors ?? { lineFields: {} });
      return;
    }
    setFieldErrors({ lineFields: {} });
    busy.current = true;
    setPendingAction("save");
    try {
      const updated = await api.editReceipt(doc.document.id, built.payload);
      const nextDraft = receiptToDraft(updated, perms.canReadCosts);
      setDoc(updated);
      setBaseline(nextDraft);
      setDraft(nextDraft);
      queries.setQueryData(inventoryReceiptKey(updated.document.id), updated);
      void queries.invalidateQueries({ queryKey: [...inventoryReceiptsKey, "list"] });
      setFeedback("Đã lưu bản nháp phiếu nhập.");
    } catch (error) {
      setApiError(error);
      if (isConcurrencyConflict(error)) setStaleConflict(true);
    } finally {
      busy.current = false;
      setPendingAction(null);
    }
  }

  async function handlePost() {
    if (busy.current || staleConflict || !doc) return;
    setFeedback("");
    setApiError(undefined);
    const idem = resolvePostIdempotencyKey(postIdemRef.current, doc.document.id, doc.document.rowVersion);
    postIdemRef.current = idem;
    busy.current = true;
    setPendingAction("post");
    try {
      const posted = await api.postReceipt(doc.document.id, doc.document.rowVersion, idem.key);
      const nextDoc: ReceiptView = {
        ...doc,
        document: {
          ...doc.document,
          status: posted.status,
          rowVersion: posted.rowVersion
        }
      };
      setDoc(nextDoc);
      queries.setQueryData(inventoryReceiptKey(doc.document.id), nextDoc);
      void queries.invalidateQueries({ queryKey: inventoryReceiptsKey });
      void queries.invalidateQueries({ queryKey: inventoryStockKey });
      void queries.invalidateQueries({ queryKey: inventoryAlertsKey });
      setFeedback(`Đã chốt phiếu nhập ${doc.document.code} vào sổ kho.`);
    } catch (error) {
      setApiError(error);
      if (isConcurrencyConflict(error)) setStaleConflict(true);
    } finally {
      busy.current = false;
      setPendingAction(null);
    }
  }

  async function handleCancel() {
    if (busy.current || staleConflict || !doc) return;
    setFeedback("");
    setApiError(undefined);
    busy.current = true;
    setPendingAction("cancel");
    try {
      const cancelled = await api.cancelReceipt(doc.document.id, doc.document.rowVersion);
      const nextDraft = receiptToDraft(cancelled, perms.canReadCosts);
      setDoc(cancelled);
      setBaseline(nextDraft);
      setDraft(nextDraft);
      queries.setQueryData(inventoryReceiptKey(cancelled.document.id), cancelled);
      void queries.invalidateQueries({ queryKey: [...inventoryReceiptsKey, "list"] });
      setFeedback(`Đã hủy phiếu nhập nháp ${cancelled.document.code}.`);
    } catch (error) {
      setApiError(error);
      if (isConcurrencyConflict(error)) setStaleConflict(true);
    } finally {
      busy.current = false;
      setPendingAction(null);
    }
  }

  async function handleReload() {
    if (!onReloadLatest || busy.current) return;
    busy.current = true;
    setPendingAction("reload");
    try {
      await onReloadLatest();
    } finally {
      busy.current = false;
      setPendingAction(null);
    }
  }

  const totalReceiptVnd = perms.canReadCosts
    ? draft.lines.reduce((sum, line) => {
      const qty = Number(line.quantity);
      const cost = Number(line.unitCostVnd.replaceAll(".", ""));
      if (!Number.isFinite(qty) || !Number.isFinite(cost) || line.unitCostVnd.trim() === "") return sum;
      return sum + qty * cost;
    }, 0)
    : null;

  return (
    <section className="inventory-panel" aria-labelledby="receipt-editor-heading">
      <div className="inventory-section-header">
        <div>
          <Link className="product-link-button" href="/inventory/receipts">← Danh sách phiếu nhập</Link>
          <h2 id="receipt-editor-heading">
            {mode === "create" ? "Tạo phiếu nhập kho" : `Phiếu nhập ${doc?.document.code ?? ""}`}
          </h2>
          {doc && (
            <div className="inventory-badges">
              <span className={`inventory-badge ${doc.document.status === "posted" ? "is-ok" : doc.document.status === "cancelled" ? "is-muted" : "is-warning"}`}>
                {receiptStatusLabels[doc.document.status] ?? doc.document.status}
              </span>
              <span className="hint">Tạo lúc {formatVnDateTime(doc.document.createdAt)}</span>
              {doc.document.postedAt && <span className="hint">Chốt lúc {formatVnDateTime(doc.document.postedAt)}</span>}
            </div>
          )}
        </div>
      </div>

      {feedback && <Feedback>{feedback}</Feedback>}
      <FormError error={apiError} />

      {(staleConflict || statusChanged) && onReloadLatest && (
        <div className="inventory-conflict-bar" role="status">
          <p>
            {staleConflict
              ? "Phiếu nhập đã được cập nhật ở phiên khác. Dữ liệu bạn đang sửa vẫn được giữ nguyên trên màn hình."
              : "Trạng thái phiếu nhập đã thay đổi. Hãy tải lại phiên bản mới nhất."}
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

      <form className="catalog-form inventory-form-card" onSubmit={handleSave} noValidate>
        <fieldset className="catalog-fieldset" disabled={!isDraft || pendingAction !== null}>
          <div className="catalog-fields">
            <div className="product-field">
              <label htmlFor="receipt-supplier">Nhà cung cấp</label>
              <input
                id="receipt-supplier"
                type="text"
                maxLength={200}
                value={draft.supplierName}
                aria-invalid={Boolean(fieldErrors.supplierName)}
                onChange={event => setDraft(prev => ({ ...prev, supplierName: event.target.value }))}
                placeholder="Tên nhà cung cấp hoặc xưởng gốm…"
              />
              {fieldErrors.supplierName && <p className="field-error">{fieldErrors.supplierName}</p>}
            </div>

            <div className="product-field">
              <label htmlFor="receipt-received-at">Ngày giờ nhận hàng (giờ VN)</label>
              <input
                id="receipt-received-at"
                type="datetime-local"
                value={draft.receivedAt}
                aria-invalid={Boolean(fieldErrors.receivedAt)}
                onChange={event => setDraft(prev => ({ ...prev, receivedAt: event.target.value }))}
              />
              {fieldErrors.receivedAt && <p className="field-error">{fieldErrors.receivedAt}</p>}
            </div>
          </div>

          <div className="product-field">
            <label htmlFor="receipt-note">Ghi chú</label>
            <textarea
              id="receipt-note"
              rows={3}
              maxLength={2000}
              value={draft.note}
              aria-invalid={Boolean(fieldErrors.note)}
              onChange={event => setDraft(prev => ({ ...prev, note: event.target.value }))}
            />
            {fieldErrors.note && <p className="field-error">{fieldErrors.note}</p>}
          </div>

          <div className="inventory-lines-header">
            <h3>Danh sách dòng nhập ({draft.lines.length})</h3>
            {isDraft && (
              <button
                type="button"
                className="button secondary"
                disabled={staleConflict}
                onClick={() => setPickerOpen(true)}
              >
                Thêm biến thể
              </button>
            )}
          </div>

          {fieldErrors.lines && <p className="field-error" role="alert">{fieldErrors.lines}</p>}

          {draft.lines.length === 0 ? (
            <p className="inventory-muted-note">Chưa có dòng biến thể nào. Hãy thêm biến thể để nhập kho.</p>
          ) : (
            <ul className="inventory-lines-list">
              {draft.lines.map(line => {
                const key = line.variantId.toLowerCase();
                const label = labels[key];
                const serverLineErr = lineErrors[key];
                const localLineErr = fieldErrors.lineFields[line.variantId];
                const showCostInput = mode === "create" || perms.canReadCosts || !line.isExisting || isDraft;
                const costRequired = mode === "create" || !line.isExisting || perms.canReadCosts;
                const lineQty = Number(line.quantity);
                const lineCost = Number(line.unitCostVnd.replaceAll(".", ""));
                const subtotal = perms.canReadCosts && line.unitCostVnd.trim() !== "" && Number.isFinite(lineQty) && Number.isFinite(lineCost)
                  ? lineQty * lineCost
                  : null;

                return (
                  <li
                    key={line.variantId}
                    className={`inventory-line-card ${(serverLineErr || localLineErr) ? "is-error" : ""}`}
                  >
                    <div className="inventory-line-top">
                      <VariantIdentity variantId={line.variantId} label={label} />
                      {isDraft && (
                        <button
                          type="button"
                          className="button secondary"
                          aria-label={`Xóa dòng ${label?.sku ?? line.variantId}`}
                          disabled={staleConflict}
                          onClick={() => setDraft(prev => ({
                            ...prev,
                            lines: prev.lines.filter(item => item.variantId !== line.variantId)
                          }))}
                        >
                          Xóa dòng
                        </button>
                      )}
                    </div>

                    {isDraft ? (
                      <div className="inventory-line-inputs">
                        <div className="product-field">
                          <label htmlFor={`qty-${line.variantId}`}>Số lượng</label>
                          <input
                            id={`qty-${line.variantId}`}
                            type="text"
                            inputMode="numeric"
                            value={line.quantity}
                            aria-invalid={Boolean(localLineErr?.quantity)}
                            onChange={event => {
                              const value = event.target.value;
                              setDraft(prev => ({
                                ...prev,
                                lines: prev.lines.map(item => item.variantId === line.variantId ? { ...item, quantity: value } : item)
                              }));
                            }}
                          />
                          {localLineErr?.quantity && <p className="field-error">{localLineErr.quantity}</p>}
                        </div>

                        {showCostInput && (
                          <div className="product-field">
                            <label htmlFor={`cost-${line.variantId}`}>
                              {costRequired ? "Đơn giá vốn (VND)" : "Đơn giá vốn mới (VND, để trống để giữ giá cũ)"}
                            </label>
                            <input
                              id={`cost-${line.variantId}`}
                              type="text"
                              inputMode="numeric"
                              value={line.unitCostVnd}
                              placeholder={costRequired ? "0" : "Giữ nguyên giá cũ"}
                              aria-invalid={Boolean(localLineErr?.unitCostVnd)}
                              onChange={event => {
                                const value = event.target.value;
                                setDraft(prev => ({
                                  ...prev,
                                  lines: prev.lines.map(item => item.variantId === line.variantId ? { ...item, unitCostVnd: value } : item)
                                }));
                              }}
                            />
                            {localLineErr?.unitCostVnd && <p className="field-error">{localLineErr.unitCostVnd}</p>}
                          </div>
                        )}

                        {perms.canReadCosts && (
                          <div className="inventory-line-subtotal">
                            <span>Thành tiền</span>
                            <strong>{subtotal !== null ? formatCostVnd(subtotal) : "—"}</strong>
                          </div>
                        )}
                      </div>
                    ) : (
                      <div className="inventory-line-readonly">
                        <span>Số lượng: <strong>{formatInt(line.quantity)}</strong></span>
                        {perms.canReadCosts && (
                          <>
                            <span>Đơn giá vốn: <strong>{formatCostVnd(line.unitCostVnd)}</strong></span>
                            <span>Thành tiền: <strong>{subtotal !== null ? formatCostVnd(subtotal) : "—"}</strong></span>
                          </>
                        )}
                      </div>
                    )}

                    {serverLineErr && (
                      <div className="inventory-line-alert" role="alert">
                        {serverLineErr.messages.map((msg, i) => <p key={i}>{msg}</p>)}
                      </div>
                    )}
                  </li>
                );
              })}
            </ul>
          )}

          {perms.canReadCosts && draft.lines.length > 0 && (
            <p className="inventory-total-line">
              Tổng giá trị phiếu nhập: <strong>{formatCostVnd(totalReceiptVnd)}</strong>
            </p>
          )}
        </fieldset>

        {isDraft && (
          <div className="catalog-actions inventory-form-actions">
            <button
              type="submit"
              className="button"
              disabled={pendingAction !== null || staleConflict}
            >
              {pendingAction === "save" ? "Đang lưu…" : mode === "create" ? "Tạo phiếu nhập nháp" : "Lưu bản nháp"}
            </button>

            {mode === "detail" && doc && (
              <>
                <Confirmation
                  title={`Chốt phiếu nhập ${doc.document.code}?`}
                  confirmLabel="Xác nhận chốt phiếu"
                  pending={pendingAction === "post"}
                  trigger={
                    <button
                      type="button"
                      className="button"
                      disabled={pendingAction !== null || staleConflict || dirty || draft.lines.length === 0}
                    >
                      Chốt phiếu nhập
                    </button>
                  }
                  onConfirm={async () => {
                    await handlePost();
                  }}
                >
                  Chốt phiếu nhập sẽ cộng tồn kho và ghi sổ kho cho {draft.lines.length} dòng biến thể. Thao tác này không thể hoàn tác.
                </Confirmation>

                <Confirmation
                  title={`Hủy phiếu nhập nháp ${doc.document.code}?`}
                  confirmLabel="Xác nhận hủy phiếu"
                  pending={pendingAction === "cancel"}
                  trigger={
                    <button
                      type="button"
                      className="button secondary"
                      disabled={pendingAction !== null || staleConflict}
                    >
                      Hủy phiếu nháp
                    </button>
                  }
                  onConfirm={async () => {
                    await handleCancel();
                  }}
                >
                  Phiếu nhập nháp sẽ chuyển sang trạng thái Đã hủy và không thể chỉnh sửa hoặc chốt về sau.
                </Confirmation>
              </>
            )}

            <Link className="button secondary" href="/inventory/receipts">Quay lại</Link>
          </div>
        )}

        {isDraft && mode === "detail" && (dirty || draft.lines.length === 0) && (
          <p className="hint">
            {dirty
              ? "Hãy lưu các thay đổi trước khi chốt phiếu nhập."
              : "Cần ít nhất 1 dòng biến thể để chốt phiếu nhập."}
          </p>
        )}
      </form>

      <VariantPicker
        open={pickerOpen}
        mode="multi"
        excludedIds={variantIds}
        canWrite={perms.canWrite}
        title="Thêm biến thể vào phiếu nhập"
        onClose={() => setPickerOpen(false)}
        onSelect={items => {
          setDraft(prev => {
            const existing = new Set(prev.lines.map(l => l.variantId.toLowerCase()));
            const added = items
              .filter(item => !existing.has(item.variantId.toLowerCase()))
              .map(item => ({ variantId: item.variantId, quantity: "1", unitCostVnd: "", isExisting: false }));
            return { ...prev, lines: [...prev.lines, ...added] };
          });
        }}
      />
    </section>
  );
}
