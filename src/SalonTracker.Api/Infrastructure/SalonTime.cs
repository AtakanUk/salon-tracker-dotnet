using System.Globalization;

namespace SalonTracker.Api.Infrastructure;

/// <summary>
/// The server runs in UTC; statistics are cut at the salon's local midnight.
/// Daylight saving changes are handled by <see cref="TimeZoneInfo"/>, no date library needed.
/// </summary>
public static class SalonTime
{
    /// <summary>The UTC instant at which a local calendar day starts in the zone.</summary>
    public static DateTime StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // zones that jump at midnight have no 00:00 on that day; the day starts at 01:00
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>The local calendar day of a UTC instant.</summary>
    public static DateOnly DayOf(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    /// <summary>Monday of the local week containing the instant.</summary>
    public static DateOnly WeekOf(DateTime utc, TimeZoneInfo zone)
    {
        var day = DayOf(utc, zone);
        var daysFromMonday = ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-daysFromMonday);
    }

    public static string MonthOf(DateTime utc, TimeZoneInfo zone) =>
        DayOf(utc, zone).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static string Format(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Dates travel as YYYY-MM-DD, always meaning the salon's calendar.</summary>
    public static bool TryParseDay(string? value, out DateOnly day) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    public static DateOnly ParseDay(string? value) =>
        TryParseDay(value, out var day) ? day : throw ApiException.Validation();
}
