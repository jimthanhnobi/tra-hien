import type { components, paths } from "@teapremium/api-client";
import { AdminApi, ApiError } from "./api";

type Schemas = components["schemas"];

/**
 * Isolated read-only client for public administrative reference geography (`GAP-P214-01 = A`),
 * bound directly to the generated `@teapremium/api-client` OpenAPI contracts:
 * - `GET /api/v1/store/provinces` → `StoreProvinceListView`
 * - `GET /api/v1/store/provinces/{code}/wards` → `StoreWardListView`
 */
export type AdministrativeProvince = Schemas["AdministrativeProvinceItem"];
export type AdministrativeWard = Schemas["AdministrativeWardItem"];
export type ProvinceListResponse =
  paths["/api/v1/store/provinces"]["get"]["responses"][200]["content"]["application/json"];
export type WardListResponse =
  paths["/api/v1/store/provinces/{code}/wards"]["get"]["responses"][200]["content"]["application/json"];

export const PROVINCE_CODE_PATTERN = /^\d{2}$/;
export const WARD_CODE_PATTERN = /^\d{5}$/;

export const adminGeoProvincesKey = ["reference", "administrative", "provinces"] as const;
export const adminGeoWardsKey = (provinceCode: string) =>
  ["reference", "administrative", "wards", provinceCode] as const;

export function isAdministrativeDataNotReady(error: unknown): boolean {
  return error instanceof ApiError && error.status === 422 && error.code === "administrative_data_not_ready";
}

/**
 * Defense-in-depth guard against out-of-order responses when switching provinces rapidly:
 * returns ward items ONLY if the response's `provinceCode` matches the currently selected province.
 */
export function selectWardsForProvince(
  data: WardListResponse | undefined,
  selectedProvinceCode: string
): AdministrativeWard[] {
  if (!data || !selectedProvinceCode || data.provinceCode !== selectedProvinceCode) {
    return [];
  }
  return data.items;
}

export class AdministrativeGeoClient {
  private readonly client;

  constructor(admin: AdminApi) {
    this.client = admin.createAdministrativeGeoClient();
  }

  async listProvinces(signal?: AbortSignal): Promise<ProvinceListResponse> {
    const { data } = await this.client.GET("/api/v1/store/provinces", { signal });
    if (!data || !Array.isArray(data.items)) {
      throw new ApiError(502, {});
    }
    return data;
  }

  async listWards(provinceCode: string, signal?: AbortSignal): Promise<WardListResponse> {
    if (!PROVINCE_CODE_PATTERN.test(provinceCode)) {
      throw new ApiError(400, { code: "validation_failed", errors: { provinceCode: ["invalid_province_code"] } });
    }
    const { data } = await this.client.GET("/api/v1/store/provinces/{code}/wards", {
      params: { path: { code: provinceCode } },
      signal
    });
    if (!data || !Array.isArray(data.items)) {
      throw new ApiError(502, {});
    }
    return data;
  }
}
