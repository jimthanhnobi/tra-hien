import type { components, paths } from "./generated/schema";

type Assert<T extends true> = T;

type ImportOp = paths["/api/v1/admin/customers/import"]["post"];
type ImportResult = ImportOp["responses"][200]["content"]["application/json"];
type ImportSummary = ImportResult["summary"];
type ImportRow = ImportResult["rows"][number];
type CustomerDetail = components["schemas"]["CustomerDetailView"];
type CustomerSummary = components["schemas"]["CustomerSummaryView"];

type ExpectedRowStatus = "imported" | "would_import" | "skipped" | "error";
type ExpectedCoreReasonCode =
  | "imported"
  | "would_import"
  | "duplicate_in_file"
  | "existing_phone_skipped"
  | "missing_full_name"
  | "invalid_full_name"
  | "missing_contact"
  | "invalid_phone"
  | "invalid_zalo_name"
  | "invalid_customer_group"
  | "invalid_tag"
  | "invalid_note"
  | "invalid_had_purchase";
type ExpectedWarningCode = "zalo_only_reimport_risk" | "possible_duplicate_zalo_customer";

export type CustomerContractAssertions = [
  Assert<boolean extends ImportOp["parameters"]["query"]["dryRun"] ? true : false>,
  Assert<string extends ImportOp["requestBody"]["content"]["multipart/form-data"]["file"] ? true : false>,
  Assert<[boolean] extends [ImportSummary["committed"]] ? true : false>,
  Assert<[ImportSummary["committed"]] extends [boolean] ? true : false>,
  Assert<[string[]] extends [ImportSummary["newTags"]] ? true : false>,
  Assert<[ImportSummary["newTags"]] extends [string[]] ? true : false>,
  Assert<[ExpectedRowStatus] extends [ImportRow["status"]] ? true : false>,
  Assert<[ImportRow["status"]] extends [ExpectedRowStatus] ? true : false>,
  Assert<[ExpectedCoreReasonCode] extends [ImportRow["reasonCode"]] ? true : false>,
  Assert<[ImportRow["reasonCode"]] extends [ExpectedCoreReasonCode] ? true : false>,
  Assert<[ExpectedWarningCode] extends [ImportRow["warnings"][number]] ? true : false>,
  Assert<[ImportRow["warnings"][number]] extends [ExpectedWarningCode] ? true : false>,
  Assert<"avgCostVnd" extends keyof CustomerDetail ? false : true>,
  Assert<"avgCostVnd" extends keyof CustomerSummary ? false : true>,
  Assert<"passwordHash" extends keyof CustomerDetail ? false : true>,
  Assert<"securityStamp" extends keyof CustomerDetail ? false : true>
];
