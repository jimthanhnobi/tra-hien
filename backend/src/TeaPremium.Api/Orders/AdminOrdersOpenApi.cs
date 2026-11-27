using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Orders;
using TeaPremium.Domain.Orders;

namespace TeaPremium.Api.Orders;

public static class AdminOrdersOpenApi
{
    private static readonly string[] OrderOrigins = [OrderOrigin.Web, OrderOrigin.Admin];
    private static readonly string[] AllSalesChannels =
    [
        SalesChannel.Web, SalesChannel.ZaloPersonal, SalesChannel.ZaloOa,
        SalesChannel.Facebook, SalesChannel.Phone, SalesChannel.InPerson, SalesChannel.Other
    ];
    private static readonly string[] AdminSalesChannels =
    [
        SalesChannel.ZaloPersonal, SalesChannel.ZaloOa,
        SalesChannel.Facebook, SalesChannel.Phone, SalesChannel.InPerson, SalesChannel.Other
    ];
    private static readonly string[] OrderStatuses =
    [
        OrderStatus.Pending, OrderStatus.Confirmed, OrderStatus.Shipping,
        OrderStatus.Delivered, OrderStatus.DeliveryFailed, OrderStatus.Cancelled
    ];
    private static readonly string[] OrderPaymentMethods = [OrderPaymentMethod.Cod, OrderPaymentMethod.BankTransfer];
    private static readonly string[] OrderPaymentStatuses =
    [
        OrderPaymentStatus.Unpaid, OrderPaymentStatus.PartiallyPaid, OrderPaymentStatus.Paid,
        OrderPaymentStatus.PartiallyRefunded, OrderPaymentStatus.Refunded
    ];
    private static readonly string[] PriceSources = [OrderPriceSource.List, OrderPriceSource.Sale, OrderPriceSource.Override];
    private static readonly string[] ActorTypes = [OrderActorType.Staff, OrderActorType.Customer, OrderActorType.System];
    private static readonly string[] PaymentMethods = [PaymentMethod.BankTransfer, PaymentMethod.Cod, PaymentMethod.Cash];
    private static readonly string[] PaymentProviders = [PaymentProvider.Manual, PaymentProvider.Sepay];
    private static readonly string[] PaymentRecordStatuses = [PaymentRecordStatus.AwaitingSettlement, PaymentRecordStatus.Confirmed, PaymentRecordStatus.Voided];
    private static readonly string[] RefundKinds = [RefundKind.Overpayment, RefundKind.Return, RefundKind.Cancellation, RefundKind.Adjustment];
    private static readonly string[] RefundMethods = [RefundMethod.BankTransfer, RefundMethod.Cash];
    private static readonly string[] ReturnReasons = [ReturnReason.DamagedInTransit, ReturnReason.Defective, ReturnReason.WrongItem];
    private static readonly string[] ReturnStatuses = [ReturnRequestStatus.Requested, ReturnRequestStatus.Approved, ReturnRequestStatus.Rejected, ReturnRequestStatus.Resolved];
    private static readonly string[] ReturnResolutions = [ReturnResolution.Refund, ReturnResolution.Replacement];

    public static Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        var schemas = document.Components!.Schemas!;

        foreach (var requestName in new[]
        {
            nameof(CreateAdminOrderRequest),
            nameof(CreateAdminOrderNewCustomerRequest),
            nameof(CreateAdminOrderLineRequest),
            nameof(CreateAdminOrderShippingRequest),
            nameof(PatchAdminOrderRequest),
            nameof(PatchAdminOrderShippingRequest),
            nameof(ConfirmAdminOrderRequest),
            nameof(CancelAdminOrderRequest),
            nameof(ReorderAdminOrderRequest)
        })
        {
            if (schemas.TryGetValue(requestName, out var raw) && raw is OpenApiSchema schema)
                schema.AdditionalPropertiesAllowed = false;
        }

        // Request schemas
        ConfigureRequired(schemas, nameof(CreateAdminOrderNewCustomerRequest), ["fullName", "phone"], ["fullName", "phone"]);
        ConfigureRequired(schemas, nameof(CreateAdminOrderLineRequest), ["variantId", "quantity"], ["variantId", "quantity"]);
        SetStrictMinimum(schemas, nameof(CreateAdminOrderLineRequest), "quantity", "1");
        SetStrictMinimum(schemas, nameof(CreateAdminOrderLineRequest), "unitPriceVnd", "0");

