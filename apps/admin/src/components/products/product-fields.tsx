"use client";

import { createContext, useContext, useState, type ComponentProps, type ReactNode } from "react";
import Image from "next/image";
import { ApiError } from "../../lib/api";
import { productStatuses } from "../../lib/product-forms";
import { ProductUploadError } from "../../lib/products-api";
import { Field, FormError } from "../ui";

export function StatusBadge({ status }: { status?: string }) {
  return <span className="catalog-badge">{productStatuses[status ?? ""] ?? "Chưa rõ trạng thái"}</span>;
}

export function ProductThumbnail({ url, alt, size = 56 }: { url?: string | null; alt?: string; size?: number }) {
  const [failed, setFailed] = useState<string>();
  const valid = url && !url.includes("\\") && (/^https:\/\//i.test(url) || /^\/(?!\/)/.test(url));
  return valid && url !== failed ? <Image className="product-thumbnail" src={url} alt={alt ?? ""} width={size} height={size} unoptimized
    referrerPolicy="no-referrer" onError={() => setFailed(url)} /> : <span className="catalog-product-placeholder" aria-hidden="true">Ảnh</span>;
}

export function ProductError({ error }: { error: unknown }) {
  if (error instanceof ProductUploadError) return <p className="alert" role="alert">{error.message}</p>;
  return <FormError error={error} />;
}

const productFormError = createContext<unknown>(undefined);

export function fieldMessage(error: unknown, field: string) {
  return error instanceof ApiError ? error.fields[field]?.join(" ") : undefined;
}

export function ProductField({ errorKey, ...props }: ComponentProps<typeof Field> & { errorKey?: string }) {
  const error = useContext(productFormError);
  return <Field {...props} error={props.error ?? fieldMessage(error, errorKey ?? props.name ?? "")} />;
}

export interface EditorActions {
  dirty: (value?: boolean) => void;
  pending: boolean;
  failure: unknown;
  stale: boolean;
  cancel: () => void;
  discardAndReload: () => void;
}

export function ProductForm({ actions, onSave, children, save = "Lưu thay đổi", disabled = false }: {
  actions?: EditorActions; onSave: () => Promise<unknown>; children: ReactNode; save?: string; disabled?: boolean;
}) {
  const [invalid, setInvalid] = useState<unknown>();
  async function submit() {
    if (!actions || actions.pending || actions.stale || disabled) return;
    setInvalid(undefined);
    try { await onSave(); } catch (error) { setInvalid(error); }
  }
  return <form className={actions ? "catalog-form product-form" : "product-readonly"} onSubmit={event => { event.preventDefault(); void submit(); }}>
    <fieldset className="catalog-fieldset" disabled={!actions || actions.pending}>
      <productFormError.Provider value={actions ? invalid ?? actions.failure : undefined}>{children}</productFormError.Provider>
    </fieldset>
    {actions && <>
    <ProductError error={invalid ?? actions.failure} />
    {actions.stale && <div className="catalog-actions">
      <button type="button" className="button secondary" disabled={actions.pending} onClick={actions.discardAndReload}>Bỏ thay đổi và tải phiên bản mới</button>
    </div>}
    <div className="catalog-actions">
      <button className="button" disabled={actions.pending || actions.stale || disabled}>{actions.pending ? "Đang xử lý…" : save}</button>
      <button className="button secondary" type="button" disabled={actions.pending} onClick={actions.cancel}>Hủy</button>
    </div></>}
  </form>;
}

export function inputError(field: string, message: string) {
  return new ApiError(400, { code: "validation_failed", errors: { [field]: [message] } });
}

export function TextArea({ label, value, onChange, id, maxLength }: {
  label: string; value: string; onChange: (value: string) => void; id: string; maxLength?: number;
}) {
  const error = fieldMessage(useContext(productFormError), id);
  return <div className="product-field"><label htmlFor={id}>{label}</label><textarea id={id} value={value} rows={3} maxLength={maxLength}
    aria-invalid={!!error} aria-describedby={error ? `${id}-error` : undefined} onChange={event => onChange(event.target.value)} />
    {error && <p className="field-error" id={`${id}-error`}>{error}</p>}</div>;
}

export function toggleId(ids: string[], id: string, checked: boolean) {
  return checked ? [...new Set([...ids, id])] : ids.filter(value => value !== id);
}
