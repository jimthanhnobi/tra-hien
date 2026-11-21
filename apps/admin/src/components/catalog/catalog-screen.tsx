"use client";

import Link from "next/link";
import { ReferencePanel } from "./reference-panel";
import { TaxonomyPanel } from "./taxonomy-panel";

export const catalogSections = [
  { key: "product-types", title: "Loại sản phẩm" }, { key: "categories", title: "Danh mục" },
  { key: "collections", title: "Bộ sưu tập" }, { key: "materials", title: "Chất liệu" }, { key: "artisans", title: "Nghệ nhân" }
] as const;
export type CatalogSection = typeof catalogSections[number]["key"];

export function CatalogScreen({ section = "product-types", permissions }: { section?: CatalogSection; permissions: string[] }) {
  const title = catalogSections.find(x => x.key === section)!.title;
  return <div className="catalog"><div className="page-heading"><span className="eyebrow">QUẢN LÝ CATALOG</span><h1>Danh mục sản phẩm</h1><p>Chăm chút loại sản phẩm, phân nhóm và nội dung giới thiệu.</p></div>
    <nav className="catalog-nav" aria-label="Quản lý catalog"><Link href="/catalog/products">Sản phẩm</Link>{catalogSections.map(item => <Link key={item.key} href={`/catalog/${item.key}`} aria-current={section === item.key ? "page" : undefined}>{item.title}</Link>)}</nav>
    {section === "product-types" ? <ReferencePanel permissions={permissions} /> : <TaxonomyPanel key={section} resource={section} title={title} permissions={permissions} />}
  </div>;
}
