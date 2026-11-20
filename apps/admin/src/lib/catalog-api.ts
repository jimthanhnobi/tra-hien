import type { components } from "@teapremium/api-client";
import { queryOptions, type QueryClient } from "@tanstack/react-query";
import { AdminApi, ApiError } from "./api";

type Schemas = components["schemas"];
export type CatalogResource = "categories" | "collections" | "materials" | "artisans";
export type CatalogRow = Schemas["CatalogView"];
export type ProductRow = Schemas["ProductData"];
export type ProductDisplay = Pick<ProductRow, "id" | "name" | "skuBase" | "status"> & {
  cover?: Pick<Schemas["ProductImage"], "url" | "altText">;
};
function productDisplay({ id, name, skuBase, status, media }: ProductRow): ProductDisplay {
  const image = media?.find(item => item.isCover && item.url);
  return { id, name, skuBase, status, cover: image && { url: image.url, altText: image.altText } };
}
export type ProductStatus = "draft" | "active" | "archived";
const productKey = (id: string) => ["catalog", "product", id];
const productCache = { staleTime: 60_000, gcTime: 300_000, refetchOnWindowFocus: false, refetchOnReconnect: false };

export function productDetailOptions(api: CatalogApi, id: string) {
  return queryOptions({ queryKey: productKey(id), queryFn: ({ signal }) => api.product(id, signal), ...productCache });
}

export function productSearchOptions(api: CatalogApi, cache: QueryClient, q: string, status: ProductStatus | "", page: number) {
  return queryOptions({
    queryKey: ["catalog", "product-search", q, status, page],
    queryFn: async ({ signal }) => {
      const result = await api.searchProducts({ q: q || undefined, status: status || undefined, page, pageSize: 20 }, signal);
      // The adapter projects both endpoints before data enters the cache.
      if (!signal.aborted) {
        cache.setQueryDefaults(["catalog", "product"], productCache);
        for (const product of result.items) if (product.id) cache.setQueryData(productKey(product.id), product);
      }
      return result;
    },
    refetchOnWindowFocus: false,
    refetchOnReconnect: false
  });
}
export type TypeRow = Schemas["ProductTypeView"];
export type AttributeRow = Schemas["AttributeView"];
export type OptionRow = Schemas["OptionView"];
export type ReferenceRow = TypeRow | AttributeRow | OptionRow;
export type ReferenceScope = { kind: "types" } | { kind: "attributes"; typeId: string } | { kind: "options"; typeId: string; attributeId: string };
type PageQuery = { page: number; pageSize: number; q?: string; status?: string };
type TaxonomyWrites = {
  categories: Schemas["CategoryWriteRequest"]; collections: Schemas["CollectionWriteRequest"];
  materials: Schemas["MaterialWriteRequest"]; artisans: Schemas["ArtisanWriteRequest"];
};
function required<T>(data: T | undefined): T {
  if (data === undefined) throw new ApiError(502, {});
  return data;
}

