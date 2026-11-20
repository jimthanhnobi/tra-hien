"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";
import { useForm, type UseFormReturn } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation } from "@tanstack/react-query";
import { ApiError, isConcurrencyConflict } from "../../lib/api";
import { formSchema, type FormField, type Values } from "../../lib/catalog-forms";
import type { CatalogRow } from "../../lib/catalog-api";
import { Field, FormError } from "../ui";

export function CatalogForm({ fields, initial, kind, row, writable, locked = false, relatedDirty = false, lockMessage, isRelatedEditing, onBusyChange, onSave, onCancel, onReload, children, footer }: {
  fields: FormField[]; initial: Values; kind: string; row?: CatalogRow; writable: boolean;
  locked?: boolean; relatedDirty?: boolean; lockMessage?: string;
  isRelatedEditing?: () => boolean; onBusyChange?: (busy: boolean) => void;
  onSave: (values: Values) => Promise<void | Values>; onCancel: () => void; onReload?: () => Promise<Values>;
  children?: (form: UseFormReturn<Values>) => ReactNode; footer?: (dirty: boolean, pending: boolean, form: UseFormReturn<Values>) => ReactNode;
}) {
  const form = useForm<Values>({ resolver: zodResolver(formSchema(fields, kind, row)), defaultValues: initial });
  const mutation = useMutation({ mutationFn: onSave });
  const [error, setError] = useState<unknown>();
  const [conflict, setConflict] = useState(false);
  const [reloading, setReloading] = useState(false);
  const busy = useRef(false);
  const { errors, isSubmitting, isDirty } = form.formState;
  useEffect(() => {
    if (!isDirty && !relatedDirty) return;
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); };
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [isDirty, relatedDirty]);
  const metadataBlocked = locked || relatedDirty;
  return <form className="catalog-form" noValidate onSubmit={event => {
    event.preventDefault();
    if (!writable || metadataBlocked || isRelatedEditing?.() || busy.current || conflict) return;
    busy.current = true; onBusyChange?.(true);
    void form.handleSubmit(async values => {
      setError(undefined);
      try {
        const saved = await mutation.mutateAsync(values);
        if (saved) form.reset(saved);
      } catch (error) {
        setError(error);
        if (error instanceof ApiError) {
          // Only existing aggregates can recover a stale version by loading a detail.
          if (isConcurrencyConflict(error) && onReload) setConflict(true);
          for (const f of fields) {
            const entry = Object.entries(error.fields).find(([key]) => key.toLowerCase() === f.name.toLowerCase());
            if (entry) form.setError(f.name, { message: entry[1].join(" ") });
          }
        }
      }
    })(event).finally(() => { busy.current = false; onBusyChange?.(false); });
  }}>
    {!writable && <p className="catalog-note">Bạn có quyền xem. Liên hệ người quản lý nếu cần chỉnh sửa.</p>}
    {locked && lockMessage && <p className="catalog-note" role="status">{lockMessage}</p>}
    {relatedDirty && <p className="catalog-note" role="status">Hãy lưu hoặc hủy lựa chọn sản phẩm trước khi lưu thông tin bộ sưu tập.</p>}
    <FormError error={error} />
    {Object.keys(errors).length > 0 && <p role="alert" className="field-error">Vui lòng kiểm tra các trường được đánh dấu.</p>}
    {conflict && <div className="alert" role="alert"><div><p>Dữ liệu có xung đột. Nội dung bạn nhập vẫn được giữ; hãy sao chép phần cần giữ trước khi tải bản mới. Hệ thống chưa ghi đè dữ liệu.</p>
      {onReload && <button className="button secondary" type="button" disabled={locked || relatedDirty || reloading} onClick={async () => {
        if (busy.current || metadataBlocked || isRelatedEditing?.()) return;
        if (window.confirm("Tải bản mới và bỏ thay đổi chưa lưu trong biểu mẫu này?")) {
          busy.current = true; onBusyChange?.(true); setReloading(true);
          try { const latest = await onReload(); form.reset(latest); setConflict(false); setError(undefined); }
          catch (error) { setError(error); }
          finally { busy.current = false; onBusyChange?.(false); setReloading(false); }
        }
      }}>Tải bản mới</button>}</div></div>}
    <fieldset disabled={!writable || metadataBlocked || isSubmitting || reloading} className="catalog-fieldset"><legend className="sr-only">Thông tin {kind}</legend>
      <div className="catalog-fields">{fields.map(f => {
        const message = errors[f.name]?.message;
        const error = typeof message === "string" ? message : undefined;
        const id = `catalog-${f.name}`;
        if (!f.type || f.type === "number") return <Field key={f.name} id={id} label={f.label} type={f.type === "number" ? "number" : "text"}
          required={f.required} hint={f.hint} error={error} {...form.register(f.name, { valueAsNumber: f.type === "number" })} />;
        return <div key={f.name} className={`field ${f.type === "checkbox" ? "catalog-check" : ""}`}>
          {f.type !== "checkbox" && <label htmlFor={id}>{f.label}</label>}
          {f.type === "textarea" ? <textarea id={id} rows={5} {...form.register(f.name)} aria-invalid={!!error} aria-describedby={`${id}-help`} />
            : f.type === "select" ? <select id={id} {...form.register(f.name)} aria-invalid={!!error} aria-describedby={`${id}-help`}>
              {f.options?.map(([key, text]) => <option key={key} value={key}>{text}</option>)}</select>
              : <label htmlFor={id}><input id={id} type="checkbox" {...form.register(f.name)} aria-invalid={!!error} aria-describedby={`${id}-help`} />{f.label}</label>}
          <div id={`${id}-help`}>{f.hint && <p className="hint">{f.hint}</p>}{error && <p className="field-error">{error}</p>}</div>
        </div>;
      })}</div>{children?.(form)}
    </fieldset>
    <div className="catalog-actions">{writable && <button className="button" type="submit" disabled={locked || relatedDirty || isSubmitting || reloading || conflict}>{isSubmitting ? "Đang lưu…" : "Lưu thay đổi"}</button>}
      <button className="button secondary" type="button" disabled={locked || isSubmitting || reloading} onClick={() => {
        if (busy.current || locked) return;
        if ((!isDirty && !relatedDirty && !isRelatedEditing?.()) || window.confirm("Bỏ các thay đổi chưa lưu?")) onCancel();
      }}>{writable ? "Hủy" : "Đóng"}</button></div>
    {footer?.(isDirty, isSubmitting || reloading || conflict, form)}
  </form>;
}
