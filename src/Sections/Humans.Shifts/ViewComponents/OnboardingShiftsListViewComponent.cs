using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// Renders the rota tables for the onboarding widget's step-2 shift picker.
/// </summary>
/// <remarks>
/// Invoked by name from <c>Humans.Onboarding</c>'s <c>Views/OnboardingWidget/Shifts.cshtml</c>.
/// The caller has already filtered <paramref name="shifts"/> to the selected priority pill.
/// It passes the event by id, not as a DTO: invocation by name has no compile-time
/// argument check, and the rota partials need this section's <see cref="BurnSettingsInfo"/>.
/// </remarks>
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
