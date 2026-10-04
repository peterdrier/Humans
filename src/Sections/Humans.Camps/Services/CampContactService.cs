using System.Globalization;
using System.Resources;
using Humans.Base.Extensions;
using Humans.Users.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Camps.Services;

internal sealed class CampContactService(
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    IAuditLogService auditLogService,
    INotificationEmitter notificationEmitter,
    IUserServiceRead users,
    IMemoryCache cache,
    ILogger<CampContactService> logger) : ICampContactService
{
    private static readonly ResourceManager NoticeResources = new(typeof(CampsResource));

    public async Task<CampContactResult> SendFacilitatedMessageAsync(
        Guid campId,
        string campContactEmail,
        string campDisplayName,
        Guid senderUserId,
        string senderDisplayName,
        string senderEmail,
        string message,
        bool includeContactInfo,
        IReadOnlyList<Guid> leadUserIds,
        string campDetailsUrl)
    {
        var rateLimitKey = CacheKeys.CampContactRateLimit(senderUserId, campId);
        if (!await cache.TryReserveAsync(rateLimitKey, TimeSpan.FromMinutes(10)))
        {
            return new CampContactResult(Success: false, RateLimited: true);
        }

        try
        {
            await emailService.SendAsync(emailMessages.FacilitatedMessage(
                campContactEmail,
                campDisplayName,
                senderDisplayName,
                message,
                includeContactInfo,
                senderEmail));

            await auditLogService.LogAsync(
                AuditAction.FacilitatedMessageSent,
                nameof(Camp), campId,
                $"Message sent to camp '{campDisplayName}' (contact info shared: {(includeContactInfo ? "yes" : "no")})",
                senderUserId);

            if (leadUserIds.Count > 0)
            {
                IReadOnlyDictionary<Guid, UserInfo> leadUsers = new Dictionary<Guid, UserInfo>();
                try
                {
                    leadUsers = await users.GetUserInfosAsync(leadUserIds);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to resolve camp lead languages for camp {CampId}; using English", campId);
                }

                var languageGroups = leadUserIds.Distinct().GroupBy(id =>
                {
                    var language = leadUsers.GetValueOrDefault(id)?.PreferredLanguage;
                    return language.IsSupportedCultureCode() ? language! : "en";
                }, StringComparer.Ordinal);
                foreach (var group in languageGroups)
                {
                    try
                    {
                        var culture = CultureInfo.GetCultureInfo(group.Key);
                        var notice = CampService.PrepareNoticeCopy(string.Format(culture,
                            NoticeResources.GetString("Camps_Notification_MessageReceived", culture)!, campDisplayName));
                        await notificationEmitter.SendAsync(
                            NotificationSource.FacilitatedMessageReceived,
                            NotificationClass.Informational,
                            NotificationPriority.Normal,
                            notice.Title,
                            group.ToList(),
                            body: notice.Body,
                            actionUrl: campDetailsUrl,
                            actionLabel: NoticeResources.GetString("Camps_Notification_ViewCamp", culture));
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to dispatch FacilitatedMessageReceived notification for camp {CampId} in {Culture}", campId, group.Key);
                    }
                }
            }

            return new CampContactResult(Success: true, RateLimited: false);
        }
        catch (Exception ex)
        {
            cache.InvalidateCampContactRateLimit(senderUserId, campId);
            logger.LogError(ex, "Failed to send facilitated message to camp {CampId}", campId);
            throw;
        }
    }
}
