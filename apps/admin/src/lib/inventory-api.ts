import type { components, paths } from "@teapremium/api-client";
import type { QueryClient } from "@tanstack/react-query";
import { AdminApi, ApiError } from "./api";
import { isUuid } from "./inventory-forms";

type Schemas = components["schemas"];
export type StockRow = Schemas["InventoryStockView"];
export type StockPage = Schemas["CatalogPageOfInventoryStockView"];
export type MovementRow = Schemas["InventoryMovementView"];
export type MovementPage = Schemas["CatalogPageOfInventoryMovementView"];
export type AlertsView = Schemas["InventoryAlertsView"];
export type LowStockItem = Schemas["LowStockAlertItem"];
export type MismatchItem = Schemas["InventoryMismatchItem"];
export type OpeningResult = Schemas["OpeningResult"];
export type ReceiptSummary = Schemas["ReceiptSummary"];
export type ReceiptView = Schemas["ReceiptView"];
export type ReceiptLineView = Schemas["ReceiptLineView"];
export type ReceiptPosted = Schemas["ReceiptPosted"];
export type CreateReceiptRequest = Schemas["CreateReceiptRequest"];
export type EditReceiptRequest = Schemas["EditReceiptRequest"];
export type AdjustmentView = Schemas["AdjustmentView"];
export type AdjustmentRequest = Schemas["InventoryAdjustmentRequest"];
export type StocktakeSummary = Schemas["StocktakeSummary"];
export type StocktakeView = Schemas["StocktakeView"];
export type StocktakeLineView = Schemas["StocktakeLineView"];
export type ProductTypeView = Schemas["ProductTypeView"];

export type StockQuery = NonNullable<paths["/api/v1/admin/inventory"]["get"]["parameters"]["query"]>;
export type MovementQuery = NonNullable<paths["/api/v1/admin/inventory/{variantId}/movements"]["get"]["parameters"]["query"]>;
export type ReceiptListQuery = NonNullable<paths["/api/v1/admin/receipts"]["get"]["parameters"]["query"]>;
export type StocktakeListQuery = NonNullable<paths["/api/v1/admin/stocktakes"]["get"]["parameters"]["query"]>;

export const inventoryKey = ["inventory"] as const;
export const inventoryStockKey = [...inventoryKey, "stock"] as const;
export const inventoryAlertsKey = [...inventoryKey, "alerts"] as const;
export const inventoryProductTypesKey = [...inventoryKey, "product-types"] as const;
export const inventoryMovementsKey = (variantId: string) => [...inventoryKey, "movements", variantId] as const;
export const inventoryReceiptsKey = [...inventoryKey, "receipts"] as const;
export const inventoryReceiptKey = (id: string) => [...inventoryReceiptsKey, "detail", id] as const;
export const inventoryStocktakesKey = [...inventoryKey, "stocktakes"] as const;
export const inventoryStocktakeKey = (id: string) => [...inventoryStocktakesKey, "detail", id] as const;

export const variantLabelRootKey = [...inventoryKey, "variant-label"] as const;
export const variantLabelKey = (variantId: string) => [...variantLabelRootKey, variantId.toLowerCase()] as const;
export const variantLabelCache = {
  staleTime: 60_000,
  gcTime: 300_000,
  refetchOnWindowFocus: false,
  refetchOnReconnect: false
} as const;

// GAP-P1-13-01 Option A: bounded direct-URL hydration (max 5 pages x 100 = 500 variants), stop early once resolved.
export const MAX_VARIANT_HYDRATE_PAGES = 5;
export const VARIANT_HYDRATE_PAGE_SIZE = 100;

export interface VariantLabel {
  variantId: string;
  productId: string;
  sku: string;
  productName: string;
  variantName: string | null;
}

export function toVariantLabel(row: Pick<StockRow, "variantId" | "productId" | "sku" | "productName" | "variantName">): VariantLabel {
  return {
    variantId: row.variantId,
    productId: row.productId,
    sku: row.sku,
    productName: row.productName,
    variantName: row.variantName ?? null
  };
}

