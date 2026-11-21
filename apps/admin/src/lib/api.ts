import createClient from "openapi-fetch";
import type { components, paths } from "@teapremium/api-client";

export type Auth = components["schemas"]["AuthResponse"];
export type Provisioning = components["schemas"]["TotpSetup"];
type Problem = components["schemas"]["ProblemDetails"];

// ProblemDetails extensions are open JSON, absent from the generated base schema.
function record(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === "object" ? value as Record<string, unknown> : {};
}

export class ApiError extends Error {
  readonly fields: Record<string, string[]> = {};
  readonly code: string;
  constructor(readonly status: number, value: unknown) {
    const body = record(value);
    const problem: Problem = body;
    const code = typeof body.code === "string" ? body.code : "";
    const messages: Record<string, string> = {
      invalid_credentials: "Thông tin đăng nhập hoặc mã xác thực chưa đúng. Vui lòng kiểm tra lại.",
      unauthenticated: "Phiên làm việc đã hết hạn. Vui lòng đăng nhập lại.",
      forbidden: "Bạn chưa được cấp quyền thực hiện thao tác này.",
      csrf_invalid: "Phiên bảo vệ đã thay đổi. Vui lòng thử lại thao tác.",
      https_required: "Kết nối quản trị yêu cầu HTTPS. Vui lòng kiểm tra địa chỉ truy cập.",
      totp_invalid: "Mã xác thực chưa đúng hoặc đã hết hạn. Hãy dùng mã mới.",
      validation_failed: "Thông tin chưa hợp lệ. Vui lòng kiểm tra các trường bên dưới.",
      concurrency_conflict: "Dữ liệu có xung đột phiên bản. Vui lòng tải bản mới trước khi lưu lại.",
      slug_exists: "Đường dẫn đã tồn tại. Hãy chọn đường dẫn khác rồi lưu lại.",
      sku_exists: "SKU này đã tồn tại. Hãy chọn mã khác rồi lưu lại.",
      sku_locked: "Biến thể đã có giao dịch. SKU hiện tại không thể thay đổi.",
      sku_prefix_missing: "Loại sản phẩm chưa có tiền tố SKU. Hãy chọn loại khác hoặc bổ sung tiền tố.",
      material_sku_code_missing: "Chất liệu chưa có mã SKU. Hãy chọn chất liệu khác hoặc bổ sung mã.",
      variant_price_missing: "Biến thể cần giá niêm yết hợp lệ trước khi kích hoạt.",
      redirect_conflict: "Đường dẫn chuyển hướng trùng hoặc tạo vòng lặp. Hãy chọn đích khác.",
      product_type_change_confirmation_required: "Cần xác nhận trước khi đổi loại sản phẩm.",
      sku_code_exists: "Mã SKU chất liệu đã tồn tại. Hãy chọn mã khác rồi lưu lại.",
      product_type_code_exists: "Mã loại sản phẩm đã tồn tại. Hãy chọn mã khác rồi lưu lại.",
      attribute_code_exists: "Mã thuộc tính đã tồn tại trong loại sản phẩm. Hãy chọn mã khác.",
      option_code_exists: "Mã lựa chọn đã tồn tại trong thuộc tính. Hãy chọn mã khác.",
      attribute_in_use: "Thuộc tính đã được sử dụng. Giữ nguyên kiểu, phạm vi, đơn vị và cờ trục biến thể.",
      attribute_has_options: "Thuộc tính đã có lựa chọn. Không thể đổi sang kiểu khác.",
      attribute_not_option: "Chỉ thuộc tính kiểu lựa chọn mới quản lý được các lựa chọn.",
      slug_allocation_exhausted: "Chưa thể cấp đường dẫn tự động. Hãy nhập đường dẫn khác rồi lưu lại.",
      publication_history_unverified: "Cần xác minh lịch sử công khai. Liên hệ người quản lý trước khi sửa."
    };
    super(status === 429 ? "Có quá nhiều yêu cầu. Vui lòng chờ một phút rồi thử lại."
      : messages[code] ?? (status === 403 ? messages.forbidden
        : status === 401 ? messages.unauthenticated
          : status === 404 ? "Không tìm thấy dữ liệu. Dữ liệu có thể đã được thay đổi hoặc gỡ bỏ."
            : status === 409 ? "Thao tác có xung đột. Hãy kiểm tra thông tin trước khi thử lưu lại."
              : status === 422 ? "Chưa thể thực hiện thao tác. Vui lòng kiểm tra các điều kiện bên dưới."
                : problem.detail ?? problem.title ?? "Không thể kết nối. Vui lòng thử lại."));
    this.code = code;
    for (const [key, messages] of Object.entries(record(body.errors))) {
      if (Array.isArray(messages) && messages.every(x => typeof x === "string")) this.fields[key] = messages;
    }
    const conflictFields: Record<string, string> = {
      slug_exists: "slug", sku_exists: "sku", sku_locked: "sku", redirect_conflict: "redirectTo", sku_code_exists: "skuCode", product_type_code_exists: "code",
      attribute_code_exists: "code", option_code_exists: "code", slug_allocation_exhausted: "slug"
    };
    if (status === 409 && conflictFields[code] && !this.fields[conflictFields[code]]) this.fields[conflictFields[code]] = [this.message];
  }
}

