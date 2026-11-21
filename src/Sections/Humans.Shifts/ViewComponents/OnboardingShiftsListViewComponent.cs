using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Microsoft.AspNetCore.Mvc;
using Humans.Shifts.Services;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// Fills Onboarding's <c>IOnboardingShiftsStep</c> slot. Onboarding has already filtered to the selected priority
/// pill and passes the surviving <c>shiftIds</c>; this reads the rows and the viewer's signups from the same cached
/// browse query Onboarding used.
/// </summary>
internal sealed class OnboardingShiftsListViewComponent(
    IBurnSettingsService burnSettings,
    IShiftService shifts,
    IShiftView shiftView) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        Guid eventSettingsId,
        Guid viewerUserId,
        IReadOnlyList<Guid> shiftIds)
    {
        var ct = HttpContext.RequestAborted;
        var eventSettings = await burnSettings.GetByIdAsync(eventSettingsId, ct);
        if (eventSettings is null)
            return Content(string.Empty);

        var wanted = shiftIds.ToHashSet();
        var rows = await shifts.GetBrowseShiftsAsync(new ShiftBrowseQuery(
            eventSettingsId,
            Flags: ShiftBrowseQueryFlags.IncludeSignups, ViewerUserId: viewerUserId), ct);
        var (signupShiftIds, statuses) = ShiftSignupHelper.ResolveActiveStatuses(
            (await shiftView.GetUserAsync(viewerUserId, ct)).Signups);

        var rotaGroups = rows
            .Where(u => wanted.Contains(u.Shift.Id))
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
            UserSignupShiftIds = signupShiftIds,
            UserSignupStatuses = statuses,
        });
    }
}
