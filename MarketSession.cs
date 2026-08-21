namespace get_assessment_no_graph;

public static class MarketSession
{
    private static readonly TimeZoneInfo NyTz =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York"
        );

    public static DateTime GetSessionOpenUtcForDay(DateTime dayAnchorUtc)
    {
        // Normalize Kind to avoid ConvertTimeFromUtc throwing on Unspecified/Local
        if (dayAnchorUtc.Kind == DateTimeKind.Unspecified)
            dayAnchorUtc = DateTime.SpecifyKind(dayAnchorUtc, DateTimeKind.Utc);
        else if (dayAnchorUtc.Kind == DateTimeKind.Local)
            dayAnchorUtc = dayAnchorUtc.ToUniversalTime();

        var ny = TimeZoneInfo.ConvertTimeFromUtc(dayAnchorUtc.Date.AddHours(12), NyTz); // noon NY that date
        var sessionOpenNy = new DateTime(ny.Year, ny.Month, ny.Day, 9, 30, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(sessionOpenNy, NyTz);
    }

    public static DateTime GetSessionCloseUtcForDay(DateTime dayAnchorUtc)
    {
        var ny = TimeZoneInfo.ConvertTimeFromUtc(dayAnchorUtc.Date.AddHours(12), NyTz);
        var sessionCloseNy = new DateTime(ny.Year, ny.Month, ny.Day, 16, 0, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(sessionCloseNy, NyTz);
    }
    public static DateTime GetSessionDateNy(DateTime utcNow)
    {
        var nyNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, NyTz);
        return new DateTime(nyNow.Year, nyNow.Month, nyNow.Day);
    }
}