export function isConcurrencyConflict(error: unknown): boolean {
  return error instanceof ApiError && error.status === 409 && error.code === "concurrency_conflict";
}

export class AdminApi {
  private csrf: string | undefined;
  onUnauthorized: () => void = () => {};
  readonly client;
  constructor(fetcher: typeof fetch = (...args) => fetch(...args)) {
    this.client = createClient<paths>({
      credentials: "include", cache: "no-store", fetch: fetcher
    });
    this.client.use({
      onRequest: async ({ request, schemaPath }) => {
        if (!schemaPath.startsWith("/api/v1/admin/")) throw new Error("Unsupported admin API path");
        if (!["GET", "HEAD", "OPTIONS"].includes(request.method)) {
          // Fresh token per unsafe operation, memory only; never replay a mutation.
          await this.refreshCsrf();
          request.headers.set("X-CSRF-TOKEN", this.csrf!);
        }
        return request;
      },
      onResponse: async ({ response, schemaPath }) => {
        if (!response.ok) {
          if (response.status === 401 && !schemaPath.endsWith("/login") && !schemaPath.endsWith("/me")) this.onUnauthorized();
          const body: unknown = await response.clone().json().catch(() => ({}));
          throw new ApiError(response.status, body);
        }
      }
    });
  }
  reset() { this.csrf = undefined; }
  async refreshCsrf() {
    const { data } = await this.client.GET("/api/v1/admin/auth/csrf");
    if (!data?.token) throw new ApiError(502, {});
    this.csrf = data.token;
  }
  async me(signal?: AbortSignal) {
    try {
      const { data } = await this.client.GET("/api/v1/admin/auth/me", { signal });
      if (!data) throw new ApiError(502, {});
      return data;
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) return null;
      throw error;
    }
  }
  async login(body: components["schemas"]["LoginRequest"]) {
    await this.client.POST("/api/v1/admin/auth/login", { body });
  }
  async logout() { await this.client.POST("/api/v1/admin/auth/logout"); }
  async changePassword(body: components["schemas"]["PasswordRequest"]) {
    await this.client.POST("/api/v1/admin/auth/change-password", { body });
  }
  async setupTotp() {
    const { data } = await this.client.POST("/api/v1/admin/auth/totp/setup");
    if (!data) throw new ApiError(502, {});
    return data;
  }
  async verifyTotp(body: components["schemas"]["TotpRequest"]) {
    await this.client.POST("/api/v1/admin/auth/totp/verify", { body });
  }
}
