"use client";

import { useState } from "react";
import type { AttributeRow } from "../../lib/catalog-api";
import { priceLimit, publishIssues, tabLabels, type ProductTab } from "../../lib/product-forms";
import type { Product, ProductValue } from "../../lib/products-api";
import { ProductField as Field, ProductForm, type EditorActions } from "./product-fields";
import { ThinDescriptionNote } from "./product-seo";

function present(values: ProductValue[] | undefined, id: string) {
  return values?.some(value => value.attributeId === id && (value.valueText == null || !!value.valueText.trim())) ?? false;
}

export function PublicationChecklist({ product, definitions, primaryVisible, issues, inspect, compact = false }: {
  product: Product; definitions?: AttributeRow[]; primaryVisible?: boolean;
  issues: ReturnType<typeof publishIssues>; inspect: (tab: ProductTab, target?: string) => void; compact?: boolean;
}) {
  const active = (product.variants ?? []).filter(variant => variant.status === "active");
  const cover = product.media?.find(image => image.isCover);
  const required = definitions?.filter(def => def.isActive && def.isRequiredForPublish);
  const axes = definitions?.filter(def => def.isActive && def.isVariantAxis);
  const combinations = active.map(variant => JSON.stringify(axes?.map(axis => {
    const value = variant.attributes?.find(item => item.attributeId === axis.id);
    return value?.valueNumber != null ? Number(value.valueNumber) : value?.optionId ?? value?.valueText;
  })));
  const checks: [string, ProductTab, boolean | undefined][] = [
    ["Tên, mô tả ngắn và mô tả chi tiết", "information", !!product.name?.trim() && !!product.shortDescription?.trim() && Number(product.descriptionWordCount) > 0],
    ["Danh mục chính hiển thị và thuộc sản phẩm", "information", primaryVisible === undefined && product.primaryCategoryId ? undefined
      : !!primaryVisible && !!product.primaryCategoryId && !!product.categoryIds?.includes(product.primaryCategoryId)],
    ["Có biến thể hoạt động và giá hợp lệ", "variants", active.length > 0 && active.every(variant => {
      const amount = Number(variant.priceVnd); return Number.isSafeInteger(amount) && amount >= 1 && amount <= priceLimit;
    })],
    ["Ảnh bìa có ALT", "images", !!cover?.altText && cover.altText.trim().length >= 3 && cover.altText.trim().length <= 200],
    ["Thuộc tính sản phẩm bắt buộc", "attributes", required?.filter(def => def.scope === "product").every(def => present(product.attributes, def.id))],
    ["Thuộc tính biến thể bắt buộc", "variants", required?.filter(def => def.scope === "variant").every(def => active.every(variant => present(variant.attributes, def.id)))],
    ["Trục biến thể đầy đủ và tổ hợp duy nhất", "variants", active.length < 2 || (axes && active.every(variant => axes.every(axis => present(variant.attributes, axis.id))) && new Set(combinations).size === active.length)],
    ["Tiêu đề và mô tả SEO hợp lệ", "seo", !!product.seoTitle?.trim() && product.seoTitle.length <= 70 && (product.seoDescription?.length ?? 0) >= 50 && (product.seoDescription?.length ?? 0) <= 160]
  ];
  return <section className="product-checklist" aria-label="Checklist công khai">
    {issues.length > 0 && <div className="alert" role="alert"><div><h2>Sản phẩm chưa đủ điều kiện công khai</h2>
      <ul>{issues.map((issue, index) => <li key={`${issue.field}-${index}`}>
        {issue.label && <strong>{issue.label} · </strong>}{issue.message}{" "}
        {issue.tab && <button type="button" className="product-link-button" onClick={() => inspect(issue.tab!, issue.variantId ? `variant-${issue.variantId}`
          : issue.attrId ? `attribute-${issue.attrId}` : issue.field)}>Xem {tabLabels[issue.tab]}</button>}
      </li>)}</ul></div>
    </div>}
    {!compact && <details open={issues.length > 0 || undefined}>
      <summary>Checklist công khai</summary>
      <p>Kiểm tra sơ bộ trên dữ liệu đã lưu. Hệ thống kiểm tra lại khi công khai.</p>
      <ul>{checks.map(([label, tab, passed]) => <li key={label}>
        <span className="catalog-badge">{passed === undefined ? "Chưa kiểm tra" : passed ? "Đạt sơ bộ" : "Cần bổ sung"}</span>
        <button className="product-link-button" type="button" onClick={() => inspect(tab)}>{label}</button>
      </li>)}</ul>
      <ThinDescriptionNote product={product} />
    </details>}
  </section>;
}

export function ArchiveForm({ product, actions, save }: {
  product: Product; actions: EditorActions; save: (redirect?: string) => Promise<unknown>;
}) {
  const [redirect, setRedirect] = useState("");
  return <ProductForm actions={actions} save="Xác nhận lưu trữ" onSave={() => save(redirect.trim() || undefined)}>
    <h2>Lưu trữ sản phẩm</h2>
    {product.firstPublishedAt ? <><p>Sản phẩm đã từng công khai. Để trống để hệ thống dùng nghệ nhân, chất liệu hoặc danh mục chính đủ điều kiện.</p>
      <Field name="redirectTo" label="Đường dẫn chuyển hướng" value={redirect} maxLength={2048} placeholder="/danh-muc/ten-danh-muc"
        onChange={e => { setRedirect(e.target.value); actions.dirty(); }} /></> : <p>Sản phẩm này chưa từng công khai. Sau khi lưu trữ, đường dẫn công khai trả về 404.</p>}
  </ProductForm>;
}
