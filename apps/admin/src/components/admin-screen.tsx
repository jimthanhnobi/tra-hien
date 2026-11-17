"use client";

import { useEffect, useRef, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import * as Dialog from "@radix-ui/react-dialog";
import * as DropdownMenu from "@radix-ui/react-dropdown-menu";
import { Sprout, LayoutDashboard, Package, Warehouse, ShoppingBag, Users, Megaphone, FileText, ChartNoAxesCombined,
  Settings, UserRoundCog, PanelLeftClose, PanelLeftOpen, Menu, X, ChevronDown, ChevronRight, LogOut, ShieldCheck, ArrowUpRight, LockKeyhole, SearchX } from "lucide-react";
import { destination, navigation, visibleNavigation } from "../lib/navigation";
import { ApiError, type Auth } from "../lib/api";
import { useAdmin, useSession } from "./providers";
import { LoginForm, PasswordForm, TotpForm } from "./auth-forms";
import { Confirmation, FormError, Pending } from "./ui";

const icons = { home: LayoutDashboard, package: Package, warehouse: Warehouse, orders: ShoppingBag, users: Users,
  megaphone: Megaphone, file: FileText, chart: ChartNoAxesCombined, settings: Settings, staff: UserRoundCog };
const authPages = ["/login", "/change-password", "/totp-setup"];

export function AdminScreen({ path }: { path: string }) {
  const session = useSession();
  const router = useRouter();
  const target = session.data === undefined ? undefined : destination(session.data);
  const optionalEnrollment = path === "/totp-setup" && session.data && !session.data.user.totpEnabled;
  const redirect = target && (target !== "/" ? path !== target : authPages.includes(path) && !optionalEnrollment);
  useEffect(() => { if (redirect && target) router.replace(target); }, [redirect, target, router]);
  // SSR includes only a neutral loading state, never auth data or provisioning secrets.
  if (session.isPending || redirect) return <Pending />;
  if (session.isError) {
    if (session.error instanceof ApiError && session.error.status === 403) return <StatusPage status={403} />;
    return <main className="standalone"><h1>Chưa thể mở không gian quản trị</h1><FormError error={session.error} />
      <button className="button" onClick={() => { void session.refetch(); }}>Thử kết nối lại</button></main>;
  }
  const item = navigation.find(item => item.path === path);
  const forbidden = path === "/403" || !!(item?.permission && !session.data?.user.permissions.includes(item.permission));
  // Preserve in-progress inputs/QR during a focus recheck, but keep stale UI hidden.
  return <>{session.isFetching && <Pending />}<div hidden={session.isFetching} key={session.data?.user.id ?? "anonymous"}>
    {!session.data ? <LoginForm /> : path === "/change-password" ? <PasswordForm /> : path === "/totp-setup" ? <TotpForm />
      : <ApplicationShell auth={session.data} path={path}>
    {forbidden ? <StatusPage status={403} embedded /> : path === "/" ? <Welcome auth={session.data} />
      : item ? <ModulePlaceholder title={item.label} /> : <StatusPage status={404} embedded />}
  </ApplicationShell>}</div></>;
}

function ApplicationShell({ auth, path, children }: { auth: Auth; path: string; children: React.ReactNode }) {
  const [collapsed, setCollapsed] = useState(false);
  const [drawer, setDrawer] = useState(false);
  const [logoutPending, setLogoutPending] = useState(false);
  const logoutBusy = useRef(false);
  const runtime = useAdmin();
  const router = useRouter();
  const items = visibleNavigation(auth.user.permissions);
  const current = items.find(item => item.path === path)?.label ?? (path === "/403" ? "Không có quyền truy cập" : "Trang không khả dụng");
  const nav = <nav aria-label="Điều hướng chính"><p className="nav-section">KHÔNG GIAN LÀM VIỆC</p>{items.map(item => {
    const Icon = icons[item.icon];
    return <Link key={item.path} href={item.path} className={`nav-link ${item.path === path ? "active" : ""}`} aria-current={item.path === path ? "page" : undefined}
      title={collapsed ? item.label : undefined} onClick={() => setDrawer(false)}><Icon size={20} aria-hidden="true" /><span>{item.label}</span></Link>;
  })}</nav>;
  return <div className={`app-shell ${collapsed ? "is-collapsed" : ""}`}>
    <a className="skip-link" href="#main-content">Đi đến nội dung chính</a>
    <aside className="sidebar"><Link href="/" className="brand" aria-labelledby="admin-brand-name"><Sprout size={30} aria-hidden="true" /><span id="admin-brand-name">TRÀ HIÊN <small>QUẢN TRỊ</small><span className="sr-only"> Tổng quan</span></span></Link>
      {nav}<div className="sidebar-bottom"><p><ShieldCheck size={16} /><span>Không gian nội bộ</span></p>
        <button className="collapse-button" onClick={() => setCollapsed(!collapsed)} aria-label={collapsed ? "Mở rộng thanh điều hướng" : "Thu gọn thanh điều hướng"}>
          {collapsed ? <PanelLeftOpen size={20} /> : <><PanelLeftClose size={20} /><span>Thu gọn</span></>}</button></div></aside>
    <div className="workspace"><header className="topbar">
      <Dialog.Root open={drawer} onOpenChange={setDrawer}><Dialog.Trigger asChild><button className="icon-button mobile-menu" aria-label="Mở điều hướng"><Menu size={22} /></button></Dialog.Trigger>
        <Dialog.Portal><Dialog.Overlay className="overlay" /><Dialog.Content className="mobile-drawer">
          <Dialog.Title className="brand"><Sprout size={28} /> Trà Hiên</Dialog.Title><Dialog.Description className="sr-only">Điều hướng các khu vực quản trị được cấp quyền.</Dialog.Description>
          {nav}<Dialog.Close className="close-button" aria-label="Đóng điều hướng"><X size={22} /></Dialog.Close>
        </Dialog.Content></Dialog.Portal></Dialog.Root>
      <div className="topbar-context"><span>Không gian quản trị</span><strong>{current}</strong></div>
      <div className="topbar-right"><span className="internal-badge"><span aria-hidden="true" /> NỘI BỘ</span>
        <DropdownMenu.Root><DropdownMenu.Trigger className="account-button" aria-labelledby="admin-account-name">
          <span className="avatar" aria-hidden="true"><UserRoundCog size={19} /></span><span className="account-name" id="admin-account-name"><span className="sr-only">Tài khoản </span>{auth.user.displayName} <small>Nhân viên Trà Hiên</small></span><ChevronDown size={16} aria-hidden="true" />
        </DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content className="account-menu" align="end" sideOffset={8}>
          <DropdownMenu.Label>{auth.user.displayName}<small>{auth.user.email}</small></DropdownMenu.Label><DropdownMenu.Separator />
          {!auth.user.totpEnabled && <DropdownMenu.Item asChild><Link className="menu-logout" href="/totp-setup"><ShieldCheck size={17} /> Thiết lập TOTP</Link></DropdownMenu.Item>}
          <Confirmation title="Kết thúc phiên làm việc?" pending={logoutPending} trigger={<DropdownMenu.Item asChild onSelect={event => event.preventDefault()}><button className="menu-logout"><LogOut size={17} /> Đăng xuất</button></DropdownMenu.Item>}
            onConfirm={async () => {
              if (logoutBusy.current) return; logoutBusy.current = true; setLogoutPending(true);
              try {
                await runtime.api.logout(); await runtime.signedOut(); runtime.setNotice("Bạn đã đăng xuất an toàn."); router.replace("/login");
              } finally { logoutBusy.current = false; setLogoutPending(false); }
            }}>Bạn có thể đăng nhập lại khi cần tiếp tục công việc.</Confirmation>
        </DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root>
      </div></header>
      <main id="main-content" className="main-content" tabIndex={-1}>
        <nav className="breadcrumbs" aria-label="Đường dẫn"><Link href="/">Trà Hiên</Link><ChevronRight size={14} aria-hidden="true" /><span aria-current="page">{current}</span></nav>
        {children}
      </main><footer className="workspace-footer">Trà Hiên <span>Chăm chút từ những điều tỉ mỉ.</span></footer>
    </div>
  </div>;
}

function Welcome({ auth }: { auth: Auth }) {
  const items = visibleNavigation(auth.user.permissions).filter(item => item.path !== "/");
  return <><div className="page-heading"><span className="eyebrow">MỘT NGÀY LÀM VIỆC AN TÂM</span><h1>Chào {auth.user.displayName},</h1><p>Chào mừng bạn đến với không gian quản trị Trà Hiên.</p></div>
    <section className="welcome-panel"><div><span className="section-tag">NỀN TẢNG QUẢN TRỊ</span><h2>Mọi việc bắt đầu<br />từ một không gian gọn gàng.</h2>
      <p>Tài khoản của bạn đã sẵn sàng. Các khu vực bên dưới được hiển thị theo quyền truy cập hiện tại.</p></div><Sprout className="welcome-sprout" size={126} strokeWidth={0.7} aria-hidden="true" /></section>
    <div className="section-heading"><h2>Khu vực làm việc</h2><span>{items.length} khu vực được cấp quyền</span></div>
    <p className="module-notice">Các chức năng nghiệp vụ đang được xây dựng. Hiện chưa có dữ liệu hay thao tác nghiệp vụ trong phiên bản này.</p>
    {items.length ? <div className="module-grid">{items.map(item => { const Icon = icons[item.icon]; return <Link key={item.path} href={item.path} className="module-card">
      <span className="module-icon"><Icon size={23} /></span><ArrowUpRight className="card-arrow" size={18} aria-hidden="true" /><h3>{item.label}</h3><p>Chưa triển khai</p></Link>; })}</div>
      : <div className="empty-state"><ShieldCheck size={28} /><h2>Chưa có khu vực được cấp quyền</h2><p>Liên hệ người quản lý để được hỗ trợ.</p></div>}
    <p className="security-note"><ShieldCheck size={18} /> Quyền truy cập được kiểm tra bởi hệ thống. Hãy đăng xuất khi kết thúc ca làm việc.</p>
  </>;
}

function ModulePlaceholder({ title }: { title: string }) {
  return <><div className="page-heading"><span className="eyebrow">KHU VỰC LÀM VIỆC</span><h1>{title}</h1></div>
    <section className="empty-state"><Package size={36} strokeWidth={1.3} /><span className="section-tag">ĐANG XÂY DỰNG</span><h2>Chức năng chưa được triển khai</h2>
      <p>Khu vực này sẽ được bổ sung trong giai đoạn tiếp theo.<br />Hiện chưa có thao tác xem hay chỉnh sửa dữ liệu nghiệp vụ.</p><Link className="button secondary" href="/">Về tổng quan</Link></section></>;
}

export function StatusPage({ status, embedded = false }: { status: 403 | 404; embedded?: boolean }) {
  const Icon = status === 403 ? LockKeyhole : SearchX;
  const body = <section className="empty-state status-page"><Icon size={40} strokeWidth={1.3} /><span className="eyebrow">{status}</span>
    <h1>{status === 403 ? "Bạn chưa có quyền truy cập" : "Không tìm thấy trang"}</h1>
    <p>{status === 403 ? "Liên hệ người quản lý nếu bạn cần sử dụng khu vực này." : "Đường dẫn có thể đã thay đổi. Hãy trở về không gian làm việc."}</p>
    <Link className="button" href="/">Về tổng quan</Link></section>;
  return embedded ? body : <main id="main-content" className="standalone">{body}</main>;
}
