using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Web.Models.Shifts;

namespace Humans.Web.Models.OnboardingWidget;

/// <summary>
/// Builds the slim <see cref="ShiftBrowseViewModel"/> for the onboarding widget's
/// step-2 view from <see cref="UrgentShift"/> entries.
///
/// Lives outside the controller so the action stays under the
/// no-business-logic-in-controllers ratchet thresholds. The widget shows a
/// simplified urgency-ranked browse — no department grouping, no tag/period
/// filters — so this is a smaller mapping than <c>ShiftsController.Index</c>'s.
/// Per-shift and per-rota mapping is shared with the full browse via
/// <see cref="ShiftBrowseMapper"/>.
/// </summary>
public static class OnboardingShiftsBrowseModelBuilder
{
    public static ShiftBrowseViewModel Build(
        EventSettings eventSettings,
        IReadOnlyList<UrgentShift> urgentShifts,
        HashSet<Guid> userSignupShiftIds,
        Dictionary<Guid, Domain.Enums.SignupStatus> userSignupStatuses)
    {
        var rotaGroups = urgentShifts
            .GroupBy(u => u.Shift.RotaId)
            .Select(rg => ShiftBrowseMapper.BuildRotaGroup(
                rg,
                eventSettings,
                departmentName: rg.First().DepartmentName))
            .OrderByDescending(r => r.MaxUrgencyScore)
            .ToList();

        return new ShiftBrowseViewModel
        {
            EventSettings = eventSettings,
            ShowSignups = true,
            Sort = "urgency",
            UrgencyRankedRotas = rotaGroups,
            UserSignupShiftIds = userSignupShiftIds,
            UserSignupStatuses = userSignupStatuses,
        };
    }
}
