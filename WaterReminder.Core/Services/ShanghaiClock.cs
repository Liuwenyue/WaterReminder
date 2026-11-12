namespace WaterReminder.Services;

/// <summary>
/// Wall clock for reminder decisions. The host zone is ignored: a UTC cloud VM
/// still schedules 08:30 and 17:30 as Asia/Shanghai.
/// </summary>
public static class ShanghaiClock
{
    public const string Id = "Asia/Shanghai";

    public static readonly TimeZoneInfo Zone = Resolve();

    public static DateTime Now() => FromUtc(DateTime.UtcNow);

    public static DateTime FromUtc(DateTime utc)
    {
        var instant = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(instant, Zone);
    }

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { Id, "China Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        throw new InvalidOperationException(
            "Asia/Shanghai time zone data is missing. Install tzdata / libicu on the host.");
    }
}
