using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Campaigns;

/// <summary>
/// Application-layer implementation of <see cref="ICampaignService"/>. Goes
/// through <see cref="ICampaignRepository"/> for owned-table access and
/// through <see cref="ITeamService"/> / <see cref="IUserEmailService"/> for
/// cross-section reads. Never imports <c>Microsoft.EntityFrameworkCore</c>.
/// </summary>
/// <remarks>
/// Per-grant commits during <see cref="SendWaveAsync"/> and
/// <see cref="RetryAllFailedAsync"/> are intentional. A batch flip-to-Queued
/// followed by a loop would lose grants whose enqueue throws: they would
/// leave the Failed set without a corresponding outbox row and never be
/// retriable again.
/// </remarks>
public sealed class CampaignService : ICampaignService, IUserDataContributor
{
    private readonly ICampaignRepository _repository;
    private readonly ITeamService _teamService;
    private readonly IUserEmailService _userEmailService;
    private readonly IUserService _userService;
    private readonly INotificationService _notificationService;
    private readonly ICommunicationPreferenceService _commPrefService;
    private readonly IEmailService _emailService;
    private readonly IClock _clock;
    private readonly ILogger<CampaignService> _logger;

    public CampaignService(
        ICampaignRepository repository,
        ITeamService teamService,
        IUserEmailService userEmailService,
        IUserService userService,
        INotificationService notificationService,
        ICommunicationPreferenceService commPrefService,
        IEmailService emailService,
        IClock clock,
        ILogger<CampaignService> logger)
    {
        _repository = repository;
        _teamService = teamService;
        _userEmailService = userEmailService;
        _userService = userService;
        _notificationService = notificationService;
        _commPrefService = commPrefService;
        _emailService = emailService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Campaign> CreateAsync(string title, string? description,
        string emailSubject, string emailBodyTemplate, string? replyToAddress,
        Guid createdByUserId, CancellationToken ct = default)
    {
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            EmailSubject = emailSubject,
            EmailBodyTemplate = emailBodyTemplate,
            ReplyToAddress = string.IsNullOrWhiteSpace(replyToAddress) ? null : replyToAddress.Trim(),
            Status = CampaignStatus.Draft,
            CreatedAt = _clock.GetCurrentInstant(),
            CreatedByUserId = createdByUserId
        };

        _repository.AddCampaign(campaign);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Campaign {CampaignId} created: {Title}", campaign.Id, title);
        return campaign;
    }

    public Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        _repository.GetActiveOrCompletedGrantsForUserAsync(userId, ct);

    public Task<IReadOnlyList<CampaignGrant>> GetAllGrantsForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        _repository.GetAllGrantsForUserAsync(userId, ct);

    public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _repository.GetByIdAsync(id, ct);

    public async Task<bool> UpdateAsync(
        Guid id,
        string title,
        string? description,
        string emailSubject,
        string emailBodyTemplate,
        string? replyToAddress,
        CancellationToken ct = default)
    {
        var campaign = await _repository.FindForMutationAsync(id, ct);
        if (campaign is null)
            return false;

        campaign.Title = title.Trim();
        campaign.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        campaign.EmailSubject = emailSubject.Trim();
        campaign.EmailBodyTemplate = emailBodyTemplate.Trim();
        campaign.ReplyToAddress = string.IsNullOrWhiteSpace(replyToAddress) ? null : replyToAddress.Trim();

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Campaign {CampaignId} updated", id);
        return true;
    }

    public Task<List<Campaign>> GetAllAsync(CancellationToken ct = default) =>
        _repository.GetAllAsync(ct);

    public async Task<CampaignDetailPageDto?> GetDetailPageAsync(Guid id, CancellationToken ct = default)
    {
        var campaign = await GetByIdAsync(id, ct);
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
            campaign,
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
        var campaign = await GetByIdAsync(campaignId, ct);
        if (campaign is null)
            return null;

        var teams = await _teamService.GetActiveTeamOptionsAsync(ct);
        var teamOptions = teams
            .Select(t => new CampaignTeamOptionDto(t.Id, t.Name))
            .ToList();

        var preview = teamId.HasValue
            ? await PreviewWaveSendAsync(campaignId, teamId.Value, ct)
            : null;

        return new CampaignSendWavePageDto(campaign, teamOptions, teamId, preview);
    }

    public Task<Guid?> GetCampaignIdForGrantAsync(Guid grantId, CancellationToken ct = default) =>
        _repository.GetCampaignIdForGrantAsync(grantId, ct);

