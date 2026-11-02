using System.Globalization;
using System.Resources;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Notifications.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Shifts.Services;

/// <summary>Post-save notice delivery: a lookup or channel failure never changes the saved signup outcome.</summary>
internal sealed class ShiftSignupNotices(
    INotificationEmitter notificationService,
    EventCalendarResolver calendarResolver,
    IUserService users,
    IServiceProvider serviceProvider,
    ILogger logger) : IApplicationService
{
    private static readonly ResourceManager NoticeResources = new(typeof(ShiftsResource));

    // Lazy, to preserve the TeamService / ShiftSignupService constructor-cycle boundary.
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();

    public async Task SendAssignmentNoticeAsync(Guid userId, Guid signupId, string resourceKey, params object[] args)
    {
        try
        {
            var culture = await GetRecipientCultureAsync(userId);
            var title = string.Format(culture, NoticeResources.GetString(resourceKey, culture)!, args);
            await notificationService.SendAsync(
                ShiftsNotificationSources.ShiftAssigned,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [userId],
                body: null,
                actionUrl: "/Shifts",
                actionLabel: NoticeResources.GetString("Shifts_BrowseAvailable", culture));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ShiftAssigned notification for user {UserId} signup {SignupId}", userId, signupId);
        }
    }

    private async Task<CultureInfo> GetRecipientCultureAsync(Guid userId)
    {
        try
        {
            var language = (await users.GetUserInfoAsync(userId))?.PreferredLanguage;
            return CultureInfo.GetCultureInfo(language.IsSupportedCultureCode() ? language! : "en");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve assignment notice language for user {UserId}; using English", userId);
            return CultureInfo.GetCultureInfo("en");
        }
    }

    public async Task CheckAndNotifyCoverageGapAsync(ShiftSignup signup, Shift shift)
    {
        try
        {
            if (shift.MinVolunteers <= 0)
                return;

            var confirmedCount = shift.ShiftSignups.Count(s => s.Status == SignupStatus.Confirmed);
            if (confirmedCount >= shift.MinVolunteers)
                return;

            var teamId = shift.Rota.TeamId;
            var team = await TeamService.GetTeamAsync(teamId);
            var coordinatorIds = team?.Members
                .Where(m => m.Role == TeamMemberRole.Coordinator)
                .Select(m => m.UserId)
                .ToList() ?? [];

            if (coordinatorIds.Count == 0)
                return;

            var title = $"Coverage gap: {shift.Rota.Name} day {shift.DayOffset}";
            await notificationService.SendAsync(
                ShiftsNotificationSources.ShiftCoverageGap,
                NotificationClass.Actionable,
                NotificationPriority.High,
                title,
                coordinatorIds,
                body: $"Only {confirmedCount}/{shift.MinVolunteers} volunteers confirmed.",
                actionUrl: $"/Shifts?departmentId={teamId}",
                actionLabel: "Find cover →");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ShiftCoverageGap notification for signup {SignupId}", signup.Id);
        }
    }

    public Task DispatchSignupChangeNotificationAsync(ShiftSignup signup, Shift shift, string changeDescription) =>
        DispatchSignupChangeNotificationAsync(signup, shift, shift.Rota, changeDescription);

    public async Task DispatchSignupChangeNotificationAsync(ShiftSignup signup, Shift shift, Rota rota, string changeDescription)
    {
        try
        {
            var teamId = rota.TeamId;
            var rotaName = rota.Name;

            // From Settings: the row's own calendar columns are dead.
            var calendar = await calendarResolver.GetAsync(rota.EventSettingsId);
            if (calendar is null) return;
            string day;
            using (new CultureScope("en"))
                day = calendar.GateOpeningDate.PlusDays(shift.DayOffset).ToWeekdayDayMonth();
            var enrichedDescription = $"{changeDescription} ({rotaName}, {day})";

            var team = await TeamService.GetTeamAsync(teamId);
            var coordinatorIds = team?.Members
                .Where(m => m.Role == TeamMemberRole.Coordinator)
                .Select(m => m.UserId)
                .ToList() ?? [];

            if (coordinatorIds.Count == 0)
                return;

            var title = $"Shift signup change: {rotaName}";
            await notificationService.SendAsync(
                ShiftsNotificationSources.ShiftSignupChange,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                coordinatorIds,
                body: enrichedDescription,
                actionUrl: $"/Shifts?departmentId={teamId}",
                actionLabel: "View →");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ShiftSignupChange notification for signup {SignupId}", signup.Id);
        }
    }
}
