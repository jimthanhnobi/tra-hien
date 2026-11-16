namespace TeaPremium.Application.Time;

public static class BusinessCalendar
{
    public static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static DateOnly ToVietnamDate(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(VietnamOffset).DateTime);

    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, VietnamOffset).ToUniversalTime();
    }

    public static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) ReportRange(DateOnly from, DateOnly to)
    {
        if (to <= from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), "Ngày kết thúc phải sau ngày bắt đầu.");
        }

        return (ToUtc(from, TimeOnly.MinValue), ToUtc(to, TimeOnly.MinValue));
    }
}
