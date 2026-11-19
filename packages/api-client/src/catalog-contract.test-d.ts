import type { components, paths } from "./generated/schema";

type Assert<T extends true> = T;
type CreateType = paths["/api/v1/admin/product-types"]["post"]["requestBody"]["content"]["application/json"];
type TypePatch = paths["/api/v1/admin/product-types/{id}"]["patch"]["requestBody"]["content"]["application/json"];
type AttributePatch = paths["/api/v1/admin/product-types/{id}/attributes/{attrId}"]["patch"]["requestBody"]["content"]["application/json"];
type OptionPatch = paths["/api/v1/admin/product-types/{id}/attributes/{attrId}/options/{optId}"]["patch"]["requestBody"]["content"]["application/json"];
type Created = paths["/api/v1/admin/product-types"]["post"]["responses"][201]["content"]["application/json"];

export type CatalogContractAssertions = [
  Assert<{ code: string; name: string; skuPrefix: null } extends CreateType ? true : false>,
  Assert<{ rowVersion: string; skuPrefix: null } extends TypePatch ? true : false>,
  Assert<{ rowVersion: string; isActive: false } extends AttributePatch ? true : false>,
  Assert<{ rowVersion: string; label: string } extends OptionPatch ? true : false>,
  Assert<Record<string, never> extends TypePatch ? false : true>,
  Assert<"id" extends keyof TypePatch ? false : true>,
  Assert<"productTypeId" extends keyof AttributePatch ? false : true>,
  Assert<"attributeId" extends keyof OptionPatch ? false : true>,
  Assert<Created extends components["schemas"]["ProductTypeView"] ? true : false>
];
