"use client";

import { useState } from "react";
import { nullable, thinDescription } from "../../lib/product-forms";
import type { Product, ProductPatch } from "../../lib/products-api";
import { MediaPicker } from "../catalog/media-picker";
import { ProductField as Field, ProductForm, TextArea, inputError, type EditorActions } from "./product-fields";

export function ThinDescriptionNote({ product }: { product: Product }) {
  return thinDescription(product) ? <p className="product-warning" role="status">
    Mô tả đã lưu có {product.descriptionWordCount ?? "dưới 150"} từ. Bạn vẫn có thể công khai nếu đủ điều kiện. Hệ thống áp dụng noindex cho đến khi mô tả có ít nhất 150 từ. Cờ noindex thủ công được giữ riêng.
  </p> : null;
}

type SeoText = "slug" | "seoTitle" | "seoDescription" | "focusKeyword" | "canonicalUrl" | "noindexReason";

export function ProductSeo({ product, actions, save }: {
  product: Product; actions?: EditorActions; save?: (body: ProductPatch) => Promise<unknown>;
}) {
  const [patch, setPatch] = useState<ProductPatch>({ rowVersion: product.rowVersion! });
  const current = { ...product, ...patch };
  const requiresReason = !!current.robotsNoindex && !product.robotsNoindex;
  function change<K extends keyof ProductPatch>(key: K, value: ProductPatch[K]) {
    setPatch(old => ({ ...old, [key]: value })); actions?.dirty();
  }
  function text(key: SeoText, value: string) { change(key, key === "slug" ? value : nullable(value)); }
  function autoFill(key: "seoDescription" | "seoTitle", source: string, limit: number) {
    if (!source || (current[key] && !window.confirm("Thay nội dung SEO hiện tại bằng nội dung đề xuất?"))) return;
    let suggestion = "";
    for (const word of source.trim().split(/\s+/)) {
      const candidate = suggestion ? `${suggestion} ${word}` : word;
      if (candidate.length > limit) break;
      suggestion = candidate;
    }
    if (suggestion) change(key, suggestion);
  }
  const content = <>
    <ThinDescriptionNote product={product} />
    <div className="catalog-fields">
      <div>
        <Field name="seoTitle" label="Tiêu đề SEO" value={current.seoTitle ?? ""} maxLength={70}
          hint={`${current.seoTitle?.length ?? 0}/70 ký tự`} onChange={e => text("seoTitle", e.target.value)} />
        {actions && <button type="button" className="button secondary" disabled={!product.name} onClick={() => autoFill("seoTitle", product.name ?? "", 70)}>Điền từ tên sản phẩm</button>}
      </div>
      <div>
        <TextArea label="Mô tả SEO" id="seoDescription" value={current.seoDescription ?? ""} maxLength={160} onChange={v => text("seoDescription", v)} />
        <p className="hint">{current.seoDescription?.length ?? 0}/160 ký tự. Cần 50–160 ký tự khi công khai.</p>
        {actions && <button type="button" className="button secondary" disabled={!product.shortDescription} onClick={() => autoFill("seoDescription", product.shortDescription ?? "", 160)}>Điền từ mô tả ngắn</button>}
      </div>
      <Field name="slug" label="Slug" value={current.slug ?? ""} required maxLength={80} onChange={e => text("slug", e.target.value)} />
      <Field name="focusKeyword" label="Từ khóa trọng tâm" value={current.focusKeyword ?? ""} onChange={e => text("focusKeyword", e.target.value)} />
      <Field name="canonicalUrl" label="URL canonical" value={current.canonicalUrl ?? ""} type="url" onChange={e => text("canonicalUrl", e.target.value)} />
      <label className="product-check"><input type="checkbox" checked={!!current.seoKeepAfterSold} onChange={e => change("seoKeepAfterSold", e.target.checked)} /> Giữ trang hàng độc bản sau khi bán</label>
      <label className="product-check"><input type="checkbox" checked={!!current.robotsNoindex} onChange={e => {
        change("robotsNoindex", e.target.checked);
        if (!e.target.checked) setPatch(old => { const next = { ...old }; delete next.noindexReason; return next; });
      }} /> Noindex thủ công</label>
      {requiresReason && <TextArea label="Lý do noindex" id="noindexReason" value={patch.noindexReason ?? ""} onChange={v => text("noindexReason", v)} />}
    </div>
    <section className="product-seo-preview" aria-label="Bản xem trước tìm kiếm"><strong>Bản xem trước tìm kiếm</strong>
      <h3>{current.seoTitle || "Chưa có tiêu đề SEO"}</h3><p>/san-pham/{current.slug || "…"}</p><p>{current.seoDescription || "Chưa có mô tả SEO"}</p>
    </section>
    {actions ? <MediaPicker label="Ảnh Open Graph" value={current.ogMediaId ?? ""} onChange={id => change("ogMediaId", id || null)} />
      : <p>Ảnh Open Graph · {current.ogMediaId ? "Đã chọn ảnh" : "Chưa chọn ảnh"}</p>}
  </>;
  if (!actions || !save) return <fieldset disabled className="catalog-fieldset product-readonly">{content}</fieldset>;
  return <ProductForm actions={actions} disabled={Object.keys(patch).length <= 1} onSave={async () => {
    if (requiresReason && !patch.noindexReason?.trim()) throw inputError("noindexReason", "Nhập lý do bật noindex thủ công.");
    await save(patch);
  }}>{content}</ProductForm>;
}
