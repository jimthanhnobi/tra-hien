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
      validation_failed: "Thông tin chưa hợp lệ. Vui lòng kiểm tra các trường bên dưới."
    };
    super(status === 429 ? "Có quá nhiều yêu cầu. Vui lòng chờ một phút rồi thử lại."
      : messages[code] ?? (status === 403 ? messages.forbidden
        : status === 401 ? messages.unauthenticated
          : problem.detail ?? problem.title ?? "Không thể kết nối. Vui lòng thử lại."));
    this.code = code;
    for (const [key, messages] of Object.entries(record(body.errors))) {
      if (Array.isArray(messages) && messages.every(x => typeof x === "string")) this.fields[key] = messages;
    }
  }
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
