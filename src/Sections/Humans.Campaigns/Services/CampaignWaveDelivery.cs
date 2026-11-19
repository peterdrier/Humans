using System.Globalization;
using System.Resources;
using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Campaigns.Services.Dtos;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Campaigns.Services;

internal sealed class CampaignWaveDelivery(
    ICampaignRepository repository,
    ITeamService teamService,
    IUserService userService,
    INotificationEmitter notificationService,
    IEmailService emailService,
    CampaignsEmails emailMessages,
    IClock clock,
    IAuditLogService audit,
    ILogger<CampaignService> logger) : IApplicationService
{
    private static readonly ResourceManager NoticeResources = new(typeof(CampaignsResource));

    public async Task<WaveSendPreview> PreviewWaveSendAsync(Guid campaignId, Guid teamId,
        CancellationToken ct = default)
    {
        var activeTeamUserIds = await GetActiveTeamUserIdsAsync(teamId, ct);

        var alreadyGrantedSet = await repository.GetAlreadyGrantedUserIdsAsync(campaignId, ct);

        var notGranted = activeTeamUserIds
            .Where(id => !alreadyGrantedSet.Contains(id))
            .ToList();

        var eligibleCount = notGranted.Count;
        var availableCodes = await repository.CountAvailableCodesAsync(campaignId, ct);

        return new WaveSendPreview(
            EligibleCount: eligibleCount,
            AlreadyGrantedExcluded: activeTeamUserIds.Count(alreadyGrantedSet.Contains),
            CodesAvailable: availableCodes,
            CodesRemainingAfterSend: availableCodes - eligibleCount);
    }

    public async Task<CampaignSendWaveResult> SendWaveAsync(Guid actorUserId, Guid campaignId, Guid teamId, CancellationToken ct = default)
    {
        var campaign = await repository.FindForMutationAsync(campaignId, ct);
        if (campaign is null)
            return new CampaignSendWaveResult(false, "NotFound");

        if (campaign.Status != CampaignStatus.Active)
            return new CampaignSendWaveResult(false, "NotActive");

        var activeTeamUserIds = await GetActiveTeamUserIdsAsync(teamId, ct);
        var alreadyGrantedSet = await repository.GetAlreadyGrantedUserIdsAsync(campaignId, ct);

        var eligibleUserIds = activeTeamUserIds
            .Where(id => !alreadyGrantedSet.Contains(id))
            .ToList();

        if (eligibleUserIds.Count == 0)
            return new CampaignSendWaveResult(true, SentCount: 0);

        var users = await userService.GetUserInfosAsync(eligibleUserIds, ct);

        var availableCodes = await repository.GetAvailableCodesAsync(
            campaignId, eligibleUserIds.Count, ct);

        if (availableCodes.Count < eligibleUserIds.Count)
            return new CampaignSendWaveResult(false, "NotEnoughCodes",
                CodesNeeded: eligibleUserIds.Count, CodesAvailable: availableCodes.Count);

        var now = clock.GetCurrentInstant();
        var failedCount = 0;
        var grantedUserIds = new List<Guid>(eligibleUserIds.Count);

        // Per-grant commit: an enqueue throw is logged for just that grant, so later grants still process and an
        // admin can resend it.
        for (var i = 0; i < eligibleUserIds.Count; i++)
        {
            var userId = eligibleUserIds[i];
            var code = availableCodes[i];

            if (!users.TryGetValue(userId, out var user))
            {
                logger.LogWarning(
                    "User {UserId} eligible for campaign {CampaignId} but lookup returned no row; skipping",
                    userId, campaignId);
                continue;
            }

            if (user.Email is not { Length: > 0 } recipientEmail)
            {
                logger.LogWarning(
                    "User {UserId} has no notification email for campaign {CampaignId}; skipping",
                    userId, campaignId);
                continue;
            }

            var grant = new CampaignGrant
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                CampaignCodeId = code.Id,
                UserId = userId,
                AssignedAt = now
            };
            await repository.AddGrantAndSaveAsync(grant, ct);
            await audit.LogAsync(CampaignsAuditActions.CampaignWaveSent, "Campaign", campaignId,
                $"Assigned grant {grant.Id} in wave to team {teamId}.", actorUserId,
                relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);
            grantedUserIds.Add(userId);

            try
            {
                await emailService.SendAsync(emailMessages.CampaignCode(
                    BuildCampaignCodeRequest(campaign, user, recipientEmail, code.Code)),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to enqueue campaign code email for user {UserId} grant {GrantId} in campaign {CampaignId}",
                    userId, grant.Id, campaignId);
                failedCount++;
            }
        }

        logger.LogInformation(
            "Campaign {CampaignId}: sent wave to team {TeamId}, {Count} grants created, {FailedCount} failed to enqueue",
            campaignId, teamId, grantedUserIds.Count, failedCount);

        if (grantedUserIds.Count == 0)
            return new CampaignSendWaveResult(true, SentCount: 0);

        foreach (var recipients in grantedUserIds.GroupBy(
            id => users[id].PreferredLanguage.IsSupportedCultureCode()
                ? users[id].PreferredLanguage : CultureCatalog.DefaultCultureCode,
            StringComparer.Ordinal))
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(recipients.Key);
                var title = string.Format(culture,
                    NoticeResources.GetString("Campaigns_Notification_ReceivedTitle", culture)!, campaign.Title);
                var body = NoticeResources.GetString("Campaigns_Notification_ReceivedBody", culture)!;

                await notificationService.SendAsync(
                    CampaignsNotificationSources.CampaignReceived,
                    NotificationClass.Informational,
                    NotificationPriority.Normal,
                    title,
                    recipients.ToList(),
                    body: body,
                    actionUrl: "/Profile/Me",
                    actionLabel: NoticeResources.GetString("Campaigns_Notification_MyCodes", culture),
                    cancellationToken: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch CampaignReceived notifications for campaign {CampaignId} in {Culture}",
                    campaignId, recipients.Key);
            }
        }

        return new CampaignSendWaveResult(true, SentCount: grantedUserIds.Count, EnqueueFailedCount: failedCount);
    }

    public async Task ResendToGrantAsync(Guid actorUserId, Guid grantId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantForResendAsync(grantId, ct)
            ?? throw new InvalidOperationException($"Grant {grantId} not found.");

        await audit.LogAsync(CampaignsAuditActions.CampaignGrantResent, "CampaignGrant", grantId,
            $"Started resend for campaign {grant.CampaignId}.", actorUserId,
            relatedEntityId: grant.UserId, relatedEntityType: AuditSubjectTypes.User);

        try
        {
            var user = await userService.GetUserInfoAsync(grant.UserId, ct)
                ?? throw new InvalidOperationException($"User {grant.UserId} for grant {grantId} not found.");
            if (user.Email is not { Length: > 0 } recipientEmail)
                throw new InvalidOperationException(
                    $"No notification email resolved for user {grant.UserId} when resending grant {grantId}.");

            await emailService.SendAsync(emailMessages.CampaignCode(
                BuildCampaignCodeRequest(
                    grant.CampaignEmailSubject,
                    grant.CampaignEmailBodyTemplate,
                    grant.CampaignReplyToAddress,
                    user, recipientEmail, grant.CodeString, grant.CampaignId)),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resend campaign code email for grant {GrantId}", grantId);
            throw;
        }

        logger.LogInformation("Resent campaign email for grant {GrantId}", grantId);
    }

    private async Task<List<Guid>> GetActiveTeamUserIdsAsync(Guid teamId, CancellationToken ct)
    {
        var team = await teamService.GetTeamAsync(teamId, ct);
        return team?.Members.Select(tm => tm.UserId).ToList() ?? [];
    }

    private static CampaignCodeEmailRequest BuildCampaignCodeRequest(
        Campaign campaign, UserInfo user, string recipientEmail, string code)
        => BuildCampaignCodeRequest(
            campaign.EmailSubject, campaign.EmailBodyTemplate, campaign.ReplyToAddress,
            user, recipientEmail, code, campaign.Id);

    private static CampaignCodeEmailRequest BuildCampaignCodeRequest(
        string emailSubject, string emailBody, string? replyToAddress,
        UserInfo user, string recipientEmail, string code, Guid campaignId)
    {
        return new CampaignCodeEmailRequest(
            UserId: user.Id,
            CampaignId: campaignId,
            RecipientEmail: recipientEmail,
            RecipientName: user.BurnerName,
            Subject: emailSubject,
            MarkdownBody: emailBody,
            Code: code,
            ReplyTo: replyToAddress);
    }
}
