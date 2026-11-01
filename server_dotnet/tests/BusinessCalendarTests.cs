using System.Globalization;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

/// <summary>
/// Pins the business calendar (China Standard Time, UTC+8, no daylight saving) with fixed instants and
/// hard-coded expected dates, so the day rollover at 16:00 UTC cannot drift unnoticed.
/// </summary>
public sealed class BusinessCalendarTests
{
    [Theory]
    [InlineData("2026-03-15T00:00:00Z", "2026-03-15")]
    [InlineData("2026-03-15T15:59:59Z", "2026-03-15")]
    [InlineData("2026-03-15T16:00:00Z", "2026-03-16")]
    [InlineData("2026-03-15T16:00:01Z", "2026-03-16")]
    [InlineData("2026-03-15T23:59:59Z", "2026-03-16")]
    [InlineData("2026-01-31T15:59:59Z", "2026-01-31")]
    [InlineData("2026-01-31T16:00:00Z", "2026-02-01")]
    [InlineData("2026-12-31T15:59:59Z", "2026-12-31")]
    [InlineData("2026-12-31T16:00:00Z", "2027-01-01")]
    [InlineData("2028-02-28T15:59:59Z", "2028-02-28")]
    [InlineData("2028-02-28T16:00:00Z", "2028-02-29")]
    [InlineData("2028-02-29T16:00:00Z", "2028-03-01")]
    [InlineData("2027-02-28T16:00:00Z", "2027-03-01")]
    public void TodayRollsOverAtSixteenHundredUtc(string utc, string expected)
    {
        var instant = DateTime.Parse(
            utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        Assert.Equal(DateTimeKind.Utc, instant.Kind);
        Assert.Equal(DateOnly.Parse(expected, CultureInfo.InvariantCulture), BusinessCalendar.Today(instant));
    }

    [Fact]
    public void TodayDoesNotDependOnTheMachineTimeZone()
    {
        // The same instant is the same business day whatever Kind the caller labels it with.
        var utc = new DateTime(2026, 6, 30, 16, 0, 0, DateTimeKind.Utc);
        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        Assert.Equal(new DateOnly(2026, 7, 1), BusinessCalendar.Today(utc));
        Assert.Equal(new DateOnly(2026, 7, 1), BusinessCalendar.Today(unspecified));
    }
}
