using NodaTime;
using NodaTime.Text;

namespace Humans.Rideshare.Models;

/// <summary>ISO date round-trip for <c>&lt;input type="date"&gt;</c> values and query strings.</summary>
internal static class RideshareDates
{
    public static LocalDate? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = LocalDatePattern.Iso.Parse(value.Trim());
        return result.Success ? result.Value : null;
    }

    public static LocalDate Today(IClock clock) => clock.GetCurrentInstant().InUtc().Date;
}
