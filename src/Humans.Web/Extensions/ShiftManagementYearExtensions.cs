using Humans.Application.Interfaces.Shifts;
using NodaTime;

namespace Humans.Web.Extensions;

public static class ShiftManagementYearExtensions
{
    /// <summary>
    /// Returns the active event's year when one is set; otherwise falls back to the current UTC year.
    /// Web-layer helper so controllers needing a "year for catalog/orders" don't repeat the
    /// same active-event-or-clock dance.
    /// </summary>
    public static async Task<int> GetActiveYearOrCurrentAsync(this IShiftManagementService shifts, IClock clock)
    {
        var activeEvent = await shifts.GetActiveAsync();
        return activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
    }
}
