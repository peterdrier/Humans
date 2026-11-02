using System.Globalization;
using System.Resources;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Notifications.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Domain;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Teams.Services;

internal sealed class TeamMembershipNotices(
    INotificationEmitter notificationService,
    IServiceProvider serviceProvider,
    ILogger logger)
{
    private static readonly ResourceManager NoticeResources = new(typeof(TeamsResource));

    // Resolve lazily: Users and Google resources can depend on Teams.
    private IUserService UserService => serviceProvider.GetRequiredService<IUserService>();
    private ITeamResourceService TeamResourceService => serviceProvider.GetRequiredService<ITeamResourceService>();
    private IEmailService EmailService => serviceProvider.GetRequiredService<IEmailService>();
    private TeamsEmails EmailMessages => serviceProvider.GetRequiredService<TeamsEmails>();

    public Task SendTeamJoinRequestSubmittedNotificationAsync(
        Team team, Guid userId, CancellationToken cancellationToken) =>
        SendCoordinatorNoticeAsync(team, userId, joinRequest: true, cancellationToken);

    public Task SendTeamMemberAddedNotificationAsync(
        Team team, Guid userId, CancellationToken cancellationToken) =>
        SendCoordinatorNoticeAsync(team, userId, joinRequest: false, cancellationToken);

    private async Task SendCoordinatorNoticeAsync(
        Team team, Guid userId, bool joinRequest, CancellationToken cancellationToken)
    {
        var coordinatorUserIds = GetCoordinatorUserIds(team);
        if (coordinatorUserIds.Count == 0) return;
        var source = joinRequest
            ? TeamsNotificationSources.TeamJoinRequestSubmitted
            : TeamsNotificationSources.TeamMemberAdded;
        try
        {
            var displayName = (await UserService.GetUserInfoAsync(userId, cancellationToken))?.BurnerName;
            var recipientsByCulture = new Dictionary<CultureInfo, List<Guid>>();
            foreach (var coordinatorId in coordinatorUserIds)
            {
                var culture = await GetRecipientCultureAsync(coordinatorId, cancellationToken);
                if (!recipientsByCulture.TryGetValue(culture, out var recipients))
                    recipientsByCulture[culture] = recipients = [];
                recipients.Add(coordinatorId);
            }

            foreach (var (culture, recipients) in recipientsByCulture)
            {
                try
                {
                    var name = displayName ?? NoticeResources.GetString("Teams_Notification_Someone", culture)!;
                    var title = joinRequest
                        ? string.Format(culture, NoticeResources.GetString("Teams_Notification_JoinRequestSubmitted", culture)!, team.Name)
                        : string.Format(culture, NoticeResources.GetString("Teams_Notification_MemberJoined", culture)!, name, team.Name);
                    var bodyKey = joinRequest ? "Teams_Notification_JoinRequestSubmittedBody" : "Teams_Notification_MemberJoinedBody";
                    var actionKey = joinRequest ? "Teams_Notification_ReviewRequest" : "Teams_Notification_ViewMembers";
                    await notificationService.SendAsync(
                        source,
                        joinRequest ? NotificationClass.Actionable : NotificationClass.Informational,
                        NotificationPriority.Normal,
                        title,
                        recipients,
                        body: string.Format(culture, NoticeResources.GetString(bodyKey, culture)!, name, team.Name),
                        actionUrl: $"/Teams/{team.Slug}/Members",
                        actionLabel: NoticeResources.GetString(actionKey, culture),
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to dispatch {Source} notification for team {TeamId} in {Culture}",
                        source, team.Id, culture.Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to prepare {Source} notification for team {TeamId}", source, team.Id);
        }
    }

    private static IReadOnlyList<Guid> GetCoordinatorUserIds(Team team) =>
        team.Members
            .Where(member => member.Role == TeamMemberRole.Coordinator && member.LeftAt is null)
            .Select(member => member.UserId)
            .Distinct()
            .ToList();

    private async Task<CultureInfo> GetRecipientCultureAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var language = (await UserService.GetUserInfoAsync(userId, cancellationToken))?.PreferredLanguage;
            return CultureInfo.GetCultureInfo(language.IsSupportedCultureCode() ? language! : "en");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve notification language for user {UserId}; using English", userId);
            return CultureInfo.GetCultureInfo("en");
        }
    }

    public async Task SendJoinRequestApprovedNotificationAsync(
        Team team,
        Guid requesterUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            var culture = await GetRecipientCultureAsync(requesterUserId, cancellationToken);
            var title = string.Format(culture, NoticeResources.GetString("Teams_Notification_JoinApproved", culture)!, team.Name);
            await notificationService.SendAsync(
                TeamsNotificationSources.TeamJoinRequestDecided,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [requesterUserId],
                body: string.Format(culture, NoticeResources.GetString("Teams_Notification_Welcome", culture)!, team.Name),
                actionUrl: $"/Teams/{team.Slug}",
                actionLabel: NoticeResources.GetString("Teams_Notification_ViewTeam", culture),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch TeamJoinRequestDecided notification for team {TeamId}", team.Id);
        }
    }

    public async Task SendJoinRequestRejectedNotificationAsync(
        Team team,
        Guid requesterUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            var culture = await GetRecipientCultureAsync(requesterUserId, cancellationToken);
            var title = string.Format(culture, NoticeResources.GetString("Teams_Notification_JoinRejected", culture)!, team.Name);
            await notificationService.SendAsync(
                TeamsNotificationSources.TeamJoinRequestDecided,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [requesterUserId],
                body: string.Format(culture, NoticeResources.GetString("Teams_Notification_JoinRejected", culture)!, team.Name) + ".",
                actionUrl: "/Teams",
                actionLabel: NoticeResources.GetString("Teams_MyTeams_BrowseTeams", culture),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch TeamJoinRequestDecided notification for team {TeamId}", team.Id);
        }
    }

    public async Task SendAddedToTeamEmailAsync(Guid userId, Team team, CancellationToken cancellationToken)
    {
        if (team.IsHidden) return;

        var culture = CultureInfo.GetCultureInfo("en");
        try
        {
            var users = await UserService.GetUserInfosAsync([userId], cancellationToken);
            if (!users.TryGetValue(userId, out var user))
                return;

            culture = CultureInfo.GetCultureInfo(user.PreferredLanguage.IsSupportedCultureCode() ? user.PreferredLanguage : "en");

            var email = user.Email;
            if (string.IsNullOrEmpty(email))
            {
                logger.LogWarning(
                    "Skipping added-to-team email for user {UserId}: no notification-target email",
                    userId);
            }
            else
            {
                var resources = await TeamResourceService.GetTeamResourcesAsync(team.Id, cancellationToken);

                await EmailService.SendAsync(EmailMessages.AddedToTeam(
                    user.Id,
                    email, user.BurnerName, team.Name, team.Slug,
                    resources.Select(r => (r.Name, r.Url)),
                    culture.Name),
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send added-to-team email for user {UserId} team {TeamId}", userId, team.Id);
        }

        try
        {
            var title = string.Format(culture, NoticeResources.GetString("Teams_Email_AddedToTeam_Subject", culture)!, team.Name);
            await notificationService.SendAsync(
                TeamsNotificationSources.TeamMemberAdded,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [userId],
                body: null,
                actionUrl: $"/Teams/{team.Slug}",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch added-to-team inbox notification for user {UserId} team {TeamId}", userId, team.Id);
        }
    }

    public async Task SendMemberRemovedNotificationAsync(
        Team team, Guid userId, Guid teamId, CancellationToken cancellationToken)
    {
        try
        {
            var culture = await GetRecipientCultureAsync(userId, cancellationToken);
            var title = string.Format(culture, NoticeResources.GetString("Teams_Notification_MemberRemoved", culture)!, team.Name);
            await notificationService.SendAsync(
                TeamsNotificationSources.TeamMemberRemoved,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [userId],
                body: null,
                actionUrl: "/Teams",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch TeamMemberRemoved notification for user {UserId} team {TeamId}", userId, teamId);
        }
    }
}
