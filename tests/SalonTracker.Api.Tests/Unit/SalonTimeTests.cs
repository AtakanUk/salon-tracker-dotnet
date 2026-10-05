using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Tests.Unit;

public sealed class SalonTimeTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static DateTime Utc(string iso) => DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    [Theory]
    [InlineData("2026-01-15", "2026-01-14T23:00:00Z")] // winter: UTC+1
    [InlineData("2026-07-15", "2026-07-14T22:00:00Z")] // summer: UTC+2
    // clocks go forward at 02:00 on 29 March 2026; midnight is still winter time
    [InlineData("2026-03-29", "2026-03-28T23:00:00Z")]
    [InlineData("2026-03-30", "2026-03-29T22:00:00Z")]
    // clocks go back at 03:00 on 25 October 2026; midnight is still summer time
    [InlineData("2026-10-25", "2026-10-24T22:00:00Z")]
    [InlineData("2026-10-26", "2026-10-25T23:00:00Z")]
    public void StartOfDay_is_local_midnight_across_daylight_saving_changes(string day, string expectedUtc)
    {
        Assert.Equal(Utc(expectedUtc), SalonTime.StartOfDay(DateOnly.Parse(day), Berlin));
    }

    [Fact]
    public void A_late_evening_UTC_instant_belongs_to_the_next_local_day()
    {
        var lateEvening = Utc("2026-07-14T22:30:00Z"); // 00:30 on the 15th in Berlin
        Assert.Equal(new DateOnly(2026, 7, 15), SalonTime.DayOf(lateEvening, Berlin));
        Assert.Equal(new DateOnly(2026, 7, 14), SalonTime.DayOf(lateEvening, TimeZoneInfo.Utc));
    }

    [Fact]
    public void New_years_eve_after_23_UTC_is_already_January()
    {
        Assert.Equal("2027-01", SalonTime.MonthOf(Utc("2026-12-31T23:30:00Z"), Berlin));
    }

    [Theory]
    [InlineData("2026-10-05T10:00:00Z", "2026-10-05")] // Monday
    [InlineData("2026-10-11T10:00:00Z", "2026-10-05")] // Sunday
    [InlineData("2026-10-11T22:30:00Z", "2026-10-12")] // already Monday in Berlin
    public void Weeks_start_on_Monday(string instant, string monday)
    {
        Assert.Equal(DateOnly.Parse(monday), SalonTime.WeekOf(Utc(instant), Berlin));
    }

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("2026-3-1")]
    [InlineData("01.03.2026")]
    [InlineData("")]
    public void Only_valid_ISO_dates_are_accepted(string value)
    {
        Assert.False(SalonTime.TryParseDay(value, out _));
    }
}
