"use client";

import { useState } from "react";
import { useQueries } from "@tanstack/react-query";
import { CatalogApi, type AttributeRow } from "../../lib/catalog-api";
import { buildAttributes, dimensionKeys, initialAttributeValue, type DimensionDraft, type DimensionKey } from "../../lib/product-forms";
import type { ProductValue } from "../../lib/products-api";
import { FormError } from "../ui";
import { useAdmin } from "../providers";
import { ProductForm, inputError, type EditorActions } from "./product-fields";
import { attributeOptions, useAttributeOptions } from "./product-references";

export type AttributeChanges = Record<string, string | DimensionDraft>;
const dimensionLabels: Record<DimensionKey, string> = { d: "Đường kính", h: "Chiều cao", mouth: "Đường kính miệng", l: "Chiều dài", w: "Chiều rộng" };

export function DimensionFields({ label, value, onChange, disabled }: {
  label: string; value: DimensionDraft; onChange: (value: DimensionDraft) => void; disabled?: boolean;
}) {
  return <fieldset className="product-dimensions" disabled={disabled}><legend>{label}</legend>
    {dimensionKeys.map(key => <label className="product-field" key={key}>{dimensionLabels[key]}
      <input inputMode="decimal" value={value[key] ?? ""} onChange={event => onChange({ ...value, [key]: event.target.value })} />
    </label>)}
  </fieldset>;
}

function OptionField({ id, definition, value, onChange, disabled }: {
  id: string; definition: AttributeRow; value: string; onChange: (value: string) => void; disabled: boolean;
}) {
  const options = useAttributeOptions(definition.productTypeId, definition.id);
  return <div><label className="product-field">{attributeLabel(definition)}<select id={id} aria-label={attributeLabel(definition)} value={value}
    onChange={event => onChange(event.target.value)} disabled={disabled || !options.data}>
    <option value="">Chưa có giá trị</option>
    {value && !options.data?.some(option => option.id === value) && <option value={value}>Lựa chọn đã lưu (chưa tải được tên)</option>}
    {options.data?.filter(option => option.isActive || option.id === value).map(option =>
      <option key={option.id} value={option.id} disabled={!option.isActive}>{option.label}{option.isActive ? "" : " · Ngừng dùng"}</option>)}
  </select></label>{options.isError && <><FormError error={options.error} /><button type="button" className="button secondary" onClick={() => void options.refetch()}>Tải lại lựa chọn</button></>}</div>;
}

function attributeLabel(def: AttributeRow) {
  return `${def.name}${def.unit ? ` (${def.unit})` : ""}${def.isRequiredForPublish ? " · Bắt buộc khi công khai" : ""}${def.isVariantAxis ? " · Trục biến thể" : ""}${def.isActive ? "" : " · Ngừng dùng"}`;
}

export function AttributeFields({ definitions, original, changes, onChange, readOnly = false, idPrefix = "attribute" }: {
  definitions: AttributeRow[]; original: ProductValue[]; changes: AttributeChanges; onChange: (id: string, value: string | DimensionDraft) => void; readOnly?: boolean; idPrefix?: string;
}) {
  const visible = definitions.filter(def => def.isActive || original.some(value => value.attributeId === def.id));
  if (!visible.length) return <p>Loại sản phẩm chưa có thuộc tính cho mục này.</p>;
  return <div className="catalog-fields">{visible.map(def => {
    const value = changes[def.id] ?? initialAttributeValue(def, original.find(item => item.attributeId === def.id));
    const disabled = readOnly || !def.isActive;
    const change = (next: string | DimensionDraft) => onChange(def.id, next);
    const label = attributeLabel(def);
    const id = `${idPrefix}-${def.id}`;
    if (def.dataType === "option") return <OptionField key={def.id} id={id} definition={def} value={String(value)} onChange={change} disabled={disabled} />;
    if (def.dataType === "dimensions") return <div key={def.id} className="product-field" id={id} tabIndex={-1}>
      {typeof value === "object"
        ? <><DimensionFields label={label} value={value} onChange={change} disabled={disabled} />
            {!disabled && <button type="button" className="button secondary" onClick={() => change("")}>Bỏ giá trị {def.name}</button>}</>
        : <><span>{label}{disabled ? " · Chưa có giá trị" : ""}</span>
            {!disabled && <button type="button" className="button secondary" onClick={() => change({})}>Nhập {def.name}</button>}</>}
    </div>;
    if (def.dataType === "boolean") return <label className="product-field" key={def.id}>{label}
      <select id={id} aria-label={label} value={String(value)} disabled={disabled} onChange={event => change(event.target.value)}>
        <option value="">Chưa có giá trị</option><option value="true">Có</option><option value="false">Không</option>
      </select></label>;
    return <label className="product-field" key={def.id}>{label}<input id={id} value={String(value)} disabled={disabled}
      inputMode={def.dataType === "number" ? "decimal" : undefined} onChange={event => change(event.target.value)} /></label>;
  })}</div>;
}

