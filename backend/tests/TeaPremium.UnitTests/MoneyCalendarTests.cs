using TeaPremium.Application.Time;
using TeaPremium.Domain;

namespace TeaPremium.UnitTests;

public sealed class MoneyCalendarTests
{
    [Fact]
    // DEC-20
    public void MoneyStoresWholeVnd()
    {
        Assert.Equal(123_456, new Money(123_456).Vnd);
        Assert.Equal(0, new Money(0).Vnd);
    }

    [Fact]
    public void NegativeMoneyRequiresExplicitAdjustment()
    {
        var error = Assert.Throws<DomainException>(() => new Money(-1));
        Assert.Equal("negative_money", error.Code);
        Assert.Equal(-1, Money.Adjustment(-1).Vnd);
    }

    [Fact]
    public void MoneyAdditionAndSubtractionAreChecked()
    {
        Assert.Equal(7, (new Money(10) - new Money(3)).Vnd);
        Assert.Throws<DomainException>(() => new Money(3) - new Money(10));
        Assert.Throws<OverflowException>(() => new Money(long.MaxValue) + new Money(1));
        Assert.Throws<OverflowException>(() => Money.Adjustment(long.MinValue) - new Money(1));
    }

    [Fact]
    public void VietnamDayBoundaryAndHalfOpenRangeUseUtc()
    {
        var from = new DateOnly(2026, 11, 16);
        var to = from.AddDays(1);
        var range = BusinessCalendar.ReportRange(from, to);

        Assert.Equal(new DateTimeOffset(2026, 11, 15, 17, 0, 0, TimeSpan.Zero), range.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 17, 0, 0, TimeSpan.Zero), range.ToUtc);
        Assert.Equal(new DateOnly(2026, 11, 15), BusinessCalendar.ToVietnamDate(range.FromUtc.AddTicks(-1)));
        Assert.Equal(from, BusinessCalendar.ToVietnamDate(range.FromUtc));
        Assert.Equal(to, BusinessCalendar.ToVietnamDate(range.ToUtc));
    }

    [Fact]
    public void InvalidReportRangeIsRejected()
    {
        var day = new DateOnly(2026, 11, 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => BusinessCalendar.ReportRange(day, day));
        Assert.Throws<ArgumentOutOfRangeException>(() => BusinessCalendar.ReportRange(day, day.AddDays(-1)));
    }
}
