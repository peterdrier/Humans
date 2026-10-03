using System.Security.Claims;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// The member dashboard's shift block: confirmed signups, urgent open shifts, the
/// "no shifts yet" guided-discovery callout, and the volunteer "Get involved" tri-card
/// row.
/// </summary>
public sealed class DashboardShiftsViewComponent(
    IShiftManagementServiceRead shiftMgmt,
    IShiftView shiftView,
    IBurnSettingsService burnSettings,
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load active event for the dashboard shifts card");
        }

        var isShiftBrowsingOpen = activeEvent is not null && activeEvent.IsShiftBrowsingOpen;
        var urgentShifts = isShiftBrowsingOpen
            ? await UrgentShiftsAsync(activeEvent!.Id, ct)
            : [];
        var hasUpcomingShifts = isShiftBrowsingOpen
            && await HasUpcomingShiftsAsync(userId, activeEvent!.Id, ct);

        return View(new DashboardShiftsViewModel(
            userId, isVolunteerMember, isShiftBrowsingOpen, urgentShifts, hasUpcomingShifts));
    }

    private async Task<IReadOnlyList<UrgentShiftInfo>> UrgentShiftsAsync(Guid eventSettingsId, CancellationToken ct)
    {
        try
        {
            return await shiftMgmt.GetUrgentShiftsAsync(eventSettingsId, limit: 3);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load urgent shifts for dashboard");
            return [];
        }
    }

    private async Task<bool> HasUpcomingShiftsAsync(Guid userId, Guid eventSettingsId, CancellationToken ct)
    {
        try
        {
            var now = clock.GetCurrentInstant();
            var userView = await shiftView.GetUserAsync(userId, ct);
            return userView.Signups.Any(s => s.EventSettingsId == eventSettingsId
                && (s.Status == SignupStatus.Pending
                    || (s.Status == SignupStatus.Confirmed && s.AbsoluteEnd > now)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load user signups for dashboard");
            return false;
        }
    }
}

internal sealed record DashboardShiftsViewModel(
    Guid UserId,
    bool IsVolunteerMember,
    bool IsShiftBrowsingOpen,
    IReadOnlyList<UrgentShiftInfo> UrgentShifts,
    bool HasUpcomingShifts);
