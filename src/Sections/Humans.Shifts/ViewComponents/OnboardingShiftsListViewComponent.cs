using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// The caller has already filtered <c>shifts</c> to the selected priority pill. Takes the event by id because the
/// rota partials need this section's own <see cref="BurnSettingsInfo"/>.
/// </summary>
public sealed class OnboardingShiftsListViewComponent(IBurnSettingsService burnSettings) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        Guid eventSettingsId,
        IReadOnlyList<UrgentShiftInfo> shifts,
        HashSet<Guid> userSignupShiftIds,
        Dictionary<Guid, SignupStatus> userSignupStatuses,
        bool earlyEntrySignupsClosed)
    {
        var eventSettings = await burnSettings.GetByIdAsync(eventSettingsId, HttpContext.RequestAborted);
        if (eventSettings is null)
            return Content(string.Empty);

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
