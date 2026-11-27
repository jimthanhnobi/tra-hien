import type { components, paths } from "./generated/schema";

type Assert<T extends true> = T;

type PutConsentOp = paths["/api/v1/admin/customers/{id}/consents"]["put"];
type MergeCustomerOp = paths["/api/v1/admin/customers/{id}/merge"]["post"];
type EraseCustomerOp = paths["/api/v1/admin/customers/{id}/erase"]["post"];
type ExportCustomerOp = paths["/api/v1/admin/customers/{id}/export"]["get"];

type PutConsentBody = components["schemas"]["PutCustomerConsentRequest"];
type MergeCustomerBody = components["schemas"]["MergeCustomersRequest"];
type EraseCustomerBody = components["schemas"]["EraseCustomerRequest"];
type ConsentsResult = components["schemas"]["CustomerConsentsResult"];
type EraseResult = components["schemas"]["CustomerEraseResult"];
type ExportView = components["schemas"]["CustomerDataExportView"];
type ExportProfile = components["schemas"]["CustomerExportProfileView"];
type ExportAccount = components["schemas"]["CustomerExportAccountView"];
type ExportLogin = components["schemas"]["CustomerExportLoginView"];
type ExportOrder = components["schemas"]["CustomerExportOrderView"];
type ExportOrderLine = components["schemas"]["CustomerExportOrderLineView"];
type CustomerDetail = components["schemas"]["CustomerDetailView"];

export type CustomerPrivacyContractAssertions = [
  // All 4 P2-12 routes exist with expected HTTP verbs and response payloads
  Assert<"/api/v1/admin/customers/{id}/consents" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/customers/{id}/merge" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/customers/{id}/erase" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/customers/{id}/export" extends keyof paths ? true : false>,
  Assert<PutConsentOp["responses"][200]["content"]["application/json"] extends ConsentsResult ? true : false>,
  Assert<MergeCustomerOp["responses"][200]["content"]["application/json"] extends CustomerDetail ? true : false>,
  Assert<EraseCustomerOp["responses"][200]["content"]["application/json"] extends EraseResult ? true : false>,
  Assert<ExportCustomerOp["responses"][200]["content"]["application/json"] extends ExportView ? true : false>,

  // ASSUMPTION-P212-01 = A: rowVersion and mergeRowVersion are optional on all 3 mutation endpoints
  Assert<{ channel: "zalo_personal"; purpose: "marketing_new_products"; status: "granted"; evidence: "Đã đồng ý" } extends PutConsentBody ? true : false>,
  Assert<{ mergeCustomerId: string } extends MergeCustomerBody ? true : false>,
  Assert<{ mergeCustomerId: string; rowVersion?: string | null; mergeRowVersion?: string | null } extends MergeCustomerBody ? true : false>,
  Assert<{ reason: "Khách yêu cầu xóa"; requestChannel: "zalo_personal" } extends EraseCustomerBody ? true : false>,

  // Strict enum checks matching docs/spec/schema.sql & CustomerRules
  Assert<"zalo_personal" | "zalo_oa" | "sms" | "email" | "phone" extends PutConsentBody["channel"] ? true : false>,
  Assert<"marketing_new_products" | "marketing_promotions" | "customer_care" extends PutConsentBody["purpose"] ? true : false>,
  Assert<"granted" | "revoked" extends PutConsentBody["status"] ? true : false>,
  Assert<"zalo_personal" | "zalo_oa" | "phone" | "facebook" | "in_person" | "email" | "web" | "account" | "other" extends EraseCustomerBody["requestChannel"] ? true : false>,
  Assert<EraseResult["contactStatus"] extends "erased" ? true : false>,

  // Strict numeric integer fields (never number | string)
  Assert<string extends EraseResult["anonymizedOrdersCount"] ? false : true>,
  Assert<string extends EraseResult["scrubbedMergedStubsCount"] ? false : true>,
  Assert<string extends ExportProfile["lifetimeValueVnd"] ? false : true>,
  Assert<string extends ExportOrder["totalVnd"] ? false : true>,
  Assert<string extends ExportOrderLine["unitPriceVnd"] ? false : true>,

  // ASSUMPTION-P212-02 = A / BR-CRM-13: zero secrets, costs, profit, or internal notes on export DTOs
  Assert<"passwordHash" extends keyof ExportAccount ? false : true>,
  Assert<"securityStamp" extends keyof ExportAccount ? false : true>,
  Assert<"providerKey" extends keyof ExportLogin ? false : true>,
  Assert<"internalNote" extends keyof ExportOrder ? false : true>,
  Assert<"unitCostVnd" extends keyof ExportOrderLine ? false : true>,
  Assert<"avgCostVnd" extends keyof ExportOrderLine ? false : true>,
  Assert<"grossProfitVnd" extends keyof ExportOrderLine ? false : true>,
  Assert<"priceOverrideReason" extends keyof ExportOrderLine ? false : true>
];
