using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;

namespace Humans.Web.Models.Shifts;

/// <summary>
/// Shared per-shift / per-rota mapping used by both the full browse
/// (<c>ShiftsController.Index</c>) and the onboarding widget step-2 view
/// (<see cref="OnboardingWidget.OnboardingShiftsBrowseModelBuilder"/>).
///
/// Pure mapping over <see cref="UrgentShift"/> + <see cref="EventSettings"/> —
/// no service dependencies. Time/period resolution delegates to the
/// <see cref="Shift"/> entity helpers (<see cref="Shift.GetAbsoluteStart"/>,
/// <see cref="Shift.GetAbsoluteEnd"/>, <see cref="Shift.GetShiftPeriod"/>).
/// </summary>
internal static class ShiftBrowseMapper
{
    /// <summary>
    /// Maps a single <see cref="UrgentShift"/> to a <see cref="ShiftDisplayItem"/>.
    /// </summary>
    internal static ShiftDisplayItem MapToDisplayItem(UrgentShift u, EventSettings eventSettings)
    {
        return new ShiftDisplayItem
        {
            Shift = u.Shift,
            AbsoluteStart = u.Shift.GetAbsoluteStart(eventSettings),
            AbsoluteEnd = u.Shift.GetAbsoluteEnd(eventSettings),
            Period = u.Shift.GetShiftPeriod(eventSettings),
            ConfirmedCount = u.ConfirmedCount,
            RemainingSlots = u.RemainingSlots,
            UrgencyScore = u.UrgencyScore,
            Signups = u.Signups
                .Select(s => new ShiftSignupInfo(s.UserId, s.DisplayName, s.Status))
                .ToList(),
        };
    }

    /// <summary>
    /// Builds a <see cref="RotaShiftGroup"/> from a group of <see cref="UrgentShift"/>s
    /// sharing a rota. Caller may supply department name/slug (full browse) or omit
    /// them (onboarding widget — no department grouping).
    /// </summary>
    internal static RotaShiftGroup BuildRotaGroup(
        IGrouping<Guid, UrgentShift> rotaGroup,
        EventSettings eventSettings,
        string? departmentName = null,
        string? departmentSlug = null)
    {
        var rota = rotaGroup.OrderBy(x => x.Shift.Id).First().Shift.Rota;
        var shifts = rotaGroup
            .Select(u => MapToDisplayItem(u, eventSettings))
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
