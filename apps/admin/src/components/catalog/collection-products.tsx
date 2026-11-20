"use client";

import { useRef, useState } from "react";
import { useMutation, useQueries, useQuery } from "@tanstack/react-query";
import type { components } from "@teapremium/api-client";
import { CatalogApi, productDetailOptions } from "../../lib/catalog-api";
import { isConcurrencyConflict } from "../../lib/api";
import { useAdmin } from "../providers";
import { Feedback, FormError, Pending } from "../ui";
import { ProductIdentity, ProductPicker } from "./product-picker";

export type Relations = components["schemas"]["CollectionProductList"];
export const collectionProductsKey = (id: string) => ["catalog", "collection-products", id];
type OrderProps = {
  writable: boolean; disabled: boolean; disabledReason?: string; parentVersion: string;
  onPendingChange: (pending: boolean) => void;
  onDirtyChange: (dirty: boolean) => void;
  isMetadataBusy: () => boolean;
};
const ordered = (items: Relations["items"]) => [...items].sort((a, b) => Number(a.sortOrder) - Number(b.sortOrder));

export function CollectionProducts({ id, onSaved, onReloadMetadata, ...props }: OrderProps & {
  id: string; onSaved: (result: Relations) => void; onReloadMetadata: () => Promise<void>;
}) {
  const runtime = useAdmin(); const api = new CatalogApi(runtime.api);
  const key = collectionProductsKey(id);
  const query = useQuery({ queryKey: key, queryFn: ({ signal }) => api.products(id, signal), refetchOnWindowFocus: false, refetchOnReconnect: false });
  if (query.isPending) return <Pending>Đang tải sản phẩm của bộ sưu tập…</Pending>;
  if (!query.data) return <><FormError error={query.error} /><button type="button" className="button secondary" onClick={() => { void query.refetch(); }}>Thử tải sản phẩm lại</button></>;
  // Version changes update state explicitly, never remount either form.
  return <><FormError error={query.error} /><ProductOrder {...props} data={query.data} reload={async () => {
    await onReloadMetadata();
    const latest = await api.products(id); runtime.queries.setQueryData(key, latest); return latest;
  }} save={async (rowVersion, items) => {
    const result = await api.orderProducts(id, rowVersion, items);
    runtime.queries.setQueryData(key, result); onSaved(result); return result;
  }} /></>;
}
function ProductOrder({ data, writable, disabled, disabledReason, parentVersion, onPendingChange, onDirtyChange, isMetadataBusy, reload, save }: OrderProps & {
  data: Relations; reload: () => Promise<Relations>; save: (version: string, items: Relations["items"]) => Promise<Relations>;
}) {
  const [draft, setDraft] = useState<{ items: Relations["items"]; version: string }>();
  const baseline = draft?.version ?? data.rowVersion;
  const items = draft?.items ?? ordered(data.items);
  const dirty = !!draft;
  const [notice, setNotice] = useState(""); const [conflict, setConflict] = useState(false);
  const [reloading, setReloading] = useState(false); const [reloadError, setReloadError] = useState<unknown>();
  const [pickerOpen, setPickerOpen] = useState(false); const [page, setPage] = useState(1);
  const runtime = useAdmin(); const api = new CatalogApi(runtime.api);
  const currentPage = Math.min(page, Math.max(1, Math.ceil(items.length / 20)));
  const start = (currentPage - 1) * 20;
  const visibleItems = items.slice(start, start + 20);
  // At most twenty observed detail requests; never hydrate the entire collection/catalog.
  const details = useQueries({ queries: visibleItems.map(item => productDetailOptions(api, item.productId)) });
  const mutation = useMutation({ mutationFn: (next: Relations["items"]) => save(baseline, next) });
  const busy = useRef(false);
  const addButton = useRef<HTMLButtonElement>(null); const heading = useRef<HTMLHeadingElement>(null);
  // A newer relation token must never silently rebase an older metadata snapshot.
  const staleParent = baseline !== parentVersion || baseline !== data.rowVersion;
  const blocked = disabled || mutation.isPending || reloading || conflict || staleParent;
  const canEdit = () => writable && !blocked && !busy.current && !isMetadataBusy();
  const changeItems = (next: Relations["items"]) => {
    setDraft({ items: next, version: baseline });
    onDirtyChange(true);
    setNotice("");
  };
  const clearDraft = () => { setDraft(undefined); onDirtyChange(false); };
  const move = (index: number, offset: number) => {
    if (!canEdit() || pickerOpen || index + offset < 0 || index + offset >= items.length) return;
    const next = [...items]; [next[index], next[index + offset]] = [next[index + offset], next[index]];
    changeItems(next);
  };
  const cancel = () => {
    if (!writable || busy.current || pickerOpen || !window.confirm("Bỏ thay đổi danh sách sản phẩm chưa lưu?")) return;
    clearDraft(); setNotice(""); mutation.reset();
  };
  return <section className="catalog-relations" aria-label="Sản phẩm trong bộ sưu tập"><h3 ref={heading} tabIndex={-1}>Sản phẩm trong bộ sưu tập</h3>
    <p className="catalog-note">Thêm, bỏ hoặc sắp xếp sản phẩm rồi lưu danh sách. Bỏ khỏi bộ sưu tập không xóa sản phẩm.</p>
    {writable && <button ref={addButton} type="button" className="button" disabled={blocked || items.length >= 5000} onClick={() => {
      if (!canEdit() || items.length >= 5000) return;
      onDirtyChange(true); setPickerOpen(true);
    }}>Thêm sản phẩm</button>}
    {dirty && <p role="status">Danh sách sản phẩm có thay đổi chưa lưu ({items.length}/5.000).</p>}
    {disabled && disabledReason && <p className="catalog-note" role="status">{disabledReason}</p>}
    {staleParent && <p role="alert">Thông tin bộ sưu tập đã thay đổi. Tải bản mới trước khi sắp xếp sản phẩm.</p>}
    {!items.length ? <p role="status">Bộ sưu tập chưa có sản phẩm.</p> : <ol start={start + 1}>{visibleItems.map((item, localIndex) => {
      const index = start + localIndex, detail = details[localIndex];
      return <li key={item.productId}><div><ProductIdentity product={detail.data} id={item.productId} />
        {detail.isPending && <p role="status">Đang tải thông tin sản phẩm…</p>}
        {detail.isError && <><FormError error={detail.error} /><button type="button" className="button secondary" onClick={() => { void detail.refetch(); }}>Tải lại thông tin {item.productId}</button></>}
      </div>{writable && <div className="catalog-actions"><button type="button" className="button secondary" disabled={index === 0 || blocked} aria-label={`Đưa sản phẩm ${item.productId} lên`} onClick={() => move(index, -1)}>Lên</button>
        <button type="button" className="button secondary" disabled={index === items.length - 1 || blocked} aria-label={`Đưa sản phẩm ${item.productId} xuống`} onClick={() => move(index, 1)}>Xuống</button>
        <button type="button" className="button secondary" disabled={blocked} aria-label={`Bỏ sản phẩm ${item.productId} khỏi bộ sưu tập`} onClick={() => {
          if (canEdit() && !pickerOpen) changeItems(items.filter(x => x.productId !== item.productId));
        }}>Bỏ khỏi bộ sưu tập</button></div>}</li>;
    })}</ol>}
    {items.length > 20 && <div className="catalog-actions"><button type="button" className="button secondary" disabled={currentPage === 1} onClick={() => setPage(currentPage - 1)}>Trang đã chọn trước</button><span>Trang đã chọn {currentPage}</span>
      <button type="button" className="button secondary" disabled={currentPage * 20 >= items.length} onClick={() => setPage(currentPage + 1)}>Trang đã chọn sau</button></div>}
    {pickerOpen && <ProductPicker selectedIds={items.map(item => item.productId)} returnFocus={() => {
      if (addButton.current && !addButton.current.disabled) addButton.current.focus(); else heading.current?.focus();
    }} onClose={() => { setPickerOpen(false); onDirtyChange(dirty); }} onAdd={ids => {
      if (!canEdit()) return;
      const combined = [...new Set([...items.map(item => item.productId), ...ids])];
      if (combined.length > 5000) return;
      changeItems(combined.map((productId, sortOrder) => ({ productId, sortOrder }))); setPickerOpen(false);
    }} />}
    <FormError error={mutation.error} /><FormError error={reloadError} />{notice && <Feedback>{notice}</Feedback>}
    {conflict && <p role="alert">Thứ tự có xung đột. Chưa ghi đè; sao chép thứ tự và thông tin cần giữ trước khi tải bản mới.</p>}
    <div className="catalog-actions">{writable && (items.length > 0 || dirty) && <button type="button" className="button" disabled={!dirty || blocked} onClick={async () => {
      if (!dirty || pickerOpen || !canEdit()) return;
      busy.current = true; onPendingChange(true);
      try {
        await mutation.mutateAsync(items.map((item, sortOrder) => ({ ...item, sortOrder })));
        clearDraft(); setNotice("Đã lưu danh sách sản phẩm.");
      } catch (error) { if (isConcurrencyConflict(error)) setConflict(true); }
      finally { busy.current = false; onPendingChange(false); }
    }}>Lưu danh sách sản phẩm</button>}
      {dirty && <button type="button" className="button secondary" disabled={mutation.isPending || reloading} onClick={cancel}>Hủy thay đổi sản phẩm</button>}
      {(conflict || staleParent) && <button type="button" className="button secondary" disabled={disabled || mutation.isPending || reloading} onClick={async () => {
        if (disabled || busy.current || pickerOpen || isMetadataBusy() || !window.confirm("Bỏ thay đổi thứ tự và thông tin chưa lưu, rồi tải bản mới?")) return;
        busy.current = true; setReloading(true); onPendingChange(true); setReloadError(undefined);
        try {
          await reload(); clearDraft(); setConflict(false); mutation.reset(); setNotice("");
        } catch (error) { setReloadError(error); }
        finally { busy.current = false; setReloading(false); onPendingChange(false); }
      }}>Tải lại thứ tự</button>}</div>
  </section>;
}
