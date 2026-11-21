"use client";

import { useEffect, useRef, useState } from "react";
import { useQueries } from "@tanstack/react-query";
import { CatalogApi, productDetailOptions } from "../../lib/catalog-api";
import { nullable } from "../../lib/product-forms";
import type { Product, ProductPatch } from "../../lib/products-api";
import { ProductPicker } from "../catalog/product-picker";
import { useAdmin } from "../providers";
import { FormError, Pending } from "../ui";
import { ProductField as Field, ProductForm, TextArea, inputError, toggleId, type EditorActions } from "./product-fields";
import { ProductRichText } from "./product-rich-text";
import { ReferenceSelect, useProductTypes, useTaxonomy } from "./product-references";

function RelatedProducts({ ids, productId, onChange }: { ids: string[]; productId: string; onChange?: (ids: string[]) => void }) {
  const catalog = new CatalogApi(useAdmin().api);
  const results = useQueries({ queries: ids.map(id => productDetailOptions(catalog, id)) });
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  return <section aria-label="Sản phẩm liên quan"><h3 ref={heading} tabIndex={-1}>Sản phẩm liên quan</h3>
    {!ids.length && <p>Chưa có sản phẩm liên quan.</p>}
    <ul className="product-related">{ids.map((id, index) => <li key={id}>
      <span>{results[index].data?.name ?? (results[index].isPending ? "Đang tải tên sản phẩm…" : "Sản phẩm đã lưu · chưa tải được tên")}</span>
      {onChange && <button type="button" className="button secondary" aria-label={`Bỏ ${results[index].data?.name ?? `sản phẩm liên quan ${index + 1}`}`}
        onClick={() => { onChange(ids.filter(value => value !== id)); heading.current?.focus(); }}>Bỏ</button>}
    </li>)}</ul>
    {onChange && <button type="button" className="button secondary" ref={trigger} disabled={ids.length >= 8} onClick={() => setOpen(true)}>Chọn sản phẩm liên quan</button>}
    {open && onChange && <ProductPicker selectedIds={ids} excludedId={productId} limit={8} onClose={() => setOpen(false)} returnFocus={() => {
      if (trigger.current?.disabled) heading.current?.focus(); else trigger.current?.focus();
    }} onAdd={selected => { onChange([...ids, ...selected]); setOpen(false); }} />}
  </section>;
}

type InfoText = "name" | "subtitle" | "slug" | "shortDescription" | "originNote";

