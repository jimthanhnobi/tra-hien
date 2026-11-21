"use client";

import { useState } from "react";
import Image from "next/image";
import * as Dialog from "@radix-ui/react-dialog";
import { useQuery } from "@tanstack/react-query";
import { CatalogApi, productSearchOptions, type ProductDisplay, type ProductStatus } from "../../lib/catalog-api";
import { useAdmin } from "../providers";
import { FormError, Pending } from "../ui";

const productStatuses: Record<string, string> = { draft: "Bản nháp", active: "Đang bán", archived: "Đã lưu trữ" };

export function ProductIdentity({ product, id }: { product?: ProductDisplay; id: string }) {
  const [failedUrl, setFailedUrl] = useState<string>();
  const cover = product?.cover;
  const url = cover?.url;
  const safeUrl = url && (/^https:\/\//i.test(url) || (url.startsWith("/") && !url.startsWith("//")));
  return <span className="catalog-product-identity">
    {safeUrl && failedUrl !== url ? <Image unoptimized src={url} width={56} height={56} alt={cover?.altText || product?.name || "Ảnh sản phẩm"} onError={() => setFailedUrl(url)} /> : <span className="catalog-product-placeholder" aria-hidden="true">Ảnh</span>}
    <span><strong>{product?.name || "Sản phẩm chưa tải được thông tin"}</strong><span className="catalog-product-sku">{product?.skuBase || "Chưa có SKU"}</span>
      <span className="catalog-badge">{product?.status ? productStatuses[product.status] ?? product.status : "Chưa rõ trạng thái"}</span>
      <small className="catalog-product-id">{id}</small></span>
  </span>;
}

export function ProductPicker({ selectedIds, onAdd, onClose, returnFocus, excludedId, limit = 5000 }: {
  selectedIds: string[]; onAdd: (ids: string[]) => void; onClose: () => void; returnFocus: () => void; excludedId?: string; limit?: number;
}) {
  const runtime = useAdmin(); const api = new CatalogApi(runtime.api);
  const [q, setQ] = useState(""); const [status, setStatus] = useState<ProductStatus | "">(""); const [page, setPage] = useState(1);
  const [chosen, setChosen] = useState<string[]>([]);
  const existing = new Set(selectedIds);
  const full = existing.size + chosen.length >= limit;
  const query = useQuery(productSearchOptions(api, runtime.queries, q, status, page));
  const close = () => { if (!chosen.length || window.confirm(excludedId ? "Bỏ lựa chọn sản phẩm liên quan?" : "Bỏ lựa chọn chưa thêm vào bộ sưu tập?")) onClose(); };
  return <Dialog.Root open onOpenChange={open => { if (!open) close(); }}><Dialog.Portal><Dialog.Overlay className="overlay" />
    <Dialog.Content className="dialog catalog catalog-product-dialog" onCloseAutoFocus={event => { event.preventDefault(); returnFocus(); }}>
      <Dialog.Title>Thêm sản phẩm</Dialog.Title><Dialog.Description>Tìm theo tên hoặc SKU. Chọn nhiều sản phẩm rồi thêm vào danh sách; chỉ ghi lên hệ thống khi bạn lưu danh sách.</Dialog.Description>
      <div className="catalog-filters"><label>Tìm sản phẩm theo tên hoặc SKU<input autoFocus type="search" maxLength={200} value={q} onChange={e => { setQ(e.target.value); setPage(1); }} /></label>
        <label>Trạng thái sản phẩm<select value={status} onChange={e => { setStatus(e.target.value as ProductStatus | ""); setPage(1); }}><option value="">Tất cả trạng thái</option>
          {Object.entries(productStatuses).map(([value, name]) => <option key={value} value={value}>{name}</option>)}</select></label></div>
      <p role="status">Đã chọn thêm {chosen.length} sản phẩm. {excludedId ? `Tối đa ${limit} sản phẩm liên quan.` : "Bộ sưu tập tối đa 5.000 sản phẩm."}</p>
      <div className="catalog-product-scroll">{query.isPending ? <Pending>Đang tìm sản phẩm…</Pending> : query.isError ? <><FormError error={query.error} /><button className="button secondary" type="button" onClick={() => { void query.refetch(); }}>Thử tìm lại</button></> : <>
        {!query.data.items.length && <p role="status">Không tìm thấy sản phẩm phù hợp.</p>}
        <ul className="catalog-product-results">{query.data.items.map(product => product.id && product.id !== excludedId && <li key={product.id}>
          <label className="catalog-product-choice"><input type="checkbox" aria-label={`Chọn ${product.name?.trim() || "sản phẩm"}, ${product.skuBase?.trim() ? `SKU ${product.skuBase.trim()}` : `mã ${product.id}`}`} checked={existing.has(product.id) || chosen.includes(product.id)} disabled={existing.has(product.id) || (full && !chosen.includes(product.id))}
            onChange={e => { const id = product.id!; setChosen(old => e.target.checked ? [...new Set([...old, id])] : old.filter(x => x !== id)); }} />
            <ProductIdentity product={product} id={product.id} />{existing.has(product.id) && <span className="catalog-product-existing">Đã trong danh sách</span>}</label>
        </li>)}</ul>
        <div className="catalog-actions"><button type="button" className="button secondary" disabled={page === 1} onClick={() => setPage(page - 1)}>Trang sản phẩm trước</button><span>Trang {page} · {query.data.totalItems} kết quả</span>
          <button type="button" className="button secondary" disabled={page * 20 >= Number(query.data.totalItems)} onClick={() => setPage(page + 1)}>Trang sản phẩm sau</button></div>
      </>}</div>
      <div className="dialog-actions"><button type="button" className="button secondary" onClick={close}>Hủy chọn</button>
        <button type="button" className="button" disabled={!chosen.length} onClick={() => onAdd(chosen)}>Thêm {chosen.length} sản phẩm đã chọn</button></div>
    </Dialog.Content></Dialog.Portal></Dialog.Root>;
}
