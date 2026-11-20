using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Humans.Shifts.Services;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// The caller has already filtered <c>shifts</c> to the selected priority pill. Takes the event by id because the
/// rota partials need this section's own <see cref="BurnSettingsInfo"/>.
/// Public so Onboarding's shifts step can render it as <c>&lt;vc:onboarding-shifts-list&gt;</c>; a public constructor
/// can't take the internal <see cref="IBurnSettingsService"/>, so it resolves it per invocation.
/// </summary>
public sealed class OnboardingShiftsListViewComponent(IServiceProvider services) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        Guid eventSettingsId,
        IReadOnlyList<UrgentShiftInfo> shifts,
        HashSet<Guid> userSignupShiftIds,
        Dictionary<Guid, SignupStatus> userSignupStatuses)
    {
        var eventSettings = await services.GetRequiredService<IBurnSettingsService>().GetByIdAsync(eventSettingsId, HttpContext.RequestAborted);
        if (eventSettings is null)
            return Content(string.Empty);

        var rotaGroups = shifts
            .OrderByDescending(u => u.UrgencyScore)
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
        });
    }
}
