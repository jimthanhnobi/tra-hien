using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Notifications;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Settings;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Jobs;
using TeaPremium.Infrastructure.Notifications;
using TeaPremium.Infrastructure.Outbox;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class NotificationInfrastructurePolicyTests
{
    private static readonly string[] LocalhostDns = ["localhost"];
    private static readonly string[] WrongSmtpDns = ["wrong-smtp.example.test"];
    private static readonly IPAddress[] LoopbackIps = [IPAddress.Loopback];

    [Fact]
    public async Task TelegramRateLimiterEnforcesPerChatSpacingAndMinuteWindowWithFakeTimeProvider()
    {
        var clock = new ManualNotificationClock(new(2026, 11, 23, 1, 0, 0, TimeSpan.Zero));
        var limiter = new TelegramRateLimiter(clock, clock);
        var chatKey = NotificationContracts.HashRecipient("-10012345");

        // 1st message passes immediately.
        await limiter.WaitTurnAsync(chatKey, CancellationToken.None);

        // 2nd message must wait 1.1s spacing.
        var second = limiter.WaitTurnAsync(chatKey, CancellationToken.None);
        Assert.False(second.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1099));
        Assert.False(second.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await second;

        // Fill up to 18 messages within the 1-minute sliding window (every 1.1s -> 18 * 1.1s < 60s).
        for (var i = 2; i < TelegramRateLimiter.MaxMessagesPerWindow; i++)
        {
            var next = limiter.WaitTurnAsync(chatKey, CancellationToken.None);
            Assert.False(next.IsCompleted);
            clock.Advance(TelegramRateLimiter.MinSpacing);
            await next;
        }

        // 19th message must wait until the 1st message expires from the 60s window.
        var nineteenth = limiter.WaitTurnAsync(chatKey, CancellationToken.None);
        Assert.False(nineteenth.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(nineteenth.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(40));
        await nineteenth;
    }

    [Theory]
    [InlineData("telegram-custom-host-prod", "Production")]
    [InlineData("telegram-http-prod", "Production")]
    [InlineData("smtp-notls-prod", "Production")]
    [InlineData("admin-http-prod", "Production")]
    [InlineData("telegram-custom-host-staging", "Staging")]
    [InlineData("telegram-http-loopback-staging", "Staging")]
    [InlineData("smtp-notls-external-staging", "Staging")]
    [InlineData("smtp-notls-loopback-staging", "Staging")]
    [InlineData("admin-http-staging", "Staging")]
    [InlineData("smtp-notls-external-dev", "Development")]
    [InlineData("telegram-http-external-test", "Test")]
    [InlineData("smtp-port-465-prod", "Production")]
    [InlineData("smtp-port-465-dev", "Development")]
    [InlineData("bad-timeout", "Development")]
    [InlineData("connect-exceeds-request", "Test")]
    [InlineData("partial-smtp-auth", "Test")]
    [InlineData("bad-smtp-port", "Test")]
    public void NotificationOptionsRejectsInsecureOverridesNonLocalAndPort465(string scenario, string environmentName)
    {
        var options = new NotificationOptions
        {
            TelegramBotToken = "123456:TEST-TOKEN",
            TelegramBaseUrl = NotificationOptions.DefaultTelegramBaseUrl,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            SmtpFromEmail = "shop@example.test",
            SmtpFromName = "Trà Hiên",
            SmtpUseTls = true,
            AdminBaseUrl = "https://admin.example.test"
        };

        options.Validate("Production");
        options.Validate("Staging");

        switch (scenario)
        {
            case "telegram-custom-host-prod": options.TelegramBaseUrl = "https://custom.example.test"; break;
            case "telegram-http-prod": options.TelegramBaseUrl = "http://api.telegram.org"; break;
            case "smtp-notls-prod": options.SmtpUseTls = false; break;
            case "admin-http-prod": options.AdminBaseUrl = "http://admin.example.test"; break;
            case "telegram-custom-host-staging": options.TelegramBaseUrl = "https://custom.example.test"; break;
            case "telegram-http-loopback-staging": options.TelegramBaseUrl = "http://127.0.0.1:8080"; break;
            case "smtp-notls-external-staging": options.SmtpUseTls = false; options.SmtpHost = "smtp.example.test"; break;
            case "smtp-notls-loopback-staging": options.SmtpUseTls = false; options.SmtpHost = "127.0.0.1"; break;
            case "admin-http-staging": options.AdminBaseUrl = "http://admin.example.test"; break;
            case "smtp-notls-external-dev": options.SmtpUseTls = false; options.SmtpHost = "smtp.example.test"; break;
            case "telegram-http-external-test": options.TelegramBaseUrl = "http://api.telegram.org"; break;
            case "smtp-port-465-prod": options.SmtpPort = 465; break;
            case "smtp-port-465-dev": options.SmtpPort = 465; break;
            case "bad-timeout": options.TelegramRequestTimeoutSeconds = 0; break;
            case "connect-exceeds-request": options.TelegramConnectTimeoutSeconds = 12; options.TelegramRequestTimeoutSeconds = 10; break;
            case "partial-smtp-auth": options.SmtpUsername = "user"; options.SmtpPassword = ""; break;
            case "bad-smtp-port": options.SmtpPort = 70000; break;
        }

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(environmentName));
        if (scenario is "smtp-port-465-prod" or "smtp-port-465-dev")
            Assert.Contains("587", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development", "127.0.0.1", "http://127.0.0.1:8080")]
    [InlineData("CI", "localhost", "http://localhost:8080")]
    [InlineData("Test", "::1", "http://[::1]:8080")]
    public void NotificationOptionsAllowsLoopbackInsecureOnlyInLocalEnvironments(string env, string smtpHost, string tgUrl)
    {
        var options = new NotificationOptions
        {
            TelegramBotToken = "123456:TEST-TOKEN",
            TelegramBaseUrl = tgUrl,
            SmtpHost = smtpHost,
            SmtpPort = 2525,
            SmtpFromEmail = "shop@example.test",
            SmtpFromName = "Trà Hiên",
            SmtpUseTls = false,
            AdminBaseUrl = "http://localhost:3001"
        };

        options.Validate(env);
    }

    [Fact]
    public void NotificationRegistrationRejectsStagingStartupWithNonTlsExternalSmtp()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SMTP_HOST"] = "smtp.external.example.test",
            ["SMTP_PORT"] = "587",
            ["SMTP_FROM_EMAIL"] = "shop@example.test",
            ["SMTP_USE_TLS"] = "false"
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock>(new ManualNotificationClock(new(2026, 11, 25, 1, 0, 0, TimeSpan.Zero)));
        services.AddNotifications(config, new FakeHostEnvironment("Staging"));

        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<NotificationOptions>());
    }

    [Fact]
    public async Task BclSmtpTransportRefusesCleartextAuthToNonLoopbackHostBeforeSendingAuth()
    {
        await using var smtp = new LoopbackSmtpServer();
        var clock = new ManualNotificationClock(new(2026, 11, 25, 1, 0, 0, TimeSpan.Zero));
        var transport = new BclSmtpTransport(clock, clock, testConnectHostOverride: "127.0.0.1");

        var envelope = new SmtpEnvelope(
            Host: "smtp.external.example.test",
            Port: smtp.Port,
            UseTls: false,
            Username: "smtp-user",
            Password: "super-secret-password",
            FromEmail: "noreply@tea.test",
            FromName: "Trà Hiên",
            ToEmail: "owner@tea.test",
            Subject: "Test",
            PlainTextBody: "Body",
            HtmlBody: null,
            ConnectTimeout: TimeSpan.FromSeconds(5),
            SendTimeout: TimeSpan.FromSeconds(15));

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(() => transport.SendAsync(envelope, CancellationToken.None));
        Assert.Equal("smtp_tls_required_for_auth", ex.ErrorCode);
        Assert.Equal(0, smtp.AuthCommandCount);
        Assert.DoesNotContain("AUTH", smtp.ReceivedCommands);
        Assert.Empty(smtp.Messages);
    }

    [Fact]
    public async Task BclSmtpTransportStartTlsRejectsUntrustedSelfSignedCertificateByDefaultWithoutSendingAuth()
    {
        using var certs = InMemoryStartTlsCertificateSet.Create(LocalhostDns, LoopbackIps);
        await using var smtp = new LoopbackSmtpServer(certs.ServerCert);
        var clock = new ManualNotificationClock(new(2026, 11, 25, 1, 0, 0, TimeSpan.Zero));
        // Production constructor: strict OS trust store, no custom trust root.
        var transport = new BclSmtpTransport(clock, clock);

        var envelope = new SmtpEnvelope(
            Host: "127.0.0.1",
            Port: smtp.Port,
            UseTls: true,
            Username: "smtp-user",
            Password: "super-secret-password",
            FromEmail: "noreply@tea.test",
            FromName: "Trà Hiên",
            ToEmail: "owner@tea.test",
            Subject: "STARTTLS Untrusted",
            PlainTextBody: "Body",
            HtmlBody: null,
            ConnectTimeout: TimeSpan.FromSeconds(5),
            SendTimeout: TimeSpan.FromSeconds(15));

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(() => transport.SendAsync(envelope, CancellationToken.None));
        Assert.Equal("smtp_tls_failed", ex.ErrorCode);
        Assert.Contains("STARTTLS", smtp.ReceivedCommands);
        Assert.Equal(0, smtp.AuthCommandCount);
        Assert.DoesNotContain("AUTH", smtp.ReceivedCommands);
        Assert.Empty(smtp.Messages);
    }

    [Fact]
    public async Task BclSmtpTransportStartTlsSucceedsWithCustomRootTrustAndExecutesStartTlsEhloAuthData()
    {
        using var certs = InMemoryStartTlsCertificateSet.Create(LocalhostDns, LoopbackIps);
        await using var smtp = new LoopbackSmtpServer(certs.ServerCert);
        var clock = new ManualNotificationClock(new(2026, 11, 25, 1, 0, 0, TimeSpan.Zero));
        var transport = new BclSmtpTransport(clock, clock, testTrustedRootCertificate: certs.RootCaPublicCert);

        var envelope = new SmtpEnvelope(
            Host: "127.0.0.1",
            Port: smtp.Port,
            UseTls: true,
            Username: "smtp-user",
            Password: "super-secret-password",
            FromEmail: "noreply@tea.test",
            FromName: "Trà Hiên",
            ToEmail: "owner@tea.test",
            Subject: "STARTTLS OK",
            PlainTextBody: "Nội dung đơn mới TP261125TLS1",
            HtmlBody: "<p>Nội dung đơn mới TP261125TLS1</p>",
            ConnectTimeout: TimeSpan.FromSeconds(5),
            SendTimeout: TimeSpan.FromSeconds(15));

        await transport.SendAsync(envelope, CancellationToken.None);

        var commands = smtp.ReceivedCommands.ToArray();
        Assert.Contains("EHLO", commands);
        Assert.Contains("STARTTLS", commands);
        Assert.Contains("EHLO_TLS", commands);
        Assert.Contains("AUTH", commands);
        Assert.Contains("DATA", commands);
        Assert.Equal(1, smtp.AuthCommandCount);
        var sent = Assert.Single(smtp.Messages);
        Assert.Equal("<owner@tea.test>", sent.RcptTo);
        Assert.NotNull(sent.AuthPlain);
    }

    [Fact]
    public async Task BclSmtpTransportStartTlsRejectsTargetHostSanMismatchEvenWithCustomRootTrust()
    {
        // Leaf cert is signed by the trusted test root, but its SAN does NOT match 127.0.0.1.
        using var certs = InMemoryStartTlsCertificateSet.Create(WrongSmtpDns, []);
        await using var smtp = new LoopbackSmtpServer(certs.ServerCert);
        var clock = new ManualNotificationClock(new(2026, 11, 25, 1, 0, 0, TimeSpan.Zero));
        var transport = new BclSmtpTransport(clock, clock, testTrustedRootCertificate: certs.RootCaPublicCert);

        var envelope = new SmtpEnvelope(
            Host: "127.0.0.1",
            Port: smtp.Port,
            UseTls: true,
            Username: "smtp-user",
            Password: "super-secret-password",
            FromEmail: "noreply@tea.test",
            FromName: "Trà Hiên",
            ToEmail: "owner@tea.test",
            Subject: "STARTTLS Mismatch",
            PlainTextBody: "Body",
            HtmlBody: null,
            ConnectTimeout: TimeSpan.FromSeconds(5),
            SendTimeout: TimeSpan.FromSeconds(15));

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(() => transport.SendAsync(envelope, CancellationToken.None));
        Assert.Equal("smtp_tls_failed", ex.ErrorCode);
        Assert.Contains("STARTTLS", smtp.ReceivedCommands);
        Assert.Equal(0, smtp.AuthCommandCount);
        Assert.DoesNotContain("AUTH", smtp.ReceivedCommands);
        Assert.Empty(smtp.Messages);
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "TeaPremium.Api";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class NotificationsOutboxAndDigestTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string FakeBotToken = "999999:SUPER-SECRET-BOT-TOKEN-XYZ";
    private const string FakeSmtpPassword = "SMTP-SECRET-PASS-XYZ";
    private static readonly string[] OwnerNotifyEmails = ["owner-notify@tea.test"];

    [Fact]
    public async Task BrPlt03BrPlt04OrderPlacedFansOutToFakeTelegramAndLoopbackSmtpAndPersistsHashedNotificationLogs()
    {
        await using var smtp = new LoopbackSmtpServer();
        var telegram = new FakeTelegramHttpMessageHandler();
        var clock = new ManualNotificationClock(new(2026, 11, 23, 2, 0, 0, TimeSpan.Zero));
        await using var h = await CreateHarnessAsync(clock, telegram, smtp);

        await ConfigureNotifySettingsAsync(h, "-100999888", ["Shop@Tea.test", "shop@tea.test", "ops@tea.test"]);

        clock.Advance(TimeSpan.FromSeconds(5));
        var orderId = Guid.CreateVersion7();
        var backfilledOrderId = Guid.CreateVersion7();
        await SeedOrderAndEnqueueOrderPlacedAsync(h, orderId, "TP261123AB12", 1_550_000L, "cod", 2, clock.UtcNow.UtcDateTime);
        await SeedOrderAndEnqueueOrderPlacedAsync(h, backfilledOrderId, "TP261123BF01", 800_000L, "cod", 1, clock.UtcNow.UtcDateTime, isBackfilled: true);

        await using (var scope = h.Factory.Services.CreateAsyncScope())
            Assert.Equal(5, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));

        var tgReq = Assert.Single(telegram.Requests);
        Assert.Equal("-100999888", tgReq.ChatId);
        Assert.Equal("HTML", tgReq.ParseMode);
        Assert.True(tgReq.DisableWebPagePreview);
        Assert.Contains("TP261123AB12", tgReq.Text, StringComparison.Ordinal);
        Assert.Contains("1.550.000 đ", tgReq.Text, StringComparison.Ordinal);
        Assert.Contains($"https://admin.example.test/orders/{orderId:D}", tgReq.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("TP261123BF01", tgReq.Text, StringComparison.Ordinal);

        Assert.Equal(2, smtp.Messages.Count);
        Assert.Contains(smtp.Messages, m => m.RcptTo == "<Shop@Tea.test>");
        Assert.Contains(smtp.Messages, m => m.RcptTo == "<ops@tea.test>");
        Assert.All(smtp.Messages, m =>
        {
            Assert.Equal("<noreply@tea.test>", m.MailFrom);
            Assert.Contains("multipart/alternative", m.RawData, StringComparison.OrdinalIgnoreCase);
            var decodedBody = DecodeBase64Parts(m.RawData);
            Assert.Contains("TP261123AB12", decodedBody, StringComparison.Ordinal);
            Assert.Contains("1.550.000 đ", decodedBody, StringComparison.Ordinal);
        });

        await using var db = h.OwnerContext();
        var logs = await db.NotificationLogs.AsNoTracking().OrderBy(x => x.Channel).ToListAsync();
        Assert.Equal(3, logs.Count);
        Assert.All(logs, log =>
        {
            Assert.Equal("sent", log.Status);
            Assert.NotNull(log.SentAt);
            Assert.Null(log.Error);
            Assert.Equal(NotificationRefTypes.Order, log.RefType);
            Assert.Equal(orderId, log.RefId);
            Assert.Equal(NotificationTemplateNames.OrderPlacedShop, log.Template);
            Assert.Equal(64, log.RecipientHash.Length);
            Assert.DoesNotContain("-100999888", log.RecipientHash, StringComparison.Ordinal);
            Assert.DoesNotContain("tea.test", log.RecipientHash, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(logs, l => l.Channel == NotificationChannels.Telegram && l.RecipientHash == NotificationContracts.HashRecipient("-100999888"));
        Assert.Contains(logs, l => l.Channel == NotificationChannels.Email && l.RecipientHash == NotificationContracts.HashRecipient("shop@tea.test"));
        Assert.Contains(logs, l => l.Channel == NotificationChannels.Email && l.RecipientHash == NotificationContracts.HashRecipient("ops@tea.test"));
    }

    [Fact]
    public async Task PartialChannelFailureDoesNotResendSucceededTelegramWhenSmtpFailsAndRetries()
    {
        await using var smtp = new LoopbackSmtpServer();
        smtp.EnqueueDataResponseCode(421); // Fail first SMTP attempt transiently, then succeed.
        var telegram = new FakeTelegramHttpMessageHandler();
        var clock = new ManualNotificationClock(new(2026, 11, 23, 2, 0, 0, TimeSpan.Zero));
        await using var h = await CreateHarnessAsync(clock, telegram, smtp);

        await ConfigureNotifySettingsAsync(h, "-100111222", ["owner@tea.test"]);
        clock.Advance(TimeSpan.FromSeconds(5));

        var orderId = Guid.CreateVersion7();
        await SeedOrderAndEnqueueOrderPlacedAsync(h, orderId, "TP261123PR01", 900_000L, "bank_transfer", 1, clock.UtcNow.UtcDateTime);

        // Pass 1: OrderPlaced fans out -> Telegram succeeds, Email fails with 421 and schedules retry at +2m.
        await using (var scope = h.Factory.Services.CreateAsyncScope())
            Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));

        Assert.Single(telegram.Requests);
        Assert.Empty(smtp.Messages);

        await using (var db = h.OwnerContext())
        {
            var emailLog = await db.NotificationLogs.SingleAsync(x => x.Channel == NotificationChannels.Email);
            Assert.Equal("failed", emailLog.Status);
            Assert.Equal("smtp_transient_error", emailLog.Error);
            var emailOutbox = await db.OutboxMessages.SingleAsync(x => x.Type == NotificationContracts.DeliveryMessageType && x.Status == "pending");
            Assert.Equal(1, emailOutbox.Attempts);
            Assert.Equal(clock.UtcNow.UtcDateTime.AddMinutes(2), emailOutbox.AvailableAt);
        }

        // Pass 2 after backoff: SMTP succeeds on retry; Telegram is NOT resent.
        clock.Advance(TimeSpan.FromMinutes(2));
        await using (var scope = h.Factory.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));

        Assert.Single(telegram.Requests);
        Assert.Single(smtp.Messages);
        await using (var db = h.OwnerContext())
        {
            var logs = await db.NotificationLogs.ToListAsync();
            Assert.Equal(2, logs.Count);
            Assert.All(logs, l => Assert.Equal("sent", l.Status));
        }
    }

    [Fact]
    public async Task IdempotentRedeliveryOfSameNotificationMessageAfterSimulatedPostSendCrashDoesNotDuplicate()
    {
        var telegram = new FakeTelegramHttpMessageHandler();
        var clock = new ManualNotificationClock(new(2026, 11, 23, 2, 0, 0, TimeSpan.Zero));
        await using var h = await CreateHarnessAsync(clock, telegram, smtp: null);

        await ConfigureNotifySettingsAsync(h, "-100333444", []);
        clock.Advance(TimeSpan.FromSeconds(5));

        var orderId = Guid.CreateVersion7();
        var rendered = NotificationTemplates.RenderOrderPlacedShop(
            new OrderPlacedNotification(orderId, "TP261123ID01", 450_000L, "cod", 1),
            "https://admin.example.test");
        var deliveryId = await InsertOutboxAsync(h, NotificationContracts.DeliveryMessageType,
            NotificationDeliveryPayload.Serialize(new NotificationDeliveryPayload(
                NotificationChannels.Telegram,
                NotificationTemplateNames.OrderPlacedShop,
                NotificationRefTypes.Order,
                orderId,
                NotificationContracts.HashRecipient("-100333444"),
                null,
                rendered.TelegramHtml,
                rendered.TelegramHtml)),
            clock.UtcNow.UtcDateTime);

        // Simulate crash right after Telegram sent and notification_logs persisted 'sent', before outbox UPDATE commits.
        await h.ExecuteAsync("""
CREATE FUNCTION platform.test_crash_outbox_processed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status = 'processed' AND NEW.type = 'NotificationDelivery' THEN
    RAISE EXCEPTION 'simulated post-send crash before outbox commit';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER test_crash_outbox_processed BEFORE UPDATE ON platform.outbox_messages
  FOR EACH ROW EXECUTE FUNCTION platform.test_crash_outbox_processed();
""");

        await using (var scope = h.Factory.Services.CreateAsyncScope())
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(1, default));

        await h.ExecuteAsync("DROP TRIGGER test_crash_outbox_processed ON platform.outbox_messages; DROP FUNCTION platform.test_crash_outbox_processed()");

        // External call happened once and notification_logs already recorded 'sent', while outbox row remained 'pending'.
        Assert.Single(telegram.Requests);
        await using (var db = h.OwnerContext())
        {
            Assert.Equal("pending", (await db.OutboxMessages.SingleAsync(x => x.Id == deliveryId)).Status);
            Assert.Equal("sent", (await db.NotificationLogs.SingleAsync(x => x.RefId == orderId)).Status);
        }

        // Redelivery completes outbox cleanly WITHOUT calling Telegram a second time.
        await using (var scope = h.Factory.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(1, default));

        Assert.Single(telegram.Requests);
        await using (var db = h.OwnerContext())
            Assert.Equal("processed", (await db.OutboxMessages.SingleAsync(x => x.Id == deliveryId)).Status);
    }

    [Fact]
    public async Task BrPlt02TransientFailuresBackoffUpToTenAttemptsThenTransitionToDeadWithSafeErrorCodesAndNoLoop()
    {
        var attemptCounter = 0;
        var telegram = new FakeTelegramHttpMessageHandler((_, _, _) =>
        {
            var n = Interlocked.Increment(ref attemptCounter);
            if (n == 1) return Task.FromResult(FakeTelegramHttpMessageHandler.StatusResponse(HttpStatusCode.TooManyRequests, "{\"ok\":false,\"parameters\":{\"retry_after\":30}}"));
            if (n == 2) throw new OperationCanceledException("simulated timeout");
            return Task.FromResult(FakeTelegramHttpMessageHandler.StatusResponse(HttpStatusCode.InternalServerError));
        });
        var clock = new ManualNotificationClock(new(2026, 11, 23, 2, 0, 0, TimeSpan.Zero));
        await using var h = await CreateHarnessAsync(clock, telegram, smtp: null);

        await ConfigureNotifySettingsAsync(h, "-100555666", []);
        clock.Advance(TimeSpan.FromSeconds(5));

        var orderId = Guid.CreateVersion7();
        var deliveryId = await InsertOutboxAsync(h, NotificationContracts.DeliveryMessageType,
            NotificationDeliveryPayload.Serialize(new NotificationDeliveryPayload(
                NotificationChannels.Telegram,
                NotificationTemplateNames.OrderPlacedShop,
                NotificationRefTypes.Order,
                orderId,
                NotificationContracts.HashRecipient("-100555666"),
                null,
                "<b>Đơn hàng mới #TP261123DL01</b>",
                "<b>Đơn hàng mới #TP261123DL01</b>")),
            clock.UtcNow.UtcDateTime);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await using (var scope = h.Factory.Services.CreateAsyncScope())
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(5, default));

            await using var db = h.OwnerContext();
            var row = await db.OutboxMessages.SingleAsync(x => x.Id == deliveryId);
            var log = await db.NotificationLogs.SingleAsync(x => x.RefId == orderId);

            Assert.Equal(attempt, row.Attempts);
            var expectedCode = attempt switch
            {
                1 => "telegram_rate_limited",
                2 => "telegram_timeout",
                _ => "telegram_server_error"
            };
            Assert.Equal(expectedCode, row.LastError);
            Assert.Equal("failed", log.Status);
            Assert.Equal(expectedCode, log.Error);

            if (attempt < 10)
            {
                Assert.Equal("pending", row.Status);
                var expectedBackoff = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempt), 360));
                Assert.Equal(clock.UtcNow.UtcDateTime + expectedBackoff, row.AvailableAt);
                clock.Advance(expectedBackoff);
            }
            else
            {
                Assert.Equal("dead", row.Status);
            }
        }

        // No 11th attempt, and NotificationDelivery turning dead MUST NOT enqueue another NotificationDelivery (no infinite loop).
        clock.Advance(TimeSpan.FromHours(12));
        await using (var scope = h.Factory.Services.CreateAsyncScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));

        Assert.Equal(10, attemptCounter);
        await using (var db = h.OwnerContext())
            Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.Type == NotificationContracts.DeliveryMessageType));
    }

    [Fact]
    public async Task BrInv11BrPlt03Gap01Gap02DailyDigestQueriesAllSixP201MetricsAt0800VietnamUnderAdvisoryLockAndSurvivesRestart()
    {
        var telegram = new FakeTelegramHttpMessageHandler();
        // Start at 2026-11-23 07:59:59 +07:00 = 2026-11-23 00:59:59 UTC (1s before 08:00 VN).
        var clock = new ManualNotificationClock(new(2026, 11, 23, 0, 59, 59, TimeSpan.Zero));
        await using var h = await CreateHarnessAsync(clock, telegram, smtp: null);

        await ConfigureNotifySettingsAsync(h, "-100777888", []);
        await SeedSixDigestMetricsAsync(h, digestTimeUtc: new DateTime(2026, 11, 23, 1, 0, 0, DateTimeKind.Utc));

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = h.RuntimeConnection }).Build();
        var scopes = h.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var options = h.Factory.Services.GetRequiredService<NotificationOptions>();

        using var job1 = new DailyShopDigestJob(scopes, config, clock, clock, options, NullLogger<DailyShopDigestJob>.Instance);
        using var job2 = new DailyShopDigestJob(scopes, config, clock, clock, options, NullLogger<DailyShopDigestJob>.Instance);

        // 1. At 07:59:59 VN -> skipped.
        Assert.True(await job1.RunOnceAsync());
        await using (var db = h.OwnerContext())
            Assert.Equal(0, await db.OutboxMessages.CountAsync(x => x.Type == NotificationContracts.DeliveryMessageType));

        // 2. Advance 1s -> 08:00:00 VN. Prove real 2-instance advisory lock contention.
        clock.Advance(TimeSpan.FromSeconds(1));
        Task<bool> run1;
        await using (var blocker = new NpgsqlConnection(h.OwnerConnection))
        {
            await blocker.OpenAsync();
            await using var held = await blocker.BeginTransactionAsync();
            await using (var lockCmd = new NpgsqlCommand("LOCK TABLE sales.orders IN ACCESS EXCLUSIVE MODE", blocker, held))
                await lockCmd.ExecuteNonQueryAsync();

            run1 = job1.RunOnceAsync();
            await WaitUntilAsync(async () =>
            {
                await using var observer = new NpgsqlConnection(h.OwnerConnection);
                await observer.OpenAsync();
                await using var check = new NpgsqlCommand(
                    "SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND granted AND ((classid::bigint << 32) | (objid::bigint & 4294967295)) = @key",
                    observer);
                check.Parameters.Add("key", NpgsqlDbType.Bigint).Value = PeriodicJob.AdvisoryLockKey(DailyShopDigestJob.JobName);
                return (long)(await check.ExecuteScalarAsync())! == 1L;
            });

            Assert.False(await job2.RunOnceAsync());
            await held.RollbackAsync();
        }
        Assert.True(await run1);

        // 3. Restarted instance at 08:05 VN produces zero duplicates.
        clock.Advance(TimeSpan.FromMinutes(5));
        using (var restarted = new DailyShopDigestJob(scopes, config, clock, clock, options, NullLogger<DailyShopDigestJob>.Instance))
            Assert.True(await restarted.RunOnceAsync());

        // Deliver the digest outbox message.
        await using (var scope = h.Factory.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));

        var sent = Assert.Single(telegram.Requests);
        Assert.Contains("23/11/2026", sent.Text, StringComparison.Ordinal);
        // Verified exact counts from SeedSixDigestMetricsAsync:
        // - Pending confirmation: 2 (1 COD pending + 1 bank_transfer paid pending; excludes unpaid/partially_paid bank_transfer & backfilled & confirmed)
        // - Expiring payment (24h window): 2 (due in +1s and +24h; excludes expired, due==now, +24h+1s, partially_paid, null due)
        // - Unsettled COD: 2 (500_000 + 750_000 = 1.250.000 đ; excludes confirmed/voided)
        // - Follow-ups due today: 2 (overdue yesterday + today 23:59 VN; excludes tomorrow, completed, erased customer)
        // - Low stock: 12 SKUs (shows first 10 + "... và 2 SKU khác", zero artisan/product names, zero costs)
        // - Reconciliation mismatch: 1 SKU
        // - Dead outbox: 2
        Assert.Contains("Đơn chờ xác nhận: <b>2</b>", sent.Text, StringComparison.Ordinal);
        Assert.Contains("Chờ thanh toán sắp hết hạn (24h): <b>2</b>", sent.Text, StringComparison.Ordinal);
        Assert.Contains("COD chưa đối soát: <b>2</b> (1.250.000 đ)", sent.Text, StringComparison.Ordinal);
        Assert.Contains("Hẹn chăm sóc hôm nay: <b>2</b>", sent.Text, StringComparison.Ordinal);
        Assert.Contains("Tồn kho thấp: <b>12 SKU</b>", sent.Text, StringComparison.Ordinal);
        Assert.Contains("... và 2 SKU khác", sent.Text, StringComparison.Ordinal);
        Assert.Contains("Lệch đối soát sổ kho: <b>1 SKU</b>", sent.Text, StringComparison.Ordinal);
        Assert.Contains("Outbox dead: <b>2</b>", sent.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Chu Bân", sent.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ấm Bí Mật", sent.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("777666555", sent.Text, StringComparison.Ordinal);

        // 4. Next Vietnam day 08:00 produces a new digest (no backfill of missed days).
        clock.SetUtc(BusinessCalendar.ToUtc(new DateOnly(2026, 11, 24), new TimeOnly(8, 0)));
        Assert.True(await job1.RunOnceAsync());
        await using (var scope = h.Factory.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));

        Assert.Equal(2, telegram.Requests.Count);
        Assert.Contains(telegram.Requests, r => r.Text.Contains("24/11/2026", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BrInv11BrInv13Gap03NoStaleP1BacklogReplayAndImmediateAlertsEnforceWatermarkAndAntiDuplicate()
    {
        var telegram = new FakeTelegramHttpMessageHandler();
        // 2026-11-23 09:00 +07:00 = 2026-11-23 02:00 UTC.
        var clock = new ManualNotificationClock(new(2026, 11, 23, 2, 0, 0, TimeSpan.Zero));
        await using var h = await CreateHarnessAsync(clock, telegram, smtp: null);

        var lowVariantId = Guid.CreateVersion7();
        var mismatchVariantId = Guid.CreateVersion7();
        var resolvedVariantId = Guid.CreateVersion7();
        await SeedVariantAsync(h, lowVariantId, "AM-LW-0001", lowStockThreshold: 5, onHand: 2, movementQty: 2);
        await SeedVariantAsync(h, mismatchVariantId, "AM-MS-0001", lowStockThreshold: 0, onHand: 9, movementQty: 4);
        await SeedVariantAsync(h, resolvedVariantId, "AM-OK-0001", lowStockThreshold: 0, onHand: 5, movementQty: 5);

        // Seed pre-watermark P1 backlog BEFORE enabling notify.telegram_chat_id.
        var staleTime = clock.UtcNow.UtcDateTime.AddMinutes(-30);
        await InsertOutboxAsync(h, InventoryAlert.Message, InventoryAlert.Serialize(new(InventoryAlert.LowStock, lowVariantId)), staleTime, status: "processed");
        await InsertOutboxAsync(h, InventoryAlert.Message, InventoryAlert.Serialize(new(InventoryAlert.LowStock, lowVariantId)), staleTime, status: "pending");
        await InsertOutboxAsync(h, InventoryAlert.Message, InventoryAlert.Serialize(new(InventoryAlert.ReconciliationMismatch, mismatchVariantId, new DateOnly(2026, 11, 22))), staleTime);
        await InsertOutboxAsync(h, InventoryAlert.Message, InventoryAlert.Serialize(new(InventoryAlert.ReconciliationMismatch, mismatchVariantId, new DateOnly(2026, 11, 23))), staleTime);
        await InsertOutboxAsync(h, InventoryAlert.Message, InventoryAlert.Serialize(new(InventoryAlert.ReconciliationMismatch, resolvedVariantId, new DateOnly(2026, 11, 23))), staleTime);
        await SeedOrderAndEnqueueOrderPlacedAsync(h, Guid.CreateVersion7(), "TP261123OLD1", 100_000L, "cod", 1, staleTime);
        await InsertOutboxAsync(h, "UnknownStaleEvent", "{}", staleTime, attempts: 10);

        // Now owner enables Telegram at T0 = 02:00 UTC.
        await ConfigureNotifySettingsAsync(h, "-100888999", []);

        // Process entire stale backlog -> ZERO Telegram notifications sent!
        await using (var scope = h.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(20, default);
        Assert.Empty(telegram.Requests);

        // Now emit NEW post-watermark events at T0 + 1m:
        clock.Advance(TimeSpan.FromMinutes(1));
        var freshTime = clock.UtcNow.UtcDateTime;

        // (a) Post-watermark low_stock NEVER sends standalone Telegram (digest 08:00 only).
        await InsertOutboxAsync(h, InventoryAlert.Message, InventoryAlert.Serialize(new(InventoryAlert.LowStock, lowVariantId)), freshTime);
        // (b) Two duplicate post-watermark active reconciliation_mismatch for same variant/day -> deduplicated to 1 Telegram alert.
        await InsertOutboxAsync(h, InventoryAlert.Message, InventoryAlert.Serialize(new(InventoryAlert.ReconciliationMismatch, mismatchVariantId, new DateOnly(2026, 11, 23))), freshTime);
        await InsertOutboxAsync(h, InventoryAlert.Message, InventoryAlert.Serialize(new(InventoryAlert.ReconciliationMismatch, mismatchVariantId, new DateOnly(2026, 11, 23))), freshTime);
        // (c) Post-watermark non-NotificationDelivery message turning dead -> 1 immediate outbox_dead_shop Telegram alert.
        var deadSourceId = await InsertOutboxAsync(h, "UnknownFreshEvent", "{}", freshTime, attempts: 10);

        await using (var scope = h.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(20, default);

        Assert.Equal(2, telegram.Requests.Count);
        Assert.Single(telegram.Requests, r => r.Text.Contains("AM-MS-0001", StringComparison.Ordinal) && r.Text.Contains("-5", StringComparison.Ordinal));
        Assert.Single(telegram.Requests, r => r.Text.Contains("UnknownFreshEvent", StringComparison.Ordinal) && r.Text.Contains(deadSourceId.ToString("D"), StringComparison.Ordinal));
        Assert.DoesNotContain(telegram.Requests, r => r.Text.Contains("AM-LW-0001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnconfiguredChannelsAndDisabledTelegramChatIdCompleteCleanlyWithoutBlockingOutbox()
    {
        var telegram = new FakeTelegramHttpMessageHandler();
        var clock = new ManualNotificationClock(new(2026, 11, 23, 2, 0, 0, TimeSpan.Zero));
        await using var h = await CreateHarnessAsync(clock, telegram, smtp: null, configureSecrets: false);

        // 1. Neither env secrets nor DB settings configured -> OrderPlaced completes as processed with 0 deliveries.
        var firstOrderId = Guid.CreateVersion7();
        var firstMsgId = await SeedOrderAndEnqueueOrderPlacedAsync(h, firstOrderId, "TP261123NC01", 300_000L, "cod", 1, clock.UtcNow.UtcDateTime);

        await using (var scope = h.Factory.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));

        await using (var db = h.OwnerContext())
            Assert.Equal("processed", (await db.OutboxMessages.SingleAsync(x => x.Id == firstMsgId)).Status);
        Assert.Empty(telegram.Requests);

        // 2. With secrets configured, owner enables Telegram then disables it via null and "" (ASSUMPTION-01 A).
        await using var h2 = await CreateHarnessAsync(clock, telegram, smtp: null, configureSecrets: true);
        await ConfigureNotifySettingsAsync(h2, "-1001234", []);
        await ConfigureNotifySettingsAsync(h2, null, []);

        clock.Advance(TimeSpan.FromSeconds(5));
        var disabledOrderId = Guid.CreateVersion7();
        var disabledMsgId = await SeedOrderAndEnqueueOrderPlacedAsync(h2, disabledOrderId, "TP261123DS01", 300_000L, "cod", 1, clock.UtcNow.UtcDateTime);

        await using (var scope = h2.Factory.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));

        await using (var db = h2.OwnerContext())
            Assert.Equal("processed", (await db.OutboxMessages.SingleAsync(x => x.Id == disabledMsgId)).Status);
        Assert.Empty(telegram.Requests);

        // Also test empty string "".
        await ConfigureNotifySettingsAsync(h2, "", []);
        clock.Advance(TimeSpan.FromSeconds(5));
        var emptyOrderId = Guid.CreateVersion7();
        await SeedOrderAndEnqueueOrderPlacedAsync(h2, emptyOrderId, "TP261123DS02", 300_000L, "cod", 1, clock.UtcNow.UtcDateTime);
        await using (var scope = h2.Factory.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));
        Assert.Empty(telegram.Requests);
    }

    [Fact]
    public async Task SecurityZeroSecretPiiOrCostLeakAcrossPayloadsLogsDatabaseAndExceptions()
    {
        var logs = new NotificationLogCollector();
        var failNext = false;
        var telegram = new FakeTelegramHttpMessageHandler((_, _, _) =>
        {
            if (failNext)
                throw new HttpRequestException($"POST https://api.telegram.org/bot{FakeBotToken}/sendMessage failed");
            return Task.FromResult(FakeTelegramHttpMessageHandler.OkResponse());
        });
        await using var smtp = new LoopbackSmtpServer();
        var clock = new ManualNotificationClock(new(2026, 11, 23, 2, 0, 0, TimeSpan.Zero));
        await using var h = await CreateHarnessAsync(clock, telegram, smtp, configureSecrets: true, logs: logs);

        const string secretPhone = "+84987654321";
        const string secretAddress = "99 Duong Bao Mat P213";
        const string secretCustomer = "Nguyen Khach Bao Mat";
        const string secretArtisan = "Chu Bân";
        const string secretCost = "876543210";

        await ConfigureNotifySettingsAsync(h, "-100999111", ["shop-secret@tea.test"]);

        // Seed product with artisan and secret cost + order with customer PII.
        var artisanId = Guid.CreateVersion7();
        var productTypeId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        await h.ExecuteAsync($$"""
INSERT INTO catalog.product_types(id,code,name) VALUES ('{{productTypeId}}','teapot_sec','Ấm');
INSERT INTO catalog.artisans(id,slug,name,status,first_published_at) VALUES ('{{artisanId}}','chu-ban-sec','{{secretArtisan}}','published',now());
INSERT INTO catalog.products(id,product_type_id,artisan_id,sku_base,slug,name) VALUES
 ('{{productId}}','{{productTypeId}}','{{artisanId}}','AM-SC-0001','am-sec-01','Ấm {{secretArtisan}}');
INSERT INTO catalog.variants(id,product_id,sku,price_vnd,low_stock_threshold) VALUES
 ('{{variantId}}','{{productId}}','AM-SC-0001',1200000,5);
INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved,avg_cost_vnd) VALUES
 ('{{variantId}}',1,0,{{secretCost}});
""");

        clock.Advance(TimeSpan.FromSeconds(5));
        // 1. Success delivery (OrderPlaced backed by real sales.orders/order_lines carrying PII & unit_cost_vnd + Digest).
        await SeedOrderAndEnqueueOrderPlacedAsync(h, Guid.CreateVersion7(), "TP261123SC01", 1_200_000L, "bank_transfer", 1, clock.UtcNow.UtcDateTime,
            contactName: secretCustomer, contactPhone: secretPhone, addressLine: secretAddress, unitCostVnd: 876_543_210L);

        await using (var scope = h.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default);

        // 2. Failure delivery (simulated HttpRequestException carrying bot token in message + SMTP 421).
        failNext = true;
        smtp.EnqueueDataResponseCode(421);
        await SeedOrderAndEnqueueOrderPlacedAsync(h, Guid.CreateVersion7(), "TP261123SC02", 1_200_000L, "cod", 1, clock.UtcNow.UtcDateTime,
            contactName: secretCustomer, contactPhone: secretPhone, addressLine: secretAddress, unitCostVnd: 876_543_210L);

        await using (var scope = h.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default);

        // Run digest as well.
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = h.RuntimeConnection }).Build();
        var scopes = h.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var options = h.Factory.Services.GetRequiredService<NotificationOptions>();
        failNext = false;
        using (var job = new DailyShopDigestJob(scopes, config, clock, clock, options, NullLogger<DailyShopDigestJob>.Instance))
            Assert.True(await job.RunOnceAsync());
        await using (var scope = h.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default);

        // Collect every artifact to scan for secrets/PII/costs.
        var sensitiveStrings = new[] { FakeBotToken, FakeSmtpPassword, secretPhone, secretAddress, secretCustomer, secretArtisan, secretCost };
        var logDump = string.Join("\n", logs.Entries.Select(e => $"{e.Category}|{e.EventName}|{e.Message}|{e.Exception}"));

        await using var db = h.OwnerContext();
        var outboxRows = await db.OutboxMessages.AsNoTracking().Select(x => new { x.Payload, x.LastError }).ToListAsync();
        var notifRows = await db.NotificationLogs.AsNoTracking().Select(x => new { x.RecipientHash, x.Error }).ToListAsync();
        var outboxDump = string.Join("\n", outboxRows.Select(x => x.Payload + "|" + x.LastError));
        var notifDump = string.Join("\n", notifRows.Select(x => x.RecipientHash + "|" + x.Error));
        var auditDump = string.Join("\n", await db.AuditLogs.AsNoTracking().Select(x => x.Changes).ToListAsync());
        var telegramBodies = string.Join("\n", telegram.Requests.Select(r => r.RawBody));
        var smtpBodies = string.Join("\n", smtp.Messages.Select(m => m.RawData + "\n" + DecodeBase64Parts(m.RawData)));

        foreach (var secret in sensitiveStrings)
        {
            Assert.DoesNotContain(secret, logDump, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, outboxDump, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, notifDump, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, auditDump, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, telegramBodies, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, smtpBodies, StringComparison.OrdinalIgnoreCase);
        }

        // Plaintext recipients must never appear in outbox payloads, notification_logs, audit_logs, or logs.
        foreach (var recipient in new[] { "-100999111", "shop-secret@tea.test" })
        {
            Assert.DoesNotContain(recipient, logDump, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(recipient, outboxDump, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(recipient, notifDump, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(recipient, auditDump, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SettingsNotifyGroupPutRequiresOwnerAuditsRedactedValuesAndUpdatesWatermark()
    {
        var clock = new ManualNotificationClock(new(2026, 11, 23, 2, 0, 0, TimeSpan.Zero));
        var telegram = new FakeTelegramHttpMessageHandler();
        await using var h = await CreateHarnessAsync(clock, telegram, smtp: null);
        await h.LoginAsync();

        // Create sales and content staff -> both get 403 on GET/PUT /api/v1/admin/settings/notify.
        foreach (var role in new[] { "sales", "content" })
        {
            var staffEmail = $"{role}-notify@example.test";
            using (var created = await h.MutateAsync(HttpMethod.Post, "/api/v1/admin/staff/", new
            {
                email = staffEmail,
                displayName = role,
                initialPassword = P009Harness.Password,
                roles = new[] { role }
            }))
            {
                Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            }

            using (var logout = await h.MutateAsync(HttpMethod.Post, "/api/v1/admin/auth/logout", new { }))
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            await h.LoginAsync(staffEmail);
            using (var change = await h.MutateAsync(HttpMethod.Post, "/api/v1/admin/auth/change-password", new
            {
                currentPassword = P009Harness.Password,
                newPassword = P009Harness.Password + "-Changed"
            }))
                Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
            await h.LoginAsync(staffEmail, P009Harness.Password + "-Changed");

            Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/admin/settings/notify")).StatusCode);
            using var deniedPut = await h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/notify", new
            {
                version = new string('a', 64),
                values = new Dictionary<string, object?> { ["notify.telegram_chat_id"] = "-1001" }
            });
            Assert.Equal(HttpStatusCode.Forbidden, deniedPut.StatusCode);

            using (var logout2 = await h.MutateAsync(HttpMethod.Post, "/api/v1/admin/auth/logout", new { }))
                Assert.Equal(HttpStatusCode.NoContent, logout2.StatusCode);
            await h.LoginAsync();
        }

        // Owner PUT succeeds, sets watermark UpdatedAt, redacts audit values, and supports null/"" disable.
        var initial = (await h.Client.GetFromJsonAsync<SettingsGroup>("/api/v1/admin/settings/notify"))!;
        using var put1 = await h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/notify", new
        {
            version = initial.Version,
            values = new Dictionary<string, object?>
            {
                ["notify.telegram_chat_id"] = "-100444555",
                ["notify.shop_emails"] = OwnerNotifyEmails
            }
        });
        Assert.Equal(HttpStatusCode.OK, put1.StatusCode);
        var updated1 = (await put1.Content.ReadFromJsonAsync<SettingsGroup>())!;

        clock.Advance(TimeSpan.FromMinutes(10));
        using var put2 = await h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/notify", new
        {
            version = updated1.Version,
            values = new Dictionary<string, object?> { ["notify.telegram_chat_id"] = null }
        });
        Assert.Equal(HttpStatusCode.OK, put2.StatusCode);

        await using var db = h.OwnerContext();
        var tgSetting = await db.Settings.AsNoTracking().SingleAsync(x => x.Key == "notify.telegram_chat_id");
        Assert.Equal("null", tgSetting.Value);
        Assert.Equal(clock.UtcNow.UtcDateTime, tgSetting.UpdatedAt);

        var audits = await db.AuditLogs.AsNoTracking()
            .Where(x => x.EntityType == "platform.settings" && (x.EntityKey == "notify.telegram_chat_id" || x.EntityKey == "notify.shop_emails"))
            .ToListAsync();
        Assert.Equal(3, audits.Count);
        Assert.All(audits, a =>
        {
            Assert.DoesNotContain("-100444555", a.Changes, StringComparison.Ordinal);
            Assert.DoesNotContain("owner-notify@tea.test", a.Changes, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[redacted]", a.Changes, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RuntimeRoleTpAppCanReadSettingsWriteNotificationLogsQueryP201DigestAndAcquireAdvisoryLocks()
    {
        var telegram = new FakeTelegramHttpMessageHandler();
        var clock = new ManualNotificationClock(new(2026, 11, 23, 1, 0, 0, TimeSpan.Zero));
        await using var h = await CreateHarnessAsync(clock, telegram, smtp: null);

        await ConfigureNotifySettingsAsync(h, "-100666777", []);
        clock.Advance(TimeSpan.FromSeconds(5));

        // Confirm runtime connection is restricted role tp_app (never tp_owner).
        await using (var cn = new NpgsqlConnection(h.RuntimeConnection))
        {
            await cn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT current_user", cn);
            Assert.Equal("tp_app", (string)(await cmd.ExecuteScalarAsync())!);
        }

        // Run OrderPlaced -> NotificationDelivery (pg_advisory_xact_lock + notification_logs) + DailyShopDigestJob (pg_try_advisory_lock + 6 P2-01/P1/P0 queries) purely as tp_app.
        await SeedOrderAndEnqueueOrderPlacedAsync(h, Guid.CreateVersion7(), "TP261123TP01", 600_000L, "cod", 1, clock.UtcNow.UtcDateTime);

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = h.RuntimeConnection }).Build();
        var scopes = h.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var options = h.Factory.Services.GetRequiredService<NotificationOptions>();

        using (var job = new DailyShopDigestJob(scopes, config, clock, clock, options, NullLogger<DailyShopDigestJob>.Instance))
            Assert.True(await job.RunOnceAsync());

        await using (var scope = h.Factory.Services.CreateAsyncScope())
            Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));

        Assert.Equal(2, telegram.Requests.Count);
        await using var db = h.OwnerContext();
        Assert.Equal(2, await db.NotificationLogs.CountAsync(x => x.Status == "sent"));
    }

    [Fact]
    public async Task P205ReservedMismatchTriggersReconciliationTelegramAlertAndDigestCountWithAntiDuplicate()
    {
        var telegram = new FakeTelegramHttpMessageHandler();
        var clock = new ManualNotificationClock(new(2026, 11, 25, 2, 0, 0, TimeSpan.Zero));
        await using var h = await CreateHarnessAsync(clock, telegram, smtp: null);

        // Variant has clean on_hand == ledger (10 == 10), but stock_levels.reserved = 3 vs 0 active rows in inventory.reservations.
        var reservedMismatchVariantId = Guid.CreateVersion7();
        await SeedVariantAsync(h, reservedMismatchVariantId, "AM-RS-0001", lowStockThreshold: 0, onHand: 10, movementQty: 10);
        await h.ExecuteAsync($"UPDATE inventory.stock_levels SET reserved = 3 WHERE variant_id = '{reservedMismatchVariantId}'");

        await ConfigureNotifySettingsAsync(h, "-100222333", []);
        clock.Advance(TimeSpan.FromSeconds(5));
        var freshTime = clock.UtcNow.UtcDateTime;
        var todayVn = BusinessCalendar.ToVietnamDate(clock.UtcNow);

        // Two identical reconciliation_mismatch alerts on same VN day must produce exactly 1 Telegram alert.
        await InsertOutboxAsync(h, InventoryAlert.Message, InventoryAlert.Serialize(new(InventoryAlert.ReconciliationMismatch, reservedMismatchVariantId, todayVn)), freshTime);
        await InsertOutboxAsync(h, InventoryAlert.Message, InventoryAlert.Serialize(new(InventoryAlert.ReconciliationMismatch, reservedMismatchVariantId, todayVn)), freshTime);

        await using (var scope = h.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default);

        var sent = Assert.Single(telegram.Requests);
        Assert.Contains("AM-RS-0001", sent.Text, StringComparison.Ordinal);
        Assert.Contains("Giữ hàng (reserved): 3 / active reservations: 0 (lệch <b>-3</b>)", sent.Text, StringComparison.Ordinal);

        // Digest also counts the reserved-only mismatch via EfDailyDigestRead.
        await using (var scope = h.Factory.Services.CreateAsyncScope())
        {
            var digest = await scope.ServiceProvider.GetRequiredService<IDailyDigestRead>().ReadAsync(clock.UtcNow, default);
            Assert.Equal(1L, digest.ReconciliationMismatchTotal);
        }
    }

    private async Task<P009Harness> CreateHarnessAsync(
        ManualNotificationClock clock,
        FakeTelegramHttpMessageHandler telegramHandler,
        LoopbackSmtpServer? smtp,
        bool configureSecrets = true,
        NotificationLogCollector? logs = null)
    {
        return await P009Harness.CreateAsync(fixture, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.RemoveAll<IClock>();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IClock>(clock);

            if (logs is not null)
                services.AddSingleton<ILoggerProvider>(logs);

            services.RemoveAll<NotificationOptions>();
            services.RemoveAll<TelegramHttpClient>();
            services.RemoveAll<TelegramRateLimiter>();

            var options = new NotificationOptions
            {
                TelegramBotToken = configureSecrets ? FakeBotToken : "",
                TelegramBaseUrl = NotificationOptions.DefaultTelegramBaseUrl,
                SmtpHost = configureSecrets && smtp is not null ? "127.0.0.1" : "",
                SmtpPort = smtp?.Port ?? 587,
                SmtpUsername = configureSecrets && smtp is not null ? "smtp-user" : "",
                SmtpPassword = configureSecrets && smtp is not null ? FakeSmtpPassword : "",
                SmtpFromEmail = configureSecrets && smtp is not null ? "noreply@tea.test" : "",
                SmtpFromName = "Trà Hiên",
                SmtpUseTls = smtp is null,
                AdminBaseUrl = "https://admin.example.test"
            };
            options.Validate("Test");

            services.AddSingleton(options);
            services.AddSingleton(new TelegramHttpClient(telegramHandler, options, disposeHandler: false));
            services.AddSingleton(new TelegramRateLimiter(clock, clock, TimeSpan.Zero));
        });
    }

    private static async Task ConfigureNotifySettingsAsync(P009Harness h, string? telegramChatId, string[] shopEmails)
    {
        await h.LoginAsync();
        var current = (await h.Client.GetFromJsonAsync<SettingsGroup>("/api/v1/admin/settings/notify"))!;
        using var put = await h.MutateAsync(HttpMethod.Put, "/api/v1/admin/settings/notify", new
        {
            version = current.Version,
            values = new Dictionary<string, object?>
            {
                ["notify.telegram_chat_id"] = telegramChatId,
                ["notify.shop_emails"] = shopEmails
            }
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    private static async Task<Guid> SeedOrderAndEnqueueOrderPlacedAsync(
        P009Harness h,
        Guid orderId,
        string code,
        long totalVnd,
        string paymentMethod,
        int itemCount,
        DateTime occurredAtUtc,
        bool isBackfilled = false,
        string? contactName = null,
        string? contactPhone = null,
        string? addressLine = null,
        long? unitCostVnd = null)
    {
        var customerId = Guid.Parse("00000000-0000-0000-0000-000000002131");
        var typeId = Guid.Parse("00000000-0000-0000-0000-000000002132");
        var productId = Guid.Parse("00000000-0000-0000-0000-000000002133");
        var variantId = Guid.Parse("00000000-0000-0000-0000-000000002134");
        await h.ExecuteAsync($$"""
INSERT INTO platform.provinces(code,name) VALUES ('79','TP HCM') ON CONFLICT DO NOTHING;
INSERT INTO platform.wards(code,province_code,name) VALUES ('79001','79','Phuong 1') ON CONFLICT DO NOTHING;
INSERT INTO crm.customers(id,code,full_name,phone_e164,origin,contact_status) VALUES
 ('{{customerId}}','KH002131','Khach Notify','+84900002131','admin','active') ON CONFLICT DO NOTHING;
INSERT INTO catalog.product_types(id,code,name) VALUES ('{{typeId}}','teapot_p213','Ấm') ON CONFLICT DO NOTHING;
INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name) VALUES
 ('{{productId}}','{{typeId}}','AM-NT-2131','am-nt-2131','Ấm Notify') ON CONFLICT DO NOTHING;
INSERT INTO catalog.variants(id,product_id,sku,price_vnd,low_stock_threshold) VALUES
 ('{{variantId}}','{{productId}}','AM-NT-2131',500000,0) ON CONFLICT DO NOTHING;
""");

        var unitPrice = totalVnd / itemCount;
        var lineTotal = unitPrice * itemCount;
        var shippingFee = totalVnd - lineTotal;
        var status = isBackfilled ? "delivered" : "pending";

        await using (var db = h.OwnerContext())
        {
            db.Set<Order>().Add(new Order
            {
                Id = orderId,
                Code = code,
                Origin = "admin",
                SalesChannel = "zalo_personal",
                CustomerId = customerId,
                Status = status,
                PaymentMethod = paymentMethod,
                PaymentStatus = isBackfilled ? "paid" : "unpaid",
                ContactName = contactName ?? "Khach Notify",
                ContactPhone = contactPhone ?? "+84900002131",
                ReceiverName = contactName ?? "Khach Notify",
                ReceiverPhone = contactPhone ?? "+84900002131",
                ProvinceCode = "79",
                ProvinceName = "TP HCM",
                WardCode = "79001",
                WardName = "Phuong 1",
                AddressLine = addressLine ?? "1 Duong Notify",
                SubtotalVnd = lineTotal,
                DiscountVnd = 0L,
                ShippingFeeVnd = shippingFee,
                TotalVnd = totalVnd,
                AcquisitionSource = "direct",
                IsFirstPurchase = true,
                PlacedAt = occurredAtUtc,
                DeliveredAt = isBackfilled ? occurredAtUtc : null,
                IsBackfilled = isBackfilled,
                CreatedAt = occurredAtUtc,
                UpdatedAt = occurredAtUtc
            });
            db.Set<OrderLine>().Add(new OrderLine
            {
                Id = Guid.CreateVersion7(),
                OrderId = orderId,
                VariantId = variantId,
                ProductId = productId,
                Sku = "AM-NT-2131",
                ProductName = "Ấm Notify",
                Quantity = itemCount,
                ListPriceVnd = unitPrice,
                UnitPriceVnd = unitPrice,
                PriceSource = "list",
                LineTotalVnd = lineTotal,
                UnitCostVnd = unitCostVnd
            });
            await db.SaveChangesAsync();
        }

        return await InsertOutboxAsync(
            h,
            NotificationContracts.OrderPlacedMessageType,
            OrderOutboxMessage.Serialize(new OrderOutboxPayload(orderId, code, status, new DateTimeOffset(occurredAtUtc, TimeSpan.Zero))),
            occurredAtUtc);
    }

    private static async Task<Guid> InsertOutboxAsync(
        P009Harness h,
        string type,
        string payload,
        DateTime occurredAtUtc,
        string status = "pending",
        int attempts = 0)
    {
        var id = Guid.CreateVersion7();
        await using var db = h.OwnerContext();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = id,
            Type = type,
            Payload = payload,
            OccurredAt = occurredAtUtc,
            AvailableAt = occurredAtUtc,
            Attempts = attempts,
            Status = status,
            ProcessedAt = status == "processed" ? occurredAtUtc : null,
            LastError = status == "dead" ? "handler_failed" : null
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task SeedVariantAsync(P009Harness h, Guid variantId, string sku, int lowStockThreshold, int onHand, int movementQty)
    {
        var typeId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        await h.ExecuteAsync($$"""
INSERT INTO catalog.product_types(id,code,name) VALUES ('{{typeId}}','type_{{variantId:N}}','Type');
INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name) VALUES
 ('{{productId}}','{{typeId}}','{{sku}}','slug-{{variantId:N}}','Product {{sku}}');
INSERT INTO catalog.variants(id,product_id,sku,price_vnd,low_stock_threshold) VALUES
 ('{{variantId}}','{{productId}}','{{sku}}',100000,{{lowStockThreshold}});
INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved,avg_cost_vnd) VALUES
 ('{{variantId}}',{{onHand}},0,50000);
INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,unit_cost_vnd,on_hand_after,actor_type,occurred_at) VALUES
 ('{{Guid.CreateVersion7()}}','{{variantId}}','opening',{{movementQty}},50000,{{movementQty}},'system',now());
""");
    }

    private static async Task SeedSixDigestMetricsAsync(P009Harness h, DateTime digestTimeUtc)
    {
        var activeCustomerId = Guid.CreateVersion7();
        var erasedCustomerId = Guid.CreateVersion7();
        var staffId = h.OwnerId;
        var deliveredCodOrderId = Guid.CreateVersion7();
        var artisanId = Guid.CreateVersion7();
        var typeId = Guid.CreateVersion7();

        await h.ExecuteAsync($$"""
INSERT INTO platform.provinces(code,name) VALUES ('79','TP HCM') ON CONFLICT DO NOTHING;
INSERT INTO platform.wards(code,province_code,name) VALUES ('79001','79','Phuong 1') ON CONFLICT DO NOTHING;
INSERT INTO crm.customers(id,code,full_name,phone_e164,origin,contact_status) VALUES
 ('{{activeCustomerId}}','KH001001','Khach Active','+84900000011','admin','active'),
 ('{{erasedCustomerId}}','KH001002','[Da xoa]',NULL,'admin','erased');
INSERT INTO catalog.product_types(id,code,name) VALUES ('{{typeId}}','teapot_dig','Ấm');
INSERT INTO catalog.artisans(id,slug,name,status,first_published_at) VALUES ('{{artisanId}}','chu-ban-dig','Chu Bân','published',now());
""");

        // 12 low-stock variants + 1 mismatch variant (with secret artisan name and cost that must never appear in digest).
        for (var i = 1; i <= 12; i++)
        {
            var pid = Guid.CreateVersion7();
            var vid = Guid.CreateVersion7();
            var sku = $"AM-DG-{i:0000}";
            await h.ExecuteAsync($$"""
INSERT INTO catalog.products(id,product_type_id,artisan_id,sku_base,slug,name) VALUES
 ('{{pid}}','{{typeId}}','{{artisanId}}','{{sku}}','am-dg-{{i:0000}}','Ấm Bí Mật {{i}}');
INSERT INTO catalog.variants(id,product_id,sku,price_vnd,low_stock_threshold) VALUES
 ('{{vid}}','{{pid}}','{{sku}}',1000000,5);
INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved,avg_cost_vnd) VALUES
 ('{{vid}}',2,0,777666555);
INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,unit_cost_vnd,on_hand_after,actor_type,occurred_at) VALUES
 ('{{Guid.CreateVersion7()}}','{{vid}}','opening',2,777666555,2,'system',now());
""");
        }
        // 1 healthy variant with reconciliation mismatch (on_hand=10 vs movement=8, threshold=0).
        var misPid = Guid.CreateVersion7();
        var misVid = Guid.CreateVersion7();
        await h.ExecuteAsync($$"""
INSERT INTO catalog.products(id,product_type_id,sku_base,slug,name) VALUES ('{{misPid}}','{{typeId}}','AM-DG-0099','am-dg-0099','Ấm Mismatch');
INSERT INTO catalog.variants(id,product_id,sku,price_vnd,low_stock_threshold) VALUES ('{{misVid}}','{{misPid}}','AM-DG-0099',1000000,0);
INSERT INTO inventory.stock_levels(variant_id,on_hand,reserved,avg_cost_vnd) VALUES ('{{misVid}}',10,0,777666555);
INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,unit_cost_vnd,on_hand_after,actor_type,occurred_at) VALUES
 ('{{Guid.CreateVersion7()}}','{{misVid}}','opening',8,777666555,8,'system',now());
""");

        await using var db = h.OwnerContext();
        Order MakeOrder(Guid id, string code, string status, string method, string payStatus, DateTime? dueAt, bool backfilled = false) => new()
        {
            Id = id,
            Code = code,
            Origin = method == "bank_transfer" && dueAt is not null ? "web" : "admin",
            SalesChannel = method == "bank_transfer" && dueAt is not null ? "web" : "zalo_personal",
            CustomerId = activeCustomerId,
            Status = status,
            PaymentMethod = method,
            PaymentStatus = payStatus,
            PaymentDueAt = dueAt,
            ContactName = "Khach Digest",
            ContactPhone = "+84900000011",
            ReceiverName = "Nguoi Nhan",
            ReceiverPhone = "+84900000012",
            ProvinceCode = "79",
            ProvinceName = "TP HCM",
            WardCode = "79001",
            WardName = "Phuong 1",
            AddressLine = "123 Duong Test",
            SubtotalVnd = 500_000L,
            DiscountVnd = 0L,
            ShippingFeeVnd = 0L,
            TotalVnd = 500_000L,
            AcquisitionSource = "direct",
            IsFirstPurchase = true,
            PlacedAt = digestTimeUtc.AddHours(-2),
            DeliveredAt = status == "delivered" ? digestTimeUtc.AddDays(-1) : null,
            IsBackfilled = backfilled,
            CreatedAt = digestTimeUtc.AddHours(-2),
            UpdatedAt = digestTimeUtc.AddHours(-2)
        };

        // Pending confirmation (expected = 2): COD pending + bank_transfer paid pending.
        db.Set<Order>().Add(MakeOrder(Guid.CreateVersion7(), "TP261123C001", "pending", "cod", "unpaid", null));
        db.Set<Order>().Add(MakeOrder(Guid.CreateVersion7(), "TP261123C002", "pending", "bank_transfer", "paid", digestTimeUtc.AddHours(5)));
        // Excluded from pending confirmation: confirmed, backfilled, bank_transfer partially_paid.
        db.Set<Order>().Add(MakeOrder(Guid.CreateVersion7(), "TP261123C003", "confirmed", "cod", "unpaid", null));
        db.Set<Order>().Add(MakeOrder(Guid.CreateVersion7(), "TP261123C004", "delivered", "cod", "paid", null, backfilled: true));
        db.Set<Order>().Add(MakeOrder(Guid.CreateVersion7(), "TP261123C005", "pending", "bank_transfer", "partially_paid", digestTimeUtc.AddHours(2)));

        // Expiring bank_transfer unpaid within (now, now + 24h] (expected = 2): +1s and +24h exact.
        db.Set<Order>().Add(MakeOrder(Guid.CreateVersion7(), "TP261123E001", "pending", "bank_transfer", "unpaid", digestTimeUtc.AddSeconds(1)));
        db.Set<Order>().Add(MakeOrder(Guid.CreateVersion7(), "TP261123E002", "pending", "bank_transfer", "unpaid", digestTimeUtc.AddHours(24)));
        // Excluded from expiring: due == now, expired (-1m), > 24h (+24h1s), null due.
        db.Set<Order>().Add(MakeOrder(Guid.CreateVersion7(), "TP261123E003", "pending", "bank_transfer", "unpaid", digestTimeUtc));
        db.Set<Order>().Add(MakeOrder(Guid.CreateVersion7(), "TP261123E004", "pending", "bank_transfer", "unpaid", digestTimeUtc.AddMinutes(-1)));
        db.Set<Order>().Add(MakeOrder(Guid.CreateVersion7(), "TP261123E005", "pending", "bank_transfer", "unpaid", digestTimeUtc.AddHours(24).AddSeconds(1)));
        db.Set<Order>().Add(MakeOrder(Guid.CreateVersion7(), "TP261123E006", "pending", "bank_transfer", "unpaid", null));

        // Delivered order for COD payments.
        db.Set<Order>().Add(MakeOrder(deliveredCodOrderId, "TP261123D001", "delivered", "cod", "unpaid", null));

        // Unsettled COD payments (expected = 2, sum = 1_250_000): 2 awaiting_settlement + 1 confirmed + 1 voided.
        db.Set<Payment>().Add(new Payment { Id = Guid.CreateVersion7(), OrderId = deliveredCodOrderId, Method = "cod", Provider = "manual", AmountVnd = 500_000L, Status = "awaiting_settlement", CreatedAt = digestTimeUtc.AddHours(-1) });
        db.Set<Payment>().Add(new Payment { Id = Guid.CreateVersion7(), OrderId = deliveredCodOrderId, Method = "cod", Provider = "manual", AmountVnd = 750_000L, Status = "awaiting_settlement", CreatedAt = digestTimeUtc.AddHours(-1) });
        db.Set<Payment>().Add(new Payment { Id = Guid.CreateVersion7(), OrderId = deliveredCodOrderId, Method = "cod", Provider = "manual", AmountVnd = 300_000L, Status = "confirmed", ConfirmedAt = digestTimeUtc.AddHours(-1), CreatedAt = digestTimeUtc.AddHours(-1) });
        db.Set<Payment>().Add(new Payment { Id = Guid.CreateVersion7(), OrderId = deliveredCodOrderId, Method = "cod", Provider = "manual", AmountVnd = 400_000L, Status = "voided", VoidReason = "dup", CreatedAt = digestTimeUtc.AddHours(-1) });

        // Follow-ups due today VN (expected = 2): overdue yesterday + today 23:59 VN.
        // Excluded: tomorrow 00:00 VN, completed, erased customer.
        var vnToday = new DateOnly(2026, 11, 23);
        var vnTomorrowStart = BusinessCalendar.ToUtc(vnToday.AddDays(1), TimeOnly.MinValue).UtcDateTime;
        db.Set<Interaction>().Add(new Interaction { Id = Guid.CreateVersion7(), CustomerId = activeCustomerId, StaffId = staffId, Channel = "phone", Kind = "follow_up", Content = "Overdue", OccurredAt = digestTimeUtc.AddDays(-2), FollowUpAt = digestTimeUtc.AddDays(-1), CreatedAt = digestTimeUtc.AddDays(-2) });
        db.Set<Interaction>().Add(new Interaction { Id = Guid.CreateVersion7(), CustomerId = activeCustomerId, StaffId = staffId, Channel = "phone", Kind = "follow_up", Content = "Due today", OccurredAt = digestTimeUtc.AddHours(-2), FollowUpAt = vnTomorrowStart.AddMinutes(-1), CreatedAt = digestTimeUtc.AddHours(-2) });
        db.Set<Interaction>().Add(new Interaction { Id = Guid.CreateVersion7(), CustomerId = activeCustomerId, StaffId = staffId, Channel = "phone", Kind = "follow_up", Content = "Tomorrow", OccurredAt = digestTimeUtc.AddHours(-2), FollowUpAt = vnTomorrowStart, CreatedAt = digestTimeUtc.AddHours(-2) });
        db.Set<Interaction>().Add(new Interaction { Id = Guid.CreateVersion7(), CustomerId = activeCustomerId, StaffId = staffId, Channel = "phone", Kind = "follow_up", Content = "Done", OccurredAt = digestTimeUtc.AddDays(-2), FollowUpAt = digestTimeUtc.AddHours(-1), FollowUpDoneAt = digestTimeUtc.AddMinutes(-30), FollowUpResult = "Called", CreatedAt = digestTimeUtc.AddDays(-2) });
        db.Set<Interaction>().Add(new Interaction { Id = Guid.CreateVersion7(), CustomerId = erasedCustomerId, StaffId = staffId, Channel = "phone", Kind = "follow_up", Content = "[Da xoa]", OccurredAt = digestTimeUtc.AddDays(-2), FollowUpAt = digestTimeUtc.AddHours(-1), CreatedAt = digestTimeUtc.AddDays(-2) });

        // Dead outbox rows (expected = 2).
        db.OutboxMessages.Add(new OutboxMessage { Id = Guid.CreateVersion7(), Type = "StorefrontInvalidate", Payload = "{}", OccurredAt = digestTimeUtc.AddHours(-3), AvailableAt = digestTimeUtc.AddHours(-3), Attempts = 10, Status = "dead", LastError = "handler_failed" });
        db.OutboxMessages.Add(new OutboxMessage { Id = Guid.CreateVersion7(), Type = "InventoryAlert", Payload = "{}", OccurredAt = digestTimeUtc.AddHours(-2), AvailableAt = digestTimeUtc.AddHours(-2), Attempts = 10, Status = "dead", LastError = "handler_failed" });

        await db.SaveChangesAsync();
    }

    private static string DecodeBase64Parts(string rawMime)
    {
        var sb = new StringBuilder();
        var lines = rawMime.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inBase64 = false;
        var current = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith("--tp-", StringComparison.Ordinal))
            {
                if (current.Length > 0)
                {
                    try { sb.AppendLine(Encoding.UTF8.GetString(Convert.FromBase64String(current.ToString()))); } catch { }
                    current.Clear();
                }
                inBase64 = false;
                continue;
            }
            if (line.Contains("Content-Transfer-Encoding: base64", StringComparison.OrdinalIgnoreCase))
            {
                inBase64 = true;
                continue;
            }
            if (inBase64 && line.Length > 0 && !line.Contains(':'))
                current.Append(line.Trim());
        }
        if (current.Length > 0)
        {
            try { sb.AppendLine(Encoding.UTF8.GetString(Convert.FromBase64String(current.ToString()))); } catch { }
        }
        return sb.ToString();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(25);
        }
        Assert.True(await predicate(), "Timed out waiting for DailyShopDigest advisory lock.");
    }
}
