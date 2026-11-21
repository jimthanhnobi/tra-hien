"use client";

import { useRef, useState } from "react";
import type { components } from "@teapremium/api-client";
import type { Product, ProductImage } from "../../lib/products-api";
import { MediaPicker } from "../catalog/media-picker";
import { ProductField as Field, ProductForm, ProductThumbnail, inputError, type EditorActions } from "./product-fields";

type Asset = components["schemas"]["AssetView"];
type ImageData = { assetId: string; altText: string; variantId: string | null };

export function ProductImages({ product, disabled, editable, onAttach, onEdit, onCover, onOrder, onRemove }: {
  product: Product; disabled: boolean; editable: boolean; onAttach: () => void; onEdit: (image: ProductImage) => void;
  onCover: (id: string) => void; onRemove: (id: string) => void; onOrder: (ids: string[]) => void;
}) {
  const images = [...(product.media ?? [])].sort((a, b) => Number(a.sortOrder ?? 0) - Number(b.sortOrder ?? 0));
  function move(index: number, offset: number) {
    const ids = images.map(image => image.id!);
    [ids[index], ids[index + offset]] = [ids[index + offset], ids[index]];
    onOrder(ids);
  }
  return <>
    <div className="product-heading"><h2 id="media.cover.altText" tabIndex={-1}>Ảnh sản phẩm</h2>
      {editable && <button type="button" className="button" disabled={disabled} onClick={onAttach}>Thêm ảnh</button>}</div>
    {!images.length && <p>Sản phẩm chưa có ảnh.</p>}
    <ol className="product-images">{images.map((image, index) => {
      const variant = product.variants?.find(item => item.id === image.variantId);
      return <li key={image.id} className="product-card">
        <ProductThumbnail size={100} url={image.url} alt={image.altText} /><div><strong>{image.altText || "Thiếu ALT"}</strong>
          {image.isCover && <span className="catalog-badge">Ảnh bìa</span>}
          <p>{variant ? `Biến thể ${variant.name || variant.sku}` : "Ảnh dùng chung"}</p></div>
        {editable && image.id && <div className="catalog-actions">
          <button type="button" className="button secondary" disabled={disabled} onClick={() => onEdit(image)}>Sửa ALT và biến thể</button>
          {!image.isCover && <button type="button" className="button secondary" disabled={disabled} onClick={() => onCover(image.id!)}>Đặt làm ảnh bìa</button>}
          <button type="button" className="button secondary" aria-label={`Đưa ảnh ${index + 1} lên`} disabled={disabled || index === 0} onClick={() => move(index, -1)}>Lên</button>
          <button type="button" className="button secondary" aria-label={`Đưa ảnh ${index + 1} xuống`} disabled={disabled || index === images.length - 1} onClick={() => move(index, 1)}>Xuống</button>
          <button type="button" className="button secondary" disabled={disabled} onClick={() => {
            if (window.confirm("Gỡ ảnh khỏi sản phẩm? Ảnh vẫn còn trong thư viện.")) onRemove(image.id!);
          }}>Gỡ ảnh</button>
        </div>}
      </li>;
    })}</ol>
  </>;
}

export function ProductImageForm({ product, image, actions, upload, save }: {
  product: Product; image?: ProductImage; actions: EditorActions; upload: (file: File) => Promise<Asset | undefined>; save: (body: ImageData) => Promise<unknown>;
}) {
  const [assetId, setAssetId] = useState(image?.assetId ?? "");
  const [asset, setAsset] = useState<Asset>();
  const [altText, setAltText] = useState(image?.altText ?? "");
  const [variantId, setVariantId] = useState(image?.variantId ?? "");
  const [file, setFile] = useState<File>();
  const fileChooser = useRef<HTMLInputElement>(null);
  const changeAsset = (media: Asset) => { setAssetId(media.id); setAsset(media); if (!altText.trim()) setAltText(media.defaultAlt ?? ""); actions.dirty(); };
  const attachedIds = product.media?.flatMap(value => value.assetId ? [value.assetId] : []) ?? [];
  return <ProductForm actions={actions} save={image ? "Lưu ảnh" : "Gắn ảnh"} disabled={!assetId} onSave={async () => {
    const alt = altText.trim();
    if (alt.length < 3 || alt.length > 200) throw inputError("altText", "ALT cần từ 3 đến 200 ký tự.");
    await save({ assetId, altText: alt, variantId: variantId || null });
  }}>
    <h2>{image ? "Sửa ảnh sản phẩm" : "Thêm ảnh sản phẩm"}</h2>
    {assetId && <ProductThumbnail url={asset?.url ?? image?.url} alt={altText} size={100} />}
    {!image && <>
      <MediaPicker value={assetId} label="Thư viện ảnh công khai" selectedLabel={asset?.defaultAlt || "Ảnh công khai"} hideIds={attachedIds}
        onChange={id => { setAssetId(id); if (!id) setAsset(undefined); actions.dirty(); }} onPick={changeAsset} />
      <div className="catalog-actions"><label className="product-field">Tệp ảnh mới<input ref={fileChooser} type="file" accept="image/jpeg,image/png,image/webp,image/avif"
        onChange={e => { const selected = e.target.files?.[0]; setFile(selected); if (selected) actions.dirty(); }} /></label>
        <button type="button" className="button secondary" disabled={!file || actions.stale} onClick={async () => {
          if (!file) return;
          const completed = await upload(file);
          if (completed) { changeAsset(completed); setFile(undefined); if (fileChooser.current) fileChooser.current.value = ""; }
        }}>Tải ảnh lên</button>
      </div>
      {asset && <p role="status">Ảnh đã sẵn sàng. Kiểm tra ALT và chọn Gắn ảnh để lưu vào sản phẩm.</p>}
    </>}
    <div className="catalog-fields">
      <Field name="altText" label="Văn bản thay thế (ALT)" maxLength={200} minLength={3} required value={altText}
        onChange={e => { setAltText(e.target.value); actions.dirty(); }} />
      <label className="product-field">Ảnh cho biến thể<select aria-label="Ảnh cho biến thể" value={variantId} onChange={e => { setVariantId(e.target.value); actions.dirty(); }}>
        <option value="">Dùng chung cho sản phẩm</option>{product.variants?.map(variant => <option key={variant.id} value={variant.id}>
          {variant.name ? `${variant.name} · ${variant.sku}` : variant.sku}</option>)}
      </select></label>
    </div>
  </ProductForm>;
}