export function ProductAttributesForm({ original, definitions, actions, save, verificationPending, onEmptyAfterDiscard }: {
  original: ProductValue[]; definitions: AttributeRow[]; actions: EditorActions; save: (values: ProductValue[]) => Promise<unknown>;
  verificationPending: boolean; onEmptyAfterDiscard: () => void;
}) {
  const [draft, setDraft] = useState<Record<string, { definition: AttributeRow; value: AttributeChanges[string] }>>({});
  const api = new CatalogApi(useAdmin().api);
  const entries = Object.values(draft);
  const options = entries.filter(({ definition, value }) => definition.dataType === "option" && !!value);
  const queries = useQueries({ queries: options.map(({ definition }) => ({
    ...attributeOptions(api, definition.productTypeId, definition.id), refetchOnMount: false
  })) });
  const byId = new Map(definitions.map(def => [def.id, def]));
  const conflicts = entries.filter(({ definition, value }) => {
    const latest = byId.get(definition.id);
    if (!latest?.isActive || latest.dataType !== definition.dataType || latest.unit !== definition.unit) return true;
    const result = queries[options.findIndex(option => option.definition.id === definition.id)];
    return !!result?.data && !result.data.some(option => option.id === value && option.isActive);
  });
  const conflictsById = new Set(conflicts.map(({ definition }) => definition.id));
  const changes = Object.fromEntries(entries.filter(({ definition }) => !conflictsById.has(definition.id))
    .map(({ definition, value }) => [definition.id, value]));
  const displayed = definitions.filter(def => !conflictsById.has(def.id));
  const unverified = verificationPending || queries.some(query => !query.data || query.isError || query.isFetching);
  return <ProductForm actions={actions} disabled={!entries.length || unverified || conflicts.length > 0} onSave={async () => {
    let values: ProductValue[];
    try { values = buildAttributes(original, definitions, changes); }
    catch (error) { throw inputError("attributes", (error as Error).message); }
    await save(values);
  }}>
    <h2>Thuộc tính sản phẩm</h2>
    {unverified && <p className="hint">Chờ xác minh định nghĩa và lựa chọn trước khi lưu.</p>}
    {conflicts.length > 0 && <div role="alert">
      <p>Định nghĩa hoặc lựa chọn đã thay đổi. Các giá trị sau cần được đối chiếu trước khi lưu:</p>
      <ul>{conflicts.map(({ definition, value }) => <li key={definition.id}>{definition.name} · {typeof value === "string" ? value || "Bỏ giá trị" : JSON.stringify(value)}</li>)}</ul>
      <button type="button" className="button secondary" onClick={() => {
        if (!actions.pending && window.confirm("Bỏ các thay đổi không còn tương thích? Các thay đổi khác sẽ được giữ lại.")) {
          const remaining = Object.entries(draft).filter(([id]) => !conflictsById.has(id));
          if (remaining.length === 0) onEmptyAfterDiscard();
          else setDraft(Object.fromEntries(remaining));
        }
      }}>Bỏ thay đổi không tương thích</button>
    </div>}
    <AttributeFields definitions={displayed} original={original} changes={changes} onChange={(id, value) => {
      const definition = byId.get(id)!;
      setDraft(current => ({ ...current, [id]: { definition, value } })); actions.dirty();
    }} />
  </ProductForm>;
}
