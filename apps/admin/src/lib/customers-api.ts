import type { components, paths } from "@teapremium/api-client";
import { AdminApi, ApiError } from "./api";
import { isUuid } from "./inventory-forms";

type Schemas = components["schemas"];

export type CustomerSummary = Schemas["CustomerSummaryView"];
export type CustomerPage = Schemas["CatalogPageOfCustomerSummaryView"];
export type CustomerDetail = Schemas["CustomerDetailView"];
export type CustomerStats = Schemas["CustomerStatsView"];
export type CustomerAccountSummary = Schemas["CustomerAccountSummaryView"];
export type CustomerTag = Schemas["CustomerTagView"];
export type CustomerTagsResult = Schemas["CustomerTagsResult"];
export type CustomerAddress = Schemas["CustomerAddressView"];
export type CustomerInterest = Schemas["CustomerInterestView"];
export type CustomerConsent = Schemas["CustomerConsentView"];
export type CustomerOrderSummary = Schemas["CustomerOrderSummaryView"];
export type CustomerOrderPage = Schemas["CatalogPageOfCustomerOrderSummaryView"];
export type CustomerInteraction = Schemas["CustomerInteractionView"];
export type CustomerInteractionPage = Schemas["CatalogPageOfCustomerInteractionView"];
export type FollowUpItem = Schemas["FollowUpItemView"];
export type FollowUpPage = Schemas["CatalogPageOfFollowUpItemView"];
export type CustomerImportResult = Schemas["CustomerImportResult"];
export type CustomerImportSummary = Schemas["CustomerImportSummary"];
export type CustomerImportRow = Schemas["CustomerImportRow"];
export type CustomerImportFieldError = Schemas["CustomerImportFieldError"];

export type CreateCustomerRequest = Schemas["CreateCustomerRequest"];
export type PatchCustomerRequest = Schemas["PatchCustomerRequest"];
export type CorrectCustomerIdentityRequest = Schemas["CorrectCustomerIdentityRequest"];
export type CreateTagRequest = Schemas["CreateTagRequest"];
export type PatchTagRequest = Schemas["PatchTagRequest"];
export type PutCustomerTagsRequest = Schemas["PutCustomerTagsRequest"];
export type CreateCustomerAddressRequest = Schemas["CreateCustomerAddressRequest"];
export type PatchCustomerAddressRequest = Schemas["PatchCustomerAddressRequest"];
export type CreateCustomerInteractionRequest = Schemas["CreateCustomerInteractionRequest"];
export type PatchCustomerInteractionRequest = Schemas["PatchCustomerInteractionRequest"];
export type CompleteFollowUpRequest = Schemas["CompleteFollowUpRequest"];

export type CustomerListQuery = NonNullable<paths["/api/v1/admin/customers"]["get"]["parameters"]["query"]>;
export type CustomerOrdersQuery = NonNullable<paths["/api/v1/admin/customers/{id}/orders"]["get"]["parameters"]["query"]>;
export type CustomerInteractionsQuery = NonNullable<paths["/api/v1/admin/customers/{id}/interactions"]["get"]["parameters"]["query"]>;
export type FollowUpsQuery = NonNullable<paths["/api/v1/admin/follow-ups"]["get"]["parameters"]["query"]>;

export const customersRootKey = ["customers"] as const;
export const customersListKey = [...customersRootKey, "list"] as const;
export const customerDetailKey = (id: string) => [...customersRootKey, "detail", id] as const;
export const customerOrdersKey = (id: string) => [...customersRootKey, "orders", id] as const;
export const customerInteractionsKey = (id: string) => [...customersRootKey, "interactions", id] as const;
export const customerTagsKey = [...customersRootKey, "tags"] as const;
export const followUpsKey = [...customersRootKey, "follow-ups"] as const;

function required<T>(data: T | undefined): T {
  if (data === undefined) throw new ApiError(502, {});
  return data;
}

function assertUuid(id: string) {
  if (!isUuid(id)) throw new ApiError(400, { code: "validation_failed" });
}

/**
 * All CRM paths and request/response DTOs are strictly bound to `@teapremium/api-client`.
 * Permission alignment notes (verified against `CustomerEndpoints.cs`, `CustomerImportEndpoint.cs`, `InteractionEndpoints.cs`):
 * - `customers.read`: `GET customers`, `GET customers/{id}`, `GET customers/{id}/orders`, `GET customers/{id}/interactions`, `GET follow-ups`.
 * - `customers.write`: all mutations, plus `GET admin/tags` and `GET admin/customers/{id}/addresses`
 *   (`GAP-P202-02 = B`, currently `IMPLEMENTED_BUT_NOT_CONFIRMED` per `ADR-P2-01` — callers must gate them by `customers.write`).
 * - `owner` role + `customers.write`: `origin`, `acquisitionSource`, `hadLegacyPurchase` in `POST correct-identity` (`BR-CRM-04`).
 */
export class CustomersApi {
  constructor(private readonly admin: AdminApi) {}

  async listCustomers(query: CustomerListQuery, signal?: AbortSignal): Promise<CustomerPage> {
    return required((await this.admin.client.GET("/api/v1/admin/customers", { params: { query }, signal })).data);
  }

  async createCustomer(body: CreateCustomerRequest): Promise<CustomerDetail> {
    return required((await this.admin.client.POST("/api/v1/admin/customers", { body })).data);
  }

