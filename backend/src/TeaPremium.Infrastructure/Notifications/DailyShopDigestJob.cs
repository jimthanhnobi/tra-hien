using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeaPremium.Application.Notifications;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Jobs;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Notifications;

// BR-PLT-03 / BR-INV-11: daily operational Telegram digest at/after 08:00 Asia/Ho_Chi_Minh,
// once per Vietnam calendar date under PeriodicJob session advisory lock and JobExecutionGuard.
// Evaluates current DB state only; never backfills past days or replays stale P1 backlog.
public sealed class DailyShopDigestJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    IClock clock,
    NotificationOptions options,
    ILogger<DailyShopDigestJob> logger)
    : PeriodicJob(JobName, PollInterval, scopeFactory, configuration, timeProvider, logger)
{
    public const string JobName = "DailyShopDigest";
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeOnly ScheduledVietnamTime = NotificationContracts.DailyDigestScheduledVietnamTime;

    private readonly Lock _gate = new();
    private DateOnly? _lastCompletedVietnamDate;
    private DateOnly? _pendingVietnamDate;

    protected override async Task ExecuteJobAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        _pendingVietnamDate = null;
        var now = clock.UtcNow;
        var vietnamDate = BusinessCalendar.ToVietnamDate(now);
        if (now < BusinessCalendar.ToUtc(vietnamDate, ScheduledVietnamTime))
            return;

        lock (_gate)
        {
            if (_lastCompletedVietnamDate == vietnamDate)
                return;
        }

        if (!options.IsTelegramConfigured)
            return;

        var telegram = await NotificationSettingsReader.ReadTelegramAsync(db, cancellationToken);
        if (!telegram.IsEnabled)
            return;

        var digestRefId = NotificationContracts.DailyDigestRefId(vietnamDate);
        var alreadySent = await db.NotificationLogs.AsNoTracking()
            .AnyAsync(x => x.RefType == NotificationRefTypes.DailyDigest
                        && x.RefId == digestRefId
                        && x.Channel == NotificationChannels.Telegram
                        && x.Template == NotificationTemplateNames.DailyDigestShop
                        && x.Status == "sent", cancellationToken);
        if (alreadySent)
        {
            _pendingVietnamDate = vietnamDate;
            return;
        }

        var dayStartUtc = BusinessCalendar.ToUtc(vietnamDate, TimeOnly.MinValue).UtcDateTime;
        var nextDayStartUtc = BusinessCalendar.ToUtc(vietnamDate.AddDays(1), TimeOnly.MinValue).UtcDateTime;
        var todaysPayloads = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.Type == NotificationContracts.DeliveryMessageType
                     && x.OccurredAt >= dayStartUtc
                     && x.OccurredAt < nextDayStartUtc)
            .Select(x => x.Payload)
            .ToListAsync(cancellationToken);

        if (todaysPayloads.Any(p => NotificationDeliveryPayload.TryParse(p, out var existing)
                && existing.Channel == NotificationChannels.Telegram
                && existing.Template == NotificationTemplateNames.DailyDigestShop
                && existing.RefType == NotificationRefTypes.DailyDigest
                && existing.RefId == digestRefId))
        {
            _pendingVietnamDate = vietnamDate;
            return;
        }

        var snapshot = await EfDailyDigestRead.ReadFromContextAsync(db, now, cancellationToken);
        var html = NotificationTemplates.RenderDailyDigestShop(snapshot, options.AdminBaseUrl);
        var recipientHash = NotificationContracts.HashRecipient(telegram.ChatId!);

        var delivery = new NotificationDeliveryPayload(
            NotificationChannels.Telegram,
            NotificationTemplateNames.DailyDigestShop,
            NotificationRefTypes.DailyDigest,
            digestRefId,
            recipientHash,
            Subject: null,
            TextBody: html,
            HtmlBody: html);

        var occurredAt = now.UtcDateTime;
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = NotificationContracts.DeliveryMessageType,
            Payload = NotificationDeliveryPayload.Serialize(delivery),
            OccurredAt = occurredAt,
            AvailableAt = occurredAt,
            Attempts = 0,
            Status = "pending"
        });

        _pendingVietnamDate = vietnamDate;
    }

    protected override void OnJobCommitted()
    {
        if (_pendingVietnamDate is { } completed)
        {
            lock (_gate) _lastCompletedVietnamDate = completed;
            _pendingVietnamDate = null;
        }
    }
}