    public async Task ImportCodesAsync(Guid campaignId, IEnumerable<string> codes, CancellationToken ct = default)
    {
        var campaign = await _repository.FindForMutationWithCodesAsync(campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        var existingCodes = campaign.Codes
            .Select(c => c.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var now = _clock.GetCurrentInstant();
        var imported = 0;
        var skipped = 0;
        var maxOrder = campaign.Codes.Any() ? campaign.Codes.Max(c => c.ImportOrder) : 0;

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
            _repository.AddCampaignCode(new CampaignCode
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

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Campaign {CampaignId}: imported {Imported} codes, skipped {Skipped} duplicates",
            campaignId, imported, skipped);
    }

    public async Task ImportGeneratedCodesAsync(Guid campaignId, IReadOnlyList<string> codes,
        CancellationToken ct = default)
    {
        var campaign = await _repository.FindForMutationWithCodesAsync(campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        var now = _clock.GetCurrentInstant();
        var maxOrder = campaign.Codes.Any() ? campaign.Codes.Max(c => c.ImportOrder) : 0;

        foreach (var code in codes)
        {
            maxOrder++;
            _repository.AddCampaignCode(new CampaignCode
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Code = code,
                ImportOrder = maxOrder,
                ImportedAt = now
            });
        }

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Campaign {CampaignId}: imported {Count} vendor-generated codes",
            campaignId, codes.Count);
    }

    public async Task ActivateAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await _repository.FindForMutationWithCodesAsync(campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        if (campaign.Status != CampaignStatus.Draft)
            throw new InvalidOperationException(
                $"Campaign {campaignId} must be in Draft status to activate (current: {campaign.Status}).");

        if (campaign.Codes.Count == 0)
            throw new InvalidOperationException(
                $"Campaign {campaignId} must have at least one code before activation.");

        if (!campaign.EmailBodyTemplate.Contains("{{Code}}", StringComparison.Ordinal))
            _logger.LogWarning(
                "Campaign {CampaignId} email template does not contain {{Code}} placeholder",
                campaignId);

        campaign.Status = CampaignStatus.Active;
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Campaign {CampaignId} activated", campaignId);
    }

    public async Task CompleteAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await _repository.FindForMutationAsync(campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        if (campaign.Status != CampaignStatus.Active)
            throw new InvalidOperationException(
                $"Campaign {campaignId} must be in Active status to complete (current: {campaign.Status}).");

        campaign.Status = CampaignStatus.Completed;
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Campaign {CampaignId} completed", campaignId);
    }

    public async Task<WaveSendPreview> PreviewWaveSendAsync(Guid campaignId, Guid teamId,
        CancellationToken ct = default)
    {
        _ = await _repository.FindForMutationAsync(campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        var activeTeamUserIds = await GetActiveTeamUserIdsAsync(teamId, ct);

        var alreadyGrantedSet = await _repository.GetAlreadyGrantedUserIdsAsync(campaignId, ct);

        var notGranted = activeTeamUserIds
            .Where(id => !alreadyGrantedSet.Contains(id))
            .ToList();

        // CampaignCodes is an always-on category, so IsOptedOutAsync returns false for every user.
        // Kept as a guard in case the category ever becomes opt-outable.
        var optedOutCount = 0;
        foreach (var userId in notGranted)
        {
            if (await _commPrefService.IsOptedOutAsync(userId, MessageCategory.CampaignCodes, ct))
                optedOutCount++;
        }

        var eligibleCount = notGranted.Count - optedOutCount;
        var availableCodes = await _repository.CountAvailableCodesAsync(campaignId, ct);

        return new WaveSendPreview(
            EligibleCount: eligibleCount,
            AlreadyGrantedExcluded: activeTeamUserIds.Count(id => alreadyGrantedSet.Contains(id)),
            UnsubscribedExcluded: optedOutCount,
            CodesAvailable: availableCodes,
            CodesRemainingAfterSend: availableCodes - eligibleCount);
    }

    public async Task<int> SendWaveAsync(Guid campaignId, Guid teamId, CancellationToken ct = default)
    {
        var campaign = await _repository.FindForMutationAsync(campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        if (campaign.Status != CampaignStatus.Active)
            throw new InvalidOperationException(
                $"Campaign {campaignId} must be in Active status to send a wave (current: {campaign.Status}).");

        // Get eligible users (not already granted, not opted out).
        var activeTeamUserIds = await GetActiveTeamUserIdsAsync(teamId, ct);
        var alreadyGrantedSet = await _repository.GetAlreadyGrantedUserIdsAsync(campaignId, ct);

        var candidateUserIds = activeTeamUserIds
            .Where(id => !alreadyGrantedSet.Contains(id))
            .ToList();

        // Filter out users who have opted out of CampaignCodes (always-on today; this is a no-op).
        var eligibleUserIds = new List<Guid>(candidateUserIds.Count);
        foreach (var userId in candidateUserIds)
        {
            if (!await _commPrefService.IsOptedOutAsync(userId, MessageCategory.CampaignCodes, ct))
                eligibleUserIds.Add(userId);
        }

        if (eligibleUserIds.Count == 0)
            return 0;

        // Cross-section fetch: users (for DisplayName) and notification emails.
        var users = await _userService.GetByIdsAsync(eligibleUserIds, ct);
        var notificationEmails = await _userEmailService.GetNotificationTargetEmailsAsync(eligibleUserIds, ct);

        // Get available codes ordered by ImportedAt, Id.
        var availableCodes = await _repository.GetAvailableCodesAsync(
            campaignId, eligibleUserIds.Count, ct);

        if (availableCodes.Count < eligibleUserIds.Count)
            throw new InvalidOperationException(
                $"Not enough codes available. Need {eligibleUserIds.Count}, have {availableCodes.Count}.");

        var now = _clock.GetCurrentInstant();
        var failedCount = 0;

        // Persist and enqueue one grant at a time. If an enqueue throws mid-loop,
        // we flip that single grant to Failed so subsequent grants still get
        // processed and RetryAllFailedAsync picks the failed ones up next pass.
        for (var i = 0; i < eligibleUserIds.Count; i++)
        {
            var userId = eligibleUserIds[i];
            var code = availableCodes[i];

            if (!users.TryGetValue(userId, out var user))
            {
                _logger.LogWarning(
                    "User {UserId} eligible for campaign {CampaignId} but lookup returned no row; skipping",
                    userId, campaignId);
                continue;
            }

            if (!notificationEmails.TryGetValue(userId, out var recipientEmail))
            {
                _logger.LogWarning(
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
            await _repository.AddGrantAndSaveAsync(grant, ct);

            try
            {
                await _emailService.SendCampaignCodeAsync(
                    BuildCampaignCodeRequest(campaign, user, recipientEmail, code.Code, grant.Id),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to enqueue campaign code email for user {UserId} grant {GrantId} in campaign {CampaignId}",
                    userId, grant.Id, campaignId);
                await _repository.UpdateGrantStatusAsync(grant.Id, EmailOutboxStatus.Failed, now, ct);
                failedCount++;
            }
        }

        _logger.LogInformation(
            "Campaign {CampaignId}: sent wave to team {TeamId}, {Count} grants created, {FailedCount} failed to enqueue",
            campaignId, teamId, eligibleUserIds.Count, failedCount);

        // In-app notification to each recipient (best-effort).
        try
        {
            await _notificationService.SendAsync(
                NotificationSource.CampaignReceived,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"You received a code from campaign: {campaign.Title}",
                eligibleUserIds,
                body: "Check your email for your campaign code.",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch CampaignReceived notifications for campaign {CampaignId}", campaignId);
        }

        return eligibleUserIds.Count;
    }

    public async Task ResendToGrantAsync(Guid grantId, CancellationToken ct = default)
    {
        var grant = await _repository.GetGrantForResendAsync(grantId, ct)
            ?? throw new InvalidOperationException($"Grant {grantId} not found.");

        var now = _clock.GetCurrentInstant();
        await _repository.UpdateGrantStatusAsync(grantId, EmailOutboxStatus.Queued, now, ct);

        // Cross-section user + notification email resolution.
        var user = await _userService.GetByIdAsync(grant.UserId, ct)
            ?? throw new InvalidOperationException($"User {grant.UserId} for grant {grantId} not found.");
        var emails = await _userEmailService.GetNotificationTargetEmailsAsync([grant.UserId], ct);
        if (!emails.TryGetValue(grant.UserId, out var recipientEmail))
            throw new InvalidOperationException(
                $"No notification email resolved for user {grant.UserId} when resending grant {grantId}.");

        await _emailService.SendCampaignCodeAsync(
            BuildCampaignCodeRequest(
                grant.CampaignTitle,
                grant.CampaignEmailSubject,
                grant.CampaignEmailBodyTemplate,
                grant.CampaignReplyToAddress,
                user, recipientEmail, grant.CodeString, grant.GrantId),
            ct);

        _logger.LogInformation("Resent campaign email for grant {GrantId}", grantId);
    }

    public Task<int> MarkGrantsRedeemedAsync(
        IReadOnlyCollection<DiscountCodeRedemption> redemptions,
        CancellationToken ct = default)
    {
        return _repository.MarkGrantsRedeemedAsync(redemptions, ct);
    }

    public async Task RetryAllFailedAsync(Guid campaignId, CancellationToken ct = default)
    {
        var failedGrants = await _repository.GetFailedGrantsForRetryAsync(campaignId, ct);
        if (failedGrants.Count == 0)
            return;

        var userIds = failedGrants.Select(g => g.UserId).Distinct().ToList();
        var users = await _userService.GetByIdsAsync(userIds, ct);
        var emails = await _userEmailService.GetNotificationTargetEmailsAsync(userIds, ct);

        var now = _clock.GetCurrentInstant();
        var stillFailedCount = 0;

        // Flip-and-enqueue one grant at a time. A batch flip-to-Queued + loop
        // enqueue would lose grants whose enqueue throws: they would leave the
        // Failed set without a corresponding outbox row and never be retriable.
        foreach (var grant in failedGrants)
        {
            await _repository.UpdateGrantStatusAsync(grant.GrantId, EmailOutboxStatus.Queued, now, ct);

            if (!users.TryGetValue(grant.UserId, out var user))
            {
                _logger.LogWarning(
                    "User {UserId} missing when retrying grant {GrantId}; marking failed",
                    grant.UserId, grant.GrantId);
                await _repository.UpdateGrantStatusAsync(grant.GrantId, EmailOutboxStatus.Failed, now, ct);
                stillFailedCount++;
                continue;
            }

            if (!emails.TryGetValue(grant.UserId, out var recipientEmail))
            {
                _logger.LogWarning(
                    "No notification email for user {UserId} when retrying grant {GrantId}; marking failed",
                    grant.UserId, grant.GrantId);
                await _repository.UpdateGrantStatusAsync(grant.GrantId, EmailOutboxStatus.Failed, now, ct);
                stillFailedCount++;
                continue;
            }

            try
            {
                await _emailService.SendCampaignCodeAsync(
                    BuildCampaignCodeRequest(
                        grant.CampaignTitle,
                        grant.CampaignEmailSubject,
                        grant.CampaignEmailBodyTemplate,
                        grant.CampaignReplyToAddress,
                        user, recipientEmail, grant.CodeString, grant.GrantId),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Retry failed to re-enqueue campaign code email for grant {GrantId} in campaign {CampaignId}",
                    grant.GrantId, campaignId);
                await _repository.UpdateGrantStatusAsync(grant.GrantId, EmailOutboxStatus.Failed, now, ct);
                stillFailedCount++;
            }
        }

        _logger.LogInformation(
            "Campaign {CampaignId}: retried {Count} failed grants, {StillFailedCount} still failed",
            campaignId, failedGrants.Count, stillFailedCount);
    }

    private async Task<List<Guid>> GetActiveTeamUserIdsAsync(Guid teamId, CancellationToken ct)
    {
        var members = await _teamService.GetTeamMembersAsync(teamId, ct);
        return members
            .Where(tm => tm.LeftAt == null)
            .Select(tm => tm.UserId)
            .ToList();
    }

    /// <summary>
    /// Build a <see cref="CampaignCodeEmailRequest"/> from a tracked campaign
    /// and the resolved recipient. The outbox email service performs the
    /// actual markdown → HTML rendering and template wrapping — keeping
    /// email_outbox_messages ownership in a single place.
    /// </summary>
    private static CampaignCodeEmailRequest BuildCampaignCodeRequest(
        Campaign campaign, User user, string recipientEmail, string code, Guid grantId)
    {
        return new CampaignCodeEmailRequest(
            UserId: user.Id,
            CampaignGrantId: grantId,
            RecipientEmail: recipientEmail,
            RecipientName: user.DisplayName,
            Subject: campaign.EmailSubject,
            MarkdownBody: campaign.EmailBodyTemplate,
            Code: code,
            ReplyTo: campaign.ReplyToAddress);
    }

    private static CampaignCodeEmailRequest BuildCampaignCodeRequest(
        string campaignTitle, string emailSubject, string emailBody, string? replyToAddress,
        User user, string recipientEmail, string code, Guid grantId)
    {
        _ = campaignTitle; // kept for future rendering-context parameters; no-op today.
        return new CampaignCodeEmailRequest(
            UserId: user.Id,
            CampaignGrantId: grantId,
            RecipientEmail: recipientEmail,
            RecipientName: user.DisplayName,
            Subject: emailSubject,
            MarkdownBody: emailBody,
            Code: code,
            ReplyTo: replyToAddress);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var grants = await _repository.GetGrantsForUserExportAsync(userId, ct);

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
}
