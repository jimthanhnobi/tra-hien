import type { components, paths } from "./generated/schema";

type Assert<T extends true> = T;
type Values = components["schemas"]["SettingsGroup"]["values"];
type Put = paths["/api/v1/admin/settings/{group}"]["put"]["requestBody"]["content"]["application/json"];
type Examples = [
  { group: "shipping"; version: string; values: { "shipping.flat_fee_vnd": 30000; "shipping.free_threshold_vnd": null } },
  { group: "orders"; version: string; values: { "orders.cod_requires_call": true } },
  { group: "payment"; version: string; values: { "payment.methods_enabled": ["cod", "bank_transfer"] } },
  { group: "referral"; version: string; values: { "referral.reward_voucher": { type: "fixed"; value: 100000; max_discount_vnd: null; min_order_vnd: 500000; valid_days: 90 } } }
];

// Type-level JSON examples: no casts, and non-JSON values must remain rejected.
export type SettingsContractAssertions = [
  Assert<Examples[number] extends components["schemas"]["SettingsGroup"] ? true : false>,
  Assert<Pick<Examples[number], "version" | "values"> extends Put ? true : false>,
  Assert<Put["values"] extends Values ? true : false>,
  Assert<Values extends Put["values"] ? true : false>,
  Assert<{ bad: () => void } extends Values ? false : true>,
  Assert<{ bad: undefined } extends Values ? false : true>,
  Assert<{ nested: { bad: () => void } } extends Values ? false : true>,
  Assert<{ bad: bigint } extends Values ? false : true>
];