export function cacheVariantLabels(queries: QueryClient, items: readonly VariantLabel[]) {
  if (!items.length) return;
  queries.setQueryDefaults(variantLabelRootKey, variantLabelCache);
  for (const item of items) {
    if (item.variantId) {
      queries.setQueryData(variantLabelKey(item.variantId), item);
    }
  }
}

export function readCachedVariantLabels(queries: QueryClient, variantIds: readonly string[]): Record<string, VariantLabel> {
  const result: Record<string, VariantLabel> = {};
  for (const id of variantIds) {
    const key = id.toLowerCase();
    const cached = queries.getQueryData<VariantLabel>(variantLabelKey(key));
    if (cached) result[key] = cached;
  }
  return result;
}

export async function hydrateVariantLabels(
  api: InventoryApi,
  queries: QueryClient,
  variantIds: readonly string[],
  signal?: AbortSignal
): Promise<Record<string, VariantLabel>> {
  const uniqueIds = [...new Set(variantIds.filter(isUuid).map(id => id.toLowerCase()))];
  const resolved = readCachedVariantLabels(queries, uniqueIds);
  const missing = new Set(uniqueIds.filter(id => !resolved[id]));
  if (missing.size === 0) return resolved;

  for (let page = 1; page <= MAX_VARIANT_HYDRATE_PAGES && missing.size > 0; page++) {
    if (signal?.aborted) break;
    const batch = await api.listStock({ page, pageSize: VARIANT_HYDRATE_PAGE_SIZE }, signal);
    const labels = batch.items.map(toVariantLabel);
    if (!signal?.aborted) cacheVariantLabels(queries, labels);
    for (const label of labels) {
      const key = label.variantId.toLowerCase();
      if (missing.has(key)) {
        resolved[key] = label;
        missing.delete(key);
      }
    }
    if (batch.items.length === 0 || page * VARIANT_HYDRATE_PAGE_SIZE >= Number(batch.totalItems)) {
      break;
    }
  }
  return resolved;
}

function required<T>(data: T | undefined): T {
  if (data === undefined) throw new ApiError(502, {});
  return data;
}

function assertUuid(id: string) {
  if (!isUuid(id)) throw new ApiError(400, { code: "validation_failed" });
}

// All paths and request/response contracts are strictly typed against @teapremium/api-client.
export class InventoryApi {
  constructor(private readonly admin: AdminApi) {}

  async listStock(query: StockQuery, signal?: AbortSignal): Promise<StockPage> {
    return required((await this.admin.client.GET("/api/v1/admin/inventory", { params: { query }, signal })).data);
  }

  async listProductTypes(signal?: AbortSignal): Promise<ProductTypeView[]> {
    const items: ProductTypeView[] = [];
    for (let page = 1; ; page++) {
      const batch = required((await this.admin.client.GET("/api/v1/admin/product-types", {
        params: { query: { page, pageSize: 100 } },
        signal
      })).data);
      items.push(...batch.items);
      if (items.length >= Number(batch.totalItems) || batch.items.length === 0) return items;
    }
  }

  async alerts(limit = 50, signal?: AbortSignal): Promise<AlertsView> {
    return required((await this.admin.client.GET("/api/v1/admin/inventory/alerts", {
      params: { query: { limit } },
      signal
    })).data);
  }

  async listMovements(variantId: string, query: MovementQuery, signal?: AbortSignal): Promise<MovementPage> {
    assertUuid(variantId);
    return required((await this.admin.client.GET("/api/v1/admin/inventory/{variantId}/movements", {
      params: { path: { variantId }, query },
      signal
    })).data);
  }

  async openingImport(file: File, options: { dryRun: boolean; previewToken?: string }): Promise<OpeningResult> {
    return required((await this.admin.client.POST("/api/v1/admin/inventory/opening-import", {
      params: {
        query: { dryRun: options.dryRun },
        header: options.previewToken ? { "X-Inventory-Preview-Token": options.previewToken } : undefined
      },
      body: { file: file.name },
      bodySerializer: () => {
        const form = new FormData();
        form.append("file", file, file.name);
        return form;
      }
    })).data);
  }

