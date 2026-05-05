using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Web.Models;

namespace Humans.Web.Models.OnboardingWidget;

/// <summary>
/// Builds the slim <see cref="ShiftBrowseViewModel"/> for the onboarding widget's
/// step-2 view from <see cref="UrgentShift"/> entries.
///
/// Lives outside the controller so the action stays under the
/// no-business-logic-in-controllers ratchet thresholds. The widget shows a
/// simplified urgency-ranked browse — no department grouping, no tag/period
/// filters — so this is a smaller mapping than <c>ShiftsController.Index</c>'s.
/// </summary>
public static class OnboardingShiftsBrowseModelBuilder
{
    public static ShiftBrowseViewModel Build(
        EventSettings eventSettings,
        IReadOnlyList<UrgentShift> urgentShifts,
        IShiftManagementService shiftMgmt,
        HashSet<Guid> userSignupShiftIds,
        Dictionary<Guid, Domain.Enums.SignupStatus> userSignupStatuses)
    {
        var rotaGroups = urgentShifts
            .GroupBy(u => u.Shift.RotaId)
            .Select(rg =>
            {
                var shifts = rg
                    .Select(u => MapToDisplayItem(u, eventSettings, shiftMgmt))
                    .OrderBy(s => s.AbsoluteStart)
                    .ToList();
                var rota = rg.OrderBy(x => x.Shift.Id).First().Shift.Rota;
                return new RotaShiftGroup
                {
                    Rota = rota,
                    Shifts = shifts,
                    DepartmentName = rg.First().DepartmentName,
                    MaxUrgencyScore = shifts.Count > 0 ? shifts.Max(s => s.UrgencyScore) : 0,
                    TotalConfirmed = shifts.Sum(s => s.ConfirmedCount),
                    TotalSlots = shifts.Sum(s => s.Shift.MaxVolunteers),
                };
            })
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

    private static ShiftDisplayItem MapToDisplayItem(
        UrgentShift u, EventSettings es, IShiftManagementService shiftMgmt)
    {
        var (start, end, shiftPeriod) = shiftMgmt.ResolveShiftTimes(u.Shift, es);
        return new ShiftDisplayItem
        {
            Shift = u.Shift,
            AbsoluteStart = start,
            AbsoluteEnd = end,
            Period = shiftPeriod,
            ConfirmedCount = u.ConfirmedCount,
            RemainingSlots = u.RemainingSlots,
            UrgencyScore = u.UrgencyScore,
            Signups = u.Signups
                .Select(s => new ShiftSignupInfo(
                    s.UserId, s.DisplayName, s.Status,
                    s.HasProfilePicture ? $"/Profile/Picture?id={s.UserId}" : null))
                .ToList(),
        };
    }
}
