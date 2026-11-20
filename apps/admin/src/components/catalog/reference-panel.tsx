"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { CatalogApi, type ReferenceRow, type ReferenceScope } from "../../lib/catalog-api";
import { defaults, referenceFields } from "../../lib/catalog-forms";
import { useAdmin } from "../providers";
import { DataTable, paginationParams } from "../data-table";
import { Feedback, FormError, Pending } from "../ui";
import { CatalogForm } from "./catalog-form";

type Level = { scope: ReferenceScope; title: string };
export function ReferencePanel({ permissions }: { permissions: string[] }) {
  const [levels, setLevels] = useState<Level[]>([{ scope: { kind: "types" }, title: "Loại sản phẩm" }]);
  const current = levels.at(-1)!;
  return <section className="catalog-panel">
    <nav className="catalog-trail" aria-label="Cấp thuộc tính">{levels.map((level, index) => <button type="button" className="button secondary" key={index} disabled={index === levels.length - 1} onClick={() => setLevels(levels.slice(0, index + 1))}>{level.title}</button>)}</nav>
    <ReferenceList key={JSON.stringify(current.scope)} scope={current.scope} title={current.title} writable={permissions.includes("catalog.write")} onDrill={row => {
      if (current.scope.kind === "types") setLevels([...levels, { scope: { kind: "attributes", typeId: row.id }, title: `Thuộc tính: ${"name" in row ? row.name : row.code}` }]);
      else if (current.scope.kind === "attributes") setLevels([...levels, { scope: { kind: "options", typeId: current.scope.typeId, attributeId: row.id }, title: `Lựa chọn: ${"name" in row ? row.name : row.code}` }]);
    }} />
  </section>;
}
function ReferenceList({ scope, title, writable, onDrill }: { scope: ReferenceScope; title: string; writable: boolean; onDrill: (row: ReferenceRow) => void }) {
  const runtime = useAdmin(); const api = new CatalogApi(runtime.api);
  const [pagination, setPagination] = useState({ pageIndex: 0, pageSize: 20 });
  const [editing, setEditing] = useState<string>(); const [notice, setNotice] = useState("");
  const key = ["catalog", "references", scope];
  const query = useQuery({ queryKey: [...key, pagination], queryFn: ({ signal }) => api.references(scope, paginationParams(pagination), signal) });
  return <>
    <div className="catalog-toolbar"><h2>{title}</h2>{writable && !editing && <button className="button" onClick={() => { setEditing("new"); setNotice(""); }}>Thêm mới</button>}</div>
    {notice && <Feedback>{notice}</Feedback>}
    {editing ? <ReferenceEditor key={editing} scope={scope} id={editing} writable={writable} onCancel={() => setEditing(undefined)} onSaved={async () => {
      await runtime.queries.invalidateQueries({ queryKey: key }); setNotice("Đã lưu thông tin."); setEditing(undefined);
    }} /> : <DataTable<ReferenceRow> caption={title} data={query.data?.items ?? []} totalItems={Number(query.data?.totalItems ?? 0)} pagination={pagination} onPaginationChange={setPagination}
      loading={query.isPending} error={query.error} onRetry={() => { void query.refetch(); }} getRowId={row => row.id} columns={[
        { accessorKey: "code", header: "Mã" }, { id: "name", header: "Tên", cell: ({ row }) => "name" in row.original ? row.original.name : row.original.label },
        { id: "state", header: "Trạng thái", cell: ({ row }) => row.original.isActive ? "Hoạt động" : "Ngừng hoạt động" },
        ...(scope.kind === "attributes" ? [{ id: "type", header: "Kiểu / Phạm vi / Đơn vị", cell: ({ row }: { row: { original: ReferenceRow } }) => "dataType" in row.original ? `${row.original.dataType} / ${row.original.scope} / ${row.original.unit ?? "—"}${row.original.isVariantAxis ? " · Trục biến thể" : ""}` : "" }] : []),
        { accessorKey: "sortOrder", header: "Thứ tự" }, { id: "actions", header: "Thao tác", cell: ({ row }) => <div className="catalog-actions">
          <button className="button secondary" onClick={() => { setEditing(row.original.id); setNotice(""); }}>{writable ? "Chỉnh sửa" : "Xem"} {row.original.code}</button>
          {scope.kind === "types" && <button className="button secondary" onClick={() => onDrill(row.original)}>Thuộc tính {row.original.code}</button>}
          {scope.kind === "attributes" && "dataType" in row.original && row.original.dataType === "option" && <button className="button secondary" onClick={() => onDrill(row.original)}>Lựa chọn {row.original.code}</button>}
        </div> }
      ]} />}
  </>;
}
function ReferenceEditor({ scope, id, writable, onCancel, onSaved }: { scope: ReferenceScope; id: string; writable: boolean; onCancel: () => void; onSaved: () => Promise<void> }) {
  const { api: admin } = useAdmin(); const api = new CatalogApi(admin);
  const query = useQuery({ queryKey: ["catalog", "reference", scope, id], queryFn: ({ signal }) => api.reference(scope, id, signal), enabled: id !== "new", refetchOnWindowFocus: false, refetchOnReconnect: false });
  if (id !== "new" && query.isPending) return <Pending>Đang tải thông tin…</Pending>;
  if (id !== "new" && query.isError && !query.data) return <><FormError error={query.error} /><button className="button secondary" onClick={() => { void query.refetch(); }}>Thử tải lại</button><button className="button secondary" onClick={onCancel}>Đóng</button></>;
  const row = query.data; const fields = referenceFields(scope.kind);
  return <><FormError error={query.error} /><CatalogForm fields={fields} initial={defaults(fields, row)} kind={scope.kind} writable={writable} onCancel={onCancel} onReload={id === "new" ? undefined : async () => {
    const result = await query.refetch(); if (result.error) throw result.error;
    return defaults(fields, result.data);
  }} onSave={async values => {
    const code = String(values.code), name = String(values.name ?? ""), sortOrder = Number(values.sortOrder), isActive = Boolean(values.isActive);
    if (scope.kind === "types") await api.saveType({ code, name, sortOrder, isActive, skuPrefix: String(values.skuPrefix) || null, description: String(values.description) || null, googleProductCategory: String(values.googleProductCategory) || null }, row);
    else if (scope.kind === "attributes") await api.saveAttribute(scope.typeId, { code, name, sortOrder, isActive, dataType: String(values.dataType), scope: String(values.scope), unit: String(values.unit) || null,
      isVariantAxis: Boolean(values.isVariantAxis), isRequiredForPublish: Boolean(values.isRequiredForPublish), isFilterable: Boolean(values.isFilterable), isVisible: Boolean(values.isVisible) }, row);
    else await api.saveOption(scope.typeId, scope.attributeId, { code, label: String(values.label), sortOrder, isActive }, row);
    await onSaved();
  }} /></>;
}