export function ProductInformation({ product, actions, save }: {
  product: Product; actions?: EditorActions; save?: (body: ProductPatch) => Promise<unknown>;
}) {
  const types = useProductTypes();
  const categories = useTaxonomy("categories");
  const materials = useTaxonomy("materials");
  const artisans = useTaxonomy("artisans");
  const [draft, setPatch] = useState<Omit<ProductPatch, "rowVersion">>({});
  const [editing, setEditing] = useState(!!actions);
  const [confirmed, setConfirmed] = useState(false);
  if (editing !== !!actions) {
    setEditing(!!actions);
    setPatch({});
    setConfirmed(false);
  }
  const patch = actions ? draft : {};
  const markDirty = actions?.dirty;
  const hasChanges = Object.keys(patch).length > 0;
  useEffect(() => { markDirty?.(hasChanges); }, [markDirty, hasChanges]);
  const typeId = patch.productTypeId ?? product.productTypeId ?? "";
  const typeChanged = typeId !== product.productTypeId;
  const categoryIds = patch.categoryIds ?? product.categoryIds ?? [];
  const current = { ...product, ...patch };
  function change<K extends keyof ProductPatch>(key: K, value: ProductPatch[K]) {
    setPatch(old => ({ ...old, [key]: value })); actions?.dirty();
  }
  function text(key: InfoText, value: string) {
    change(key, key === "name" || key === "slug" ? value : nullable(value));
  }
  function rich(htmlKey: "descriptionHtml" | "careHtml", value?: { html: string | null } | { json: unknown }) {
    setPatch(old => {
      const next = { ...old };
      delete next[htmlKey];
      if (htmlKey === "descriptionHtml") delete next.descriptionJson;
      if (value && "html" in value) next[htmlKey] = value.html;
      if (value && "json" in value) next.descriptionJson = value.json;
      return next;
    });
    if (value) actions?.dirty();
  }
  const failed = [types, categories, materials, artisans].find(query => query.isError);
  const content = <>
    <div className="catalog-fields">
      <Field name="name" label="Tên sản phẩm" value={current.name ?? ""} required minLength={2} maxLength={200} onChange={e => text("name", e.target.value)} />
      <Field name="subtitle" label="Tên phụ" value={current.subtitle ?? ""} onChange={e => text("subtitle", e.target.value)} />
      <Field name="slug" label="Slug" value={current.slug ?? ""} maxLength={80} required onChange={e => text("slug", e.target.value)} />
      <ReferenceSelect label="Loại sản phẩm" value={typeId} rows={types.data ?? []} activeOnly disabled={product.status !== "draft" || types.isPending} onChange={value => {
        if (value) { change("productTypeId", value); setConfirmed(false); }
      }} />
      <ReferenceSelect label="Chất liệu" value={current.materialId ?? ""} rows={materials.data ?? []} disabled={materials.isPending} onChange={value => change("materialId", value || null)} />
      <ReferenceSelect label="Nghệ nhân" value={current.artisanId ?? ""} rows={artisans.data ?? []} disabled={artisans.isPending} onChange={value => change("artisanId", value || null)} />
      <TextArea id="shortDescription" label="Mô tả ngắn" value={current.shortDescription ?? ""} onChange={value => text("shortDescription", value)} />
      <TextArea id="originNote" label="Ghi chú xuất xứ" value={current.originNote ?? ""} onChange={value => text("originNote", value)} />
      <ReferenceSelect label="Danh mục chính" value={current.primaryCategoryId ?? ""} rows={categories.data ?? []} disabled={categories.isPending} onChange={value => {
        change("primaryCategoryId", value || null);
        if (value && !categoryIds.includes(value)) change("categoryIds", [...categoryIds, value]);
      }} />
      <fieldset className="product-checks"><legend>Danh mục của sản phẩm</legend>
        {categories.data?.map(category => <label key={category.id}><input type="checkbox" checked={categoryIds.includes(category.id)}
          onChange={event => {
            change("categoryIds", toggleId(categoryIds, category.id, event.target.checked));
            if (!event.target.checked && current.primaryCategoryId === category.id) change("primaryCategoryId", null);
          }} />{category.name}{category.status === "hidden" ? " · Ẩn" : ""}</label>)}
      </fieldset>
      <label className="product-check"><input type="checkbox" checked={!!current.isFeatured} onChange={event => change("isFeatured", event.target.checked)} /> Sản phẩm nổi bật</label>
    </div>
    {typeChanged && <label className="product-check alert"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} />
      Xác nhận đổi loại sản phẩm. Hệ thống sẽ xóa giá trị thuộc tính của sản phẩm và biến thể không thuộc loại mới. SKU đã cấp được giữ nguyên.
    </label>}
    {failed && <><FormError error={failed.error} /><button type="button" className="button secondary" onClick={() => void failed.refetch()}>Tải lại dữ liệu tham chiếu</button></>}
    {[types, categories, materials, artisans].some(query => query.isPending) && <Pending>Đang tải dữ liệu tham chiếu…</Pending>}
    <ProductRichText id="descriptionHtml" label="Mô tả chi tiết" initialHtml={product.descriptionHtml} initialJson={product.descriptionJson} disabled={actions?.pending}
      onChange={actions ? value => rich("descriptionHtml", value) : undefined} />
    <ProductRichText id="careHtml" label="Hướng dẫn bảo quản" initialHtml={product.careHtml} disabled={actions?.pending}
      onChange={actions ? value => rich("careHtml", value) : undefined} />
    <RelatedProducts productId={product.id!} ids={current.relatedProductIds ?? []} onChange={actions ? ids => change("relatedProductIds", ids) : undefined} />
  </>;
  return <ProductForm actions={actions} disabled={!Object.keys(patch).length || (typeChanged && !confirmed)} onSave={async () => {
    if (typeChanged && !confirmed) return;
    if ((current.name?.trim().length ?? 0) < 2) throw inputError("name", "Tên sản phẩm cần ít nhất 2 ký tự.");
    const body = { ...patch, ...(patch.name !== undefined && { name: patch.name.trim() }), ...(typeChanged && { confirmTypeChange: true }) };
    if (!typeChanged) delete body.productTypeId;
    await save?.({ ...body, rowVersion: product.rowVersion! });
  }}>{content}</ProductForm>;
}
