using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TeaPremium.Application.Payments;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Api.Payments;

public static class PaymentOpenApi
{
    private static readonly string[] ManualMethods = [PaymentMethod.BankTransfer, PaymentMethod.Cash];
    private static readonly string[] RefundKinds = [RefundKind.Overpayment, RefundKind.Return, RefundKind.Cancellation, RefundKind.Adjustment];
    private static readonly string[] PaymentProviders = [PaymentProvider.Manual, PaymentProvider.Sepay];
    private static readonly string[] PaymentStatuses = [PaymentRecordStatus.AwaitingSettlement, PaymentRecordStatus.Confirmed, PaymentRecordStatus.Voided];
    private static readonly string[] AwaitingTypes = [AwaitingPaymentType.BankTransfer, AwaitingPaymentType.Cod];
    private static readonly string[] OrderOrigins = [OrderOrigin.Web, OrderOrigin.Admin];
    private static readonly string[] SalesChannels =
    [
        SalesChannel.Web, SalesChannel.ZaloPersonal, SalesChannel.ZaloOa,
        SalesChannel.Facebook, SalesChannel.Phone, SalesChannel.InPerson, SalesChannel.Other
    ];
    private static readonly string[] OrderStatuses =
    [
        OrderStatus.Pending, OrderStatus.Confirmed, OrderStatus.Shipping,
        OrderStatus.Delivered, OrderStatus.DeliveryFailed, OrderStatus.Cancelled
    ];
    private static readonly string[] OrderPaymentStatuses =
    [
        OrderPaymentStatus.Unpaid, OrderPaymentStatus.PartiallyPaid, OrderPaymentStatus.Paid,
        OrderPaymentStatus.PartiallyRefunded, OrderPaymentStatus.Refunded
    ];
    private static readonly string[] ReturnStatuses =
    [
        ReturnRequestStatus.Requested, ReturnRequestStatus.Approved,
        ReturnRequestStatus.Rejected, ReturnRequestStatus.Resolved
    ];

    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        var schemas = document.Components!.Schemas!;

        foreach (var requestName in new[]
        {
            nameof(RecordOrderPaymentRequest),
            nameof(VoidPaymentRequest),
            nameof(CreateOrderRefundRequest)
        })
        {
            if (schemas.TryGetValue(requestName, out var raw) && raw is OpenApiSchema schema)
            {
                schema.AdditionalPropertiesAllowed = false;
            }
        }

        ConfigureRequired(schemas, nameof(RecordOrderPaymentRequest), ["method", "amountVnd", "receivedAt"], ["method", "amountVnd", "receivedAt"]);
        SetEnum(schemas, nameof(RecordOrderPaymentRequest), "method", ManualMethods, nullable: false);
        SetStrictMinimum(schemas, nameof(RecordOrderPaymentRequest), "amountVnd", "1");

        ConfigureRequired(schemas, nameof(VoidPaymentRequest), ["reason"], ["reason"]);

        ConfigureRequired(
            schemas,
            nameof(CreateOrderRefundRequest),
            ["kind", "amountVnd", "method", "reason", "refundedAt"],
            ["kind", "amountVnd", "shippingAmountVnd", "method", "reason", "refundedAt"]);
        SetEnum(schemas, nameof(CreateOrderRefundRequest), "kind", RefundKinds, nullable: false);
        SetEnum(schemas, nameof(CreateOrderRefundRequest), "method", ManualMethods, nullable: false);
        SetStrictMinimum(schemas, nameof(CreateOrderRefundRequest), "amountVnd", "1");
        SetStrictMinimum(schemas, nameof(CreateOrderRefundRequest), "shippingAmountVnd", "0", clearDefault: true);

        SetEnum(schemas, nameof(PaymentRecordedView), "method", ManualMethods, nullable: false);
        SetEnum(schemas, nameof(PaymentRecordedView), "provider", PaymentProviders, nullable: false);
        SetEnum(schemas, nameof(PaymentRecordedView), "status", PaymentStatuses, nullable: false);
        SetEnum(schemas, nameof(PaymentRecordedView), "orderStatus", OrderStatuses, nullable: false);
        SetEnum(schemas, nameof(PaymentRecordedView), "orderPaymentStatus", OrderPaymentStatuses, nullable: false);
        StripStringNumericType(schemas, nameof(PaymentRecordedView), "amountVnd", "paidVnd", "refundedVnd", "netPaidVnd");

