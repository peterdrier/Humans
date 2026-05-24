using System.Globalization;
using NodaTime;

namespace Humans.Application.Extensions;

public static class DateFormattingExtensions
{
    /// <summary>
    /// Formats a date as "Wed Jul 1". Mirrors the Web-layer ToDisplayShiftDate() extension.
    /// </summary>
    public static string ToDisplayShiftDate(this LocalDate date) =>
        date.DayOfWeek.ToString()[..3] + " " + date.ToString("MMM d", null);

    public static string ToIsoDateString(this LocalDate value) =>
        value.ToString("yyyy-MM-dd", null);

    public static string? ToIsoDateString(this LocalDate? value) =>
        value?.ToIsoDateString();

    public static string ToIsoDateString(this DateTime value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? ToIsoDateString(this DateTime? value) =>
        value?.ToIsoDateString();

    public static string ToInvariantInstantString(this Instant value) =>
        value.ToString(null, CultureInfo.InvariantCulture);

    public static string? ToInvariantInstantString(this Instant? value) =>
        value?.ToInvariantInstantString();
}
