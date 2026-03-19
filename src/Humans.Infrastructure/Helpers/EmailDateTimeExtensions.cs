using System.Globalization;

namespace Humans.Infrastructure.Helpers;

public static class EmailDateTimeExtensions
{
    public static string ToInvariantLongDate(this DateTime value) =>
        value.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
}
