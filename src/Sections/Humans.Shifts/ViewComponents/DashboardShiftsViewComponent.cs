using System.Security.Claims;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// The member dashboard's shift block: confirmed signups, urgent open shifts, the
/// "no shifts yet" guided-discovery callout, and the volunteer "Get involved" tri-card
/// row. Absorbed from Shell's DashboardService (nobodies-collective/Humans#1091).
/// </summary>
public sealed class DashboardShiftsViewComponent(
    IShiftManagementServiceRead shiftMgmt,
    IShiftView shiftView,
    IBurnSettingsService burnSettings,
    ITeamServiceRead teamService,
    IMembershipCalculatorRead membershipCalculator,
    IClock clock,
    ILogger<DashboardShiftsViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Content(string.Empty);
        }

        var ct = HttpContext.RequestAborted;
        var isVolunteerMember = (await membershipCalculator.GetMembershipSnapshotAsync(userId, ct)).IsVolunteerMember;

        BurnSettingsInfo? activeEvent = null;
        try
        {
            activeEvent = await burnSettings.GetActiveAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load active event for the dashboard shifts card");
        }

        var isShiftBrowsingOpen = activeEvent is not null && activeEvent.IsShiftBrowsingOpen;
        var urgentShifts = isShiftBrowsingOpen
            ? await UrgentShiftsAsync(activeEvent!.Id)
            : [];
        var (nextShifts, pendingCount) = isShiftBrowsingOpen
            ? await NextShiftsAsync(userId, activeEvent!.Id)
            : ((IReadOnlyList<DashboardShiftSignup>)[], 0);

        return View(new DashboardShiftsViewModel(
            userId, isVolunteerMember, isShiftBrowsingOpen, urgentShifts, nextShifts, pendingCount));
    }

    private async Task<IReadOnlyList<UrgentShiftInfo>> UrgentShiftsAsync(Guid eventSettingsId)
    {
        try
        {
            return await shiftMgmt.GetUrgentShiftsAsync(eventSettingsId, limit: 3);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load urgent shifts for dashboard");
            return [];
        }
    }

    private async Task<(IReadOnlyList<DashboardShiftSignup> NextShifts, int PendingCount)> NextShiftsAsync(
        Guid userId, Guid eventSettingsId)
    {
        try
        {
            var now = clock.GetCurrentInstant();
            var userView = await shiftView.GetUserAsync(userId);
            var userSignups = userView.Signups.Where(s => s.EventSettingsId == eventSettingsId).ToList();
            var pendingCount = userSignups
                .Where(s => s.Status == SignupStatus.Pending)
                .Select(s => s.SignupBlockId ?? s.Id)
                .Distinct()
                .Count();

            var confirmedSignups = userSignups.Where(s => s.Status == SignupStatus.Confirmed).ToList();
            var teamsById = await teamService.GetTeamsAsync();

            var nextShifts = confirmedSignups
                .Where(s => s.AbsoluteEnd > now)
                .Select(s => new DashboardShiftSignup(
                    s.RotaName,
                    teamsById.TryGetValue(s.TeamId, out var team) ? team.Name : "Unknown",
                    s.AbsoluteStart,
                    s.AbsoluteEnd))
                .OrderBy(s => s.AbsoluteStart)
                .Take(3)
                .ToList();

            return (nextShifts, pendingCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load user signups for dashboard");
            return ([], 0);
        }
    }
}

internal sealed record DashboardShiftsViewModel(
    Guid UserId,
    bool IsVolunteerMember,
    bool IsShiftBrowsingOpen,
    IReadOnlyList<UrgentShiftInfo> UrgentShifts,
    IReadOnlyList<DashboardShiftSignup> NextShifts,
    int PendingCount);

/// <summary>Dashboard-shaped confirmed signup entry with the department name resolved.</summary>
internal sealed record DashboardShiftSignup(string RotaName, string DepartmentName, Instant AbsoluteStart, Instant AbsoluteEnd);