  async getCustomer(id: string, signal?: AbortSignal): Promise<CustomerDetail> {
    assertUuid(id);
    return required((await this.admin.client.GET("/api/v1/admin/customers/{id}", {
      params: { path: { id } },
      signal
    })).data);
  }

  async patchCustomer(id: string, body: PatchCustomerRequest): Promise<CustomerDetail> {
    assertUuid(id);
    return required((await this.admin.client.PATCH("/api/v1/admin/customers/{id}", {
      params: { path: { id } },
      body
    })).data);
  }

  async correctIdentity(id: string, body: CorrectCustomerIdentityRequest): Promise<CustomerDetail> {
    assertUuid(id);
    return required((await this.admin.client.POST("/api/v1/admin/customers/{id}/correct-identity", {
      params: { path: { id } },
      body
    })).data);
  }

  async listOrders(id: string, query: CustomerOrdersQuery, signal?: AbortSignal): Promise<CustomerOrderPage> {
    assertUuid(id);
    return required((await this.admin.client.GET("/api/v1/admin/customers/{id}/orders", {
      params: { path: { id }, query },
      signal
    })).data);
  }

  async listTags(signal?: AbortSignal): Promise<CustomerTag[]> {
    return required((await this.admin.client.GET("/api/v1/admin/tags", { signal })).data);
  }

  async createTag(body: CreateTagRequest): Promise<CustomerTag> {
    return required((await this.admin.client.POST("/api/v1/admin/tags", { body })).data);
  }

  async patchTag(id: string, body: PatchTagRequest): Promise<CustomerTag> {
    assertUuid(id);
    return required((await this.admin.client.PATCH("/api/v1/admin/tags/{id}", {
      params: { path: { id } },
      body
    })).data);
  }

  async putCustomerTags(id: string, body: PutCustomerTagsRequest): Promise<CustomerTagsResult> {
    assertUuid(id);
    return required((await this.admin.client.PUT("/api/v1/admin/customers/{id}/tags", {
      params: { path: { id } },
      body
    })).data);
  }

  async listAddresses(id: string, signal?: AbortSignal): Promise<CustomerAddress[]> {
    assertUuid(id);
    return required((await this.admin.client.GET("/api/v1/admin/customers/{id}/addresses", {
      params: { path: { id } },
      signal
    })).data);
  }

  async createAddress(id: string, body: CreateCustomerAddressRequest): Promise<CustomerAddress> {
    assertUuid(id);
    return required((await this.admin.client.POST("/api/v1/admin/customers/{id}/addresses", {
      params: { path: { id } },
      body
    })).data);
  }

  async patchAddress(id: string, aid: string, body: PatchCustomerAddressRequest): Promise<CustomerAddress> {
    assertUuid(id);
    assertUuid(aid);
    return required((await this.admin.client.PATCH("/api/v1/admin/customers/{id}/addresses/{aid}", {
      params: { path: { id, aid } },
      body
    })).data);
  }

  async deleteAddress(id: string, aid: string, rowVersion?: string | null): Promise<void> {
    assertUuid(id);
    assertUuid(aid);
    await this.admin.client.DELETE("/api/v1/admin/customers/{id}/addresses/{aid}", {
      params: { path: { id, aid } },
      body: rowVersion ? { rowVersion } : undefined
    });
  }

  async importCustomers(file: File, dryRun: boolean): Promise<CustomerImportResult> {
    return required((await this.admin.client.POST("/api/v1/admin/customers/import", {
      params: { query: { dryRun } },
      body: { file: file.name },
      bodySerializer: () => {
        const form = new FormData();
        form.append("file", file, file.name);
        return form;
      }
    })).data);
  }

  async listInteractions(
    id: string,
    query: CustomerInteractionsQuery,
    signal?: AbortSignal
  ): Promise<CustomerInteractionPage> {
    assertUuid(id);
    return required((await this.admin.client.GET("/api/v1/admin/customers/{id}/interactions", {
      params: { path: { id }, query },
      signal
    })).data);
  }

  async createInteraction(id: string, body: CreateCustomerInteractionRequest): Promise<CustomerInteraction> {
    assertUuid(id);
    return required((await this.admin.client.POST("/api/v1/admin/customers/{id}/interactions", {
      params: { path: { id } },
      body
    })).data);
  }

  async patchInteraction(
    id: string,
    iid: string,
    body: PatchCustomerInteractionRequest
  ): Promise<CustomerInteraction> {
    assertUuid(id);
    assertUuid(iid);
    return required((await this.admin.client.PATCH("/api/v1/admin/customers/{id}/interactions/{iid}", {
      params: { path: { id, iid } },
      body
    })).data);
  }

  async completeFollowUp(id: string, iid: string, body: CompleteFollowUpRequest): Promise<CustomerInteraction> {
    assertUuid(id);
    assertUuid(iid);
    return required((await this.admin.client.POST("/api/v1/admin/customers/{id}/interactions/{iid}/complete-follow-up", {
      params: { path: { id, iid } },
      body
    })).data);
  }

  async listFollowUps(query: FollowUpsQuery, signal?: AbortSignal): Promise<FollowUpPage> {
    return required((await this.admin.client.GET("/api/v1/admin/follow-ups", {
      params: { query },
      signal
    })).data);
  }
}
