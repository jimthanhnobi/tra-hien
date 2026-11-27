"use client";

import Link from "next/link";
import type { Auth } from "../../lib/api";
import { customerPermissions } from "../../lib/customers-forms";
import { StatusPage } from "../admin-screen";
import { CustomerCreatePanel } from "./customer-create-panel";
import { CustomerDetailScreen } from "./customer-detail-screen";
import { CustomerImportPanel } from "./customer-import-panel";
import { CustomerListPanel } from "./customer-list-panel";
import { CustomerTagsPanel } from "./customer-tags-panel";
import { FollowUpsPanel } from "./follow-ups-panel";

export type CustomersPage =
  | "list"
  | "new"
  | "follow-ups"
  | "import"
  | "tags"
  | { kind: "detail"; id: string };

export function CustomersScreen({ page, user }: { page: CustomersPage; user: Auth["user"] }) {
  const perms = customerPermissions(user.permissions, user.roles);

  // Route-level permission gate before mounting child panels: 0 unauthorized requests fired.
  if (!perms.canRead) return <StatusPage status={403} embedded />;
  if (page === "new" && !perms.canCreate) return <StatusPage status={403} embedded />;
  if (page === "import" && !perms.canImport) return <StatusPage status={403} embedded />;
  if (page === "tags" && !perms.canManageTags) return <StatusPage status={403} embedded />;

  const activeTab =
    page === "follow-ups"
      ? "follow-ups"
      : page === "import"
        ? "import"
        : page === "tags"
          ? "tags"
          : "list";

  const tabs: Array<{ key: string; href: string; label: string; visible: boolean }> = [
    { key: "list", href: "/customers", label: "Khách hàng", visible: perms.canRead },
    { key: "follow-ups", href: "/customers/follow-ups", label: "Hẹn chăm sóc", visible: perms.canRead },
    { key: "import", href: "/customers/import", label: "Import khách cũ", visible: perms.canImport },
    { key: "tags", href: "/customers/tags", label: "Nhãn (Tags)", visible: perms.canManageTags }
  ];

  return (
    <div className="catalog inventory-admin customers-admin">
      <header className="page-heading">
        <span className="eyebrow">QUẢN LÝ KHÁCH HÀNG (CRM)</span>
        <h1>Khách hàng</h1>
        <p>Quản lý hồ sơ khách 360°, lịch sử tương tác, hẹn chăm sóc Zalo/điện thoại và nhập danh sách khách cũ.</p>
      </header>

      <nav className="catalog-nav inventory-nav" aria-label="Điều hướng khách hàng">
        {tabs.filter(tab => tab.visible).map(tab => (
          <Link
            key={tab.key}
            href={tab.href}
            aria-current={activeTab === tab.key ? "page" : undefined}
          >
            {tab.label}
          </Link>
        ))}
      </nav>

      {page === "list" && <CustomerListPanel perms={perms} />}
      {page === "new" && <CustomerCreatePanel />}
      {page === "follow-ups" && <FollowUpsPanel canWrite={perms.canWrite} />}
      {page === "import" && <CustomerImportPanel />}
      {page === "tags" && <CustomerTagsPanel />}
      {typeof page === "object" && page.kind === "detail" && (
        <CustomerDetailScreen key={page.id} id={page.id} perms={perms} />
      )}
    </div>
  );
}