        SetEnum(schemas, nameof(PaymentVoidedView), "status", [PaymentRecordStatus.Voided], nullable: false);
        SetEnum(schemas, nameof(PaymentVoidedView), "orderStatus", OrderStatuses, nullable: false);
        SetEnum(schemas, nameof(PaymentVoidedView), "orderPaymentStatus", OrderPaymentStatuses, nullable: false);
        StripStringNumericType(schemas, nameof(PaymentVoidedView), "amountVnd", "paidVnd", "refundedVnd", "netPaidVnd");

        SetEnum(schemas, nameof(RefundRecordedView), "kind", RefundKinds, nullable: false);
        SetEnum(schemas, nameof(RefundRecordedView), "method", ManualMethods, nullable: false);
        SetEnum(schemas, nameof(RefundRecordedView), "returnRequestStatus", ReturnStatuses, nullable: true);
        SetEnum(schemas, nameof(RefundRecordedView), "orderStatus", OrderStatuses, nullable: false);
        SetEnum(schemas, nameof(RefundRecordedView), "orderPaymentStatus", OrderPaymentStatuses, nullable: false);
        StripStringNumericType(schemas, nameof(RefundRecordedView), "amountVnd", "shippingAmountVnd", "paidVnd", "refundedVnd", "netPaidVnd");

        SetEnum(schemas, nameof(AwaitingPaymentsPage), "type", AwaitingTypes, nullable: false);
        StripStringNumericType(schemas, nameof(AwaitingPaymentsPage), "page", "pageSize", "totalItems", "totalAmountVnd");
        SetEnum(schemas, nameof(AwaitingBankTransferItem), "origin", OrderOrigins, nullable: false);
        SetEnum(schemas, nameof(AwaitingBankTransferItem), "salesChannel", SalesChannels, nullable: false);
        SetEnum(schemas, nameof(AwaitingBankTransferItem), "orderStatus", OrderStatuses, nullable: false);
        SetEnum(schemas, nameof(AwaitingBankTransferItem), "paymentStatus", OrderPaymentStatuses, nullable: false);
        StripStringNumericType(schemas, nameof(AwaitingBankTransferItem), "totalVnd", "paidVnd", "effectivePaidVnd", "remainingVnd");
        StripStringNumericType(schemas, nameof(AwaitingCodPaymentItem), "amountVnd");

        if (document.Paths.TryGetValue("/api/v1/admin/payments/awaiting", out var awaitingPath)
            && awaitingPath.Operations is not null
            && awaitingPath.Operations.TryGetValue(HttpMethod.Get, out var awaitingOp)
            && awaitingOp.Parameters is not null)
        {
            var typeParam = awaitingOp.Parameters
                .FirstOrDefault(p => string.Equals(p.Name, "type", StringComparison.Ordinal)) as OpenApiParameter;
            if (typeParam is not null)
            {
                typeParam.Required = true;
                typeParam.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Enum = AwaitingTypes.Select(v => (JsonNode)JsonValue.Create(v)!).ToList()
                };
            }
        }

        return Task.CompletedTask;
    }

    private static void ConfigureRequired(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        string[] required,
        string[] nonNullable)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema || schema.Properties is null)
            return;

        schema.Required = required.ToHashSet(StringComparer.Ordinal);
        foreach (var field in nonNullable)
        {
            if (schema.Properties.TryGetValue(field, out var prop))
            {
                var copy = (OpenApiSchema)prop.CreateShallowCopy();
                copy.Type &= ~JsonSchemaType.Null;
                schema.Properties[field] = copy;
            }
        }
    }

    private static void SetStrictMinimum(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        string field,
        string minimum,
        bool clearDefault = false)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema
            || schema.Properties is null || !schema.Properties.TryGetValue(field, out var prop))
            return;

        var copy = (OpenApiSchema)prop.CreateShallowCopy();
        copy.Type &= ~JsonSchemaType.String;
        copy.Pattern = null;
        copy.Minimum = minimum;
        if (clearDefault) copy.Default = null;
        schema.Properties[field] = copy;
    }

    private static void StripStringNumericType(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        params string[] fields)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema || schema.Properties is null)
            return;

        foreach (var field in fields)
        {
            if (!schema.Properties.TryGetValue(field, out var prop))
                continue;
            var copy = (OpenApiSchema)prop.CreateShallowCopy();
            copy.Type &= ~JsonSchemaType.String;
            copy.Pattern = null;
            schema.Properties[field] = copy;
        }
    }

    private static void SetEnum(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        string field,
        string[] values,
        bool nullable)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema
            || schema.Properties is null || !schema.Properties.TryGetValue(field, out var prop))
            return;

        var copy = (OpenApiSchema)prop.CreateShallowCopy();
        copy.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        copy.Enum = values.Select(v => (JsonNode)JsonValue.Create(v)!).ToList();
        schema.Properties[field] = copy;
    }
}
