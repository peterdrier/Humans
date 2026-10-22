using NodaTime;
using NodaTime.Text;

namespace Humans.Rideshare.Models;

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
