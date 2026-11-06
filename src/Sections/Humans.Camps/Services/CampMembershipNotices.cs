using System.Globalization;
using System.Resources;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

namespace Humans.Camps.Services;

internal sealed class CampMembershipNotices(
    ICampRepository repo,
    IUserService userService,
    INotificationEmitter notificationEmitter,
    ILogger logger) : IApplicationService
{
    private static readonly ResourceManager NoticeResources = new(typeof(CampsResource));

    public async Task NotifyPendingRequestersOfSeasonClosureAsync(
        Guid seasonId, Guid campId, int year)
    {
        // The season change is committed; finish its notifications independently of the request.
        try
        {
            var pendingUserIds = await repo.GetPendingRequesterUserIdsForSeasonAsync(seasonId, CancellationToken.None);
            if (pendingUserIds.Count == 0) return;

            var camp = await repo.GetByIdAsync(campId, CancellationToken.None);
            var name = camp?.Seasons.FirstOrDefault(s => s.Id == seasonId)?.Name ?? camp?.Slug;
            var slug = camp?.Slug;
            var recipientsByCulture = new Dictionary<CultureInfo, List<Guid>>();
            var recipientIds = new HashSet<Guid>();
            foreach (var userId in pendingUserIds)
            {
                var (recipientId, culture) = await ResolveRecipientAsync(userId);
                if (!recipientIds.Add(recipientId)) continue;
                if (!recipientsByCulture.TryGetValue(culture, out var recipients))
                {
                    recipients = [];
                    recipientsByCulture.Add(culture, recipients);
                }
                recipients.Add(recipientId);
            }
            foreach (var (culture, recipients) in recipientsByCulture)
            {
                try
                {
                    var campName = name ?? NoticeResources.GetString("Camps_Notification_GenericCamp", culture)!;
                    var title = string.Format(culture, NoticeResources.GetString("Camps_Notification_SeasonClosed", culture)!, year, campName);
                    await notificationEmitter.SendAsync(
                        CampsNotificationSources.CampMembershipSeasonClosed,
                        NotificationClass.Informational,
                        NotificationPriority.Normal,
                        title,
                        recipients,
                        body: NoticeResources.GetString("Camps_Notification_SeasonClosedBody", culture),
                        actionUrl: slug is null ? null : $"/Barrios/{slug}",
                        actionLabel: slug is null ? null : NoticeResources.GetString("Camps_Notification_ViewCamp", culture),
                        cancellationToken: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send CampMembershipSeasonClosed notification for season {SeasonId} in {Culture}", seasonId, culture.Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to prepare CampMembershipSeasonClosed notifications for season {SeasonId}", seasonId);
        }
    }

    public async Task NotifyDecisionAsync(Guid campId, Guid seasonId, Guid userId, Guid memberId, bool approved)
    {
        // Membership is committed; request cancellation must not skip its notification.
        try
        {
            var camp = await repo.GetByIdAsync(campId, CancellationToken.None);
            var slug = approved ? camp?.Slug : null;
            var (recipientId, culture) = await ResolveRecipientAsync(userId);
            var campName = camp?.Seasons.FirstOrDefault(s => s.Id == seasonId)?.Name ?? camp?.Slug
                ?? NoticeResources.GetString("Camps_Notification_GenericCamp", culture)!;
            var titleKey = approved ? "Camps_Notification_MembershipApproved" : "Camps_Notification_MembershipRejected";
            var title = string.Format(culture, NoticeResources.GetString(titleKey, culture)!, campName);
            await notificationEmitter.SendAsync(
                approved ? CampsNotificationSources.CampMembershipApproved : CampsNotificationSources.CampMembershipRejected,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [recipientId],
                body: null,
                actionUrl: slug is null ? null : $"/Barrios/{slug}",
                actionLabel: slug is null ? null : NoticeResources.GetString("Camps_Notification_ViewCamp", culture),
                cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to notify requester {UserId} about {Decision} camp membership {MemberId}",
                userId, approved ? "approved" : "rejected", memberId);
        }
    }

    private async Task<(Guid UserId, CultureInfo Culture)> ResolveRecipientAsync(Guid userId)
    {
        try
        {
            var person = await userService.GetUserInfoAsync(userId, CancellationToken.None);
            var language = person?.PreferredLanguage;
            return (person?.Id ?? userId, CultureInfo.GetCultureInfo(language.IsSupportedCultureCode() ? language! : "en"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve notification language for user {UserId}; using English", userId);
            return (userId, CultureInfo.GetCultureInfo("en"));
        }
    }
}
