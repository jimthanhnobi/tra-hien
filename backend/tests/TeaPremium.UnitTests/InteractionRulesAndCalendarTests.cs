using TeaPremium.Application.Crm;
using TeaPremium.Domain.Crm;

namespace TeaPremium.UnitTests;

public sealed class InteractionRulesAndCalendarTests
{
    [Theory]
    [InlineData(0, true, null, null)]
    [InlineData(-1, true, null, null)]
    [InlineData(-86_400_000_0000L, true, null, null)] // backdated 1 day
    [InlineData(1, false, "occurredAt", "occurred_at_in_future")] // strict: +1 tick rejected (zero positive clock skew)
    [InlineData(10_000_000L, false, "occurredAt", "occurred_at_in_future")] // +1 second rejected
    public void BrCrm06IsValidScheduleAcceptsBackdatedAndCurrentOccurredAtAndRejectsFutureTimestamp(
        long offsetTicksFromNow,
        bool expectedValid,
        string? expectedField,
        string? expectedCode)
    {
        var nowUtc = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
        var occurredAtUtc = nowUtc.AddTicks(offsetTicksFromNow);

        var ok = InteractionRules.IsValidSchedule(occurredAtUtc, null, nowUtc, out var field, out var code);
        Assert.Equal(expectedValid, ok);
        Assert.Equal(expectedField, field);
        Assert.Equal(expectedCode, code);
    }

