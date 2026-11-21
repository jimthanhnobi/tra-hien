"use client";

import { useId } from "react";
import { useQuery } from "@tanstack/react-query";
import { CatalogApi, type CatalogRow, type CatalogResource, type ReferenceRow, type TypeRow, type AttributeRow, type OptionRow } from "../../lib/catalog-api";
import { ApiError } from "../../lib/api";
import { useAdmin } from "../providers";

async function referencePages<T>(load: (page: number) => Promise<{ items: T[]; totalItems: string | number }>) {
  const items: T[] = [];
  for (let page = 1; ; page++) {
    const result = await load(page);
    items.push(...result.items);
    if (items.length >= Number(result.totalItems)) return items;
    if (!result.items.length) throw new ApiError(502, {});
  }
}

export function useProductTypes() {
  const catalog = new CatalogApi(useAdmin().api);
  return useQuery({ queryKey: ["product-references", "types"], queryFn: ({ signal }) =>
    referencePages<ReferenceRow>(page => catalog.references({ kind: "types" }, { page, pageSize: 100 }, signal)).then(rows => rows.filter((row): row is TypeRow => "skuPrefix" in row)) });
}

export function useTaxonomy(resource: Exclude<CatalogResource, "collections">) {
  const catalog = new CatalogApi(useAdmin().api);
  return useQuery({ queryKey: ["product-references", resource], queryFn: ({ signal }) =>
    resource === "categories" ? catalog.categoryTree(signal) : referencePages<CatalogRow>(page => catalog.list(resource, { page, pageSize: 100 }, signal)) });
}

export function useAttributes(typeId?: string) {
  const catalog = new CatalogApi(useAdmin().api);
  return useQuery({ enabled: !!typeId, queryKey: ["product-references", "attributes", typeId], queryFn: ({ signal }) =>
    referencePages<ReferenceRow>(page => catalog.references({ kind: "attributes", typeId: typeId! }, { page, pageSize: 100 }, signal)).then(rows => rows.filter((row): row is AttributeRow => "dataType" in row)) });
}

export function useAttributeOptions(typeId: string, attributeId: string) {
  const catalog = new CatalogApi(useAdmin().api);
  return useQuery(attributeOptions(catalog, typeId, attributeId));
}

export function attributeOptions(catalog: CatalogApi, typeId: string, attributeId: string) {
  return { queryKey: ["product-references", "options", typeId, attributeId], queryFn: ({ signal }: { signal: AbortSignal }) =>
    referencePages<ReferenceRow>(page => catalog.references({ kind: "options", typeId, attributeId }, { page, pageSize: 100 }, signal)).then(rows => rows.filter((row): row is OptionRow => "label" in row)) };
}

export function ReferenceSelect({ label, value, onChange, rows, disabled, empty = "Chưa chọn", required, activeOnly = false }: {
  label: string; value: string; onChange: (value: string) => void; rows: readonly { id: string; name: string; isActive?: boolean; status?: string }[];
  disabled?: boolean; empty?: string; required?: boolean; activeOnly?: boolean;
}) {
  const id = useId();
  return <div className="product-field"><label htmlFor={id}>{label}</label><select id={id} value={value} onChange={event => onChange(event.target.value)} required={required} disabled={disabled}>
    <option value="">{empty}</option>
    {value && !rows.some(row => row.id === value) && <option value={value}>Tham chiếu đã lưu (chưa tải được tên)</option>}
    {rows.filter(row => !activeOnly || row.id === value || row.isActive !== false).map(row =>
      <option key={row.id} value={row.id} disabled={activeOnly && row.isActive === false}>{row.name}{row.isActive === false ? " · Ngừng dùng" : row.status === "hidden" ? " · Ẩn" : ""}</option>)}
  </select></div>;
}
