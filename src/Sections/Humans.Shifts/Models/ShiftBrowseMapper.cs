using Humans.Shifts.Contracts;

namespace Humans.Shifts.Models;

/// <summary>
/// Shared by the browse page and the onboarding widget. Pure: the projection already resolved the window and period.
/// </summary>
internal static class ShiftBrowseMapper
{
    internal static ShiftDisplayItem MapToDisplayItem(UrgentShiftInfo u)
    {
        return new ShiftDisplayItem
        {
            Shift = u.Shift,
            AbsoluteStart = u.AbsoluteStart,
            AbsoluteEnd = u.AbsoluteEnd,
            Period = u.Period,
            ConfirmedCount = u.ConfirmedCount,
            RemainingSlots = u.RemainingSlots,
            UrgencyScore = u.UrgencyScore,
            Signups = u.Signups,
        };
    }

    /// <summary>The onboarding widget omits the department, as it has no department grouping.</summary>
    internal static RotaShiftGroup BuildRotaGroup(
        IGrouping<Guid, UrgentShiftInfo> rotaGroup,
        string? departmentName = null,
        string? departmentSlug = null)
    {
        var rota = rotaGroup.OrderBy(x => x.Shift.Id).First().Rota;
        var shifts = rotaGroup
            .Select(MapToDisplayItem)
            .OrderBy(s => s.AbsoluteStart)
            .ToList();
        return new RotaShiftGroup
        {
            Rota = rota,
            Shifts = shifts,
            DepartmentName = departmentName,
            DepartmentSlug = departmentSlug,
            MaxUrgencyScore = shifts.Count > 0 ? shifts.Max(s => s.UrgencyScore) : 0,
            TotalConfirmed = shifts.Sum(s => s.ConfirmedCount),
            TotalSlots = shifts.Sum(s => s.Shift.MaxVolunteers),
        };
    }
}
