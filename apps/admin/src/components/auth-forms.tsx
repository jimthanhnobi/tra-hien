"use client";

import { useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { useForm, type FieldValues, type Path, type UseFormSetError } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import QRCode from "qrcode";
import { ArrowRight, ShieldCheck, Sprout } from "lucide-react";
import { ApiError, type Provisioning } from "../lib/api";
import { useAdmin } from "./providers";
import { Feedback, Field, FormError } from "./ui";

const code = z.string().regex(/^\d{6}$/, "Nhập đủ 6 chữ số từ ứng dụng xác thực.");
const loginSchema = z.object({
  email: z.email("Nhập địa chỉ email hợp lệ.").max(256, "Email tối đa 256 ký tự."),
  password: z.string().min(1, "Nhập mật khẩu.").max(1024, "Mật khẩu tối đa 1024 ký tự."),
  totpCode: z.union([z.literal(""), code])
});
const passwordSchema = z.object({
  currentPassword: z.string().min(1, "Nhập mật khẩu hiện tại.").max(1024, "Mật khẩu tối đa 1024 ký tự."),
  newPassword: z.string().min(12, "Mật khẩu cần ít nhất 12 ký tự.").max(1024, "Mật khẩu tối đa 1024 ký tự.")
    .regex(/[A-Z]/, "Cần có chữ hoa.").regex(/[a-z]/, "Cần có chữ thường.")
    .regex(/[0-9]/, "Cần có chữ số.").regex(/[^a-zA-Z0-9]/, "Cần có ký tự đặc biệt."),
  confirmPassword: z.string()
}).refine(value => value.currentPassword !== value.newPassword, { path: ["newPassword"], message: "Mật khẩu mới cần khác mật khẩu hiện tại." })
  .refine(value => value.newPassword === value.confirmPassword, { path: ["confirmPassword"], message: "Mật khẩu xác nhận chưa khớp." });
const totpSchema = z.object({ code });

function serverErrors<T extends FieldValues>(error: unknown, setError: UseFormSetError<T>, fields: readonly Path<T>[]) {
  if (!(error instanceof ApiError)) return;
  for (const field of fields) {
    const match = Object.entries(error.fields).find(([key]) => key.toLowerCase() === field.toLowerCase());
    if (match) setError(field, { message: match[1].join(" ") });
  }
}

function ValidationSummary({ invalid }: { invalid: boolean }) {
  return invalid ? <p className="field-error" role="alert">Vui lòng kiểm tra các trường được đánh dấu trước khi tiếp tục.</p> : null;
}

export function AuthFrame({ title, description, children }: { title: string; description: string; children: React.ReactNode }) {
  return <main id="main-content" className="auth-page">
    <aside className="auth-story" aria-label="Trà Hiên"><div className="brand"><Sprout size={30} /><span>TRÀ HIÊN<small>KHÔNG GIAN QUẢN TRỊ</small></span></div>
      <div className="story-copy"><span className="eyebrow">TỪ NHỮNG ĐIỀU TỈ MỈ</span><h2>Chăm chút từng<br />trải nghiệm trà.</h2>
        <p>Một không gian làm việc gọn gàng,<br />để mỗi ngày vận hành thêm an tâm.</p><div className="tea-mark" aria-hidden="true"><Sprout size={120} strokeWidth={0.7} /></div></div>
      <p className="story-footer">TRÀ • TRÀ CỤ • NHỮNG GIÁ TRỊ BỀN LÂU</p></aside>
    <section className="auth-side"><div className="auth-card"><div className="mobile-brand"><Sprout size={24} /> Trà Hiên</div>
      <span className="section-tag"><ShieldCheck size={16} /> Dành cho nhân viên</span>
      <h1>{title}</h1><p className="intro">{description}</p>{children}
      <p className="auth-footer"><ShieldCheck size={15} /> Phiên làm việc được bảo vệ bằng kết nối an toàn.</p>
    </div></section>
  </main>;
}

export function LoginForm() {
  const runtime = useAdmin();
  const [error, setError] = useState<unknown>();
  const busy = useRef(false);
  const form = useForm({ resolver: zodResolver(loginSchema), defaultValues: { email: "", password: "", totpCode: "" } });
  const { errors, isSubmitting } = form.formState;
  return <AuthFrame title="Chào bạn trở lại" description="Đăng nhập để bắt đầu phiên làm việc tại Trà Hiên.">
    {runtime.notice && <Feedback>{runtime.notice}</Feedback>}
    <form noValidate onSubmit={event => { void form.handleSubmit(async values => {
      if (busy.current) return;
      busy.current = true; setError(undefined); runtime.setNotice("");
      try {
        await runtime.api.login({ email: values.email, password: values.password, totpCode: values.totpCode || null });
        form.reset();
        await runtime.authenticated();
      } catch (error) { setError(error); serverErrors(error, form.setError, ["email", "password", "totpCode"]); }
      finally { busy.current = false; }
    })(event); }}>
      <FormError error={error} /><ValidationSummary invalid={Object.keys(errors).length > 0} />
      <Field label="Email công việc" type="email" autoComplete="username" autoCapitalize="none" spellCheck={false} {...form.register("email")} error={errors.email?.message} />
      <Field label="Mật khẩu" type="password" autoComplete="current-password" {...form.register("password")} error={errors.password?.message} />
      <Field label="Mã xác thực TOTP" inputMode="numeric" autoComplete="one-time-code" maxLength={6} {...form.register("totpCode")} error={errors.totpCode?.message}
        hint="Nếu đã bật xác thực hai lớp, nhập mã 6 số trong ứng dụng. Nếu chưa bật, để trống trường này." />
      <button className="button primary wide" type="submit" disabled={isSubmitting}>{isSubmitting ? "Đang đăng nhập…" : <>Đăng nhập <ArrowRight size={18} /></>}</button>
    </form><p className="help-text">Cần hỗ trợ tài khoản? Liên hệ người quản lý của bạn.</p>
  </AuthFrame>;
}

export function PasswordForm() {
  const runtime = useAdmin();
  const router = useRouter();
  const busy = useRef(false);
  const [error, setError] = useState<unknown>();
  const form = useForm({ resolver: zodResolver(passwordSchema), defaultValues: { currentPassword: "", newPassword: "", confirmPassword: "" } });
  const { errors, isSubmitting } = form.formState;
  return <AuthFrame title="Đặt mật khẩu của bạn" description="Đổi mật khẩu được cấp để bảo vệ tài khoản. Sau đó, bạn sẽ đăng nhập lại bằng mật khẩu mới.">
    <form noValidate onSubmit={event => { void form.handleSubmit(async values => {
      if (busy.current) return;
      busy.current = true; setError(undefined);
      try {
        await runtime.api.changePassword({ currentPassword: values.currentPassword, newPassword: values.newPassword });
        form.reset(); await runtime.signedOut();
        runtime.setNotice("Đã đổi mật khẩu. Vui lòng đăng nhập lại bằng mật khẩu mới."); router.replace("/login");
      } catch (error) { setError(error); serverErrors(error, form.setError, ["currentPassword", "newPassword"]); }
      finally { busy.current = false; }
    })(event); }}>
      <FormError error={error} /><ValidationSummary invalid={Object.keys(errors).length > 0} />
      <Field label="Mật khẩu hiện tại" type="password" autoComplete="current-password" {...form.register("currentPassword")} error={errors.currentPassword?.message} />
      <Field label="Mật khẩu mới" type="password" autoComplete="new-password" {...form.register("newPassword")} error={errors.newPassword?.message} hint="Ít nhất 12 ký tự, gồm chữ hoa, chữ thường, số và ký tự đặc biệt." />
      <Field label="Xác nhận mật khẩu mới" type="password" autoComplete="new-password" {...form.register("confirmPassword")} error={errors.confirmPassword?.message} />
      <button className="button wide" disabled={isSubmitting}>{isSubmitting ? "Đang lưu…" : "Lưu mật khẩu mới"}</button>
    </form><OnboardingLogout />
  </AuthFrame>;
}

export function TotpForm() {
  const runtime = useAdmin();
  const router = useRouter();
  const busy = useRef(false);
  const [provisioning, setProvisioning] = useState<Provisioning>();
  const [qr, setQr] = useState<string>();
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<unknown>();
  const form = useForm({ resolver: zodResolver(totpSchema), defaultValues: { code: "" } });
  const { errors, isSubmitting } = form.formState;
  return <AuthFrame title="Bảo vệ bằng xác thực hai lớp" description="Thiết lập ứng dụng xác thực trên điện thoại. Mỗi lần đăng nhập, bạn sẽ dùng thêm một mã TOTP.">
    <FormError error={error} />
    {!provisioning ? <><ol className="setup-steps"><li>Mở ứng dụng như Google Authenticator hoặc Microsoft Authenticator.</li><li>Chọn thêm tài khoản và quét mã QR ở bước tiếp theo.</li><li>Nhập mã 6 số để hoàn tất.</li></ol>
      <button className="button wide" disabled={starting} onClick={async () => {
        if (busy.current) return; busy.current = true; setStarting(true); setError(undefined);
        try {
          const data = await runtime.api.setupTotp();
          // Generate locally: provisioning data never leaves this origin.
          const image = await QRCode.toDataURL(data.provisioningUri, { width: 224, margin: 2, errorCorrectionLevel: "M" });
          setProvisioning(data); setQr(image);
        } catch (error) { setError(error); } finally { busy.current = false; setStarting(false); }
      }}>{starting ? "Đang chuẩn bị…" : "Bắt đầu thiết lập"}</button></> : <>
      <div className="provisioning">
        {/* Local data URI only, no remote QR service or image optimization. */}
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img src={qr} alt="Mã QR để thêm tài khoản vào ứng dụng xác thực" width={224} height={224} />
        <details><summary>Không quét được mã QR?</summary><p>Nhập thủ công khóa này vào ứng dụng (TOTP, 6 số, 30 giây):</p><code>{provisioning.sharedKey}</code></details>
        <p className="hint">Không chia sẻ mã QR hoặc khóa này. Thiết lập có hiệu lực trong 10 phút; nếu hết hạn, đăng xuất và thực hiện lại.</p>
      </div>
      <form noValidate onSubmit={event => { void form.handleSubmit(async values => {
        if (busy.current) return; busy.current = true; setError(undefined);
        try {
          await runtime.api.verifyTotp(values);
          setProvisioning(undefined); setQr(undefined); form.reset(); await runtime.signedOut();
          runtime.setNotice("Đã bật xác thực hai lớp. Hãy đợi mã TOTP mới rồi đăng nhập lại bằng mật khẩu và mã đó."); router.replace("/login");
        } catch (error) { setError(error); serverErrors(error, form.setError, ["code"]); } finally { busy.current = false; }
      })(event); }}>
        <ValidationSummary invalid={!!errors.code} />
        <Field label="Mã xác thực 6 số" inputMode="numeric" autoComplete="one-time-code" maxLength={6} {...form.register("code")} error={errors.code?.message} />
        <button className="button wide" disabled={isSubmitting}>{isSubmitting ? "Đang xác minh…" : "Xác minh và hoàn tất"}</button>
      </form></>}
    <OnboardingLogout />
  </AuthFrame>;
}

function OnboardingLogout() {
  const runtime = useAdmin();
  const router = useRouter();
  const [pending, setPending] = useState(false);
  const busy = useRef(false);
  const [error, setError] = useState<unknown>();
  return <><FormError error={error} /><button className="text-button" disabled={pending} onClick={async () => {
    if (busy.current) return; busy.current = true; setPending(true);
    try { await runtime.api.logout(); await runtime.signedOut(); router.replace("/login"); }
    catch (error) { setError(error); } finally { busy.current = false; setPending(false); }
  }}>{pending ? "Đang đăng xuất…" : "Đăng xuất và quay lại sau"}</button></>;
}
