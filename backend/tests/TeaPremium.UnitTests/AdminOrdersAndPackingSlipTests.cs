using System.Net;
using System.Text.Json;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders;
using TeaPremium.Domain;
using TeaPremium.Domain.Orders;

namespace TeaPremium.UnitTests;

public sealed class AdminOrdersAndPackingSlipTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 4, 0, 0, TimeSpan.Zero);
    private static readonly OrderContactSnapshot SampleContact = new(
        "Nguyễn Văn An", "+84901234567", "an@example.invalid",
        "Trần Thị Bình", "+84909876543", "79", "TP Hồ Chí Minh", "79001", "Phường 1", "10 Lê Lợi");
    private static readonly OrderAttributionSnapshot SampleAttribution = new(OrderAcquisitionSource.Direct, true);

    private static OrderLineItem SampleLine(long unitPrice = 500_000L, int qty = 2, long? unitCost = null) =>
        OrderLineItem.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "TRA-001",
            "Ấm Tử Sa Chu Nê",
            "200ml",
            qty,
            unitPrice,
            unitPrice,
            OrderPriceSource.List,
            null,
            unitCost);

    [Fact]
    public void CreateBackfilledCreatesDeliveredOrderWithUnpaidStatusBackfillHistoryAndOnlyOrderDeliveredEvent()
    {
        var deliveredAt = Now.AddDays(-30);
        var staffId = Guid.CreateVersion7();
        var line = SampleLine(unitPrice: 500_000L, qty: 2);

        var order = Order.CreateBackfilled(
            Guid.CreateVersion7(),
            "TP261127K7Q2",
            SalesChannel.InPerson,
            Guid.CreateVersion7(),
            staffId,
            OrderPaymentMethod.Cod,
            SampleContact,
            [line],
            1_000_000L,
            100_000L,
            0L,
            900_000L,
            "Khách quen trước đây",
            SampleAttribution,
            "Ghi chú cũ",
            "Nhập lịch sử",
            deliveredAt,
            Now);

        Assert.Equal(OrderOrigin.Admin, order.Origin);
        Assert.Equal(SalesChannel.InPerson, order.SalesChannel);
        Assert.Equal(OrderStatus.Delivered, order.Status);
        Assert.Equal(OrderPaymentStatus.Unpaid, order.PaymentStatus);
        Assert.True(order.IsBackfilled);
        Assert.Equal(deliveredAt, order.PlacedAt);
        Assert.Equal(deliveredAt, order.DeliveredAt);
        Assert.Null(order.ConfirmedAt);
        Assert.Null(order.ShippedAt);
        Assert.Null(order.PaymentDueAt);
        Assert.Equal(Now, order.CreatedAt);
        Assert.Equal(staffId, order.CreatedByStaffId);

        var history = order.DrainNewHistory();
        var entry = Assert.Single(history);
        Assert.Null(entry.FromStatus);
        Assert.Equal(OrderStatus.Delivered, entry.ToStatus);
        Assert.Equal(OrderActorType.Staff, entry.ActorType);
        Assert.Equal(staffId, entry.ActorId);
        Assert.Equal("backfill", entry.Reason);

        var events = order.DrainEvents();
        var evt = Assert.Single(events);
        Assert.Equal(OrderEventTypes.OrderDelivered, evt.EventType);
        Assert.Equal(OrderStatus.Delivered, evt.Status);
        Assert.Equal(OrderDeliveredReasonCodes.Backfilled, evt.ReasonCode);
    }

    [Fact]
    public void CreateBackfilledRejectsFutureDeliveredAtWebChannelEmptyActorAndNonNullUnitCost()
    {
        var line = SampleLine();

        var futureEx = Assert.Throws<DomainException>(() => Order.CreateBackfilled(
            Guid.CreateVersion7(), "TP261127K7Q2", SalesChannel.Phone, Guid.CreateVersion7(), Guid.CreateVersion7(),
            OrderPaymentMethod.Cod, SampleContact, [line], 1_000_000L, 0L, 0L, 1_000_000L, null, SampleAttribution, null, null,
            Now.AddMinutes(1), Now));
        Assert.Equal("invalid_delivered_at", futureEx.Code);

        var webEx = Assert.Throws<DomainException>(() => Order.CreateBackfilled(
            Guid.CreateVersion7(), "TP261127K7Q2", SalesChannel.Web, Guid.CreateVersion7(), Guid.CreateVersion7(),
            OrderPaymentMethod.Cod, SampleContact, [line], 1_000_000L, 0L, 0L, 1_000_000L, null, SampleAttribution, null, null,
            Now.AddDays(-1), Now));
        Assert.Equal("invalid_sales_channel", webEx.Code);

        var actorEx = Assert.Throws<DomainException>(() => Order.CreateBackfilled(
            Guid.CreateVersion7(), "TP261127K7Q2", SalesChannel.Phone, Guid.CreateVersion7(), Guid.Empty,
            OrderPaymentMethod.Cod, SampleContact, [line], 1_000_000L, 0L, 0L, 1_000_000L, null, SampleAttribution, null, null,
            Now.AddDays(-1), Now));
        Assert.Equal("invalid_actor", actorEx.Code);

        var costLine = SampleLine(unitCost: 200_000L);
        var costEx = Assert.Throws<DomainException>(() => Order.CreateBackfilled(
            Guid.CreateVersion7(), "TP261127K7Q2", SalesChannel.Phone, Guid.CreateVersion7(), Guid.CreateVersion7(),
            OrderPaymentMethod.Cod, SampleContact, [costLine], 1_000_000L, 0L, 0L, 1_000_000L, null, SampleAttribution, null, null,
            Now.AddDays(-1), Now));
        Assert.Equal("validation_failed", costEx.Code);
    }

    [Fact]
    public void OrderOutboxMessageAcceptsBackfilledReasonCodeAndRejectsFreeText()
    {
        var validPayload = new OrderOutboxPayload(
            Guid.CreateVersion7(), "TP261127K7Q2", OrderStatus.Delivered, Now, OrderDeliveredReasonCodes.Backfilled);
        var json = OrderOutboxMessage.Serialize(validPayload);
        Assert.True(OrderOutboxMessage.TryParse(json, out var parsed));
        Assert.Equal("backfilled", parsed.ReasonCode);

        var invalidPayload = new OrderOutboxPayload(
            Guid.CreateVersion7(), "TP261127K7Q2", OrderStatus.Delivered, Now, "Khách gọi hủy SĐT 0901234567");
        Assert.False(OrderOutboxMessage.TryParse(OrderOutboxMessage.Serialize(invalidPayload), out _));
    }

    [Fact]
    public void UpdateAdminDetailsAndAdjustmentsMutatesShippingNotesTotalsAndRejectsTerminalOrBackfilledOrders()
    {
        var order = Order.CreateAdmin(
            Guid.CreateVersion7(), "TP261127K7Q2", SalesChannel.ZaloPersonal, Guid.CreateVersion7(), Guid.CreateVersion7(),
            OrderPaymentMethod.Cod, SampleContact, [SampleLine(500_000L, 2)], 1_000_000L, 0L, 30_000L, 1_030_000L,
            null, null, SampleAttribution, "Ghi chú cũ", "Nội bộ cũ", Now);

        order.UpdateAdminDetails(
            "Người Nhận Mới", "+84908888888", "01", "Hà Nội", "00001", "Phường Phúc Xá", "25 Phan Đình Phùng",
            "Giao sáng", "Khách VIP", Now.AddMinutes(5));
        Assert.Equal("Nguyễn Văn An", order.Contact.ContactName); // Ordering contact preserved
        Assert.Equal("Người Nhận Mới", order.Contact.ReceiverName);
        Assert.Equal("+84908888888", order.Contact.ReceiverPhone);
        Assert.Equal("01", order.Contact.ProvinceCode);
        Assert.Equal("Giao sáng", order.CustomerNote);
        Assert.Equal("Khách VIP", order.InternalNote);

        order.UpdateAdminAdjustments(100_000L, 20_000L, null, "Giảm 10%", Now.AddMinutes(10));
        Assert.Equal(100_000L, order.DiscountVnd);
        Assert.Equal("Giảm 10%", order.DiscountNote);
        Assert.Equal(20_000L, order.ShippingFeeVnd);
        Assert.Equal(920_000L, order.TotalVnd);

        // Clearing discount clears discountNote
        order.UpdateAdminAdjustments(0L, 30_000L, null, "Ignored", Now.AddMinutes(15));
        Assert.Equal(0L, order.DiscountVnd);
        Assert.Null(order.DiscountNote);
        Assert.Equal(1_030_000L, order.TotalVnd);

        // Terminal order rejects mutations
        order.Cancel(OrderActorType.Staff, Guid.CreateVersion7(), "Khách hủy", Now.AddMinutes(20));
        var cancelledEx = Assert.Throws<DomainException>(() => order.UpdateAdminDetails(
            "A", "+84901234567", "01", "Hà Nội", null, "Phường 1", "1 Phố", null, null, Now.AddMinutes(25)));
        Assert.Equal("invalid_status_transition", cancelledEx.Code);

        var backfilled = Order.CreateBackfilled(
            Guid.CreateVersion7(), "TP261127K7Q3", SalesChannel.InPerson, Guid.CreateVersion7(), Guid.CreateVersion7(),
            OrderPaymentMethod.Cod, SampleContact, [SampleLine()], 1_000_000L, 0L, 0L, 1_000_000L, null, SampleAttribution, null, null,
            Now.AddDays(-5), Now);
        var backfillEx = Assert.Throws<DomainException>(() => backfilled.UpdateAdminAdjustments(0L, 0L, null, null, Now));
        Assert.Equal("invalid_status_transition", backfillEx.Code);
    }

    [Fact]
    public void PackingSlipHtmlRendererCode128TableSatisfiesIso15417Invariants()
    {
        var table = PackingSlipHtmlRenderer.Code128Patterns;
        Assert.Equal(107, table.Count);
        Assert.Equal(107, table.Distinct(StringComparer.Ordinal).Count());

        for (var i = 0; i < 106; i++)
        {
            var p = table[i];
            Assert.Equal(6, p.Length);
            var barSum = (p[0] - '0') + (p[2] - '0') + (p[4] - '0');
            var spaceSum = (p[1] - '0') + (p[3] - '0') + (p[5] - '0');
            Assert.Equal(11, barSum + spaceSum);
            Assert.Equal(0, barSum % 2);
            Assert.Equal(1, spaceSum % 2);
        }

        var stop = table[106];
        Assert.Equal(7, stop.Length);
        var stopBars = (stop[0] - '0') + (stop[2] - '0') + (stop[4] - '0') + (stop[6] - '0');
        var stopSpaces = (stop[1] - '0') + (stop[3] - '0') + (stop[5] - '0');
        Assert.Equal(13, stopBars + stopSpaces);
        Assert.Equal(0, stopBars % 2);
    }

    [Fact]
    public void PackingSlipHtmlRendererRendersCode128SvgLineItemsAndCodAmount()
    {
        var doc = new PackingSlipDocument(
            "Trà Hiên",
            "0901234567",
            "10 Lê Lợi, Q1",
            "TP261127K7Q2",
            Now,
            "Trần Thị Bình",
            "+84909876543",
            "25 Phan Đình Phùng",
            "Phường 1",
            "TP Hồ Chí Minh",
            "Gọi trước khi giao",
            1_000_000L,
            100_000L,
            30_000L,
            930_000L,
            930_000L,
            [new PackingSlipLine("TRA-001", "Ấm Tử Sa", "200ml", 2, 500_000L, 1_000_000L)]);

        var html = PackingSlipHtmlRenderer.Render(doc, hidePrices: false);

        Assert.Contains("<svg xmlns=\"http://www.w3.org/2000/svg\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"TP261127K7Q2\"", html, StringComparison.Ordinal);
        Assert.Contains("TP261127K7Q2", html, StringComparison.Ordinal);
        Assert.Contains(WebUtility.HtmlEncode("Trần Thị Bình"), html, StringComparison.Ordinal);
        Assert.Contains("Trần Thị Bình", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.Contains("500.000 ₫", html, StringComparison.Ordinal);
        Assert.Contains("1.000.000 ₫", html, StringComparison.Ordinal);
        Assert.Contains("930.000 ₫", html, StringComparison.Ordinal);
        Assert.Contains("Tiền thu hộ (COD)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PackingSlipHtmlRendererHidePricesRemovesLineAndSummaryPricesButKeepsCod()
    {
        var doc = new PackingSlipDocument(
            "Trà Hiên",
            null,
            null,
            "TP261127K7Q2",
            Now,
            "Người Nhận Quà",
            "+84909876543",
            "25 Phan Đình Phùng",
            "Phường 1",
            "TP Hồ Chí Minh",
            null,
            1_234_000L,
            111_000L,
            35_000L,
            1_158_000L,
            500_000L,
            [new PackingSlipLine("TRA-002", "Trà Ô Long", "100g", 2, 617_000L, 1_234_000L)]);

        var html = PackingSlipHtmlRenderer.Render(doc, hidePrices: true);

        Assert.DoesNotContain("Đơn giá", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Thành tiền", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Tạm tính", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Phí giao hàng", html, StringComparison.Ordinal);
        Assert.DoesNotContain("617.000", html, StringComparison.Ordinal);
        Assert.DoesNotContain("1.234.000", html, StringComparison.Ordinal);
        Assert.DoesNotContain("111.000", html, StringComparison.Ordinal);
        Assert.DoesNotContain("35.000", html, StringComparison.Ordinal);
        Assert.DoesNotContain("1.158.000", html, StringComparison.Ordinal);
        // COD amount remains visible for courier
        Assert.Contains("Tiền thu hộ (COD)", html, StringComparison.Ordinal);
        Assert.Contains("500.000 ₫", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PackingSlipHtmlRendererEscapesAllUntrustedFieldsAgainstXss()
    {
        const string xss = "<script>alert(1)</script>\"'&<img src=x onerror=alert(1)>";
        var doc = new PackingSlipDocument(
            xss, xss, xss, "TP261127K7Q2", Now, xss, xss, xss, xss, xss, xss,
            500_000L, 0L, 0L, 500_000L, 500_000L,
            [new PackingSlipLine(xss, xss, xss, 1, 500_000L, 500_000L)]);

        var html = PackingSlipHtmlRenderer.Render(doc, hidePrices: false);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&quot;", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("INVALID")]
    [InlineData("TP261127<script>")]
    [InlineData("TP261127K7Q2;DROP")]
    public void PackingSlipHtmlRendererRejectsInvalidOrderCodeForBarcode(string badCode)
    {
        var doc = new PackingSlipDocument(
            "Trà Hiên", null, null, badCode, Now, "A", "+84901234567", "1 Đường", "Phường 1", "HCM", null,
            100_000L, 0L, 0L, 100_000L, 0L, []);

        var ex = Assert.Throws<DomainException>(() => PackingSlipHtmlRenderer.Render(doc, hidePrices: false));
        Assert.Equal("invalid_order_code", ex.Code);
    }

    [Fact]
    public void SafeCreateOrderResponseAllowlistsExact18FieldsAndRejectsExtraPiiOrInvalidEnums()
    {
        var valid = new AdminOrderCreatedView(
            Guid.CreateVersion7(),
            "TP261127K7Q2",
            Guid.CreateVersion7(),
            "KH001000",
            OrderOrigin.Admin,
            SalesChannel.ZaloPersonal,
            OrderStatus.Pending,
            OrderPaymentMethod.Cod,
            OrderPaymentStatus.Unpaid,
            500_000L,
            0L,
            30_000L,
            530_000L,
            false,
            Now,
            null,
            null,
            CatalogVersions.Encode(42u));

        var webOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using (var validDoc = JsonDocument.Parse(JsonSerializer.Serialize(valid, webOptions)))
            Assert.True(AdminOrderIdempotencyPolicy.SafeCreateOrderResponse(validDoc.RootElement));

        // Extra PII field rejected
        var withPii = JsonSerializer.Serialize(new
        {
            valid.Id,
            valid.Code,
            valid.CustomerId,
            valid.CustomerCode,
            valid.Origin,
            valid.SalesChannel,
            valid.Status,
            valid.PaymentMethod,
            valid.PaymentStatus,
            valid.SubtotalVnd,
            valid.DiscountVnd,
            valid.ShippingFeeVnd,
            valid.TotalVnd,
            valid.IsBackfilled,
            valid.PlacedAt,
            valid.ConfirmedAt,
            valid.DeliveredAt,
            valid.RowVersion,
            receiverPhone = "+84901234567"
        }, webOptions);
        using (var piiDoc = JsonDocument.Parse(withPii))
            Assert.False(AdminOrderIdempotencyPolicy.SafeCreateOrderResponse(piiDoc.RootElement));

        // Invalid salesChannel ('web' forbidden for admin create) rejected
        var webChannel = valid with { SalesChannel = SalesChannel.Web };
        using (var webDoc = JsonDocument.Parse(JsonSerializer.Serialize(webChannel, webOptions)))
            Assert.False(AdminOrderIdempotencyPolicy.SafeCreateOrderResponse(webDoc.RootElement));

        // Non-canonical rowVersion rejected
        var badVersion = valid with { RowVersion = "not-valid-version" };
        using (var badVersionDoc = JsonDocument.Parse(JsonSerializer.Serialize(badVersion, webOptions)))
            Assert.False(AdminOrderIdempotencyPolicy.SafeCreateOrderResponse(badVersionDoc.RootElement));

        // Invalid customer code rejected
        var badCustomerCode = valid with { CustomerCode = "Nguyễn Văn A" };
        using (var badCodeDoc = JsonDocument.Parse(JsonSerializer.Serialize(badCustomerCode, webOptions)))
            Assert.False(AdminOrderIdempotencyPolicy.SafeCreateOrderResponse(badCodeDoc.RootElement));
    }

    [Theory]
    [InlineData("79", true, "79")]
    [InlineData(" 01 ", true, "01")]
    [InlineData("VN-HN_01", true, "VN-HN_01")]
    [InlineData("1234567890123456", true, "1234567890123456")]
    [InlineData("", false, "")]
    [InlineData("   ", false, "")]
    [InlineData("12345678901234567", false, "")]
    [InlineData("79 01", false, "")]
    [InlineData("79;DROP", false, "")]
    [InlineData("79\0", false, "")]
    [InlineData("79\n", false, "")]
    [InlineData("HàNội", false, "")]
    public void AdministrativeProvinceCodeValidatesExactAllowlistAndRejectsControlOrLongInputs(
        string input, bool expectedValid, string expectedNormalized)
    {
        var ok = AdministrativeProvinceCode.TryNormalize(input, out var normalized);
        Assert.Equal(expectedValid, ok);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Fact]
    public void AdminOrderFilterParserParsesVietnamDatesHalfOpenAndExplicitIsoOffsets()
    {
        Assert.True(AdminOrderFilterParser.TryParseFromFilter("2026-11-27", out var fromUtc));
        Assert.Equal(new DateTime(2026, 11, 26, 17, 0, 0, DateTimeKind.Utc), fromUtc);

        Assert.True(AdminOrderFilterParser.TryParseToFilter("2026-11-27", out var toUtc, out var toExclusive));
        Assert.True(toExclusive);
        Assert.Equal(new DateTime(2026, 11, 27, 17, 0, 0, DateTimeKind.Utc), toUtc);

        Assert.True(AdminOrderFilterParser.TryParseToFilter("2026-11-27T10:00:00Z", out var isoToUtc, out var isoExclusive));
        Assert.False(isoExclusive);
        Assert.Equal(new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc), isoToUtc);

        // Bare ISO without offset rejected
        Assert.False(AdminOrderFilterParser.TryParseFromFilter("2026-11-27T10:00:00", out _));
        Assert.False(AdminOrderFilterParser.TryParseToFilter("2026-11-27T10:00:00", out _, out _));
    }
}
