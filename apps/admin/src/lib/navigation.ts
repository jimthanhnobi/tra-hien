import type { Auth } from "./api";

export const navigation = [
  { path: "/", label: "Tổng quan", permission: null, icon: "home" },
  { path: "/catalog", label: "Sản phẩm", permission: "catalog.read", icon: "package" },
  { path: "/inventory", label: "Kho hàng", permission: "inventory.read", icon: "warehouse" },
  { path: "/orders", label: "Đơn hàng", permission: "orders.read", icon: "orders" },
  { path: "/customers", label: "Khách hàng", permission: "customers.read", icon: "users" },
  { path: "/marketing", label: "Tiếp thị", permission: "marketing.write", icon: "megaphone" },
  { path: "/content", label: "Nội dung", permission: "content.write", icon: "file" },
  { path: "/finance", label: "Tài chính", permission: "finance.read", icon: "chart" },
  { path: "/settings", label: "Cài đặt", permission: "settings.write", icon: "settings" },
  { path: "/staff", label: "Nhân viên", permission: "staff.manage", icon: "staff" }
] as const;

export function visibleNavigation(permissions: readonly string[]) {
  return navigation.filter(item => item.permission === null || permissions.includes(item.permission));
}

// BR-IAM-02: backend flags alone decide onboarding; roles never grant UI permissions.
export function destination(auth: Auth | null) {
  if (!auth) return "/login";
  if (auth.passwordChangeRequired) return "/change-password";
  if (auth.totpRequired) return "/totp-setup";
  return "/";
}