  async listReceipts(query: ReceiptListQuery, signal?: AbortSignal) {
    return required((await this.admin.client.GET("/api/v1/admin/receipts", { params: { query }, signal })).data);
  }

  async getReceipt(id: string, signal?: AbortSignal): Promise<ReceiptView> {
    assertUuid(id);
    return required((await this.admin.client.GET("/api/v1/admin/receipts/{id}", {
      params: { path: { id } },
      signal
    })).data);
  }

  async createReceipt(body: CreateReceiptRequest): Promise<ReceiptView> {
    return required((await this.admin.client.POST("/api/v1/admin/receipts", { body })).data);
  }

  async editReceipt(id: string, body: EditReceiptRequest): Promise<ReceiptView> {
    assertUuid(id);
    return required((await this.admin.client.PATCH("/api/v1/admin/receipts/{id}", {
      params: { path: { id } },
      body
    })).data);
  }

  async postReceipt(id: string, rowVersion: string, idempotencyKey: string): Promise<ReceiptPosted> {
    assertUuid(id);
    return required((await this.admin.client.POST("/api/v1/admin/receipts/{id}/post", {
      params: { path: { id }, header: { "Idempotency-Key": idempotencyKey } },
      body: { rowVersion }
    })).data);
  }

  async cancelReceipt(id: string, rowVersion: string): Promise<ReceiptView> {
    assertUuid(id);
    return required((await this.admin.client.POST("/api/v1/admin/receipts/{id}/cancel", {
      params: { path: { id } },
      body: { rowVersion }
    })).data);
  }

  async createAdjustment(body: AdjustmentRequest): Promise<AdjustmentView> {
    assertUuid(body.variantId);
    return required((await this.admin.client.POST("/api/v1/admin/inventory/adjustments", { body })).data);
  }

  async listStocktakes(query: StocktakeListQuery, signal?: AbortSignal) {
    return required((await this.admin.client.GET("/api/v1/admin/stocktakes", { params: { query }, signal })).data);
  }

  async createStocktake(note: string | null): Promise<StocktakeView> {
    return required((await this.admin.client.POST("/api/v1/admin/stocktakes", { body: { note } })).data);
  }

  async getStocktake(id: string, signal?: AbortSignal): Promise<StocktakeView> {
    assertUuid(id);
    return required((await this.admin.client.GET("/api/v1/admin/stocktakes/{id}", {
      params: { path: { id } },
      signal
    })).data);
  }

  async countStocktakeLine(id: string, variantId: string, rowVersion: string, countedQty: number): Promise<StocktakeView> {
    assertUuid(id);
    assertUuid(variantId);
    return required((await this.admin.client.PUT("/api/v1/admin/stocktakes/{id}/lines/{variantId}", {
      params: { path: { id, variantId } },
      body: { rowVersion, countedQty }
    })).data);
  }

  async submitStocktake(id: string, rowVersion: string): Promise<StocktakeView> {
    assertUuid(id);
    return required((await this.admin.client.POST("/api/v1/admin/stocktakes/{id}/submit", {
      params: { path: { id } },
      body: { rowVersion }
    })).data);
  }

  async approveStocktake(id: string, rowVersion: string): Promise<StocktakeView> {
    assertUuid(id);
    return required((await this.admin.client.POST("/api/v1/admin/stocktakes/{id}/approve", {
      params: { path: { id } },
      body: { rowVersion }
    })).data);
  }

  async cancelStocktake(id: string, rowVersion: string): Promise<StocktakeView> {
    assertUuid(id);
    return required((await this.admin.client.POST("/api/v1/admin/stocktakes/{id}/cancel", {
      params: { path: { id } },
      body: { rowVersion }
    })).data);
  }
}
