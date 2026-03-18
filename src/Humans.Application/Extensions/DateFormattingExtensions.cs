using System.Globalization;
using NodaTime;

namespace Humans.Application.Extensions;

public static class DateFormattingExtensions
{
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
