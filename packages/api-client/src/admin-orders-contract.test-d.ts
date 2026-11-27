import type { components, paths } from "./generated/schema";

type Assert<T extends true> = T;

type ListOrdersOp = paths["/api/v1/admin/orders"]["get"];
type CreateOrderOp = paths["/api/v1/admin/orders"]["post"];
type GetOrderOp = paths["/api/v1/admin/orders/{id}"]["get"];
type PatchOrderOp = paths["/api/v1/admin/orders/{id}"]["patch"];
type ConfirmOrderOp = paths["/api/v1/admin/orders/{id}/confirm"]["post"];
type CancelOrderOp = paths["/api/v1/admin/orders/{id}/cancel"]["post"];
type ReorderOrderOp = paths["/api/v1/admin/orders/{id}/reorder"]["post"];
type PackingSlipOp = paths["/api/v1/admin/orders/{id}/packing-slip"]["get"];
type StoreProvincesOp = paths["/api/v1/store/provinces"]["get"];
type StoreWardsOp = paths["/api/v1/store/provinces/{code}/wards"]["get"];

type CreateOrderBody = components["schemas"]["CreateAdminOrderRequest"];
type PatchOrderBody = components["schemas"]["PatchAdminOrderRequest"];
type ConfirmOrderBody = components["schemas"]["ConfirmAdminOrderRequest"];
type CancelOrderBody = components["schemas"]["CancelAdminOrderRequest"];
type OrderCreated = components["schemas"]["AdminOrderCreatedView"];
type OrderListItem = components["schemas"]["AdminOrderListItemView"];
type OrderDetail = components["schemas"]["AdminOrderDetailView"];
type OrderLine = components["schemas"]["AdminOrderLineView"];
type StoreProvinceList = components["schemas"]["StoreProvinceListView"];
type StoreWardList = components["schemas"]["StoreWardListView"];
type ListOrdersQuery = NonNullable<ListOrdersOp["parameters"]["query"]>;