        ConfigureRequired(
            schemas,
            nameof(CreateAdminOrderShippingRequest),
            ["receiverName", "receiverPhone", "provinceCode", "addressLine"],
            ["receiverName", "receiverPhone", "provinceCode", "addressLine"]);

        ConfigureRequired(
            schemas,
            nameof(CreateAdminOrderRequest),
            ["salesChannel", "paymentMethod", "lines", "shipping"],
            ["salesChannel", "paymentMethod", "lines", "shipping", "confirmNow", "callConfirmed", "isBackfilled"]);
        ClearDefault(schemas, nameof(CreateAdminOrderRequest), "confirmNow", "callConfirmed", "isBackfilled");
        SetEnum(schemas, nameof(CreateAdminOrderRequest), "salesChannel", AdminSalesChannels, nullable: false);
        SetEnum(schemas, nameof(CreateAdminOrderRequest), "paymentMethod", OrderPaymentMethods, nullable: false);
        SetStrictMinimum(schemas, nameof(CreateAdminOrderRequest), "discountVnd", "0");
        SetStrictMinimum(schemas, nameof(CreateAdminOrderRequest), "shippingFeeVnd", "0");
        NormalizeInlineOrderLines(schemas, nameof(CreateAdminOrderRequest));
        DisallowInlineObjectAdditionalProperties(schemas, nameof(CreateAdminOrderRequest), "newCustomer", "shipping");

        ConfigureRequired(
            schemas,
            nameof(PatchAdminOrderRequest),
            ["rowVersion"],
            ["rowVersion", "lines", "discountVnd", "shipping"]);
        SetStrictMinimum(schemas, nameof(PatchAdminOrderRequest), "discountVnd", "0");
        SetStrictMinimum(schemas, nameof(PatchAdminOrderRequest), "shippingFeeVnd", "0");
        NormalizeInlineOrderLines(schemas, nameof(PatchAdminOrderRequest));
        DisallowInlineObjectAdditionalProperties(schemas, nameof(PatchAdminOrderRequest), "shipping");
        StripInlineObjectNullability(
            schemas,
            nameof(PatchAdminOrderRequest),
            "shipping",
            "receiverName", "receiverPhone", "provinceCode", "addressLine");
        ConfigureRequired(
            schemas,
            nameof(PatchAdminOrderShippingRequest),
            [],
            ["receiverName", "receiverPhone", "provinceCode", "addressLine"]);

        ConfigureRequired(schemas, nameof(ConfirmAdminOrderRequest), ["rowVersion"], ["rowVersion", "callConfirmed"]);
        ClearDefault(schemas, nameof(ConfirmAdminOrderRequest), "callConfirmed");
        ConfigureRequired(schemas, nameof(CancelAdminOrderRequest), ["rowVersion", "reason"], ["rowVersion", "reason"]);

        ConfigureRequired(schemas, nameof(ReorderAdminOrderRequest), [], []);
        SetEnum(schemas, nameof(ReorderAdminOrderRequest), "salesChannel", AdminSalesChannels, nullable: true);
        SetEnum(schemas, nameof(ReorderAdminOrderRequest), "paymentMethod", OrderPaymentMethods, nullable: true);

        // Response schemas
        SetEnum(schemas, nameof(AdminOrderListItemView), "origin", OrderOrigins, nullable: false);
        SetEnum(schemas, nameof(AdminOrderListItemView), "salesChannel", AllSalesChannels, nullable: false);
        SetEnum(schemas, nameof(AdminOrderListItemView), "status", OrderStatuses, nullable: false);
        SetEnum(schemas, nameof(AdminOrderListItemView), "paymentMethod", OrderPaymentMethods, nullable: false);
        SetEnum(schemas, nameof(AdminOrderListItemView), "paymentStatus", OrderPaymentStatuses, nullable: false);
        StripStringNumericType(schemas, nameof(AdminOrderListItemView),
            "subtotalVnd", "discountVnd", "shippingFeeVnd", "totalVnd", "paidVnd", "refundedVnd", "netPaidVnd", "itemCount");

        SetEnum(schemas, nameof(AdminOrderCreatedView), "origin", OrderOrigins, nullable: false);
        SetEnum(schemas, nameof(AdminOrderCreatedView), "salesChannel", AdminSalesChannels, nullable: false);
        SetEnum(schemas, nameof(AdminOrderCreatedView), "status", OrderStatuses, nullable: false);
        SetEnum(schemas, nameof(AdminOrderCreatedView), "paymentMethod", OrderPaymentMethods, nullable: false);
        SetEnum(schemas, nameof(AdminOrderCreatedView), "paymentStatus", OrderPaymentStatuses, nullable: false);
        StripStringNumericType(schemas, nameof(AdminOrderCreatedView),
            "subtotalVnd", "discountVnd", "shippingFeeVnd", "totalVnd");