// All paths and bodies remain checked against the committed generated OpenAPI client.
export class CatalogApi {
  constructor(private readonly api: AdminApi) {}
  async list(resource: CatalogResource, query: PageQuery, signal?: AbortSignal) {
    return required((await this.api.client.GET(`/api/v1/admin/${resource}`, { params: { query }, signal })).data);
  }
  async get(resource: CatalogResource, id: string, signal?: AbortSignal) {
    return required((await this.api.client.GET(`/api/v1/admin/${resource}/{id}`, { params: { path: { id } }, signal })).data);
  }
  async categoryTree(signal?: AbortSignal) {
    const rows: CatalogRow[] = [];
    for (let page = 1; ; page++) {
      const batch = await this.list("categories", { page, pageSize: 100 }, signal);
      rows.push(...batch.items);
      if (rows.length >= Number(batch.totalItems)) return rows;
      if (!batch.items.length) throw new ApiError(502, { title: "Danh sách danh mục chưa đầy đủ. Hãy tải lại." });
    }
  }
  async save(resource: CatalogResource, body: TaxonomyWrites[CatalogResource], id?: string) {
    return required(id
      ? (await this.api.client.PATCH(`/api/v1/admin/${resource}/{id}`, { params: { path: { id } }, body })).data
      : (await this.api.client.POST(`/api/v1/admin/${resource}`, { body })).data);
  }
  async publish(resource: "materials" | "artisans", id: string, rowVersion: string) {
    return required((await this.api.client.POST(`/api/v1/admin/${resource}/{id}/publish`, { params: { path: { id } }, body: { rowVersion } })).data);
  }
  async products(id: string, signal?: AbortSignal) {
    return required((await this.api.client.GET("/api/v1/admin/collections/{id}/products", { params: { path: { id } }, signal })).data);
  }
  async searchProducts(query: { page: number; pageSize: number; q?: string; status?: ProductStatus }, signal?: AbortSignal) {
    const { items, page, pageSize, totalItems } = required((await this.api.client.GET("/api/v1/admin/products", { params: { query }, signal })).data);
    return { items: items.map(productDisplay), page, pageSize, totalItems };
  }
  async product(id: string, signal?: AbortSignal) {
    return productDisplay(required((await this.api.client.GET("/api/v1/admin/products/{id}", { params: { path: { id } }, signal })).data));
  }
  async orderProducts(id: string, rowVersion: string, items: Schemas["CollectionProductView"][]) {
    return required((await this.api.client.PUT("/api/v1/admin/collections/{id}/products", { params: { path: { id } }, body: { rowVersion, items } })).data);
  }
  async media(page: number, q: string, signal?: AbortSignal) {
    return required((await this.api.client.GET("/api/v1/admin/media", { params: { query: { page, pageSize: 20, q } }, signal })).data);
  }
  async references(scope: ReferenceScope, query: { page: number; pageSize: number }, signal?: AbortSignal) {
    if (scope.kind === "types") return required((await this.api.client.GET("/api/v1/admin/product-types", { params: { query }, signal })).data);
    if (scope.kind === "attributes") return required((await this.api.client.GET("/api/v1/admin/product-types/{id}/attributes", { params: { path: { id: scope.typeId }, query }, signal })).data);
    return required((await this.api.client.GET("/api/v1/admin/product-types/{id}/attributes/{attrId}/options", { params: { path: { id: scope.typeId, attrId: scope.attributeId }, query }, signal })).data);
  }
  async reference(scope: ReferenceScope, id: string, signal?: AbortSignal): Promise<ReferenceRow> {
    if (scope.kind === "types") return required((await this.api.client.GET("/api/v1/admin/product-types/{id}", { params: { path: { id } }, signal })).data);
    if (scope.kind === "attributes") return required((await this.api.client.GET("/api/v1/admin/product-types/{id}/attributes/{attrId}", { params: { path: { id: scope.typeId, attrId: id } }, signal })).data);
    return required((await this.api.client.GET("/api/v1/admin/product-types/{id}/attributes/{attrId}/options/{optId}", { params: { path: { id: scope.typeId, attrId: scope.attributeId, optId: id } }, signal })).data);
  }
  async saveType(body: Schemas["ProductTypeInput"], row?: ReferenceRow) {
    return required(row ? (await this.api.client.PATCH("/api/v1/admin/product-types/{id}", { params: { path: { id: row.id } }, body: { ...body, rowVersion: row.rowVersion } })).data
      : (await this.api.client.POST("/api/v1/admin/product-types", { body })).data);
  }
  async saveAttribute(typeId: string, body: Schemas["AttributeInput"], row?: ReferenceRow) {
    return required(row ? (await this.api.client.PATCH("/api/v1/admin/product-types/{id}/attributes/{attrId}", { params: { path: { id: typeId, attrId: row.id } }, body: { ...body, rowVersion: row.rowVersion } })).data
      : (await this.api.client.POST("/api/v1/admin/product-types/{id}/attributes", { params: { path: { id: typeId } }, body })).data);
  }
  async saveOption(typeId: string, attributeId: string, body: Schemas["OptionInput"], row?: ReferenceRow) {
    const path = { id: typeId, attrId: attributeId };
    return required(row ? (await this.api.client.PATCH("/api/v1/admin/product-types/{id}/attributes/{attrId}/options/{optId}", { params: { path: { ...path, optId: row.id } }, body: { ...body, rowVersion: row.rowVersion } })).data
      : (await this.api.client.POST("/api/v1/admin/product-types/{id}/attributes/{attrId}/options", { params: { path }, body })).data);
  }
}
