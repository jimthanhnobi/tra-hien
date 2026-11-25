using System.Text.Json;
using TeaPremium.Application.Notifications;
using TeaPremium.Application.Settings;
using TeaPremium.Application.Time;

namespace TeaPremium.UnitTests;

public sealed class NotificationTemplateAndSecurityTests
{
    [Fact]
    public void BrPlt03OrderPlacedShopTemplateRendersVietnameseFieldsWithoutPiiOrCosts()
    {
        var orderId = Guid.Parse("01933000-0000-7000-8000-000000000042");
        var order = new OrderPlacedNotification(orderId, "TP261123AB12", 1_250_000L, "bank_transfer", 3);

        var rendered = NotificationTemplates.RenderOrderPlacedShop(order, "https://admin.example.test");

        Assert.Contains("TP261123AB12", rendered.TelegramHtml, StringComparison.Ordinal);
        Assert.Contains("1.250.000 đ", rendered.TelegramHtml, StringComparison.Ordinal);
        Assert.Contains("Chuyển khoản", rendered.TelegramHtml, StringComparison.Ordinal);
        Assert.Contains("3", rendered.TelegramHtml, StringComparison.Ordinal);
        Assert.Contains($"https://admin.example.test/orders/{orderId:D}", rendered.TelegramHtml, StringComparison.Ordinal);

        Assert.Contains("TP261123AB12", rendered.EmailSubject, StringComparison.Ordinal);
        Assert.Contains("1.250.000 đ", rendered.EmailPlainText, StringComparison.Ordinal);
        Assert.Contains("1.250.000 đ", rendered.EmailHtml, StringComparison.Ordinal);
        Assert.Contains($"https://admin.example.test/orders/{orderId:D}", rendered.EmailHtml, StringComparison.Ordinal);

        // Static model check: OrderPlacedNotification has no PII or cost properties.
        var propNames = typeof(OrderPlacedNotification).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(propNames, p => p.Contains("Phone", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propNames, p => p.Contains("Address", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propNames, p => p.Contains("Email", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propNames, p => p.Contains("Name", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propNames, p => p.Contains("Note", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propNames, p => p.Contains("Cost", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propNames, p => p.Contains("Profit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BrPlt03DailyDigestShopTemplateRendersAllSectionsAndCapsLowStockSkuListWithoutArtisanOrProductNames()
    {
        var lowItems = Enumerable.Range(1, 12)
            .Select(i => new DailyDigestLowStockItem($"AM-TD-{i:0000}", i % 3, 5))
            .ToArray();

        var snapshot = new DailyDigestSnapshot(
            VietnamDate: new DateOnly(2026, 11, 23),
            PendingConfirmationOrders: 4,
            ExpiringPaymentOrders: 2,
            UnsettledCodCount: 3,
            UnsettledCodTotalVnd: 2_400_000L,
            FollowUpsDueToday: 5,
            LowStockTotal: 15,
            LowStockItems: lowItems,
            ReconciliationMismatchTotal: 1,
            DeadOutboxCount: 2);

        var html = NotificationTemplates.RenderDailyDigestShop(snapshot, "https://admin.example.test");

        Assert.True(html.Length < NotificationContracts.TelegramMaxMessageLength);
        Assert.Contains("23/11/2026", html, StringComparison.Ordinal);
        Assert.Contains("Đơn chờ xác nhận: <b>4</b>", html, StringComparison.Ordinal);
        Assert.Contains("Chờ thanh toán sắp hết hạn (24h): <b>2</b>", html, StringComparison.Ordinal);
        Assert.Contains("COD chưa đối soát: <b>3</b> (2.400.000 đ)", html, StringComparison.Ordinal);
        Assert.Contains("Hẹn chăm sóc hôm nay: <b>5</b>", html, StringComparison.Ordinal);
        Assert.Contains("Tồn kho thấp: <b>15 SKU</b>", html, StringComparison.Ordinal);
        Assert.Contains("AM-TD-0001", html, StringComparison.Ordinal);
        Assert.Contains("AM-TD-0010", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AM-TD-0011", html, StringComparison.Ordinal);
        Assert.Contains("... và 5 SKU khác", html, StringComparison.Ordinal);
        Assert.Contains("Lệch đối soát sổ kho: <b>1 SKU</b>", html, StringComparison.Ordinal);
        Assert.Contains("Outbox dead: <b>2</b>", html, StringComparison.Ordinal);

        // Static model check: digest models expose no PII, artisan/product names, or cost fields.
        var forbidden = new[] { "Phone", "Address", "Email", "Artisan", "ProductName", "Customer", "Cost", "Profit" };
        foreach (var type in new[] { typeof(DailyDigestSnapshot), typeof(DailyDigestLowStockItem) })
        {
            foreach (var prop in type.GetProperties())
                Assert.DoesNotContain(forbidden, word => prop.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TelegramHtmlEscapeNeutralizesTagsQuotesAmpersandsAndControlCharacters()
    {
        var malicious = "<script>alert(\"x\")</script>&<a href=\"https://evil.test\">\r\n\0click</a>";
        var escaped = TelegramHtml.Escape(malicious);

        Assert.Equal("&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt;&amp;&lt;a href=&quot;https://evil.test&quot;&gt;click&lt;/a&gt;", escaped);
        Assert.DoesNotContain("<", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain(">", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", escaped, StringComparison.Ordinal);

        // Reject admin URLs with UserInfo, query, fragment or non-http(s) schemes.
        Assert.Null(NotificationTemplates.BuildSafeAdminLink("https://user:pass@admin.example.test", "/orders/1"));
        Assert.Null(NotificationTemplates.BuildSafeAdminLink("javascript:alert(1)", "/orders/1"));
        Assert.Null(NotificationTemplates.BuildSafeAdminLink("https://admin.example.test?x=1", "/orders/1"));
    }

    [Fact]
    public void TelegramMarkdownV2EscapeCoversAllReservedCharacters()
    {
        const string reserved = "_*[]()~`>#+-=|{}.!\\";
        var escaped = TelegramMarkdownV2.Escape(reserved);

        foreach (var ch in reserved)
            Assert.Contains("\\" + ch, escaped, StringComparison.Ordinal);
        Assert.Equal(reserved.Length * 2, escaped.Length);
    }

    [Theory]
    [InlineData("Đơn mới\r\nBcc: attacker@evil.test")]
    [InlineData("Đơn mới\nInjected: 1")]
    [InlineData("Đơn mới\0null")]
    [InlineData("   ")]
    public void EmailHeaderValidationRejectsCrlfAndControlCharactersInSubjectAndSender(string badHeader)
    {
        var ex = Assert.Throws<NotificationDeliveryException>(() => EmailSecurity.ValidateHeaderValue(badHeader));
        Assert.Equal("invalid_email_header", ex.ErrorCode);
        Assert.DoesNotContain("attacker", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("shop@example.test\r\nBcc: evil@example.test")]
    [InlineData("shop@example.test\n")]
    [InlineData("shop@example.test\t")]
    [InlineData("user name@example.test")]
    [InlineData("\"quoted name\"@example.test")]
    [InlineData("\"user\"@example.test")]
    [InlineData("<shop@example.test>")]
    [InlineData("user<tag>@example.test")]
    [InlineData("user@localhost")]
    [InlineData("user@.example.test")]
    [InlineData("user@example.test.")]
    [InlineData("user@sub..example.test")]
    [InlineData("a@b@example.test")]
    [InlineData("not-an-email")]
    [InlineData("Display Name <shop@example.test>")]
    [InlineData("")]
    public void EmailRecipientValidationRejectsCrlfAndInvalidAddresses(string badRecipient)
    {
        Assert.False(EmailSecurity.IsValidEmailAddress(badRecipient));
        var ex = Assert.Throws<NotificationDeliveryException>(() => EmailSecurity.ValidateRecipient(badRecipient));
        Assert.Equal("invalid_email_recipient", ex.ErrorCode);
    }

    [Fact]
    public void RecipientHashComputesDeterministicLowercaseSha256WithoutPlaintext()
    {
        var hash1 = NotificationContracts.HashRecipient(NotificationContracts.NormalizeEmail("  Shop@Example.Test "));
        var hash2 = NotificationContracts.HashRecipient("shop@example.test");

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
        Assert.All(hash1, c => Assert.True(c is (>= '0' and <= '9') or (>= 'a' and <= 'f')));
        Assert.DoesNotContain("shop", hash1, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example", hash1, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChannelWatermarkSkipsEventsOlderThanSettingUpdatedAtAndAllowsNewerEvents()
    {
        var enabledAt = new DateTime(2026, 11, 23, 2, 0, 0, DateTimeKind.Utc);

        Assert.False(NotificationContracts.ShouldDeliverForWatermark(enabledAt.AddSeconds(-1), enabledAt));
        Assert.True(NotificationContracts.ShouldDeliverForWatermark(enabledAt, enabledAt));
        Assert.True(NotificationContracts.ShouldDeliverForWatermark(enabledAt.AddSeconds(1), enabledAt));
        Assert.False(NotificationContracts.ShouldDeliverForWatermark(enabledAt, null));
    }

    [Fact]
    public void BackfilledOrderPayloadRoundTripsAndRejectsInvalidPayloads()
    {
        var orderId = Guid.CreateVersion7();
        var backfilled = new OrderPlacedNotification(orderId, "TP261123BF01", 500_000L, "cod", 1, IsBackfilled: true);
        var json = OrderPlacedNotification.Serialize(backfilled);

        Assert.True(OrderPlacedNotification.TryParse(json, out var parsed));
        Assert.True(parsed.IsBackfilled);
        Assert.Equal(orderId, parsed.OrderId);

        Assert.False(OrderPlacedNotification.TryParse("{\"orderId\":\"00000000-0000-0000-0000-000000000000\",\"orderCode\":\"TP261123AB12\",\"totalVnd\":1000,\"paymentMethod\":\"cod\",\"itemCount\":1}", out _));
        Assert.False(OrderPlacedNotification.TryParse("{\"orderId\":\"" + orderId + "\",\"orderCode\":\"TP\\r\\n\",\"totalVnd\":1000,\"paymentMethod\":\"cod\",\"itemCount\":1}", out _));
        Assert.False(OrderPlacedNotification.TryParse("{\"orderId\":\"" + orderId + "\",\"orderCode\":\"TP261123AB12\",\"totalVnd\":-1,\"paymentMethod\":\"cod\",\"itemCount\":1}", out _));
        Assert.False(OrderPlacedNotification.TryParse("{\"orderId\":\"" + orderId + "\",\"orderCode\":\"TP261123AB12\",\"totalVnd\":1000,\"paymentMethod\":\"crypto\",\"itemCount\":1}", out _));
    }

    [Fact]
    public void ShopEmailsDeduplicatesCaseInsensitivelyAndSkipsWhenEmpty()
    {
        var deduped = NotificationContracts.DeduplicateShopEmails(
            ["Shop@Tea.vn", "shop@tea.vn", "  OPS@tea.vn ", "", null, "invalid\r\n@tea.vn"]);

        Assert.Equal(["Shop@Tea.vn", "OPS@tea.vn"], deduped);
        Assert.Empty(NotificationContracts.DeduplicateShopEmails([]));
    }

    [Fact]
    public void DailyDigestScheduleBoundaryUsesAsiaHoChiMinh0800()
    {
        var vnDate = new DateOnly(2026, 11, 23);
        var before = new DateTimeOffset(2026, 11, 23, 0, 59, 59, TimeSpan.Zero); // 07:59:59 +07:00
        var exact = new DateTimeOffset(2026, 11, 23, 1, 0, 0, TimeSpan.Zero);   // 08:00:00 +07:00
        var scheduledUtc = BusinessCalendar.ToUtc(vnDate, NotificationContracts.DailyDigestScheduledVietnamTime);

        Assert.Equal(exact, scheduledUtc);
        Assert.True(before < scheduledUtc);
        Assert.False(exact < scheduledUtc);
        Assert.Equal(vnDate, BusinessCalendar.ToVietnamDate(before));
        Assert.Equal(vnDate, BusinessCalendar.ToVietnamDate(exact));
    }

    [Theory]
    [InlineData("{\"notify.telegram_chat_id\":null}", true)]
    [InlineData("{\"notify.telegram_chat_id\":\"\"}", true)]
    [InlineData("{\"notify.telegram_chat_id\":\"-1001234567890\"}", true)]
    [InlineData("{\"notify.telegram_chat_id\":\"   \"}", false)]
    [InlineData("{\"notify.telegram_chat_id\":\"123\\n456\"}", false)]
    [InlineData("{\"notify.telegram_chat_id\":123456}", false)]
    public void SettingsCatalogAllowsNullAndEmptyStringToDisableTelegramChatId(string json, bool expectedValid)
    {
        var values = JsonSerializer.Deserialize<JsonElement>(json);
        var errors = SettingsCatalog.Validate("notify", values).ToArray();
        Assert.Equal(expectedValid, errors.Length == 0);
    }

    [Fact]
    public void Gap03TechnicalAlertTemplatesEscapeValuesAndOmitPiiAndCosts()
    {
        var mismatchHtml = NotificationTemplates.RenderInventoryReconciliationShop(
            new InventoryReconciliationAlertModel(Guid.CreateVersion7(), "AM-<TD>-01", 10, 7, new DateOnly(2026, 11, 23), Reserved: 3, ActiveReservationQuantity: 1),
            "https://admin.example.test");
        Assert.Contains("AM-&lt;TD&gt;-01", mismatchHtml, StringComparison.Ordinal);
        Assert.Contains("-3", mismatchHtml, StringComparison.Ordinal);
        Assert.Contains("Giữ hàng (reserved): 3 / active reservations: 1 (lệch <b>-2</b>)", mismatchHtml, StringComparison.Ordinal);
        Assert.Contains("23/11/2026", mismatchHtml, StringComparison.Ordinal);
        Assert.Contains("https://admin.example.test/inventory", mismatchHtml, StringComparison.Ordinal);

        var deadId = Guid.CreateVersion7();
        var deadHtml = NotificationTemplates.RenderOutboxDeadShop(
            new OutboxDeadAlertModel(deadId, "Order<Placed>", 10, "handler_failed"),
            "https://admin.example.test");
        Assert.Contains("Order&lt;Placed&gt;", deadHtml, StringComparison.Ordinal);
        Assert.Contains(deadId.ToString("D"), deadHtml, StringComparison.Ordinal);
        Assert.Contains("handler_failed", deadHtml, StringComparison.Ordinal);
        Assert.Contains("https://admin.example.test/system/outbox", deadHtml, StringComparison.Ordinal);
    }
}
