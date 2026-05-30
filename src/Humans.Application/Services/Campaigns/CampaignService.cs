using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Campaigns;

/// <summary>
/// Application-layer implementation of <see cref="ICampaignService"/>.
/// </summary>
public sealed class CampaignService(
    ICampaignRepository repository,
    ITeamServiceRead teamService,
    IUserEmailService userEmailService,
    IUserServiceRead userService,
    INotificationService notificationService,
    ICommunicationPreferenceService commPrefService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    ITicketVendorService ticketVendorService,
    IClock clock,
    ILogger<CampaignService> logger) : ICampaignService, IUserDataContributor, IUserMerge
{
    public async Task<CampaignCreateResult> CreateAsync(string title, string? description,
        string emailSubject, string emailBodyTemplate, string? replyToAddress,
        Guid createdByUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new CampaignCreateResult(false, ErrorKey: "TitleRequired");

        if (string.IsNullOrWhiteSpace(emailSubject))
            return new CampaignCreateResult(false, ErrorKey: "EmailSubjectRequired");

        if (string.IsNullOrWhiteSpace(emailBodyTemplate))
            return new CampaignCreateResult(false, ErrorKey: "EmailBodyTemplateRequired");

        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            Title = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            EmailSubject = emailSubject.Trim(),
            EmailBodyTemplate = emailBodyTemplate.Trim(),
            ReplyToAddress = string.IsNullOrWhiteSpace(replyToAddress) ? null : replyToAddress.Trim(),
            Status = CampaignStatus.Draft,
            CreatedAt = clock.GetCurrentInstant(),
            CreatedByUserId = createdByUserId
        };

        await repository.AddCampaignAsync(campaign, ct);

        logger.LogInformation("Campaign {CampaignId} created: {Title}", campaign.Id, title);
        return new CampaignCreateResult(true, campaign);
    }

    public async Task<IReadOnlyList<CampaignGrantSummary>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var grants = await repository.GetActiveOrCompletedGrantsForUserAsync(userId, ct);
        return grants.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<CampaignGrantSummary>> GetAllGrantsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var grants = await repository.GetAllGrantsForUserAsync(userId, ct);
        return grants.Select(ToSummary).ToList();
    }

    private static CampaignGrantSummary ToSummary(CampaignGrant grant) =>
        new(
            grant.Id,
            grant.CampaignId,
            grant.Campaign.Title,
            grant.CampaignCodeId,
            grant.Code.Code,
            grant.UserId,
            grant.AssignedAt,
            grant.LatestEmailStatus,
            grant.LatestEmailAt,
            grant.RedeemedAt);

    private static CampaignAdminSummary ToAdminSummary(Campaign campaign) =>
        new(
            campaign.Id,
            campaign.Title,
            campaign.Description,
            campaign.Status,
            campaign.Grants.Select(ToSummary).ToList());

    public async Task<CampaignEditSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var campaign = await repository.GetByIdAsync(id, ct);
        return campaign is null ? null : ToEditSnapshot(campaign);
    }

    private static CampaignEditSnapshot ToEditSnapshot(Campaign campaign) =>
        new(
            campaign.Id,
            campaign.Title,
            campaign.Description,
            campaign.EmailSubject,
            campaign.EmailBodyTemplate,
            campaign.ReplyToAddress,
            campaign.Status);

    public async Task<CampaignUpdateResult> UpdateAsync(
        Guid id,
        string title,
        string? description,
        string emailSubject,
        string emailBodyTemplate,
        string? replyToAddress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new CampaignUpdateResult(false, "TitleRequired");

        if (string.IsNullOrWhiteSpace(emailSubject))
            return new CampaignUpdateResult(false, "EmailSubjectRequired");

        if (string.IsNullOrWhiteSpace(emailBodyTemplate))
            return new CampaignUpdateResult(false, "EmailBodyTemplateRequired");

        var campaign = await repository.FindForMutationAsync(id, ct);
        if (campaign is null)
            return new CampaignUpdateResult(false, "NotFound");

        campaign.Title = title.Trim();
        campaign.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        campaign.EmailSubject = emailSubject.Trim();
        campaign.EmailBodyTemplate = emailBodyTemplate.Trim();
        campaign.ReplyToAddress = string.IsNullOrWhiteSpace(replyToAddress) ? null : replyToAddress.Trim();

        await repository.UpdateCampaignAsync(campaign, ct);

        logger.LogInformation("Campaign {CampaignId} updated", id);
        return new CampaignUpdateResult(true);
    }

    public async Task<IReadOnlyList<CampaignListSummary>> GetAllAsync(CancellationToken ct = default)
    {
        var campaigns = await repository.GetAllAsync(ct);
        return campaigns.Select(ToListSummary).ToList();
    }

    private static CampaignListSummary ToListSummary(Campaign campaign)
    {
        var assignedCodes = campaign.Grants.Select(g => g.CampaignCodeId).Distinct().Count();
        return new CampaignListSummary(
            campaign.Id,
            campaign.Title,
            campaign.Status,
            campaign.Codes.Count,
            assignedCodes,
            campaign.Grants.Count(g => g.LatestEmailStatus == EmailOutboxStatus.Sent),
            campaign.Grants.Count(g => g.LatestEmailStatus == EmailOutboxStatus.Failed),
            campaign.CreatedAt);
    }

    public async Task<CampaignDetailPageDto?> GetDetailPageAsync(Guid id, CancellationToken ct = default)
    {
        var campaign = await repository.GetByIdAsync(id, ct);
        if (campaign is null)
            return null;

        var totalCodes = campaign.Codes.Count;
        var assignedCodeIds = campaign.Grants.Select(g => g.CampaignCodeId).ToHashSet();
        var availableCodes = totalCodes - assignedCodeIds.Count;
        var sentCount = campaign.Grants.Count(g => g.LatestEmailStatus == EmailOutboxStatus.Sent);
        var failedCount = campaign.Grants.Count(g => g.LatestEmailStatus == EmailOutboxStatus.Failed);
        var codesRedeemed = campaign.Grants.Count(g => g.RedeemedAt is not null);
        var totalGrants = campaign.Grants.Count;

        return new CampaignDetailPageDto(
            ToAdminSummary(campaign),
            new CampaignDetailStatsDto(
                totalCodes,
                availableCodes,
                sentCount,
                failedCount,
                codesRedeemed,
                totalGrants));
    }

    public async Task<CampaignSendWavePageDto?> GetSendWavePageAsync(
        Guid campaignId,
        Guid? teamId,
        CancellationToken ct = default)
    {
        var campaign = await repository.GetByIdAsync(campaignId, ct);
        if (campaign is null)
            return null;

        var teams = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name, StringComparer.Ordinal);
        var teamOptions = teams
            .Select(t => new CampaignTeamOptionDto(t.Id, t.Name))
            .ToList();

        var preview = teamId.HasValue
            ? await PreviewWaveSendAsync(campaignId, teamId.Value, ct)
            : null;

        return new CampaignSendWavePageDto(ToAdminSummary(campaign), teamOptions, teamId, preview);
    }

    public Task<Guid?> GetCampaignIdForGrantAsync(Guid grantId, CancellationToken ct = default) =>
        repository.GetCampaignIdForGrantAsync(grantId, ct);

    public async Task ImportCodesAsync(Guid campaignId, IEnumerable<string> codes, CancellationToken ct = default)
    {
        var campaign = await repository.FindForMutationWithCodesAsync(campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        var existingCodes = campaign.Codes
            .Select(c => c.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var now = clock.GetCurrentInstant();
        var imported = 0;
        var skipped = 0;
        var maxOrder = campaign.Codes.Any() ? campaign.Codes.Max(c => c.ImportOrder) : 0;
        var newCodes = new List<CampaignCode>();

        foreach (var code in codes)
        {
            var trimmed = code.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (existingCodes.Contains(trimmed))
            {
                skipped++;
                continue;
            }

            maxOrder++;
            newCodes.Add(new CampaignCode
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Code = trimmed,
                ImportOrder = maxOrder,
                ImportedAt = now
            });
            existingCodes.Add(trimmed);
            imported++;
        }

        await repository.AddCampaignCodesAsync(newCodes, ct);

        logger.LogInformation(
            "Campaign {CampaignId}: imported {Imported} codes, skipped {Skipped} duplicates",
            campaignId, imported, skipped);
    }

    private async Task ImportGeneratedCodesAsync(Guid campaignId, IReadOnlyList<string> codes,
        CancellationToken ct = default)
    {
        var campaign = await repository.FindForMutationWithCodesAsync(campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        var now = clock.GetCurrentInstant();
        var maxOrder = campaign.Codes.Any() ? campaign.Codes.Max(c => c.ImportOrder) : 0;
        var newCodes = new List<CampaignCode>(codes.Count);

        foreach (var code in codes)
        {
            maxOrder++;
            newCodes.Add(new CampaignCode
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Code = code,
                ImportOrder = maxOrder,
                ImportedAt = now
            });
        }

        await repository.AddCampaignCodesAsync(newCodes, ct);

        logger.LogInformation(
            "Campaign {CampaignId}: imported {Count} vendor-generated codes",
            campaignId, codes.Count);
    }

    public async Task<CampaignGenerateCodesResult> GenerateAndImportDiscountCodesAsync(
        Guid campaignId,
        int count,
        string discountType,
        decimal discountValue,
        CancellationToken ct = default)
    {
        var campaign = await GetByIdAsync(campaignId, ct);
        if (campaign is null)
            return new CampaignGenerateCodesResult(false, "NotFound");

        if (campaign.Status != CampaignStatus.Draft)
            return new CampaignGenerateCodesResult(false, "NotDraft");

        if (count <= 0)
            return new CampaignGenerateCodesResult(false, "InvalidCount");

        if (!Enum.TryParse<DiscountType>(discountType, ignoreCase: true, out var parsedType))
            return new CampaignGenerateCodesResult(false, "InvalidDiscountType");

        var spec = new DiscountCodeSpec(count, parsedType, discountValue, ExpiresAt: null);
        var codes = await ticketVendorService.GenerateDiscountCodesAsync(spec, ct);
        await ImportGeneratedCodesAsync(campaignId, codes, ct);

        return new CampaignGenerateCodesResult(true, GeneratedCount: codes.Count);
    }

    public async Task ActivateAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await repository.FindForMutationWithCodesAsync(campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        if (campaign.Status != CampaignStatus.Draft)
            throw new InvalidOperationException(
                $"Campaign {campaignId} must be in Draft status to activate (current: {campaign.Status}).");

        if (campaign.Codes.Count == 0)
            throw new InvalidOperationException(
                $"Campaign {campaignId} must have at least one code before activation.");

        if (!campaign.EmailBodyTemplate.Contains("{{Code}}", StringComparison.Ordinal))
            logger.LogWarning(
                "Campaign {CampaignId} email template does not contain {{Code}} placeholder",
                campaignId);

        campaign.Status = CampaignStatus.Active;
        await repository.UpdateCampaignAsync(campaign, ct);

        logger.LogInformation("Campaign {CampaignId} activated", campaignId);
    }

    public async Task CompleteAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await repository.FindForMutationAsync(campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        if (campaign.Status != CampaignStatus.Active)
            throw new InvalidOperationException(
                $"Campaign {campaignId} must be in Active status to complete (current: {campaign.Status}).");

        campaign.Status = CampaignStatus.Completed;
        await repository.UpdateCampaignAsync(campaign, ct);

        logger.LogInformation("Campaign {CampaignId} completed", campaignId);
    }

    public async Task<WaveSendPreview> PreviewWaveSendAsync(Guid campaignId, Guid teamId,
        CancellationToken ct = default)
    {
        _ = await repository.FindForMutationAsync(campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        var activeTeamUserIds = await GetActiveTeamUserIdsAsync(teamId, ct);

        var alreadyGrantedSet = await repository.GetAlreadyGrantedUserIdsAsync(campaignId, ct);

        var notGranted = activeTeamUserIds
            .Where(id => !alreadyGrantedSet.Contains(id))
            .ToList();

        // CampaignCodes is always-on today; guard kept for future opt-outability.
        var optedOutCount = 0;
        foreach (var userId in notGranted)
        {
            if (await commPrefService.IsOptedOutAsync(userId, MessageCategory.CampaignCodes, ct))
                optedOutCount++;
        }

        var eligibleCount = notGranted.Count - optedOutCount;
        var availableCodes = await repository.CountAvailableCodesAsync(campaignId, ct);

        return new WaveSendPreview(
            EligibleCount: eligibleCount,
            AlreadyGrantedExcluded: activeTeamUserIds.Count(alreadyGrantedSet.Contains),
            UnsubscribedExcluded: optedOutCount,
            CodesAvailable: availableCodes,
            CodesRemainingAfterSend: availableCodes - eligibleCount);
    }

    public async Task<int> SendWaveAsync(Guid campaignId, Guid teamId, CancellationToken ct = default)
    {
        var campaign = await repository.FindForMutationAsync(campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        if (campaign.Status != CampaignStatus.Active)
            throw new InvalidOperationException(
                $"Campaign {campaignId} must be in Active status to send a wave (current: {campaign.Status}).");

        var activeTeamUserIds = await GetActiveTeamUserIdsAsync(teamId, ct);
        var alreadyGrantedSet = await repository.GetAlreadyGrantedUserIdsAsync(campaignId, ct);

        var candidateUserIds = activeTeamUserIds
            .Where(id => !alreadyGrantedSet.Contains(id))
            .ToList();

        var eligibleUserIds = new List<Guid>(candidateUserIds.Count);
        foreach (var userId in candidateUserIds)
        {
            if (!await commPrefService.IsOptedOutAsync(userId, MessageCategory.CampaignCodes, ct))
                eligibleUserIds.Add(userId);
        }

        if (eligibleUserIds.Count == 0)
            return 0;

        var users = await userService.GetUserInfosAsync(eligibleUserIds, ct);
        var notificationEmails = await userEmailService.GetNotificationTargetEmailsAsync(eligibleUserIds, ct);

        var availableCodes = await repository.GetAvailableCodesAsync(
            campaignId, eligibleUserIds.Count, ct);

        if (availableCodes.Count < eligibleUserIds.Count)
            throw new InvalidOperationException(
                $"Not enough codes available. Need {eligibleUserIds.Count}, have {availableCodes.Count}.");

        var now = clock.GetCurrentInstant();
        var failedCount = 0;
        var grantedUserIds = new List<Guid>(eligibleUserIds.Count);

        // Per-grant commit: on enqueue throw, flip just that grant to Failed
        // so subsequent grants still process and RetryAllFailedAsync can pick
        // it up next pass.
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

            if (!notificationEmails.TryGetValue(userId, out var recipientEmail))
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
                AssignedAt = now,
                LatestEmailStatus = EmailOutboxStatus.Queued,
                LatestEmailAt = now
            };
            await repository.AddGrantAndSaveAsync(grant, ct);
            grantedUserIds.Add(userId);

            try
            {
                await emailService.SendAsync(emailMessages.CampaignCode(
                    BuildCampaignCodeRequest(campaign, user, recipientEmail, code.Code, grant.Id)),
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to enqueue campaign code email for user {UserId} grant {GrantId} in campaign {CampaignId}",
                    userId, grant.Id, campaignId);
                await repository.UpdateGrantStatusAsync(grant.Id, EmailOutboxStatus.Failed, now, ct);
                failedCount++;
            }
        }

        logger.LogInformation(
            "Campaign {CampaignId}: sent wave to team {TeamId}, {Count} grants created, {FailedCount} failed to enqueue",
            campaignId, teamId, grantedUserIds.Count, failedCount);

        if (grantedUserIds.Count == 0)
            return 0;

        try
        {
            await notificationService.SendAsync(
                NotificationSource.CampaignReceived,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"You received a code from campaign: {campaign.Title}",
                grantedUserIds,
                body: "Check your email for your campaign code.",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch CampaignReceived notifications for campaign {CampaignId}", campaignId);
        }

        return grantedUserIds.Count;
    }

    public async Task ResendToGrantAsync(Guid grantId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantForResendAsync(grantId, ct)
            ?? throw new InvalidOperationException($"Grant {grantId} not found.");

        var now = clock.GetCurrentInstant();
        await repository.UpdateGrantStatusAsync(grantId, EmailOutboxStatus.Queued, now, ct);

        var user = await userService.GetUserInfoAsync(grant.UserId, ct)
            ?? throw new InvalidOperationException($"User {grant.UserId} for grant {grantId} not found.");
        var emails = await userEmailService.GetNotificationTargetEmailsAsync([grant.UserId], ct);
        if (!emails.TryGetValue(grant.UserId, out var recipientEmail))
            throw new InvalidOperationException(
                $"No notification email resolved for user {grant.UserId} when resending grant {grantId}.");

        await emailService.SendAsync(emailMessages.CampaignCode(
            BuildCampaignCodeRequest(
                grant.CampaignTitle,
                grant.CampaignEmailSubject,
                grant.CampaignEmailBodyTemplate,
                grant.CampaignReplyToAddress,
                user, recipientEmail, grant.CodeString, grant.GrantId)),
            ct);

        logger.LogInformation("Resent campaign email for grant {GrantId}", grantId);
    }

    public Task<int> MarkGrantsRedeemedAsync(
        IReadOnlyCollection<DiscountCodeRedemption> redemptions,
        CancellationToken ct = default)
    {
        return repository.MarkGrantsRedeemedAsync(redemptions, ct);
    }

    public async Task<CampaignCodeTrackingData> GetCodeTrackingAsync(CancellationToken ct = default)
    {
        var summaryRows = await repository.GetCodeTrackingSummariesAsync(ct);
        var grantRowsRaw = await repository.GetCodeTrackingGrantRowsAsync(ct);

        var userIds = grantRowsRaw.Select(r => r.UserId).Distinct().ToList();
        var users = userIds.Count > 0
            ? await userService.GetUserInfosAsync(userIds, ct)
            : new Dictionary<Guid, UserInfo>();

        var grantRows = new List<CampaignCodeTrackingGrant>(grantRowsRaw.Count);
        foreach (var row in grantRowsRaw)
        {
            var recipientName = users.TryGetValue(row.UserId, out var user)
                ? user.BurnerName
                : string.Empty;

            grantRows.Add(new CampaignCodeTrackingGrant(
                GrantId: row.GrantId,
                CampaignId: row.CampaignId,
                CampaignTitle: row.CampaignTitle,
                UserId: row.UserId,
                RecipientName: recipientName,
                Code: row.Code,
                RedeemedAt: row.RedeemedAt,
                LatestEmailStatus: row.LatestEmailStatus?.ToString()));
        }

        var grantsByCampaign = grantRowsRaw
            .GroupBy(r => r.CampaignId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var summaries = summaryRows
            .Select(s =>
            {
                grantsByCampaign.TryGetValue(s.CampaignId, out var grants);
                var total = grants?.Count ?? 0;
                var redeemed = grants?.Count(g => g.RedeemedAt is not null) ?? 0;
                return new CampaignCodeTrackingSummary(
                    s.CampaignId, s.CampaignTitle, total, redeemed);
            })
            .ToList();

        return new CampaignCodeTrackingData(summaries, grantRows);
    }

    public async Task RetryAllFailedAsync(Guid campaignId, CancellationToken ct = default)
    {
        var failedGrants = await repository.GetFailedGrantsForRetryAsync(campaignId, ct);
        if (failedGrants.Count == 0)
            return;

        var userIds = failedGrants.Select(g => g.UserId).Distinct().ToList();
        var users = await userService.GetUserInfosAsync(userIds, ct);
        var emails = await userEmailService.GetNotificationTargetEmailsAsync(userIds, ct);

        var now = clock.GetCurrentInstant();
        var stillFailedCount = 0;

        // Per-grant flip-and-enqueue: a batch flip-to-Queued + loop would lose
        // grants whose enqueue throws, leaving them un-retriable.
        foreach (var grant in failedGrants)
        {
            await repository.UpdateGrantStatusAsync(grant.GrantId, EmailOutboxStatus.Queued, now, ct);

            if (!users.TryGetValue(grant.UserId, out var user))
            {
                logger.LogWarning(
                    "User {UserId} missing when retrying grant {GrantId}; marking failed",
                    grant.UserId, grant.GrantId);
                await repository.UpdateGrantStatusAsync(grant.GrantId, EmailOutboxStatus.Failed, now, ct);
                stillFailedCount++;
                continue;
            }

            if (!emails.TryGetValue(grant.UserId, out var recipientEmail))
            {
                logger.LogWarning(
                    "No notification email for user {UserId} when retrying grant {GrantId}; marking failed",
                    grant.UserId, grant.GrantId);
                await repository.UpdateGrantStatusAsync(grant.GrantId, EmailOutboxStatus.Failed, now, ct);
                stillFailedCount++;
                continue;
            }

            try
            {
                await emailService.SendAsync(emailMessages.CampaignCode(
                    BuildCampaignCodeRequest(
                        grant.CampaignTitle,
                        grant.CampaignEmailSubject,
                        grant.CampaignEmailBodyTemplate,
                        grant.CampaignReplyToAddress,
                        user, recipientEmail, grant.CodeString, grant.GrantId)),
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Retry failed to re-enqueue campaign code email for grant {GrantId} in campaign {CampaignId}",
                    grant.GrantId, campaignId);
                await repository.UpdateGrantStatusAsync(grant.GrantId, EmailOutboxStatus.Failed, now, ct);
                stillFailedCount++;
            }
        }

        logger.LogInformation(
            "Campaign {CampaignId}: retried {Count} failed grants, {StillFailedCount} still failed",
            campaignId, failedGrants.Count, stillFailedCount);
    }

    private async Task<List<Guid>> GetActiveTeamUserIdsAsync(Guid teamId, CancellationToken ct)
    {
        var team = await teamService.GetTeamAsync(teamId, ct);
        return team?.Members.Select(tm => tm.UserId).ToList() ?? [];
    }

    private static CampaignCodeEmailRequest BuildCampaignCodeRequest(
        Campaign campaign, UserInfo user, string recipientEmail, string code, Guid grantId)
    {
        return new CampaignCodeEmailRequest(
            UserId: user.Id,
            CampaignGrantId: grantId,
            RecipientEmail: recipientEmail,
            RecipientName: user.BurnerName,
            Subject: campaign.EmailSubject,
            MarkdownBody: campaign.EmailBodyTemplate,
            Code: code,
            ReplyTo: campaign.ReplyToAddress);
    }

    private static CampaignCodeEmailRequest BuildCampaignCodeRequest(
        string campaignTitle, string emailSubject, string emailBody, string? replyToAddress,
        UserInfo user, string recipientEmail, string code, Guid grantId)
    {
        _ = campaignTitle; // kept for future rendering-context parameters; no-op today.
        return new CampaignCodeEmailRequest(
            UserId: user.Id,
            CampaignGrantId: grantId,
            RecipientEmail: recipientEmail,
            RecipientName: user.BurnerName,
            Subject: emailSubject,
            MarkdownBody: emailBody,
            Code: code,
            ReplyTo: replyToAddress);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var grants = await repository.GetGrantsForUserExportAsync(userId, ct);

        var shaped = grants.Select(g => new
        {
            CampaignTitle = g.CampaignTitle,
            Code = g.Code,
            AssignedAt = g.AssignedAt.ToInvariantInstantString(),
            RedeemedAt = g.RedeemedAt.ToInvariantInstantString(),
            EmailStatus = g.LatestEmailStatus?.ToString()
        }).ToList();

        return [new UserDataSlice(GdprExportSections.CampaignGrants, shaped)];
    }

    public Task<bool> UpdateGrantEmailStatusAsync(
        Guid grantId,
        EmailOutboxStatus status,
        Instant latestEmailAt,
        CancellationToken ct = default) =>
        repository.UpdateGrantStatusAsync(grantId, status, latestEmailAt, ct);

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct) =>
        repository.ReassignGrantsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
}
