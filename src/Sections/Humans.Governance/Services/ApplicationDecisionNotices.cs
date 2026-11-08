using System.Globalization;
using System.Resources;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using MemberApplication = Humans.Governance.Domain.Application;

namespace Humans.Governance.Services;

internal sealed class ApplicationDecisionNotices(
    IUserService userService,
    IEmailService emailService,
    GovernanceEmails emailMessages,
    INotificationEmitter notificationService,
    ILogger logger) : IApplicationService
{
    private static readonly ResourceManager NoticeResources = new(typeof(GovernanceResource));

    internal async Task SendApprovalAsync(MemberApplication application, CancellationToken cancellationToken)
    {
        var (recipientId, culture) = await SendDecisionEmailAsync(
            application,
            "approval",
            (recipientEmail, user, language) => emailMessages.ApplicationApproved(
                user.Id,
                recipientEmail,
                user.BurnerName,
                application.MembershipTier,
                language),
            cancellationToken);

        await SendDecisionNotificationAsync(
            application,
            recipientId,
            GovernanceNotificationSources.ApplicationApproved,
            "Governance_Notification_ApplicationApproved",
            "Governance_Notification_ApplicationApprovedBody",
            "ApplicationApproved",
            culture,
            cancellationToken);

    }

    internal async Task SendRejectionAsync(MemberApplication application, string reason, CancellationToken cancellationToken)
    {
        var (recipientId, culture) = await SendDecisionEmailAsync(
            application,
            "rejection",
            (recipientEmail, user, language) => emailMessages.ApplicationRejected(
                user.Id,
                recipientEmail,
                user.BurnerName,
                application.MembershipTier,
                reason,
                language),
            cancellationToken);

        await SendDecisionNotificationAsync(
            application,
            recipientId,
            GovernanceNotificationSources.ApplicationRejected,
            "Governance_Notification_ApplicationRejected",
            "Governance_Notification_ApplicationRejectedBody",
            "ApplicationRejected",
            culture,
            cancellationToken);

    }

    private async Task<(Guid UserId, CultureInfo Culture)> SendDecisionEmailAsync(
        MemberApplication application,
        string decisionName,
        Func<string, UserInfo, string, EmailMessage> buildMessage,
        CancellationToken cancellationToken)
    {
        var recipientId = application.UserId;
        var culture = CultureInfo.GetCultureInfo("en");
        try
        {
            var user = await userService.GetUserInfoAsync(application.UserId, cancellationToken);
            if (user is null)
                return (recipientId, culture);

            recipientId = user.Id;
            culture = CultureInfo.GetCultureInfo(user.PreferredLanguage.IsSupportedCultureCode() ? user.PreferredLanguage : "en");

            var recipientEmail = user.Email;
            if (!string.IsNullOrWhiteSpace(recipientEmail))
            {
                await emailService.SendAsync(buildMessage(recipientEmail, user, culture.Name));
            }
            else
            {
                logger.LogWarning(
                    "Skipping {DecisionName} email for application {ApplicationId} because user {UserId} has no notification-target email",
                    decisionName, application.Id, application.UserId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to send {DecisionName} email for {ApplicationId}",
                decisionName, application.Id);
        }
        return (recipientId, culture);
    }

    private async Task SendDecisionNotificationAsync(
        MemberApplication application,
        Guid recipientUserId,
        NotificationSourceDefinition source,
        string titleKey,
        string bodyKey,
        string logName,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        try
        {
            await notificationService.SendAsync(
                source,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                string.Format(culture, NoticeResources.GetString(titleKey, culture)!, application.MembershipTier),
                [recipientUserId],
                body: string.Format(culture, NoticeResources.GetString(bodyKey, culture)!, application.MembershipTier),
                actionUrl: "/Governance/Applications",
                actionLabel: NoticeResources.GetString("Governance_Notification_ViewApplication", culture),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch {NotificationName} notification for {ApplicationId}",
                logName, application.Id);
        }
    }
}
