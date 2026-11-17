"use client";

import { forwardRef, useState, type InputHTMLAttributes, type ReactNode } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { Eye, EyeOff, X, CircleAlert, LoaderCircle } from "lucide-react";
import { ApiError } from "../lib/api";

export const Field = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement> & {
  label: string; error?: string; hint?: string;
}>(function Field({ label, error, hint, id, type, ...props }, ref) {
  const [visible, setVisible] = useState(false);
  const isPassword = type === "password";
  const fieldId = id ?? props.name;
  return <div className="field">
    <label htmlFor={fieldId}>{label}</label>
    <div className="input-wrap"><input {...props} id={fieldId} ref={ref}
      type={isPassword && visible ? "text" : type} aria-invalid={!!error}
      aria-describedby={[hint && `${fieldId}-hint`, error && `${fieldId}-error`].filter(Boolean).join(" ") || undefined} />
      {isPassword && <button type="button" className="visibility" aria-label={`${visible ? "Ẩn" : "Hiện"} ${label.toLowerCase()}`}
        aria-pressed={visible} onClick={() => setVisible(!visible)}>{visible ? <EyeOff size={18} /> : <Eye size={18} />}</button>}
    </div>
    {hint && <p className="hint" id={`${fieldId}-hint`}>{hint}</p>}
    {error && <p className="field-error" id={`${fieldId}-error`}>{error}</p>}
  </div>;
});

export function FormError({ error }: { error: unknown }) {
  if (!error) return null;
  return <div className="alert" role="alert" tabIndex={-1}>
    <CircleAlert size={19} aria-hidden="true" />
    <div><p>{error instanceof ApiError ? error.message : "Không thể kết nối. Vui lòng kiểm tra mạng và thử lại."}</p>
      {error instanceof ApiError && Object.entries(error.fields).length > 0 &&
        <ul>{Object.values(error.fields).flat().map((text, index) => <li key={index}>{text}</li>)}</ul>}
    </div>
  </div>;
}

export function Pending({ children = "Đang kiểm tra phiên làm việc…" }: { children?: ReactNode }) {
  return <div className="pending" role="status"><LoaderCircle size={24} aria-hidden="true" /><p>{children}</p></div>;
}

export function Feedback({ children }: { children: ReactNode }) {
  return <div className="feedback" role="status">{children}</div>;
}

export function Confirmation({ trigger, title, children, pending, onConfirm }: {
  trigger: ReactNode; title: string; children: ReactNode; pending: boolean; onConfirm: () => Promise<void>;
}) {
  const [open, setOpen] = useState(false);
  const [error, setError] = useState<unknown>();
  return <Dialog.Root open={open} onOpenChange={value => { if (!pending) { setOpen(value); setError(undefined); } }}>
    <Dialog.Trigger asChild>{trigger}</Dialog.Trigger>
    <Dialog.Portal><Dialog.Overlay className="overlay" /><Dialog.Content className="dialog">
      <Dialog.Title>{title}</Dialog.Title><Dialog.Description>{children}</Dialog.Description>
      <FormError error={error} />
      <div className="dialog-actions"><Dialog.Close asChild><button className="button secondary" disabled={pending}>Quay lại</button></Dialog.Close>
        <button className="button" disabled={pending} onClick={async () => {
          try { await onConfirm(); setOpen(false); } catch (error) { setError(error); }
        }}>{pending ? "Đang xử lý…" : "Xác nhận đăng xuất"}</button></div>
      <Dialog.Close className="close-button" aria-label="Đóng hộp thoại" disabled={pending}><X size={20} /></Dialog.Close>
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>;
}
