"use client";

import { useRef, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { CatalogApi, type CatalogResource, type CatalogRow } from "../../lib/catalog-api";
import { categoryPath, defaults, paragraphHtml, plainText, statuses, taxonomyFields, validParent, type FormField, type Values } from "../../lib/catalog-forms";
import { ApiError, isConcurrencyConflict } from "../../lib/api";
import { useAdmin } from "../providers";
import { DataTable, paginationParams } from "../data-table";
import { Confirmation, Feedback, FormError, Pending } from "../ui";
import { CatalogForm } from "./catalog-form";
import { MediaPicker } from "./media-picker";
import { CollectionProducts, collectionProductsKey, type Relations } from "./collection-products";

export function TaxonomyPanel({ resource, title, permissions }: { resource: CatalogResource; title: string; permissions: string[] }) {
  const runtime = useAdmin(); const api = new CatalogApi(runtime.api);
  const [pagination, setPagination] = useState({ pageIndex: 0, pageSize: 20 });
  const [q, setQ] = useState(""); const [status, setStatus] = useState(""); const [editing, setEditing] = useState<string>(); const [notice, setNotice] = useState("");
  const writable = permissions.includes("catalog.write"); const editorial = resource === "materials" || resource === "artisans";
  const query = useQuery({ queryKey: ["catalog", resource, pagination, q, status], queryFn: ({ signal }) => api.list(resource, { ...paginationParams(pagination), q: q || undefined, status: status || undefined }, signal) });
  const tree = useQuery({ queryKey: ["catalog", "category-tree"], queryFn: ({ signal }) => api.categoryTree(signal), enabled: resource === "categories" });
  return <section className="catalog-panel"><div className="catalog-toolbar"><h2>{title}</h2>
    {writable && !editing && <button className="button" onClick={() => { setEditing("new"); setNotice(""); }}>Thêm mới</button>}</div>
    {notice && <Feedback>{notice}</Feedback>}
    {editing ? <TaxonomyEditor key={editing} id={editing} resource={resource} permissions={permissions} tree={tree.data} treeError={tree.error} reloadTree={() => tree.refetch()} onCancel={() => setEditing(undefined)} onSaved={async () => {
      await runtime.queries.invalidateQueries({ queryKey: ["catalog", resource] });
      if (resource === "categories") await runtime.queries.invalidateQueries({ queryKey: ["catalog", "category-tree"] });
      setEditing(undefined); setNotice("Đã lưu thông tin.");
    }} /> : <>
      <div className="catalog-filters"><label>Tìm theo tên<input type="search" value={q} maxLength={200} onChange={e => { setQ(e.target.value); setPagination({ ...pagination, pageIndex: 0 }); }} /></label>
        <label>Lọc trạng thái<select value={status} onChange={e => { setStatus(e.target.value); setPagination({ ...pagination, pageIndex: 0 }); }}><option value="">Tất cả trạng thái</option>
          {(editorial ? ["draft", "published", "archived"] : ["hidden", "visible"]).map(s => <option key={s} value={s}>{statuses[s]}</option>)}</select></label></div>
      {resource === "categories" && tree.isError && <FormError error={tree.error} />}
      <DataTable<CatalogRow> caption={title} data={query.data?.items ?? []} totalItems={Number(query.data?.totalItems ?? 0)} pagination={pagination} onPaginationChange={setPagination}
        loading={query.isPending} error={query.error} onRetry={() => { void query.refetch(); }} getRowId={row => row.id} columns={[
          { accessorKey: "name", header: resource === "categories" ? "Danh mục / Phân cấp" : "Tên", cell: ({ row }) => <span>{resource === "categories" ? categoryPath(row.original, tree.data ?? []) : row.original.name}</span> },
          { accessorKey: "slug", header: "Đường dẫn" }, { accessorKey: "status", header: "Trạng thái", cell: ({ row }) => <span className="catalog-badge">{statuses[row.original.status] ?? row.original.status}</span> },
          ...(resource !== "artisans" ? [{ accessorKey: "sortOrder", header: "Thứ tự" }] : []),
          { id: "actions", header: "Thao tác", cell: ({ row }) => <button className="button secondary" onClick={() => { setEditing(row.original.id); setNotice(""); }}>{writable ? "Chỉnh sửa" : "Xem"} {row.original.name}</button> }
        ]} />
    </>}
  </section>;
}

function TaxonomyEditor({ id, resource, permissions, tree, treeError, reloadTree, onCancel, onSaved }: {
  id: string; resource: CatalogResource; permissions: string[]; tree?: CatalogRow[]; treeError: unknown; reloadTree: () => Promise<unknown>; onCancel: () => void; onSaved: () => Promise<void>;
}) {
  const runtime = useAdmin(); const api = new CatalogApi(runtime.api);
  const [warnings, setWarnings] = useState<string[]>([]); const [notice, setNotice] = useState("");
  const [reloadError, setReloadError] = useState<unknown>();
  const [reorderPending, setReorderPending] = useState(false);
  const [productsDirty, setProductsDirty] = useState(false);
  const relatedEditing = useRef(false);
  const metadataBusy = useRef(false);
  const detailKey = ["catalog", "detail", resource, id];
  const query = useQuery({ queryKey: detailKey, queryFn: ({ signal }) => api.get(resource, id, signal), enabled: id !== "new", refetchOnWindowFocus: false, refetchOnReconnect: false });
  const publish = useMutation({ mutationFn: async (row: CatalogRow) => {
    if (resource !== "materials" && resource !== "artisans") return;
    const result = await api.publish(resource, row.id, row.rowVersion);
    setWarnings(result.warnings.map(w => `Nội dung có ${w.actualWords} từ; khuyến nghị ${w.recommendedWords} từ. Đây là cảnh báo chất lượng, nội dung đã được xuất bản.`));
    setNotice("Đã xuất bản thành công.");
    runtime.queries.setQueryData(detailKey, { ...row, status: result.status, rowVersion: result.rowVersion });
    await runtime.queries.invalidateQueries({ queryKey: ["catalog", resource] });
    return result;
  } });
  if (id !== "new" && query.isPending) return <Pending>Đang tải thông tin…</Pending>;
  if (id !== "new" && query.isError && !query.data) return <><FormError error={query.error} /><button className="button secondary" onClick={() => { void query.refetch(); }}>Thử tải lại</button><button className="button secondary" onClick={onCancel}>Đóng</button></>;
  if (resource === "categories" && !tree) return <><FormError error={treeError} />{treeError ? <button className="button secondary" onClick={() => { void reloadTree(); }}>Thử tải cây lại</button> : <Pending>Đang tải cây danh mục đầy đủ…</Pending>}<button className="button secondary" onClick={onCancel}>Đóng</button></>;
  const row = query.data; const writable = permissions.includes("catalog.write");
  const fields = taxonomyFields(resource, row);
  const initial = taxonomyValues(resource, row);
  if (resource === "categories") {
    const parents: FormField = { name: "parentId", label: "Danh mục cha", type: "select", hint: "Tối đa 3 cấp; không chọn chính mình hoặc nhánh con.", options: [["", "Không có (cấp gốc)"], ...(tree ?? []).filter(x => validParent(tree ?? [], row?.id ?? "new", x.id)).map(x => [x.id, categoryPath(x, tree ?? [])] as const)] };
    fields.splice(3, 0, parents); initial.parentId = row?.parentId ?? "";
  }
  const reload = async () => {
    if (id === "new") return initial;
    const r = await query.refetch(); if (r.error) throw r.error;
    return taxonomyValues(resource, r.data);
  };
  return <>
    {notice && <Feedback>{notice}</Feedback>}{warnings.map((w, index) => <p role="status" className="catalog-warning" key={index}>{w}</p>)}<FormError error={query.error} />
    <CatalogForm fields={fields} initial={initial} kind={resource} row={row} writable={writable} relatedDirty={productsDirty} locked={reorderPending || publish.isPending}
      isRelatedEditing={() => relatedEditing.current} onBusyChange={busy => { metadataBusy.current = busy; }}
      lockMessage={reorderPending ? "Đang lưu danh sách sản phẩm. Vui lòng chờ trước khi sửa thông tin." : "Đang xuất bản. Vui lòng chờ trước khi sửa thông tin."}
      onCancel={onCancel} onReload={id === "new" ? undefined : reload} onSave={async values => {
      const optional = (key: string) => String(values[key] ?? "") || null;
      const body = { name: String(values.name), ...(values.slug ? { slug: String(values.slug) } : {}), rowVersion: row?.rowVersion,
        ...(values.status !== row?.status ? { status: String(values.status) } : {}), seoTitle: optional("seoTitle"), seoDescription: optional("seoDescription"), focusKeyword: optional("focusKeyword"),
        robotsNoindex: Boolean(values.robotsNoindex), ...(values.noindexReason ? { noindexReason: String(values.noindexReason) } : {}) };
      const changed = String(values.content) !== String(initial.content);
      const bodyChanged = String(values.body) !== String(initial.body);
      if (resource === "artisans") await api.save(resource, { ...body, title: optional("title"), portraitMediaId: optional("mediaId"), ...(changed ? { bioHtml: paragraphHtml(String(values.content)), bioJson: null } : {}) }, row?.id);
      else if (resource === "materials") await api.save(resource, { ...body, kind: String(values.kind), skuCode: optional("skuCode"), summary: optional("summary"), sortOrder: Number(values.sortOrder), coverMediaId: optional("mediaId"), ...(changed ? { descriptionHtml: paragraphHtml(String(values.content)), descriptionJson: null } : {}) }, row?.id);
      else {
        const shared = { ...body, sortOrder: Number(values.sortOrder), coverMediaId: optional("mediaId"), ...(changed ? { descriptionHtml: paragraphHtml(String(values.content)) } : {}), ...(bodyChanged ? { bodyHtml: paragraphHtml(String(values.body)), bodyJson: null } : {}) };
        if (resource === "categories") {
          const parentId = optional("parentId");
          if (!validParent(tree ?? [], row?.id ?? "new", parentId)) throw new ApiError(400, { code: "validation_failed", errors: { parentId: ["Danh mục cha tạo vòng hoặc vượt 3 cấp."] } });
          await api.save(resource, { ...shared, parentId }, row?.id);
        } else {
          const saved = await api.save(resource, shared, row?.id);
          if (row) {
            // A successful PATCH proves the previous aggregate version was current.
            // Only advance a relation snapshot that was read at that same version.
            runtime.queries.setQueryData<Relations>(collectionProductsKey(row.id), old =>
              old?.rowVersion === row.rowVersion ? { ...old, rowVersion: saved.rowVersion } : old);
            runtime.queries.setQueryData(detailKey, saved);
            await runtime.queries.invalidateQueries({ queryKey: ["catalog", resource] });
            setNotice("Đã lưu thông tin.");
            return taxonomyValues(resource, saved);
          }
        }
      }
      await onSaved();
    }} footer={(dirty, pending, form) => <>
      {row && (resource === "materials" || resource === "artisans") && permissions.includes("catalog.publish") && row.status !== "published" && <div className="catalog-publication">
        <p className="hint">Lưu các thay đổi trước khi xuất bản.</p><Confirmation title="Xuất bản nội dung này?" confirmLabel="Xác nhận xuất bản" pending={publish.isPending}
          trigger={<button className="button" type="button" disabled={dirty || pending || publish.isPending || isConcurrencyConflict(publish.error)}>Xuất bản</button>} onConfirm={async () => {
            if (isConcurrencyConflict(publish.error)) throw publish.error;
            const result = await publish.mutateAsync(row);
            if (result) form.resetField("status", { defaultValue: result.status });
          }}>Nội dung sẽ hiển thị công khai. Cảnh báo số từ không chặn xuất bản.</Confirmation>
        {isConcurrencyConflict(publish.error) && <><p role="alert">Dữ liệu đã thay đổi; tải bản mới trước khi xuất bản.</p><FormError error={reloadError} /><button className="button secondary" type="button" onClick={async () => {
          if (dirty && !window.confirm("Tải bản mới và bỏ thay đổi chưa lưu?")) return;
          try { form.reset(await reload()); publish.reset(); setReloadError(undefined); } catch (error) { setReloadError(error); }
        }}>Tải bản mới để xuất bản</button></>}
      </div>}
      {row && resource === "collections" && <CollectionProducts id={row.id} parentVersion={row.rowVersion} writable={writable} disabled={dirty || pending}
        disabledReason={dirty ? "Hãy lưu hoặc hủy thay đổi thông tin trước khi sắp xếp sản phẩm." : "Thông tin đang được xử lý. Chưa thể sắp xếp sản phẩm."}
        isMetadataBusy={() => metadataBusy.current} onDirtyChange={editing => {
          relatedEditing.current = editing; setProductsDirty(editing);
        }} onPendingChange={setReorderPending} onReloadMetadata={async () => { form.reset(await reload()); }} onSaved={result => {
          runtime.queries.setQueryData<CatalogRow>(detailKey, old => old ? { ...old, rowVersion: result.rowVersion } : old);
        }} />}
    </>}>
      {form => <><MediaPicker label={resource === "artisans" ? "Ảnh chân dung" : "Ảnh bìa"} value={String(form.watch("mediaId") ?? "")} onChange={value => {
        if (!relatedEditing.current && !metadataBusy.current && !reorderPending) form.setValue("mediaId", value, { shouldDirty: true });
      }} />
        <SeoPreview values={form.watch()} row={row} /></>}
    </CatalogForm>
  </>;
}
function taxonomyValues(resource: CatalogResource, row?: CatalogRow): Values {
  const initial = defaults(taxonomyFields(resource, row), row);
  initial.content = plainText((resource === "artisans" ? row?.bioHtml : row?.descriptionHtml) ?? null);
  initial.body = plainText(row?.bodyHtml ?? null);
  initial.mediaId = (resource === "artisans" ? row?.portraitMediaId : row?.coverMediaId) ?? "";
  if (resource === "categories") initial.parentId = row?.parentId ?? "";
  return initial;
}
function SeoPreview({ values, row }: { values: Values; row?: CatalogRow }) {
  return <section className="catalog-seo" aria-label="Xem trước SEO"><h3>Xem trước SEO</h3>
    <p className="hint">Tiêu đề: {String(values.seoTitle ?? "").length} ký tự · Mô tả: {String(values.seoDescription ?? "").length} ký tự. SEO để trống dùng nội dung mặc định từ hệ thống sau khi lưu.</p>
    <p className="catalog-seo-title">{String(values.seoTitle || row?.resolvedSeoTitle || values.name || "Tiêu đề trang")}</p>
    <p>{String(values.seoDescription || row?.resolvedSeoDescription || "Lưu để xem mô tả mặc định từ hệ thống.")}</p>
  </section>;
}
