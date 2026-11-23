"use client";

import Link from "next/link";
import type { Auth } from "../../lib/api";
import { inventoryPermissions } from "../../lib/inventory-forms";
import { StatusPage } from "../admin-screen";
import { AdjustmentsPanel } from "./adjustments-panel";
import { MovementsPanel } from "./movements-panel";
import { OpeningImportPanel } from "./opening-import-panel";
import { ReceiptCreate, ReceiptDetail } from "./receipt-editor";
import { ReceiptsPanel } from "./receipts-panel";
import { StockListPanel } from "./stock-list-panel";
import { StocktakeDetail } from "./stocktake-detail";
import { StocktakesPanel } from "./stocktakes-panel";

export type InventoryPage =
  | "stock"
  | { kind: "movements"; variantId: string }
  | "opening-import"
  | "receipts"
  | { kind: "receipt-new"; variantId?: string }
  | { kind: "receipt-detail"; id: string }
  | "adjustments"
  | "stocktakes"
  | { kind: "stocktake-detail"; id: string };

export function InventoryScreen({ page, user }: { page: InventoryPage; user: Auth["user"] }) {
  const perms = inventoryPermissions(user.permissions);

  // Route-level permission gate before mounting child panels: 0 unauthorized requests fired.
  if (!perms.canRead) return <StatusPage status={403} embedded />;
  if (typeof page === "object" && page.kind === "movements" && !perms.canViewMovements) {
    return <StatusPage status={403} embedded />;
  }
  if (page === "opening-import" && !perms.canOpenImport) {
    return <StatusPage status={403} embedded />;
  }
  if ((page === "receipts" || (typeof page === "object" && (page.kind === "receipt-new" || page.kind === "receipt-detail"))) && !perms.canUseReceipts) {
    return <StatusPage status={403} embedded />;
  }
  if (page === "adjustments" && !perms.canAdjust) {
    return <StatusPage status={403} embedded />;
  }
  if ((page === "stocktakes" || (typeof page === "object" && page.kind === "stocktake-detail")) && !perms.canUseStocktakes) {
    return <StatusPage status={403} embedded />;
  }

  const activeTab = page === "stock" || (typeof page === "object" && page.kind === "movements")
    ? "stock"
    : page === "receipts" || (typeof page === "object" && (page.kind === "receipt-new" || page.kind === "receipt-detail"))
      ? "receipts"
      : page === "adjustments"
        ? "adjustments"
        : page === "stocktakes" || (typeof page === "object" && page.kind === "stocktake-detail")
          ? "stocktakes"
          : "opening-import";

  const tabs: Array<{ key: string; href: string; label: string; visible: boolean }> = [
    { key: "stock", href: "/inventory", label: "Tồn kho", visible: perms.canRead },
    { key: "receipts", href: "/inventory/receipts", label: "Phiếu nhập", visible: perms.canUseReceipts },
    { key: "adjustments", href: "/inventory/adjustments", label: "Điều chỉnh", visible: perms.canAdjust },
    { key: "stocktakes", href: "/inventory/stocktakes", label: "Kiểm kê", visible: perms.canUseStocktakes },
    { key: "opening-import", href: "/inventory/opening-import", label: "Tồn đầu kỳ", visible: perms.canOpenImport }
  ];

  return (
    <div className="catalog inventory-admin">
      <header className="page-heading">
        <span className="eyebrow">QUẢN LÝ KHO HÀNG</span>
        <h1>Kho hàng</h1>
        <p>Theo dõi tồn kho, sổ kho, phiếu nhập, điều chỉnh và kiểm kê theo quyền được cấp.</p>
      </header>

      <nav className="catalog-nav inventory-nav" aria-label="Điều hướng kho hàng">
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

      {page === "stock" && <StockListPanel perms={perms} />}
      {typeof page === "object" && page.kind === "movements" && <MovementsPanel variantId={page.variantId} perms={perms} />}
      {page === "opening-import" && <OpeningImportPanel />}
      {page === "receipts" && <ReceiptsPanel />}
      {typeof page === "object" && page.kind === "receipt-new" && <ReceiptCreate perms={perms} initialVariantId={page.variantId} />}
      {typeof page === "object" && page.kind === "receipt-detail" && <ReceiptDetail key={page.id} id={page.id} perms={perms} />}
      {page === "adjustments" && <AdjustmentsPanel perms={perms} />}
      {page === "stocktakes" && <StocktakesPanel />}
      {typeof page === "object" && page.kind === "stocktake-detail" && <StocktakeDetail key={page.id} id={page.id} perms={perms} />}
    </div>
  );
}
