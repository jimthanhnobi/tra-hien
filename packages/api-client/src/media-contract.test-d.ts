import type { components, paths } from "./generated/schema";

type Assert<T extends true> = T;
type Intent = paths["/api/v1/admin/media/upload-intents"]["post"]["requestBody"]["content"]["application/json"];
type Complete = paths["/api/v1/admin/media/upload-intents/{id}/complete"]["post"]["responses"][200]["content"]["application/json"];
type Library = paths["/api/v1/admin/media"]["get"]["responses"][200]["content"]["application/json"];

export type MediaContractAssertions = [
  Assert<{ fileName: string; mimeType: string; byteSize: number; purpose: string; slugHint: string } extends Intent ? true : false>,
  Assert<Complete extends components["schemas"]["AssetView"] ? true : false>,
  Assert<Library extends components["schemas"]["MediaPage"] ? true : false>,
  Assert<"storageKey" extends keyof Complete ? false : true>,
  Assert<"createdBy" extends keyof Complete ? false : true>,
  Assert<"originalName" extends keyof Complete ? false : true>
];
