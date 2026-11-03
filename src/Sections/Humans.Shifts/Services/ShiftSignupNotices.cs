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
            logger.LogError(ex, "Failed to resolve shift notice language for user {UserId}; using English", userId);
            return CultureInfo.GetCultureInfo("en");
        }
    }

    public async Task CheckAndNotifyCoverageGapAsync(ShiftSignup signup, Shift shift)
    {
        try
        {
            if (shift.MinVolunteers <= 0) return;
            var confirmedCount = shift.ShiftSignups.Count(s => s.Status == SignupStatus.Confirmed);
            if (confirmedCount >= shift.MinVolunteers) return;

            await SendCoordinatorNoticeAsync(shift.Rota.TeamId, ShiftsNotificationSources.ShiftCoverageGap,
                NotificationClass.Actionable, NotificationPriority.High, culture => (
                    string.Format(culture, NoticeResources.GetString("Shifts_Notification_CoverageGapTitle", culture)!, shift.Rota.Name, shift.DayOffset),
                    string.Format(culture, NoticeResources.GetString("Shifts_Notification_CoverageGapBody", culture)!, confirmedCount, shift.MinVolunteers),
                    NoticeResources.GetString("Shifts_Notification_FindCover", culture)!));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ShiftCoverageGap notification for signup {SignupId}", signup.Id);
        }
    }

    public Task DispatchSignupChangeNotificationAsync(
        ShiftSignup signup, Shift shift, string resourceKey, params object[] args) =>
        DispatchSignupChangeNotificationAsync(signup, shift, shift.Rota, resourceKey, args);

    public async Task DispatchSignupChangeNotificationAsync(
        ShiftSignup signup, Shift shift, Rota rota, string resourceKey, params object[] args)
    {
        try
        {
            // From Settings: the row's own calendar columns are dead.
            var calendar = await calendarResolver.GetAsync(rota.EventSettingsId);
            if (calendar is null) return;

            await SendCoordinatorNoticeAsync(rota.TeamId, ShiftsNotificationSources.ShiftSignupChange,
                NotificationClass.Informational, NotificationPriority.Normal, culture =>
                {
                    using var recipientCulture = new CultureScope(culture.Name);
                    var day = calendar.GateOpeningDate.PlusDays(shift.DayOffset).ToWeekdayDayMonth();
                    var description = string.Format(culture, NoticeResources.GetString(resourceKey, culture)!, args);
                    return (
                        string.Format(culture, NoticeResources.GetString("Shifts_Notification_SignupChangeTitle", culture)!, rota.Name),
                        $"{description} ({rota.Name}, {day})",
                        NoticeResources.GetString("Shifts_Notification_View", culture)!);
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ShiftSignupChange notification for signup {SignupId}", signup.Id);
        }
    }

    private async Task SendCoordinatorNoticeAsync(
        Guid teamId, NotificationSourceDefinition source, NotificationClass notificationClass,
        NotificationPriority priority, Func<CultureInfo, (string Title, string Body, string ActionLabel)> createCopy)
    {
        var team = await TeamService.GetTeamAsync(teamId);
        var coordinatorIds = team?.Members
            .Where(m => m.Role == TeamMemberRole.Coordinator)
            .Select(m => m.UserId)
            .Distinct()
            .ToList() ?? [];
        var recipientsByCulture = new Dictionary<CultureInfo, List<Guid>>();
        foreach (var coordinatorId in coordinatorIds)
        {
            var culture = await GetRecipientCultureAsync(coordinatorId);
            if (!recipientsByCulture.TryGetValue(culture, out var recipients))
                recipientsByCulture[culture] = recipients = [];
            recipients.Add(coordinatorId);
        }

        foreach (var (culture, recipients) in recipientsByCulture)
        {
            try
            {
                var (title, body, actionLabel) = createCopy(culture);
                await notificationService.SendAsync(source, notificationClass, priority, title, recipients,
                    body: body, actionUrl: $"/Shifts?departmentId={teamId}", actionLabel: actionLabel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch {Source} notification for team {TeamId} in {Culture}",
                    source, teamId, culture.Name);
            }
        }
    }
}
