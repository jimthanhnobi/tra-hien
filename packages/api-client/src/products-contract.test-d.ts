import type { components, paths } from "./generated/schema";

type Assert<T extends true> = T;
type Create = paths["/api/v1/admin/products"]["post"]["requestBody"]["content"]["application/json"];
type Patch = paths["/api/v1/admin/products/{id}"]["patch"]["requestBody"]["content"]["application/json"];
type VariantPatch = paths["/api/v1/admin/products/{id}/variants/{childId}"]["patch"]["requestBody"]["content"]["application/json"];
type Published = paths["/api/v1/admin/products/{id}/publish"]["post"]["responses"][200]["content"]["application/json"];

type Import = paths["/api/v1/admin/products/import"]["post"];
type ImportRow = Import["responses"][200]["content"]["application/json"]["rows"][number];

export type ProductContractAssertions = [
  Assert<boolean extends Import["parameters"]["query"]["dryRun"] ? true : false>,
  Assert<string extends Import["requestBody"]["content"]["multipart/form-data"]["file"] ? true : false>,
  Assert<number | null extends ImportRow["priceVnd"] ? true : false>,
  Assert<ImportRow["errors"][number] extends { row: number | string; column: string; code: string; message: string } ? true : false>,
  Assert<{ name: string; productTypeId: string } extends Create ? true : false>,
  Assert<Record<string, never> extends Create ? false : true>,
  Assert<{ name: null; productTypeId: string } extends Create ? false : true>,
  Assert<{ name: string; productTypeId: null } extends Create ? false : true>,
  Assert<{ name: string; productTypeId: string; defaultVariant: null } extends Create ? false : true>,
  Assert<{ rowVersion: string; descriptionHtml: null } extends Patch ? true : false>,
  Assert<Record<string, never> extends Patch ? false : true>,
  Assert<{ rowVersion: null } extends Patch ? false : true>,
  Assert<{ rowVersion: string; priceVnd: null } extends VariantPatch ? true : false>,
  Assert<Record<string, never> extends VariantPatch ? false : true>,
  Assert<{ rowVersion: null } extends VariantPatch ? false : true>,
  Assert<{ rowVersion: string; name: null } extends Patch ? false : true>,
  Assert<{ rowVersion: string; isFeatured: null } extends Patch ? false : true>,
  Assert<{ rowVersion: string; categoryIds: null } extends Patch ? false : true>,
  Assert<{ rowVersion: string; position: null } extends VariantPatch ? false : true>,
  Assert<{ rowVersion: string; status: null } extends VariantPatch ? false : true>,
  Assert<{ rowVersion: string; subtitle: null; noindexReason: null; confirmTypeChange: null } extends Patch ? true : false>,
  Assert<{ rowVersion: string; confirmTypeChange: false } extends Patch ? true : false>,
  Assert<{ rowVersion: string; confirmTypeChange: "wrong" } extends Patch ? false : true>,
  Assert<{ rowVersion: string; noindexReason: { malformed: true } } extends Patch ? false : true>,
  Assert<"skuBase" extends keyof Patch ? false : true>,
  Assert<"status" extends keyof Patch ? false : true>,
  Assert<"descriptionWordCount" extends keyof Patch ? false : true>,
  Assert<"defaultVariant" extends keyof Patch ? false : true>,
  Assert<"avgCostVnd" extends keyof components["schemas"]["ProductData"] ? false : true>,
  Assert<Published extends components["schemas"]["ProductData"] ? true : false>
];
