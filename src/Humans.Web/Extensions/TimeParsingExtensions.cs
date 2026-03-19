using System.Globalization;
using NodaTime;

namespace Humans.Web.Extensions;

public static class TimeParsingExtensions
{
    public static bool TryParseInvariantTimeOnly(this string value, out TimeOnly parsedTime) =>
        TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out parsedTime);

    public static bool TryParseInvariantLocalTime(this string value, out LocalTime localTime)
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