    [Theory]
    [InlineData(null, true, null, null)]
    [InlineData(1L, true, null, null)] // followUpAt strictly > occurredAt even when followUpAt <= now (backdated overdue)
    [InlineData(3_600_000_0000L, true, null, null)] // followUpAt > now
    [InlineData(0L, false, "followUpAt", "follow_up_before_or_equal_occurred_at")] // equal -> invalid
    [InlineData(-1L, false, "followUpAt", "follow_up_before_or_equal_occurred_at")] // earlier -> invalid
    public void BrCrm06IsValidScheduleRequiresFollowUpStrictlyAfterOccurredAt(
        long? followUpOffsetTicksFromOccurred,
        bool expectedValid,
        string? expectedField,
        string? expectedCode)
    {
        var nowUtc = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
        var occurredAtUtc = nowUtc.AddHours(-2);
        DateTime? followUpAtUtc = followUpOffsetTicksFromOccurred is { } ticks ? occurredAtUtc.AddTicks(ticks) : null;

        var ok = InteractionRules.IsValidSchedule(occurredAtUtc, followUpAtUtc, nowUtc, out var field, out var code);
        Assert.Equal(expectedValid, ok);
        Assert.Equal(expectedField, field);
        Assert.Equal(expectedCode, code);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   \t\r\n  ", false)]
    [InlineData("a", true)]
    [InlineData("  a  ", true)]
    [InlineData("Nội dung\0chứa NUL", false)]
    public void BrCrm06ContentAndFollowUpResultRuneBoundsMatchPostgresCharacterLength(string? raw, bool expected)
    {
        Assert.Equal(expected, InteractionRules.IsValidText(raw));

        // 4000 ASCII characters -> valid; 4001 -> invalid
        Assert.True(InteractionRules.IsValidText(new string('a', 4000)));
        Assert.False(InteractionRules.IsValidText(new string('a', 4001)));

        // 4000 emoji (surrogate pairs: 8000 UTF-16 chars = 4000 Unicode scalar Runes) -> valid, matching PG length(text) = 4000
        var emojis4000 = string.Concat(Enumerable.Repeat("🍵", 4000));
        Assert.Equal(8000, emojis4000.Length);
        Assert.True(InteractionRules.IsValidText(emojis4000));

        // 4001 emoji -> invalid
        var emojis4001 = emojis4000 + "🍵";
        Assert.False(InteractionRules.IsValidText(emojis4001));

        // Unpaired UTF-16 high surrogate -> rejected
        Assert.False(InteractionRules.IsValidText("text " + '\uD83C'));
    }

    [Theory]
    [InlineData("inquiry", true)]
    [InlineData("consultation", true)]
    [InlineData("quote", true)]
    [InlineData("follow_up", true)]
    [InlineData("complaint", true)]
    [InlineData("campaign_message", true)]
    [InlineData("note", false)]
    public void BrCrm07LastContactedRulesExcludeNoteAndTakeGreatestTimestamp(string kind, bool expectedCounts)
    {
        Assert.Equal(expectedCounts, InteractionRules.CountsTowardLastContacted(kind));

        var t1 = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(t2, InteractionRules.MaxLastContacted(null, t2));
        Assert.Equal(t2, InteractionRules.MaxLastContacted(t2, t1)); // backdated does not move backwards
        Assert.Equal(t2, InteractionRules.MaxLastContacted(t1, t2)); // newer advances
    }

    [Theory]
    // 2026-11-25T16:59:59Z == 2026-11-25 23:59:59 +07:00 (still Nov 25 VN) -> end of today is Nov 26 00:00 VN == 2026-11-25T17:00:00Z
    [InlineData("2026-11-25T16:59:59Z", "today", "2026-11-25T17:00:00Z")]
    [InlineData("2026-11-25T16:59:59Z", null, "2026-11-25T17:00:00Z")]
    [InlineData("2026-11-25T16:59:59Z", "overdue", "2026-11-25T16:59:59Z")]
    // 2026-11-25T17:00:00Z == 2026-11-26 00:00:00 +07:00 (rolled over to Nov 26 VN) -> end of today is Nov 27 00:00 VN == 2026-11-26T17:00:00Z
    [InlineData("2026-11-25T17:00:00Z", "today", "2026-11-26T17:00:00Z")]
    [InlineData("2026-11-25T17:00:00Z", "overdue", "2026-11-25T17:00:00Z")]
    public void BrCrm06FollowUpWindowTodayAndOverdueRespectAsiaHoChiMinhDayRolloverAt1700Utc(
        string nowIso,
        string? due,
        string expectedCutoffIso)
    {
        var now = DateTimeOffset.Parse(nowIso, System.Globalization.CultureInfo.InvariantCulture);
        var expectedCutoff = DateTime.Parse(expectedCutoffIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

        var window = FollowUpWindowCalculator.Compute(due, now);

        Assert.Equal(due ?? "today", window.DueMode);
        Assert.Equal(now.UtcDateTime, window.NowUtc);
        Assert.Equal(expectedCutoff, window.CutoffExclusiveUtc);
    }

    [Theory]
    // Year-end rollover: 2026-12-28 08:00 VN (2026-12-28T01:00:00Z) + 7 VN days = 2027-01-04 00:00 VN = 2027-01-03T17:00:00Z
    [InlineData("2026-12-28T01:00:00Z", "2027-01-03T17:00:00Z")]
    // Month-end rollover at 23:59 VN on Nov 25 -> vnToday=2026-11-25 -> +7 days = 2026-12-02 00:00 VN = 2026-12-01T17:00:00Z
    [InlineData("2026-11-25T16:59:59Z", "2026-12-01T17:00:00Z")]
    // Exact rollover 00:00 VN on Nov 26 -> vnToday=2026-11-26 -> +7 days = 2026-12-03 00:00 VN = 2026-12-02T17:00:00Z
    [InlineData("2026-11-25T17:00:00Z", "2026-12-02T17:00:00Z")]
    // Leap year February rollover: 2028-02-25 12:00 VN -> +7 days = 2028-03-03 00:00 VN = 2028-03-02T17:00:00Z
    [InlineData("2028-02-25T05:00:00Z", "2028-03-02T17:00:00Z")]
    public void FollowUpWindowWeekComputesSevenVietnamDaysBoundaryAcrossMonthAndYearEnd(string nowIso, string expectedCutoffIso)
    {
        var now = DateTimeOffset.Parse(nowIso, System.Globalization.CultureInfo.InvariantCulture);
        var expectedCutoff = DateTime.Parse(expectedCutoffIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

        var window = FollowUpWindowCalculator.Compute("week", now);

        Assert.Equal("week", window.DueMode);
        Assert.Equal(expectedCutoff, window.CutoffExclusiveUtc);
    }

    [Theory]
    [InlineData("Tue, 24 Nov 2026 10:00:00 +07:00", false)]
    [InlineData("Thu, 26 Nov 2026 10:00:00 +07:00", false)]
    [InlineData("2026-11-25 10:00:00Z", false)]
    [InlineData("2026-11-25T10:00:00", false)]
    [InlineData("2026-11-25", false)]
    [InlineData("2026-11-25T10:00:00+0700", false)]
    [InlineData("2026-11-25T10:00:00Z\n", false)]
    [InlineData("2026-02-30T10:00:00Z", false)]
    [InlineData("2026-11-25T10:00:00+25:00", false)]
    [InlineData("2026-11-25T09:00:00Z", true)]
    [InlineData("2026-11-25T09:00:00z", true)]
    [InlineData("2026-11-25T16:00:00+07:00", true)]
    [InlineData("2026-11-25T09:00:00.0000001Z", true)]
    public void InteractionTimestampParserRejectsRfc2822SpaceSeparatorAndMissingTimezoneOffset(string raw, bool expectedValid)
    {
        var ok = InteractionTimestampParser.TryParseUtc(raw, out var utc);
        Assert.Equal(expectedValid, ok);
        if (expectedValid)
        {
            Assert.Equal(DateTimeKind.Utc, utc.Kind);
        }
    }

    [Fact]
    public void BrCrm06TimestampMicrosecondNormalizationAlignsFollowUpInvariantWhileKeepingStrictFutureTickCheck()
    {
        // Same microsecond (.0000001Z vs .0000002Z) -> truncates to identical microsecond -> followUp <= occurredAt rejected
        Assert.True(InteractionTimestampParser.TryParseUtc("2026-11-25T09:00:00.0000001Z", out var occ1Raw));
        Assert.True(InteractionTimestampParser.TryParseUtc("2026-11-25T09:00:00.0000002Z", out var fu1Raw));
        var occ1 = InteractionTimestampParser.TruncateToMicroseconds(occ1Raw);
        var fu1 = InteractionTimestampParser.TruncateToMicroseconds(fu1Raw);
        Assert.Equal(occ1, fu1);
        Assert.Equal(0L, occ1.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.False(InteractionRules.IsFollowUpAfterOccurred(occ1, fu1));

        // Adjacent microseconds (.0000009Z vs .0000010Z) -> truncates to .000000Z < .000001Z -> accepted
        Assert.True(InteractionTimestampParser.TryParseUtc("2026-11-25T09:00:00.0000009Z", out var occ2Raw));
        Assert.True(InteractionTimestampParser.TryParseUtc("2026-11-25T09:00:00.0000010Z", out var fu2Raw));
        var occ2 = InteractionTimestampParser.TruncateToMicroseconds(occ2Raw);
        var fu2 = InteractionTimestampParser.TruncateToMicroseconds(fu2Raw);
        Assert.Equal(new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc), occ2);
        Assert.Equal(new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc).AddMicroseconds(1), fu2);
        Assert.True(InteractionRules.IsFollowUpAfterOccurred(occ2, fu2));

        // Strict future check at full tick precision still rejects rawOccurredAt == rawNow + 1 tick
        var rawNow = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc).AddTicks(5);
        var futureByOneTick = rawNow.AddTicks(1);
        Assert.False(InteractionRules.IsOccurredAtValid(futureByOneTick, rawNow));
        Assert.True(InteractionRules.IsOccurredAtValid(rawNow, rawNow));
    }
}
