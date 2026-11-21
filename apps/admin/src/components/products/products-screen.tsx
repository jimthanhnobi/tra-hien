"use client";

import { useEffect, useRef, useState } from "react";
import dynamic from "next/dynamic";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import type { PaginationState } from "@tanstack/react-table";
import { ApiError, type Auth } from "../../lib/api";
import { nullable, productStatuses } from "../../lib/product-forms";
import { ProductsApi, editorKey, productIdPattern, productRoute, productsKey, type ProductQuery } from "../../lib/products-api";
import { catalogSections } from "../catalog/catalog-screen";
import { DataTable, paginationParams } from "../data-table";
import { Field, FormError, Pending } from "../ui";
import { useAdmin } from "../providers";
import { ReferenceSelect, useProductTypes, useTaxonomy } from "./product-references";
import { useProductNavigationGuard } from "./product-navigation";
import { fieldMessage, ProductThumbnail, StatusBadge } from "./product-fields";

export type ProductsPage = "list" | "new" | { id: string };
export type ProductUser = Auth["user"];
type ListRow = Awaited<ReturnType<ProductsApi["list"]>>["items"][number];
const dateTime = new Intl.DateTimeFormat("vi-VN", { dateStyle: "short", timeStyle: "short" });
const ProductEditor = dynamic(() => import("./product-editor").then(module => module.ProductEditor), {
  loading: () => <Pending>Đang tải trình chỉnh sửa…</Pending>
});

export function ProductsScreen({ page, user }: { page: ProductsPage; user: ProductUser }) {
  const canWrite = user.permissions.includes("catalog.write");
  return <div className="catalog products-admin">
    {page === "list" ? <ProductList canWrite={canWrite} />
      : page === "new" ? canWrite ? <CreateProduct /> : <><h1>Bạn chưa có quyền tạo sản phẩm</h1><Link href={productRoute}>Về danh sách sản phẩm</Link></>
        : <ProductEditor key={page.id} id={page.id} user={user} />}
  </div>;
}

function ProductList({ canWrite }: { canWrite: boolean }) {
  const api = new ProductsApi(useAdmin().api);
  const types = useProductTypes();
  const categories = useTaxonomy("categories");
  const materials = useTaxonomy("materials");
  const artisans = useTaxonomy("artisans");
  const [search, setSearch] = useState("");
  const [q, setQ] = useState("");
  const [filters, setFilters] = useState<ProductQuery>({});
  const [pagination, setPagination] = useState<PaginationState>({ pageIndex: 0, pageSize: 20 });
  const searchPending = search.trim() !== q;
  useEffect(() => {
    if (!searchPending) return;
    const timer = window.setTimeout(() => {
      setQ(search.trim());
      setPagination(previous => ({ ...previous, pageIndex: 0 }));
    }, 250);
    return () => window.clearTimeout(timer);
  }, [search, searchPending]);
  const params = { ...paginationParams(pagination), ...filters, q: q || undefined };
  const products = useQuery({ queryKey: [...productsKey, "list", params], queryFn: ({ signal }) => api.list(params, signal) });
  const setFilter = (key: keyof ProductQuery, value: string) => {
    setFilters(previous => ({ ...previous, [key]: value || undefined }));
    setPagination(previous => ({ ...previous, pageIndex: 0 }));
  };
  const readError = [types, categories, materials, artisans].find(query => query.isError);
  return <>
    <header className="page-heading product-heading"><div><h1>Sản phẩm</h1><p>Tìm và quản lý sản phẩm trong catalog.</p></div>
      {canWrite && <Link href={`${productRoute}/new`} className="button">Tạo sản phẩm</Link>}</header>
    <nav className="catalog-nav" aria-label="Quản lý catalog"><Link href={productRoute} aria-current="page">Sản phẩm</Link>
      {catalogSections.map(section => <Link key={section.key} href={`/catalog/${section.key}`}>{section.title}</Link>)}</nav>
    <div className="product-filters">
      <label className="product-field">Tìm tên hoặc SKU<input type="search" maxLength={200} value={search} onChange={event => setSearch(event.target.value)} /></label>
      <label className="product-field">Trạng thái<select aria-label="Trạng thái" value={filters.status ?? ""} onChange={event => setFilter("status", event.target.value)}>
        <option value="">Tất cả trạng thái</option>{Object.entries(productStatuses).map(([value, label]) => <option value={value} key={value}>{label}</option>)}</select></label>
      <ReferenceSelect label="Loại sản phẩm" value={filters.type ?? ""} onChange={v => setFilter("type", v)} rows={types.data ?? []} empty="Tất cả loại" />
      <ReferenceSelect label="Danh mục" value={filters.category ?? ""} onChange={v => setFilter("category", v)} rows={categories.data ?? []} empty="Tất cả danh mục" />
      <ReferenceSelect label="Chất liệu" value={filters.material ?? ""} onChange={v => setFilter("material", v)} rows={materials.data ?? []} empty="Tất cả chất liệu" />
      <ReferenceSelect label="Nghệ nhân" value={filters.artisan ?? ""} onChange={v => setFilter("artisan", v)} rows={artisans.data ?? []} empty="Tất cả nghệ nhân" />
    </div>
    {readError && <><FormError error={readError.error} /><button className="button secondary" onClick={() => void readError.refetch()}>Tải lại bộ lọc</button></>}
    <DataTable<ListRow> caption="Danh sách sản phẩm" data={products.data?.items ?? []} totalItems={Number(products.data?.totalItems ?? 0)}
      getRowId={row => row.id!} pagination={pagination} onPaginationChange={setPagination} loading={searchPending || products.isPending} error={products.error}
      onRetry={() => void products.refetch()} columns={[
        { id: "name", header: "Sản phẩm", cell: ({ row: { original: row } }) => <div className="catalog-product-identity">
          <ProductThumbnail url={row.cover?.url} alt={row.cover?.altText} /><span><strong>{row.name}</strong><span className="catalog-product-sku">{row.skuBase}</span></span></div> },
        { id: "type", header: "Loại", cell: ({ row }) => types.data?.find(type => type.id === row.original.productTypeId)?.name ?? "—" },
        { id: "status", header: "Trạng thái", cell: ({ row }) => <StatusBadge status={row.original.status} /> },
        { id: "variants", header: "Biến thể", cell: ({ row }) => `${row.original.activeCount}/${row.original.variantsCount} hoạt động` },
        { id: "updated", header: "Cập nhật", cell: ({ row }) => row.original.updatedAt && Number.isFinite(Date.parse(row.original.updatedAt)) ? dateTime.format(new Date(row.original.updatedAt)) : "—" },
        { id: "actions", header: "Thao tác", cell: ({ row }) => row.original.id && productIdPattern.test(row.original.id) &&
          <Link className="button secondary" href={`${productRoute}/${row.original.id}`} aria-label={`${canWrite ? "Chỉnh sửa" : "Xem"} ${row.original.name}`}>
            {canWrite ? "Chỉnh sửa" : "Xem"}</Link> }
      ]} />
  </>;
}