        SetEnum(schemas, nameof(AdminOrderDetailView), "origin", OrderOrigins, nullable: false);
        SetEnum(schemas, nameof(AdminOrderDetailView), "salesChannel", AllSalesChannels, nullable: false);
        SetEnum(schemas, nameof(AdminOrderDetailView), "status", OrderStatuses, nullable: false);
        SetEnum(schemas, nameof(AdminOrderDetailView), "paymentMethod", OrderPaymentMethods, nullable: false);
        SetEnum(schemas, nameof(AdminOrderDetailView), "paymentStatus", OrderPaymentStatuses, nullable: false);
        StripStringNumericType(schemas, nameof(AdminOrderDetailView),
            "subtotalVnd", "discountVnd", "shippingFeeVnd", "totalVnd",
            "paidVnd", "refundedVnd", "overpaymentRefundedVnd", "effectivePaidVnd",
            "netPaidVnd", "remainingToPayVnd", "overpaidVnd");

        SetEnum(schemas, nameof(AdminOrderLineView), "priceSource", PriceSources, nullable: false);
        StripStringNumericType(schemas, nameof(AdminOrderLineView),
            "quantity", "listPriceVnd", "unitPriceVnd", "lineTotalVnd", "unitCostVnd", "returnedQuantity");
        MakeOptional(schemas, nameof(AdminOrderLineView), "unitCostVnd");
        StripStringNumericType(schemas, "CatalogPageOfAdminOrderListItemView", "page", "pageSize", "totalItems");

        SetEnum(schemas, nameof(AdminOrderPaymentView), "method", PaymentMethods, nullable: false);
        SetEnum(schemas, nameof(AdminOrderPaymentView), "provider", PaymentProviders, nullable: false);
        SetEnum(schemas, nameof(AdminOrderPaymentView), "status", PaymentRecordStatuses, nullable: false);
        StripStringNumericType(schemas, nameof(AdminOrderPaymentView), "amountVnd");

        SetEnum(schemas, nameof(AdminOrderRefundView), "kind", RefundKinds, nullable: false);
        SetEnum(schemas, nameof(AdminOrderRefundView), "method", RefundMethods, nullable: false);
        StripStringNumericType(schemas, nameof(AdminOrderRefundView), "amountVnd", "shippingAmountVnd");

        SetEnum(schemas, nameof(AdminOrderReturnView), "reason", ReturnReasons, nullable: false);
        SetEnum(schemas, nameof(AdminOrderReturnView), "status", ReturnStatuses, nullable: false);
        SetEnum(schemas, nameof(AdminOrderReturnView), "resolution", ReturnResolutions, nullable: true);
        StripStringNumericType(schemas, nameof(AdminOrderReturnLineView), "quantity");

        SetEnum(schemas, nameof(AdminOrderHistoryView), "fromStatus", OrderStatuses, nullable: true);
        SetEnum(schemas, nameof(AdminOrderHistoryView), "toStatus", OrderStatuses, nullable: false);
        SetEnum(schemas, nameof(AdminOrderHistoryView), "actorType", ActorTypes, nullable: false);

        // GAP-P214-01: clean strict integer type on AdministrativeProvinceItem.sortOrder
        StripStringNumericType(schemas, nameof(AdministrativeProvinceItem), "sortOrder");

        // Query parameter enums on GET /api/v1/admin/orders and remove phantom 200 on POST /api/v1/admin/orders (201 is sole 2xx)
        if (document.Paths.TryGetValue("/api/v1/admin/orders", out var listPath)
            && listPath.Operations is not null)
        {
            if (listPath.Operations.TryGetValue(HttpMethod.Get, out var listOp)
                && listOp.Parameters is not null)
            {
                SetQueryParamEnum(listOp.Parameters, "sort", AdminOrderSortOptions.All);
                SetQueryParamEnum(listOp.Parameters, "status", OrderStatuses);
                SetQueryParamEnum(listOp.Parameters, "paymentStatus", OrderPaymentStatuses);
                SetQueryParamEnum(listOp.Parameters, "channel", AllSalesChannels);
                SetQueryParamEnum(listOp.Parameters, "origin", OrderOrigins);
                SetQueryParamEnum(listOp.Parameters, "flag", AdminOrderFlags.All);
            }

            if (listPath.Operations.TryGetValue(HttpMethod.Post, out var createOp)
                && createOp.Responses is not null)
            {
                createOp.Responses.Remove("200");
            }
        }

