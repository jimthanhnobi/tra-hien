import type { components, paths } from "@teapremium/api-client";
import { AdminApi, ApiError } from "./api";

type Schemas = components["schemas"];
export type Product = Schemas["ProductData"];
export type Variant = Schemas["VariantData"];
export type ProductImage = Schemas["ProductImage"];
export type ProductPatch = Schemas["ProductPatch"];
export type VariantPatch = Schemas["VariantPatch"];
export type ProductValue = Schemas["ProductValue"];
export type ProductQuery = NonNullable<paths["/api/v1/admin/products"]["get"]["parameters"]["query"]>;

function required<T>(data: T | undefined): T {
  if (data === undefined) throw new ApiError(502, {});
  return data;
}

export const productsKey = ["products-admin"] as const;
export const editorKey = (id: string) => [...productsKey, "detail", id] as const;
export const productRoute = "/catalog/products";
export const productIdPattern = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
export class ProductUploadError extends Error {}

// Product writes are bound from RouteValues, so the current OpenAPI omits their
// path parameters. Keep the generated path/body/response types and bind only IDs.
function productWritePath(id: string, childId?: string) {
  if (!productIdPattern.test(id) || (childId !== undefined && !productIdPattern.test(childId))) {
    throw new ApiError(400, { code: "validation_failed" });
  }
  return {
    params: { path: {} as never },
    pathSerializer: (path: string) => path.replace("{id}", id).replace("{childId}", childId ?? "")
  };
}

export class ProductsApi {
  constructor(private readonly admin: AdminApi) {}

  async list(query: ProductQuery, signal?: AbortSignal) {
    const result = required((await this.admin.client.GET("/api/v1/admin/products", { params: { query }, signal })).data);
    return { ...result, items: result.items.map(({ id, name, skuBase, status, productTypeId, media, variants, updatedAt }) => ({
      id, name, skuBase, status, productTypeId, updatedAt,
      cover: media?.find(image => image.isCover), variantsCount: variants?.length ?? 0,
      activeCount: variants?.filter(variant => variant.status === "active").length ?? 0
    })) };
  }

  async get(id: string, signal?: AbortSignal) {
    return required((await this.admin.client.GET("/api/v1/admin/products/{id}", { params: { path: { id } }, signal })).data);
  }

  async create(body: Schemas["ProductCreate"]) {
    return required((await this.admin.client.POST("/api/v1/admin/products", { body })).data);
  }

  async patch(id: string, body: ProductPatch) {
    return required((await this.admin.client.PATCH("/api/v1/admin/products/{id}", { ...productWritePath(id), body })).data);
  }

  async variant(id: string, childId: string, signal?: AbortSignal) {
    return required((await this.admin.client.GET("/api/v1/admin/products/{id}/variants/{childId}", { params: { path: { id, childId } }, signal })).data);
  }

  async saveVariant(id: string, childId: string | undefined, body: VariantPatch) {
    return required(childId
      ? (await this.admin.client.PATCH("/api/v1/admin/products/{id}/variants/{childId}", { ...productWritePath(id, childId), body })).data
      : (await this.admin.client.POST("/api/v1/admin/products/{id}/variants", { ...productWritePath(id), body })).data);
  }

  async attach(id: string, body: Schemas["ProductMediaCreate"]) {
    return required((await this.admin.client.POST("/api/v1/admin/products/{id}/media", { ...productWritePath(id), body })).data);
  }

  async patchImage(id: string, childId: string, body: Schemas["ProductMediaRequest"]) {
    return required((await this.admin.client.PATCH("/api/v1/admin/products/{id}/media/{childId}", { ...productWritePath(id, childId), body })).data);
  }

  async removeImage(id: string, childId: string, rowVersion: string) {
    return required((await this.admin.client.DELETE("/api/v1/admin/products/{id}/media/{childId}", { ...productWritePath(id, childId), body: { rowVersion } })).data);
  }

  async setCover(id: string, childId: string, rowVersion: string) {
    return required((await this.admin.client.POST("/api/v1/admin/products/{id}/media/{childId}/cover", { ...productWritePath(id, childId), body: { rowVersion } })).data);
  }

  async orderImages(id: string, mediaIds: string[], rowVersion: string) {
    return required((await this.admin.client.PUT("/api/v1/admin/products/{id}/media/order", { ...productWritePath(id), body: { mediaIds, rowVersion } })).data);
  }

  async transition(id: string, action: "publish" | "archive" | "restore-draft", body: Schemas["ProductArchiveRequest"]) {
    return required((await this.admin.client.POST(`/api/v1/admin/products/{id}/${action}`, { ...productWritePath(id), body })).data);
  }

  async upload(file: File, slug?: string, send = fetch) {
    if (!/^image\/(jpeg|png|webp|avif)$/.test(file.type) || file.size === 0 || file.size > 15 * 1024 * 1024) {
      throw new ProductUploadError("Chọn ảnh JPEG, PNG, WebP hoặc AVIF có dung lượng từ 1 byte đến 15 MiB.");
    }
    const intent = required((await this.admin.client.POST("/api/v1/admin/media/upload-intents", {
      body: { fileName: file.name, mimeType: file.type, byteSize: file.size, purpose: "catalog", slugHint: slug }
    })).data);
    const url = URL.canParse(intent.putUrl) ? new URL(intent.putUrl) : null;
    const remaining = Date.parse(intent.expiresAt) - Date.now();
    if (!url || url.protocol !== "https:" || url.username || url.password || !(remaining > 0)) {
      throw new ProductUploadError("Liên kết tải ảnh không hợp lệ hoặc đã hết hạn. Hãy thử tải ảnh lại.");
    }
    const headers = new Headers(intent.headers);
    headers.delete("Authorization");
    headers.delete("X-CSRF-TOKEN");
    headers.delete("Cookie");
    try {
      const response = await send(url, {
        method: "PUT", headers, body: file, credentials: "omit", redirect: "error", referrerPolicy: "no-referrer",
        signal: AbortSignal.timeout(Math.min(remaining, 90000))
      });
      if (!response.ok) throw new Error();
    } catch {
      throw new ProductUploadError("Không tải được ảnh. Liên kết có thể đã hết hạn. Hãy thử tải ảnh lại.");
    }
    const asset = required((await this.admin.client.POST("/api/v1/admin/media/upload-intents/{id}/complete", {
      params: { path: { id: intent.uploadId } }
    })).data);
    if (asset.purpose !== "catalog" || !asset.mimeType.startsWith("image/") || asset.urlExpiresAt) {
      throw new ProductUploadError("Ảnh chưa sẵn sàng để sử dụng công khai.");
    }
    return asset;
  }
}