function CreateProduct() {
  const { api: admin, queries } = useAdmin();
  const api = new ProductsApi(admin);
  const router = useRouter();
  const types = useProductTypes();
  const materials = useTaxonomy("materials");
  const [type, setType] = useState("");
  const [material, setMaterial] = useState("");
  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const [subtitle, setSubtitle] = useState("");
  const [error, setError] = useState<unknown>();
  const [pending, setPending] = useState(false);
  const busy = useRef(false);
  useProductNavigationGuard(!!(type || name || slug || material || subtitle), pending);
  return <><Link href={productRoute}>← Danh sách sản phẩm</Link><div className="page-heading"><h1>Tạo bản nháp</h1></div>
    <form className="catalog-form" onSubmit={async event => {
      event.preventDefault();
      if (busy.current || !type || name.trim().length < 2) return;
      busy.current = true; setPending(true); setError(undefined);
      try {
        const product = await api.create({ productTypeId: type, name: name.trim(), ...(slug.trim() && { slug: slug.trim() }),
          ...(material && { materialId: material }), ...(nullable(subtitle) && { subtitle }) });
        if (!product.id || !productIdPattern.test(product.id) || !product.rowVersion) throw new ApiError(502, {});
        queries.setQueryData(editorKey(product.id), product);
        void queries.invalidateQueries({ queryKey: [...productsKey, "list"] });
        router.replace(`${productRoute}/${product.id}`);
      } catch (failure) { setError(failure); busy.current = false; setPending(false); }
    }}>
      <fieldset className="catalog-fieldset" disabled={pending}><div className="catalog-fields">
        <ReferenceSelect label="Loại sản phẩm" value={type} onChange={setType} rows={types.data ?? []} disabled={types.isPending} activeOnly required />
        <Field name="name" label="Tên sản phẩm" value={name} error={fieldMessage(error, "name")} onChange={event => setName(event.target.value)} required minLength={2} maxLength={200} />
        <Field name="slug" label="Slug" hint="Để trống để hệ thống tạo từ tên." value={slug} error={fieldMessage(error, "slug")} onChange={event => setSlug(event.target.value)} maxLength={80} />
        <Field name="subtitle" label="Tên phụ" value={subtitle} onChange={event => setSubtitle(event.target.value)} />
        <ReferenceSelect label="Chất liệu" value={material} onChange={setMaterial} rows={materials.data ?? []} />
      </div></fieldset>
      {[types, materials].filter(query => query.isError).map((query, index) => <div key={index}><FormError error={query.error} /><button type="button" className="button secondary" onClick={() => void query.refetch()}>Tải lại dữ liệu tham chiếu</button></div>)}
      {types.isPending && <Pending>Đang tải loại sản phẩm…</Pending>}<FormError error={error} />
      <div className="catalog-actions"><button className="button" disabled={pending || !type || name.trim().length < 2}>{pending ? "Đang tạo…" : "Tạo bản nháp"}</button>
        <Link className="button secondary" href={productRoute}>Hủy</Link></div>
    </form></>;
}
