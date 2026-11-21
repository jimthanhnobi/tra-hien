"use client";

import { useId, useState } from "react";
import { ApiError } from "../../lib/api";
import type { AttributeRow } from "../../lib/catalog-api";
import { buildAttributes, nullable, parseDimensions, parseVnd, readDimensions, variantPayload, vnd, type DimensionDraft } from "../../lib/product-forms";
import type { Variant, VariantPatch } from "../../lib/products-api";
import { AttributeFields, DimensionFields, type AttributeChanges } from "./product-attributes";
import { ProductField as Field, ProductForm, inputError, type EditorActions } from "./product-fields";

export function VariantPrice({ amount }: { amount?: number | string | null }) {
  return <span>{amount == null ? "Chưa có giá" : vnd.format(Number(amount))}</span>;
}

export function VariantDetails({ variant, definitions }: { variant: Variant; definitions: AttributeRow[] }) {
  return <section className="product-variant-details">
    <dl className="product-facts">
      <div><dt>Mã vạch</dt><dd>{variant.barcode || "—"}</dd></div><div><dt>Khối lượng</dt><dd>{variant.weightG == null ? "—" : `${variant.weightG} g`}</dd></div>
      <div><dt>Vị trí</dt><dd>{variant.position ?? "—"}</dd></div><div><dt>Hàng độc bản</dt><dd>{variant.isUniquePiece ? "Có" : "Không"}</dd></div>
    </dl>
    {!!variant.packageDimsCm && <DimensionFields label="Kích thước gói (cm)" value={readDimensions(variant.packageDimsCm)} onChange={() => {}} disabled />}
    <AttributeFields definitions={definitions} original={variant.attributes ?? []} changes={{}} onChange={() => {}} idPrefix={`display-${variant.id}`} readOnly />
  </section>;
}

function integer(raw: string, key: string, label: string, minimum: number) {
  const value = Number(raw);
  if (!/^\d+$/.test(raw) || !Number.isSafeInteger(value) || value < minimum || value > 2_147_483_647) {
    throw inputError(key, `${label} cần là số nguyên từ ${minimum} đến 2.147.483.647.`);
  }
  return value;
}

