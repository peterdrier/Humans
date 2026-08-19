using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using NodaTime;
using Humans.Users.Contracts;
using Humans.Governance.Contracts;

namespace Humans.Web.Services.Dashboard;

/// <summary>Orchestrates the member dashboard snapshot across profile/membership/shifts.</summary>
public class DashboardService(
    IMembershipCalculatorRead membershipCalculator,
    IShiftManagementServiceRead shiftMgmt,
    IBurnSettingsService burnSettings,
    IShiftView shiftView,
    IUserServiceRead userService,
    ITeamServiceRead teamService,
    IClock clock,
    ILogger<DashboardService> logger) : IDashboardService
{
    public async Task<MemberDashboardData> GetMemberDashboardAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var userInfo = await userService.GetUserInfoAsync(userId, cancellationToken);
        var profile = userInfo?.Profile;
        var userView = await shiftView.GetUserAsync(userId, cancellationToken);
        var dashboardProfile = profile is null
            ? null
            : new DashboardProfile(
                ProfileComplete: !string.IsNullOrEmpty(profile.FirstName),
                IsRejected: profile.RejectedAt is not null,
                RejectionReason: profile.RejectionReason);
        var membershipSnapshot = await membershipCalculator.GetMembershipSnapshotAsync(userId, cancellationToken);

        // Shift cards (urgent shifts + confirmed signups) — guarded, failures never crash the dashboard.
        BurnSettingsInfo? activeEvent = null;
        try
        {
            activeEvent = await burnSettings.GetActiveAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load active event for dashboard");
        }

        var urgentItems = new List<DashboardUrgentShift>();
        var nextShifts = new List<DashboardSignup>();
        var pendingCount = 0;

        if (activeEvent is not null && activeEvent.IsShiftBrowsingOpen)
        {
            try
            {
                var urgentShifts = await shiftMgmt.GetUrgentShiftsAsync(activeEvent.Id, limit: 3);
                foreach (var u in urgentShifts)
                {
                    try
                    {
                        urgentItems.Add(new DashboardUrgentShift(
                            RotaName: u.Rota.Name,
                            DepartmentName: u.DepartmentName,
                            AbsoluteStart: u.AbsoluteStart,
                            RemainingSlots: u.RemainingSlots,
                            UrgencyScore: u.UrgencyScore));
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to build urgent shift item for shift {ShiftId}", u.Shift.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load urgent shifts for dashboard");
            }

            try
            {
                var now = clock.GetCurrentInstant();
                // Filter signups to active event in memory.
                var userSignups = userView.Signups
                    .Where(s => s.EventSettingsId == activeEvent.Id)
                    .ToList();
                pendingCount = userSignups
                    .Where(s => s.Status == SignupStatus.Pending)
                    .Select(s => s.SignupBlockId ?? s.Id)
                    .Distinct()
                    .Count();

                var confirmedSignups = userSignups.Where(s => s.Status == SignupStatus.Confirmed).ToList();
                var dashboardTeamIds = confirmedSignups
                    .Select(s => s.TeamId)
                    .Distinct()
                    .ToList();
                var teamsById = await teamService.GetTeamsAsync();
                var teamNames = dashboardTeamIds
                    .Where(teamsById.ContainsKey)
                    .ToDictionary(id => id, id => teamsById[id].Name);

                foreach (var s in confirmedSignups)
                {
                    try
                    {
                        var item = new DashboardSignup(
                            RotaName: s.RotaName,
                            DepartmentName: teamNames.GetValueOrDefault(s.TeamId, "Unknown"),
                            AbsoluteStart: s.AbsoluteStart,
                            AbsoluteEnd: s.AbsoluteEnd);
                        if (item.AbsoluteEnd > now)
                            nextShifts.Add(item);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to build shift item for signup {SignupId}", s.Id);
                    }
                }

                nextShifts = nextShifts.OrderBy(i => i.AbsoluteStart).Take(3).ToList();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load user signups for dashboard");
            }
        }

        return new MemberDashboardData(
            Profile: dashboardProfile,
            MembershipSnapshot: membershipSnapshot,
            ActiveEvent: activeEvent is null
                ? null
                : new DashboardEvent(activeEvent.EventName, activeEvent.IsShiftBrowsingOpen, activeEvent.Year),
            UrgentShifts: urgentItems,
            NextShifts: nextShifts,
            PendingSignupCount: pendingCount);
    }
}
