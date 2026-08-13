using Humans.Shifts.Contracts;

namespace Humans.Shifts.Models;

/// <summary>
/// Shared per-shift / per-rota mapping used by both the full browse
/// (<c>ShiftsController.Index</c>) and the onboarding widget step-2 view
/// (<see cref="OnboardingWidget.OnboardingShiftsBrowseModelBuilder"/>).
///
/// Pure mapping over <see cref="UrgentShiftInfo"/> — no service dependencies and
/// no arithmetic: the section resolves the absolute window and the period inside
/// the projection, where the event is in scope.
/// </summary>
internal static class ShiftBrowseMapper
{
    /// <summary>
    /// Maps a single <see cref="UrgentShift"/> to a <see cref="ShiftDisplayItem"/>.
    /// </summary>
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

    /// <summary>
    /// Builds a <see cref="RotaShiftGroup"/> from a group of <see cref="UrgentShiftInfo"/>s
    /// sharing a rota. Caller may supply department name/slug (full browse) or omit
    /// them (onboarding widget — no department grouping).
    /// </summary>
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
