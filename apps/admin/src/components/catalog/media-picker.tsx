"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { CatalogApi } from "../../lib/catalog-api";
import { useAdmin } from "../providers";
import { FormError, Pending } from "../ui";

export function MediaPicker({ value, onChange, label }: { value: string; onChange: (id: string) => void; label: string }) {
  const { api: admin } = useAdmin(); const api = new CatalogApi(admin);
  const [open, setOpen] = useState(false); const [q, setQ] = useState(""); const [page, setPage] = useState(1);
  const query = useQuery({ queryKey: ["catalog", "public-images", page, q], queryFn: ({ signal }) => api.media(page, q, signal), enabled: open });
  return <section className="catalog-media" aria-label={label}><h3>{label}</h3>
    <p>{value ? `Ảnh đã chọn: ${value}` : "Chưa chọn ảnh."}</p>
    <div className="catalog-actions"><button className="button secondary" type="button" onClick={() => setOpen(!open)}>{open ? "Đóng thư viện ảnh" : "Chọn từ thư viện ảnh"}</button>
      {value && <button className="button secondary" type="button" onClick={() => onChange("")}>Bỏ chọn ảnh</button>}</div>
    {open && <><label className="catalog-search">Tìm ảnh<input type="search" value={q} maxLength={200} onChange={e => { setQ(e.target.value); setPage(1); }} /></label>
      {query.isPending ? <Pending>Đang tải ảnh…</Pending> : query.isError ? <><FormError error={query.error} /><button type="button" className="button secondary" onClick={() => { void query.refetch(); }}>Thử tải ảnh lại</button></>
        : <><ul className="catalog-image-list">{query.data.items.filter(x => x.purpose !== "evidence" && x.mimeType.startsWith("image/") && !x.urlExpiresAt).map(image => <li key={image.id}>
          <button type="button" className="button secondary" aria-pressed={value === image.id} onClick={() => { onChange(image.id); setOpen(false); }}>{image.defaultAlt || "Ảnh chưa có mô tả"} · {image.id}</button>
        </li>)}</ul>{!query.data.items.some(x => x.purpose !== "evidence" && x.mimeType.startsWith("image/") && !x.urlExpiresAt) && <p role="status">Không có ảnh công khai phù hợp trên trang này.</p>}
          <div className="catalog-actions"><button type="button" className="button secondary" disabled={page === 1} onClick={() => setPage(page - 1)}>Trang ảnh trước</button>
            <span>Trang {page}</span><button type="button" className="button secondary" disabled={page * 20 >= Number(query.data.totalItems)} onClick={() => setPage(page + 1)}>Trang ảnh sau</button></div></>}
    </>}
  </section>;
}
