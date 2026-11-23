"use client";

import { useMemo, useRef, useState } from "react";
import Link from "next/link";
import {
  InventoryApi,
  inventoryAlertsKey,
  inventoryStockKey,
  type AdjustmentView,
  type StockRow
} from "../../lib/inventory-api";
import {
  buildAdjustmentPayload,
  formatInt,
  formatSignedDelta,
  type AdjustmentDirection,
  type AdjustmentFieldErrors,
  type AdjustmentKind,
  type InventoryPermissions
} from "../../lib/inventory-forms";
import { useProductNavigationGuard } from "../products/product-navigation";
import { useAdmin } from "../providers";
import { Feedback, FormError } from "../ui";
import { VariantIdentity, VariantPicker } from "./variant-picker";

export function AdjustmentsPanel({ perms }: { perms: InventoryPermissions }) {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new InventoryApi(admin), [admin]);

  const [selected, setSelected] = useState<StockRow | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [type, setType] = useState<AdjustmentKind>("adjustment");
  const [direction, setDirection] = useState<AdjustmentDirection>("decrease");
  const [quantity, setQuantity] = useState("1");
  const [reason, setReason] = useState("");
  const [fieldErrors, setFieldErrors] = useState<AdjustmentFieldErrors>({});
  const [apiError, setApiError] = useState<unknown>();
  const [result, setResult] = useState<AdjustmentView | null>(null);
  const [pending, setPending] = useState(false);
  const busy = useRef(false);

  const effectiveDirection: AdjustmentDirection =
    type === "damage" || !perms.canIncreaseAdjustment ? "decrease" : direction;
  const dirty = Boolean(selected || reason.trim() || (quantity.trim() && quantity.trim() !== "1"));

  useProductNavigationGuard(dirty, pending);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (busy.current) return;
    setApiError(undefined);
    setResult(null);

    const built = buildAdjustmentPayload(
      {
        variantId: selected?.variantId ?? "",
        type,
        direction: effectiveDirection,
        quantity,
        reason
      },
      perms.canIncreaseAdjustment
    );
    if (built.errors || !built.payload) {
      setFieldErrors(built.errors ?? {});
      return;
    }

    setFieldErrors({});
    busy.current = true;
    setPending(true);
    try {
      const saved = await api.createAdjustment(built.payload);
      setResult(saved);
      setQuantity("1");
      setReason("");
      void queries.invalidateQueries({ queryKey: inventoryStockKey });
      void queries.invalidateQueries({ queryKey: inventoryAlertsKey });
    } catch (error) {
      // Preserve every form input on 400/403/404/409/422 so the user can inspect or correct without retyping.
      setApiError(error);
    } finally {
      busy.current = false;
      setPending(false);
    }
  }

  return (
    <section className="inventory-panel" aria-labelledby="inventory-adjustments-heading">
      <div className="inventory-section-header">
        <div>
          <h2 id="inventory-adjustments-heading">Điều chỉnh tồn kho &amp; báo vỡ hỏng</h2>
          <p className="hint">
            Mọi điều chỉnh đều bắt buộc ghi lý do (tối thiểu 3 ký tự) và tự động tạo bút toán sổ kho.
          </p>
        </div>
      </div>

      {result && (
        <Feedback>
          <p>
            Đã ghi nhận biến động <strong>{formatSignedDelta(result.quantityDelta)}</strong>. Tồn kho sau điều chỉnh:{" "}
            <strong>{formatInt(result.onHandAfter)}</strong>.{" "}
            <Link className="product-link-button" href={`/inventory/${result.variantId}/movements`}>
              Xem sổ kho biến thể
            </Link>
          </p>
        </Feedback>
      )}

      <FormError error={apiError} />

      <form className="catalog-form inventory-form-card" onSubmit={handleSubmit} noValidate>
        <fieldset className="catalog-fieldset" disabled={pending}>
          <div className="product-field">
            <span className="inventory-field-label">Biến thể</span>
            <div className="inventory-selected-box">
              {selected ? (
                <div className="inventory-selected-info">
                  <VariantIdentity variantId={selected.variantId} label={selected} />
                  <span className="hint">
                    Tồn: {formatInt(selected.onHand)} · Đang giữ: {formatInt(selected.reserved)} · Khả dụng: {formatInt(selected.available)}
                  </span>
                </div>
              ) : (
                <span className="hint">Chưa chọn biến thể.</span>
              )}
              <button type="button" className="button secondary" onClick={() => setPickerOpen(true)}>
                {selected ? "Đổi biến thể" : "Chọn biến thể"}
              </button>
            </div>
            {fieldErrors.variantId && <p className="field-error" role="alert">{fieldErrors.variantId}</p>}
          </div>

          <div className="catalog-fields">
            <div className="product-field">
              <label htmlFor="adjustment-type">Loại nghiệp vụ</label>
              <select
                id="adjustment-type"
                value={type}
                onChange={event => {
                  const next = event.target.value as AdjustmentKind;
                  setType(next);
                  if (next === "damage") setDirection("decrease");
                }}
              >
                <option value="adjustment">Điều chỉnh tồn kho</option>
                <option value="damage">Vỡ / hỏng trong kho</option>
              </select>
            </div>

            <div className="product-field">
              <label htmlFor="adjustment-direction">Chiều điều chỉnh</label>
              <select
                id="adjustment-direction"
                value={effectiveDirection}
                onChange={event => setDirection(event.target.value as AdjustmentDirection)}
              >
                <option value="decrease">Giảm tồn kho (-)</option>
                {type === "adjustment" && perms.canIncreaseAdjustment && (
                  <option value="increase">Tăng tồn kho (+)</option>
                )}
              </select>
              {type === "damage" && <p className="hint">Báo vỡ / hỏng luôn ghi giảm tồn kho.</p>}
              {type === "adjustment" && !perms.canIncreaseAdjustment && (
                <p className="hint">Điều chỉnh tăng tồn kho yêu cầu quyền inventory.approve.</p>
              )}
              {fieldErrors.direction && <p className="field-error">{fieldErrors.direction}</p>}
            </div>
          </div>

          <div className="product-field">
            <label htmlFor="adjustment-qty">Số lượng (nguyên dương)</label>
            <input
              id="adjustment-qty"
              type="text"
              inputMode="numeric"
              value={quantity}
              aria-invalid={Boolean(fieldErrors.quantity)}
              onChange={event => setQuantity(event.target.value)}
            />
            {fieldErrors.quantity && <p className="field-error">{fieldErrors.quantity}</p>}
          </div>

          <div className="product-field">
            <label htmlFor="adjustment-reason">Lý do điều chỉnh (tối thiểu 3 ký tự)</label>
            <textarea
              id="adjustment-reason"
              rows={3}
              maxLength={2000}
              value={reason}
              aria-invalid={Boolean(fieldErrors.reason)}
              onChange={event => setReason(event.target.value)}
              placeholder="Mô tả nguyên nhân điều chỉnh hoặc tình trạng vỡ hỏng…"
            />
            {fieldErrors.reason && <p className="field-error">{fieldErrors.reason}</p>}
          </div>
        </fieldset>

        <div className="catalog-actions">
          <button type="submit" className="button" disabled={pending}>
            {pending ? "Đang lưu…" : "Lưu điều chỉnh"}
          </button>
        </div>
      </form>

      <VariantPicker
        open={pickerOpen}
        mode="single"
        canWrite={perms.canWrite}
        title="Chọn biến thể cần điều chỉnh"
        onClose={() => setPickerOpen(false)}
        onSelect={items => {
          if (items[0]) setSelected(items[0]);
        }}
      />
    </section>
  );
}
