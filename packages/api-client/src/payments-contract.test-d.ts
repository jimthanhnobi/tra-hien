import type { components, paths } from "./generated/schema";

type Assert<T extends true> = T;
type RecordPaymentOp = paths["/api/v1/admin/orders/{id}/payments"]["post"];
type VoidPaymentOp = paths["/api/v1/admin/payments/{id}/void"]["post"];
type AwaitingOp = paths["/api/v1/admin/payments/awaiting"]["get"];
type CreateRefundOp = paths["/api/v1/admin/orders/{id}/refunds"]["post"];

type RecordPaymentBody = components["schemas"]["RecordOrderPaymentRequest"];
type CreateRefundBody = components["schemas"]["CreateOrderRefundRequest"];
type PaymentRecorded = components["schemas"]["PaymentRecordedView"];
type PaymentVoided = components["schemas"]["PaymentVoidedView"];
type RefundRecorded = components["schemas"]["RefundRecordedView"];
type AwaitingPage = components["schemas"]["AwaitingPaymentsPage"];
type AwaitingBankTransferItem = components["schemas"]["AwaitingBankTransferItem"];
type AwaitingQuery = AwaitingOp["parameters"]["query"];

export type PaymentsContractAssertions = [
  Assert<"/api/v1/admin/orders/{id}/payments" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/payments/{id}/void" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/payments/awaiting" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/orders/{id}/refunds" extends keyof paths ? true : false>,
  Assert<"/api/v1/admin/cod-settlements" extends keyof paths ? false : true>,
  Assert<"Idempotency-Key" extends keyof RecordPaymentOp["parameters"]["header"] ? true : false>,
  Assert<"Idempotency-Key" extends keyof CreateRefundOp["parameters"]["header"] ? true : false>,
  Assert<RecordPaymentBody["method"] extends "bank_transfer" | "cash" ? true : false>,
  Assert<"cod" extends RecordPaymentBody["method"] ? false : true>,
  Assert<string extends RecordPaymentBody["amountVnd"] ? false : true>,
  Assert<number extends RecordPaymentBody["amountVnd"] ? true : false>,
  Assert<{ method: "bank_transfer"; amountVnd: 500000; receivedAt: "2026-11-25T07:00:00Z" } extends RecordPaymentBody ? true : false>,
  Assert<CreateRefundBody["kind"] extends "overpayment" | "return" | "cancellation" | "adjustment" ? true : false>,
  Assert<"other" extends CreateRefundBody["kind"] ? false : true>,
  Assert<{ kind: "cancellation"; amountVnd: 200000; method: "cash"; reason: "Hoan tien"; refundedAt: "2026-11-25T08:00:00Z" } extends CreateRefundBody ? true : false>,
  Assert<{ kind: "return"; amountVnd: 230000; shippingAmountVnd: 30000; method: "bank_transfer"; reason: "Hoan tra hang"; returnRequestId: "019a0000-0000-7000-8000-000000000001"; refundedAt: "2026-11-25T08:00:00Z" } extends CreateRefundBody ? true : false>,
  Assert<"cod" extends CreateRefundBody["method"] ? false : true>,
  Assert<string extends NonNullable<CreateRefundBody["shippingAmountVnd"]> ? false : true>,
  Assert<"reason" extends keyof VoidPaymentOp["requestBody"]["content"]["application/json"] ? true : false>,
  Assert<AwaitingQuery["type"] extends "bank_transfer" | "cod" ? true : false>,
  Assert<PaymentVoided["status"] extends "voided" ? true : false>,
  Assert<number extends PaymentRecorded["netPaidVnd"] ? true : false>,
  Assert<string extends PaymentRecorded["netPaidVnd"] ? false : true>,
  Assert<RefundRecorded["kind"] extends "overpayment" | "return" | "cancellation" | "adjustment" ? true : false>,
  Assert<number extends RefundRecorded["shippingAmountVnd"] ? true : false>,
  Assert<string extends RefundRecorded["shippingAmountVnd"] ? false : true>,
  Assert<number extends RefundRecorded["refundedVnd"] ? true : false>,
  Assert<string extends RefundRecorded["refundedVnd"] ? false : true>,
  Assert<number extends AwaitingBankTransferItem["effectivePaidVnd"] ? true : false>,
  Assert<string extends AwaitingBankTransferItem["effectivePaidVnd"] ? false : true>,
  Assert<number extends AwaitingPage["totalAmountVnd"] ? true : false>,
  Assert<"note" extends keyof PaymentRecorded ? false : true>,
  Assert<"bankReference" extends keyof PaymentRecorded ? false : true>,
  Assert<"reason" extends keyof RefundRecorded ? false : true>,
  Assert<"bankReference" extends keyof RefundRecorded ? false : true>
];
