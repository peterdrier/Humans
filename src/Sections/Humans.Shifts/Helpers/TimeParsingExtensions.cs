using System.Globalization;
using NodaTime;

namespace Humans.Shifts.Helpers;

internal static class TimeParsingExtensions
{
    internal static bool TryParseInvariantTimeOnly(this string value, out TimeOnly parsedTime) =>
        TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out parsedTime);

    internal static bool TryParseInvariantLocalTime(this string value, out LocalTime localTime)
    {
        if (!value.TryParseInvariantTimeOnly(out var parsedTime))
        {
            localTime = default;
            return false;
        }

        localTime = new LocalTime(parsedTime.Hour, parsedTime.Minute);
        return true;
    }
}