export function VariantForm({ variant, rowVersion, canPrice, owner, canEnableUniquePiece, definitions, actions, save }: {
  variant?: Variant; rowVersion: string; canPrice: boolean; owner: boolean; canEnableUniquePiece: boolean; definitions: AttributeRow[]; actions: EditorActions;
  save: (body: VariantPatch) => Promise<unknown>;
}) {
  const [patch, setPatch] = useState<VariantPatch>({ rowVersion });
  const [price, setPrice] = useState<string>();
  const [weight, setWeight] = useState<string>();
  const [position, setPosition] = useState<string>();
  const [threshold, setThreshold] = useState<string>();
  const [dims, setDims] = useState<DimensionDraft | null>();
  const [attrs, setAttrs] = useState<AttributeChanges>({});
  const skuLocked = actions.failure instanceof ApiError && actions.failure.code === "sku_locked";
  const skuEditable = owner && !skuLocked;
  const current = { ...variant, ...patch };
  const uniqueRestricted = !!variant && !variant.isUniquePiece && !canEnableUniquePiece;
  const uniqueHint = useId();
  const currentDims = dims === undefined ? variant?.packageDimsCm == null ? null : readDimensions(variant.packageDimsCm) : dims;
  const original = variant?.attributes ?? [];
  function change<K extends keyof VariantPatch>(key: K, value: VariantPatch[K]) { setPatch(old => ({ ...old, [key]: value })); actions.dirty(); }
  const setString = (set: (value: string) => void, value: string) => { set(value); actions.dirty(); };
  const changed = !variant || Object.keys(patch).length > 1 || [price, weight, position, threshold, dims].some(value => value !== undefined) || Object.keys(attrs).length > 0;
  return <ProductForm actions={actions} save={variant ? "Lưu biến thể" : "Tạo biến thể"} disabled={!changed} onSave={async () => {
    const body = { ...patch };
    if (uniqueRestricted) delete body.isUniquePiece;
    if (canPrice && price !== undefined && price !== String(variant?.priceVnd ?? "")) {
      try { body.priceVnd = parseVnd(price); } catch (error) { throw inputError("priceVnd", (error as Error).message); }
    }
    if (skuEditable && body.sku !== undefined) {
      body.sku = body.sku.trim();
      if (!body.sku && !variant) delete body.sku;
      else if (!/^[A-Z0-9][A-Z0-9-]{1,39}$/.test(body.sku)) throw inputError("sku", "SKU cần 2–40 ký tự in hoa, số hoặc dấu gạch nối.");
      else if (body.sku === variant?.sku) delete body.sku;
    }
    if (weight !== undefined) body.weightG = weight === "" ? null : integer(weight, "weightG", "Khối lượng", 1);
    if (position !== undefined && position !== "") body.position = integer(position, "position", "Vị trí", 0);
    if (threshold !== undefined && threshold !== "") body.lowStockThreshold = integer(threshold, "lowStockThreshold", "Ngưỡng tồn thấp", 0);
    if (dims !== undefined) {
      try { body.packageDimsCm = dims === null ? null : parseDimensions(dims); } catch (error) { throw inputError("packageDimsCm", (error as Error).message); }
    }
    if (Object.keys(attrs).length) {
      try { body.attributes = buildAttributes(original, definitions, attrs); } catch (error) { throw inputError("attributes", (error as Error).message); }
    }
    await save(variantPayload(rowVersion, body, canPrice, skuEditable));
  }}>
    <h2>{variant ? `Chỉnh sửa biến thể ${variant.sku ?? ""}` : "Tạo biến thể"}</h2>
    <div className="catalog-fields">
      <Field name="variant-name" label="Tên biến thể" value={current.name ?? ""} onChange={event => change("name", nullable(event.target.value))} />
      <Field name="sku" label="SKU biến thể" value={(skuLocked ? variant?.sku : current.sku) ?? ""} disabled={!skuEditable} maxLength={40} onChange={event => change("sku", event.target.value)}
        hint={skuLocked ? "SKU đã khóa sau giao dịch." : variant ? "Chỉ chủ cửa hàng có thể đổi SKU trước khi biến thể có giao dịch." : "Để trống để hệ thống cấp SKU."} />
      {canPrice ? <Field name="priceVnd" label="Giá niêm yết (VND)" inputMode="numeric" value={price ?? current.priceVnd ?? ""}
        onChange={event => setString(setPrice, event.target.value)} hint="Để trống nếu chưa có giá." />
        : <p>Giá niêm yết · <VariantPrice amount={variant?.priceVnd} /></p>}
      <label className="product-field">Trạng thái biến thể<select aria-label="Trạng thái biến thể" value={current.status ?? "active"} onChange={event => change("status", event.target.value)}>
        <option value="active">Hoạt động</option><option value="inactive">Ngừng bán</option></select></label>
      <Field name="barcode" label="Mã vạch" value={current.barcode ?? ""} onChange={event => change("barcode", nullable(event.target.value))} />
      <Field name="weightG" label="Khối lượng (g)" inputMode="numeric" value={weight ?? current.weightG ?? ""} onChange={event => setString(setWeight, event.target.value)} />
      <Field name="position" label="Vị trí" inputMode="numeric" value={position ?? current.position ?? ""} onChange={event => setString(setPosition, event.target.value)} />
      <Field name="lowStockThreshold" label="Ngưỡng tồn thấp" inputMode="numeric" value={threshold ?? current.lowStockThreshold ?? ""} onChange={event => setString(setThreshold, event.target.value)} />
      <div>
        <label className="product-check"><input type="checkbox" checked={!uniqueRestricted && !!current.isUniquePiece} disabled={uniqueRestricted}
          aria-describedby={uniqueRestricted ? uniqueHint : undefined} onChange={event => change("isUniquePiece", event.target.checked)} /> Hàng độc bản</label>
        {uniqueRestricted && <small id={uniqueHint}>Cần quyền đọc và ghi kho để chuyển biến thể này thành hàng độc bản.</small>}
      </div>
    </div>
    {currentDims === null ? <button type="button" className="button secondary" onClick={() => { setDims({}); actions.dirty(); }}>Nhập kích thước gói</button>
      : <><DimensionFields label="Kích thước gói (cm)" value={currentDims} onChange={value => { setDims(value); actions.dirty(); }} />
        <button type="button" className="button secondary" onClick={() => { setDims(null); actions.dirty(); }}>Bỏ kích thước gói</button></>}
    <h3>Thuộc tính biến thể</h3>
    <AttributeFields definitions={definitions} original={original} changes={attrs} onChange={(id, value) => {
      setAttrs(old => ({ ...old, [id]: value })); actions.dirty();
    }} />
  </ProductForm>;
}