export type AdminOrdersAndStoreAdminContractAssertions = [
  // All 8 Admin Orders routes exist; extend-payment stays in P3-03 (O6a = A)
  Assert<"/api/v1/admin/orders" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/orders/{id}" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/orders/{id}/confirm" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/orders/{id}/cancel" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/orders/{id}/reorder" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/orders/{id}/packing-slip" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/orders/{id}/extend-payment" extends keyof paths ? false : true>,

  // GAP-P214-01 = A: 2 public store administrative routes
  Assert<"/api/v1/store/provinces" extends keyof paths ? true : false>,
  Assert<"/api/v1/store/provinces/{code}/wards" extends keyof paths ? true : false>,
  Assert<StoreProvincesOp["responses"][200]["content"]["application/json"] extends StoreProvinceList ? true : false>,
  Assert<StoreWardsOp["responses"][200]["content"]["application/json"] extends StoreWardList ? true : false>,
  Assert<number extends StoreProvinceList["items"][number]["sortOrder"] ? true : false>,
  Assert<string extends StoreProvinceList["items"][number]["sortOrder"] ? false : true>,

  // Idempotency-Key required on POST admin/orders; 201 is sole 2xx response (no phantom 200)
  Assert<"Idempotency-Key" extends keyof CreateOrderOp["parameters"]["header"] ? true : false>,
  Assert<200 extends keyof CreateOrderOp["responses"] ? false : true>,
  Assert<CreateOrderOp["responses"][201]["content"]["application/json"] extends OrderCreated ? true : false>,

  // G11 = A: callConfirmed & callNote supported on both create & confirm
  Assert<"callConfirmed" extends keyof CreateOrderBody ? true : false>,
  Assert<"callNote" extends keyof CreateOrderBody ? true : false>,
  Assert<"callConfirmed" extends keyof ConfirmOrderBody ? true : false>,
  Assert<"callNote" extends keyof ConfirmOrderBody ? true : false>,
  Assert<"reason" extends keyof CancelOrderBody ? true : false>,

  // ASSUMPTION-P206-01 = A: PATCH supports discountNote and 3-way shippingFeeVnd (omitted | null | number),
  // while non-nullable PATCH fields exclude null to match strict runtime parser
  Assert<"discountNote" extends keyof PatchOrderBody ? true : false>,
  Assert<{ rowVersion: "v1" } extends PatchOrderBody ? true : false>,
  Assert<{ rowVersion: "v1"; shippingFeeVnd: null } extends PatchOrderBody ? true : false>,
  Assert<{ rowVersion: "v1"; shippingFeeVnd: 25000 } extends PatchOrderBody ? true : false>,
  Assert<string extends NonNullable<PatchOrderBody["shippingFeeVnd"]> ? false : true>,
  Assert<string extends NonNullable<CreateOrderBody["lines"][number]["unitPriceVnd"]> ? false : true>,
  Assert<null extends PatchOrderBody["lines"] ? false : true>,
  Assert<null extends PatchOrderBody["discountVnd"] ? false : true>,
  Assert<null extends PatchOrderBody["shipping"] ? false : true>,
  Assert<null extends NonNullable<PatchOrderBody["shipping"]>["receiverName"] ? false : true>,
  Assert<null extends NonNullable<PatchOrderBody["shipping"]>["receiverPhone"] ? false : true>,
  Assert<null extends NonNullable<PatchOrderBody["shipping"]>["provinceCode"] ? false : true>,
  Assert<null extends NonNullable<PatchOrderBody["shipping"]>["addressLine"] ? false : true>,
  Assert<null extends PatchOrderBody["discountNote"] ? true : false>,
  Assert<null extends PatchOrderBody["shippingFeeVnd"] ? true : false>,
  Assert<null extends PatchOrderBody["internalNote"] ? true : false>,
  Assert<null extends NonNullable<PatchOrderBody["shipping"]>["wardCode"] ? true : false>,
  Assert<null extends NonNullable<PatchOrderBody["shipping"]>["wardNameText"] ? true : false>,
  Assert<null extends NonNullable<PatchOrderBody["shipping"]>["customerNote"] ? true : false>,

  // Sales channel on admin create excludes 'web' (R-P201-04)
  Assert<"web" extends CreateOrderBody["salesChannel"] ? false : true>,
  Assert<"zalo_personal" extends CreateOrderBody["salesChannel"] ? true : false>,

  // Query flags aligned with O1/O2/O3a/O3b & P2-13
  Assert<"needs_refund" | "overpaid" | "expiring" extends NonNullable<ListOrdersQuery["flag"]> ? true : false>,

  // ASSUMPTION-P206-04 = A & BR-PRC-07: zero PII/cost on AdminOrderCreatedView, zero cost on AdminOrderListItemView, optional unitCostVnd on detail lines
  Assert<"receiverPhone" extends keyof OrderCreated ? false : true>,
  Assert<"unitCostVnd" extends keyof OrderCreated ? false : true>,
  Assert<"unitCostVnd" extends keyof OrderListItem ? false : true>,
  Assert<undefined extends OrderLine["unitCostVnd"] ? true : false>,
  Assert<GetOrderOp["responses"][200]["content"]["application/json"] extends OrderDetail ? true : false>,
  Assert<PatchOrderOp["responses"][200]["content"]["application/json"] extends OrderDetail ? true : false>,
  Assert<ConfirmOrderOp["responses"][200]["content"]["application/json"] extends OrderDetail ? true : false>,
  Assert<CancelOrderOp["responses"][200]["content"]["application/json"] extends OrderDetail ? true : false>,
  Assert<ReorderOrderOp["responses"][201]["content"]["application/json"] extends OrderDetail ? true : false>,
  Assert<string extends PackingSlipOp["responses"][200]["content"]["text/html"] ? true : false>
];