        // Packing slip hidePrices boolean query parameter & text/html response
        if (document.Paths.TryGetValue("/api/v1/admin/orders/{id}/packing-slip", out var slipPath)
            && slipPath.Operations is not null
            && slipPath.Operations.TryGetValue(HttpMethod.Get, out var slipOp))
        {
            if (slipOp.Parameters is not null)
            {
                var hideParam = slipOp.Parameters
                    .FirstOrDefault(p => string.Equals(p.Name, "hidePrices", StringComparison.Ordinal)) as OpenApiParameter;
                if (hideParam is not null)
                {
                    hideParam.Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean };
                }
            }

            if (slipOp.Responses is not null
                && slipOp.Responses.TryGetValue("200", out var okResp)
                && okResp is OpenApiResponse concreteResp
                && concreteResp.Content is not null
                && concreteResp.Content.TryGetValue("text/html", out var htmlMedia))
            {
                htmlMedia.Schema = new OpenApiSchema { Type = JsonSchemaType.String };
            }
        }

        return Task.CompletedTask;
    }

    private static void SetQueryParamEnum(IList<IOpenApiParameter> parameters, string name, string[] values)
    {
        if (parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal)) is OpenApiParameter param)
        {
            param.Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = values.Select(v => (JsonNode)JsonValue.Create(v)!).ToList()
            };
        }
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
        string minimum)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema
            || schema.Properties is null || !schema.Properties.TryGetValue(field, out var prop))
            return;

        var copy = (OpenApiSchema)prop.CreateShallowCopy();
        copy.Type &= ~JsonSchemaType.String;
        copy.Pattern = null;
        copy.Minimum = minimum;
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

    private static void ClearDefault(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        params string[] fields)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema || schema.Properties is null)
            return;

        foreach (var field in fields)
        {
            if (!schema.Properties.TryGetValue(field, out var prop)) continue;
            var copy = (OpenApiSchema)prop.CreateShallowCopy();
            copy.Default = null;
            schema.Properties[field] = copy;
        }
    }

    private static void MakeOptional(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        params string[] fields)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema || schema.Required is null)
            return;

        foreach (var field in fields)
            schema.Required.Remove(field);
    }

    private static void NormalizeInlineOrderLines(IDictionary<string, IOpenApiSchema> schemas, string schemaName)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema
            || schema.Properties is null || !schema.Properties.TryGetValue("lines", out var linesRaw)
            || linesRaw is not OpenApiSchema linesSchema || linesSchema.Items is not OpenApiSchema itemSchema
            || itemSchema.Properties is null)
            return;

        itemSchema.AdditionalPropertiesAllowed = false;
        itemSchema.Required = new HashSet<string>(["variantId", "quantity"], StringComparer.Ordinal);
        foreach (var (field, min) in new[] { ("quantity", "1"), ("unitPriceVnd", "0") })
        {
            if (!itemSchema.Properties.TryGetValue(field, out var prop)) continue;
            var copy = (OpenApiSchema)prop.CreateShallowCopy();
            copy.Type &= ~JsonSchemaType.String;
            copy.Pattern = null;
            copy.Minimum = min;
            itemSchema.Properties[field] = copy;
        }
    }

    private static void DisallowInlineObjectAdditionalProperties(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        params string[] fields)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema || schema.Properties is null)
            return;

        foreach (var field in fields)
        {
            if (schema.Properties.TryGetValue(field, out var prop) && prop is OpenApiSchema objSchema)
                objSchema.AdditionalPropertiesAllowed = false;
        }
    }

    private static void StripInlineObjectNullability(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName,
        string objectField,
        params string[] nonNullableFields)
    {
        if (!schemas.TryGetValue(schemaName, out var raw) || raw is not OpenApiSchema schema
            || schema.Properties is null || !schema.Properties.TryGetValue(objectField, out var prop)
            || prop is not OpenApiSchema objSchema || objSchema.Properties is null)
            return;

        foreach (var field in nonNullableFields)
        {
            if (!objSchema.Properties.TryGetValue(field, out var nested)) continue;
            var copy = (OpenApiSchema)nested.CreateShallowCopy();
            copy.Type &= ~JsonSchemaType.Null;
            objSchema.Properties[field] = copy;
        }
    }
}
