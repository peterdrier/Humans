using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Domain.Enums;
using Humans.Shifts.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// Renders the rota tables for the onboarding widget's step-2 shift picker.
/// </summary>
/// <remarks>
/// Stays in Shell and is invoked by name from <c>Humans.Onboarding</c>'s
/// <c>Views/OnboardingWidget/Shifts.cshtml</c>: everything below the first line of this method is
/// Shifts' presentation — <see cref="ShiftBrowseMapper"/> (internal to Shell),
/// <see cref="ShiftBrowseViewModel"/>, <see cref="RotaShiftGroup"/> and the
/// <c>_BuildStrikeRotaTable</c>/<c>_EventRotaTable</c> partials — and Shifts has not gone
/// to G5. Dragging that layer down into <c>Humans.UI</c> to serve one onboarding step is
/// the registry-inversion trade design §15 step 5b rules out, and it would be undone at
/// Shifts' own move.
/// <para>
/// Every parameter is a <c>Humans.Domain</c> or <c>Humans.Application</c> type, which is
/// what lets a section name them in the invocation (Governance's rider: invoking by name
/// is not free when the component takes anything but primitives). The caller has already
/// filtered <paramref name="shifts"/> to the selected priority pill.
/// </para>
/// </remarks>
public sealed class OnboardingShiftsListViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(
        BurnSettingsInfo eventSettings,
        IReadOnlyList<UrgentShiftInfo> shifts,
        HashSet<Guid> userSignupShiftIds,
        Dictionary<Guid, SignupStatus> userSignupStatuses,
        bool earlyEntrySignupsClosed)
    {
        var rotaGroups = shifts
            .GroupBy(u => u.Shift.RotaId)
            .Select(rg => ShiftBrowseMapper.BuildRotaGroup(
                rg,
                departmentName: rg.First().DepartmentName))
            .OrderByDescending(r => r.MaxUrgencyScore)
            .ToList();

        return View(new ShiftBrowseViewModel
        {
            EventSettings = eventSettings,
            ShowSignups = true,
            Sort = "urgency",
            UrgencyRankedRotas = rotaGroups,
            UserSignupShiftIds = userSignupShiftIds,
            UserSignupStatuses = userSignupStatuses,
            EarlyEntrySignupsClosed = earlyEntrySignupsClosed,
        });
    }
}
