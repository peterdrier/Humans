using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Resources;
using Humans.AuditLog.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Campaigns.Contracts;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Campaigns.Services.Dtos;
using Humans.Base.Enums;
using Humans.Gdpr.Contracts;
using NodaTime;

namespace Humans.Campaigns.Services;

internal sealed class CampaignService(
    ICampaignRepository repository,
    ITeamService teamService,
    IUserEmailService userEmailService,
    IUserService userService,
    INotificationEmitter notificationService,
    IEmailService emailService,
    CampaignsEmails emailMessages,
    ITicketDiscountCodes ticketDiscountCodes,
    IClock clock,
    IAuditLogService audit,
    ILogger<CampaignService> logger) : ICampaignManagement, IUserDataContributor, IUserMerge
{
    private static readonly ResourceManager NoticeResources = new(typeof(CampaignsResource));

    internal const string CampaignGrants = "CampaignGrants";

    public async Task<CampaignCreateResult> CreateAsync(string title, string? description,
        string emailSubject, string emailBodyTemplate, string? replyToAddress,
        Guid createdByUserId, CancellationToken ct = default)
    {
        var errorKey = ValidateCampaignInput(title, description, emailSubject, emailBodyTemplate, replyToAddress);
        if (errorKey is not null)
            return new CampaignCreateResult(false, ErrorKey: errorKey);

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
        await audit.LogAsync(AuditAction.CampaignCreated, "Campaign", campaign.Id,
            "Created campaign.", createdByUserId);

        logger.LogInformation("Campaign {CampaignId} created: {Title}", campaign.Id, title);
        return new CampaignCreateResult(true, campaign);
    }

    private static string? ValidateCampaignInput(string title, string? description,
        string emailSubject, string emailBodyTemplate, string? replyToAddress)
    {
        if (string.IsNullOrWhiteSpace(title)) return "TitleRequired";
        if (string.IsNullOrWhiteSpace(emailSubject)) return "EmailSubjectRequired";
        if (string.IsNullOrWhiteSpace(emailBodyTemplate)) return "EmailBodyTemplateRequired";
        if (title.Trim().Length > 200) return "TitleTooLong";
        if (description?.Trim().Length > 2000) return "DescriptionTooLong";
        if (emailSubject.Trim().Length > Campaign.EmailSubjectMaxLength) return "EmailSubjectTooLong";
        if (!string.IsNullOrWhiteSpace(replyToAddress))
        {
            var address = replyToAddress.Trim();
            if (address.Length > 320) return "ReplyToAddressTooLong";
            if (!new EmailAddressAttribute().IsValid(address)) return "ReplyToAddressInvalid";
        }
        return null;
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
        Guid actorUserId,
        Guid id,
        string title,
        string? description,
        string emailSubject,
        string emailBodyTemplate,
        string? replyToAddress,
        CancellationToken ct = default)
    {
        var errorKey = ValidateCampaignInput(title, description, emailSubject, emailBodyTemplate, replyToAddress);
        if (errorKey is not null)
            return new CampaignUpdateResult(false, errorKey);

        var campaign = await repository.FindForMutationAsync(id, ct);
        if (campaign is null)
            return new CampaignUpdateResult(false, "NotFound");

        campaign.Title = title.Trim();
        campaign.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        campaign.EmailSubject = emailSubject.Trim();
        campaign.EmailBodyTemplate = emailBodyTemplate.Trim();
        campaign.ReplyToAddress = string.IsNullOrWhiteSpace(replyToAddress) ? null : replyToAddress.Trim();

        await repository.UpdateCampaignAsync(campaign, ct);

        await audit.LogAsync(AuditAction.CampaignUpdated, "Campaign", id,
            "Updated campaign configuration.", actorUserId);

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
        var codesRedeemed = campaign.Grants.Count(g => g.RedeemedAt is not null);
        var totalGrants = campaign.Grants.Count;

        return new CampaignDetailPageDto(
            ToAdminSummary(campaign),
            new CampaignDetailStatsDto(
                totalCodes,
                availableCodes,
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

    public async Task<CampaignImportResult> ImportCodesAsync(Guid actorUserId, Guid campaignId, IEnumerable<string> codes, CancellationToken ct = default)
    {
        var campaign = await repository.FindForMutationWithCodesAsync(campaignId, ct);
        if (campaign is null)
            return new CampaignImportResult(false, ErrorKey: "NotFound");

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

            var characterCount = trimmed.EnumerateRunes().Count();
            if (characterCount > CampaignCode.CodeMaxLength)
            {
                logger.LogWarning("Rejected campaign {CampaignId} code import: length {Length} exceeds {Limit}",
                    campaignId, characterCount, CampaignCode.CodeMaxLength);
                return new CampaignImportResult(false, ErrorKey: "CodeTooLong");
            }

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
        await audit.LogAsync(AuditAction.CampaignCodesImported, "Campaign", campaignId,
            $"Imported {newCodes.Count} codes.", actorUserId);

        logger.LogInformation(
            "Campaign {CampaignId}: imported {Imported} codes, skipped {Skipped} duplicates",
            campaignId, imported, skipped);
        return new CampaignImportResult(true, imported, skipped);
    }

    [ExternalWrite]
    public async Task<CampaignGenerateCodesResult> GenerateAndImportDiscountCodesAsync(
        Guid actorUserId,
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

        if (!Enum.TryParse<TicketDiscountKind>(discountType, ignoreCase: true, out var parsedKind) ||
            !Enum.IsDefined(parsedKind))
            return new CampaignGenerateCodesResult(false, "InvalidDiscountType");

        // Through Tickets' contract, never the Base vendor port: Tickets is the only door to ticketing.
        var request = new TicketDiscountCodeRequest(count, parsedKind, discountValue, ExpiresAt: null);
        var codes = await ticketDiscountCodes.GenerateAsync(request, ct);
        var imported = await ImportCodesAsync(actorUserId, campaignId, codes, ct);

        return imported.Success
            ? new CampaignGenerateCodesResult(true, GeneratedCount: imported.Imported)
            : new CampaignGenerateCodesResult(false, imported.ErrorKey);
    }

    public async Task<CampaignUpdateResult> ActivateAsync(Guid actorUserId, Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await repository.FindForMutationWithCodesAsync(campaignId, ct);
        if (campaign is null)
            return new CampaignUpdateResult(false, "NotFound");

        if (campaign.Status != CampaignStatus.Draft)
            return new CampaignUpdateResult(false, "NotDraft");

        if (campaign.Codes.Count == 0)
            return new CampaignUpdateResult(false, "NoCodes");

        if (!campaign.EmailBodyTemplate.Contains("{{Code}}", StringComparison.Ordinal))
            logger.LogWarning(
                "Campaign {CampaignId} email template does not contain {{Code}} placeholder",
                campaignId);

        campaign.Status = CampaignStatus.Active;
        await repository.UpdateCampaignAsync(campaign, ct);

        await audit.LogAsync(AuditAction.CampaignActivated, "Campaign", campaignId,
            "Activated campaign.", actorUserId);

        logger.LogInformation("Campaign {CampaignId} activated", campaignId);
        return new CampaignUpdateResult(true);
    }

    public async Task<CampaignUpdateResult> CompleteAsync(Guid actorUserId, Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await repository.FindForMutationAsync(campaignId, ct);
        if (campaign is null)
            return new CampaignUpdateResult(false, "NotFound");

        if (campaign.Status != CampaignStatus.Active)
            return new CampaignUpdateResult(false, "NotActive");

        campaign.Status = CampaignStatus.Completed;
        await repository.UpdateCampaignAsync(campaign, ct);

        await audit.LogAsync(AuditAction.CampaignCompleted, "Campaign", campaignId,
            "Completed campaign.", actorUserId);

        logger.LogInformation("Campaign {CampaignId} completed", campaignId);
        return new CampaignUpdateResult(true);
    }

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
        var notificationEmails = await userEmailService.GetNotificationTargetEmailsAsync(eligibleUserIds, ct);

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
                AssignedAt = now
            };
            await repository.AddGrantAndSaveAsync(grant, ct);
            await audit.LogAsync(AuditAction.CampaignWaveSent, "Campaign", campaignId,
                $"Assigned grant {grant.Id} in wave to team {teamId}.", actorUserId,
                relatedEntityId: userId, relatedEntityType: "User");
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
                    NotificationSource.CampaignReceived,
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

        return new CampaignSendWaveResult(true, SentCount: grantedUserIds.Count);
    }

    public async Task ResendToGrantAsync(Guid actorUserId, Guid grantId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantForResendAsync(grantId, ct)
            ?? throw new InvalidOperationException($"Grant {grantId} not found.");

        await audit.LogAsync(AuditAction.CampaignGrantResent, "CampaignGrant", grantId,
            $"Started resend for campaign {grant.CampaignId}.", actorUserId,
            relatedEntityId: grant.UserId, relatedEntityType: "User");

        try
        {
            var user = await userService.GetUserInfoAsync(grant.UserId, ct)
                ?? throw new InvalidOperationException($"User {grant.UserId} for grant {grantId} not found.");
            var emails = await userEmailService.GetNotificationTargetEmailsAsync([grant.UserId], ct);
            if (!emails.TryGetValue(grant.UserId, out var recipientEmail))
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
                RedeemedAt: row.RedeemedAt));
        }

        var grantsByCampaign = grantRowsRaw
            .GroupBy(r => r.CampaignId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var summaries = summaryRows
            .OrderByDescending(s => s.CreatedAt)
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

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        userId = (await userService.GetUserInfoAsync(userId, ct))?.Id ?? userId;
        var grants = await repository.GetGrantsForUserExportAsync(userId, ct);

        var shaped = grants.Select(g => new
        {
            g.CampaignTitle,
            g.Code,
            AssignedAt = g.AssignedAt.ToIso8601(),
            RedeemedAt = g.RedeemedAt.ToIso8601(),
            EmailStatus = g.LatestEmailStatus?.ToString()
        }).ToList();

        return [new UserDataSlice(CampaignGrants, shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CampaignGrants] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>A grant is a user-to-code link and nothing else, so it is hard-deleted.</summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repository.DeleteGrantsForUserAsync(userId, ct);

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct) =>
        repository.ReassignGrantsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
}
