using System.Globalization;
using AwesomeAssertions;
using Humans.Base.Extensions;
using NodaTime;

namespace Humans.Base.Tests.Extensions;

public sealed class DateFormattingExtensionsTests
{
    [HumansTheory]
    [Xunit.InlineData("en", false)]
    [Xunit.InlineData("es", true)]
    [Xunit.InlineData("de", true)]
    [Xunit.InlineData("it", true)]
    [Xunit.InlineData("fr", true)]
    [Xunit.InlineData("ca", true)]
    public void Display_uses_ui_culture_without_changing_parsing_or_machine_formats(
        string culture, bool dayFirst)
    {
        using var scope = new CultureScope(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        var ui = CultureInfo.CurrentUICulture;
        var date = new DateTime(2026, 6, 5, 12, 34, 56, DateTimeKind.Unspecified);
        var localDate = new LocalDate(2026, 6, 5);
        var localDateTime = localDate.At(new LocalTime(12, 34, 56));
        var instant = Instant.FromUtc(2026, 6, 5, 10, 34, 56);
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var month = ui.DateTimeFormat.AbbreviatedMonthGenitiveNames[5];
        var dayMonth = dayFirst ? $"5 {month}" : $"{month} 5";
        var fullDate = dayFirst ? $"{dayMonth} 2026" : $"{dayMonth}, 2026";
        var weekdayDate = $"{ui.DateTimeFormat.GetAbbreviatedDayName(DayOfWeek.Friday)} {dayMonth}";

        date.ToDate().Should().Be(fullDate);
        localDate.ToDate().Should().Be(fullDate);
        instant.ToDate(zone).Should().Be(fullDate);
        date.ToWeekdayDayMonth().Should().Be(weekdayDate);
        localDate.ToWeekdayDayMonth().Should().Be(weekdayDate);
        instant.ToWeekdayDayMonth(zone).Should().Be(weekdayDate);
        date.ToMonthDayTime().Should().Be($"{dayMonth} @ 12:34");
        localDateTime.ToMonthDayTime().Should().Be($"{dayMonth} @ 12:34");
        instant.ToMonthDayTime(zone).Should().Be($"{dayMonth} @ 12:34");
        date.ToDateTime().Should().Be($"{fullDate} 12:34");
        localDateTime.ToDateTime().Should().Be($"{fullDate} 12:34");
        instant.ToDateTime(zone).Should().Be($"{fullDate} 12:34");
        date.ToMonthYear().Should().Be($"{ui.DateTimeFormat.GetAbbreviatedMonthName(6)} 2026");
        localDate.ToMonthAbbrev().Should().Be(ui.DateTimeFormat.GetAbbreviatedMonthName(6));
        date.ToMonthName().Should().Be(ui.DateTimeFormat.GetMonthName(6));
        date.ToTime().Should().Be("12:34");
        localDateTime.TimeOfDay.ToTime().Should().Be("12:34");
        instant.ToTime(zone).Should().Be("12:34");
        new DateTimeOffset(date, TimeSpan.Zero).ToTimeWithSeconds().Should().Be("12:34:56");

        date.ToInvariantDate().Should().Be("2026-06-05");
        localDate.ToInvariantDate().Should().Be("2026-06-05");
        date.ToInvariantTimestamp().Should().Be("2026-06-05 12:34:56");
        date.ToInvariantTime().Should().Be("12:34");
        date.ToInvariantLongDate().Should().Be("5 June 2026");
        date.ToSepaDateTime().Should().Be("2026-06-05T12:34:56");
        date.ToFileTimestamp().Should().Be("2026-06-05-1234");
        instant.ToIso8601().Should().Be("2026-06-05T10:34:56Z");
        CultureInfo.CurrentCulture.Name.Should().Be("en");
        CultureInfo.CurrentUICulture.Name.Should().Be(culture);
        60m.ToString("0.00", CultureInfo.CurrentCulture).Should().Be("60.00");
    }
}
