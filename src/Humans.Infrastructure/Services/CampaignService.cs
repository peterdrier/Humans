using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class CampaignService : ICampaignService, IUserDataContributor
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly INotificationService _notificationService;
    private readonly ICommunicationPreferenceService _commPrefService;
    private readonly IUserEmailService _userEmailService;
    private readonly IEmailService _emailService;
    private readonly ILogger<CampaignService> _logger;

    public CampaignService(
        HumansDbContext dbContext,
        IClock clock,
        INotificationService notificationService,
        ICommunicationPreferenceService commPrefService,
        IUserEmailService userEmailService,
        IEmailService emailService,
        ILogger<CampaignService> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _notificationService = notificationService;
        _commPrefService = commPrefService;
        _userEmailService = userEmailService;
        _emailService = emailService;
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

        _dbContext.Campaigns.Add(campaign);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Campaign {CampaignId} created: {Title}", campaign.Id, title);
        return campaign;
    }

    public async Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.CampaignGrants
            .AsNoTracking()
            .Include(g => g.Campaign)
            .Include(g => g.Code)
            .Where(g => g.UserId == userId
                && (g.Campaign.Status == CampaignStatus.Active || g.Campaign.Status == CampaignStatus.Completed))
            .OrderByDescending(g => g.AssignedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CampaignGrant>> GetAllGrantsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.CampaignGrants
            .AsNoTracking()
            .Include(g => g.Campaign)
            .Include(g => g.Code)
            .Where(g => g.UserId == userId)
            .OrderByDescending(g => g.AssignedAt)
            .ToListAsync(ct);
    }

    public async Task<Campaign?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _dbContext.Campaigns
            .Include(c => c.Codes)
            .Include(c => c.Grants).ThenInclude(g => g.User)
            .Include(c => c.Grants).ThenInclude(g => g.Code)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<bool> UpdateAsync(
        Guid id,
        string title,
        string? description,
        string emailSubject,
        string emailBodyTemplate,
        string? replyToAddress,
        CancellationToken ct = default)
    {
        var campaign = await _dbContext.Campaigns
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return false;
        }

        campaign.Title = title.Trim();
        campaign.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        campaign.EmailSubject = emailSubject.Trim();
        campaign.EmailBodyTemplate = emailBodyTemplate.Trim();
        campaign.ReplyToAddress = string.IsNullOrWhiteSpace(replyToAddress) ? null : replyToAddress.Trim();

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Campaign {CampaignId} updated", id);
        return true;
    }

    public async Task<List<Campaign>> GetAllAsync(CancellationToken ct = default)
    {
        return await _dbContext.Campaigns
            .Include(c => c.Codes)
            .Include(c => c.Grants)
            .AsSplitQuery()
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<CampaignDetailPageDto?> GetDetailPageAsync(Guid id, CancellationToken ct = default)
    {
        var campaign = await GetByIdAsync(id, ct);
        if (campaign is null)
        {
            return null;
        }

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
        {
            return null;
        }

        var teams = await _dbContext.Teams
            .OrderBy(t => t.Name)
            .Select(t => new CampaignTeamOptionDto(t.Id, t.Name))
            .ToListAsync(ct);

        var preview = teamId.HasValue
            ? await PreviewWaveSendAsync(campaignId, teamId.Value, ct)
            : null;

        return new CampaignSendWavePageDto(campaign, teams, teamId, preview);
    }

    public async Task<Guid?> GetCampaignIdForGrantAsync(Guid grantId, CancellationToken ct = default)
    {
        return await _dbContext.CampaignGrants
            .Where(g => g.Id == grantId)
            .Select(g => (Guid?)g.CampaignId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task ImportCodesAsync(Guid campaignId, IEnumerable<string> codes, CancellationToken ct = default)
    {
        var campaign = await _dbContext.Campaigns
            .Include(c => c.Codes)
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
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
            _dbContext.CampaignCodes.Add(new CampaignCode
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

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Campaign {CampaignId}: imported {Imported} codes, skipped {Skipped} duplicates",
            campaignId, imported, skipped);
    }

    public async Task ImportGeneratedCodesAsync(Guid campaignId, IReadOnlyList<string> codes,
        CancellationToken ct = default)
    {
        var campaign = await _dbContext.Campaigns
            .Include(c => c.Codes)
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        var now = _clock.GetCurrentInstant();
        var maxOrder = campaign.Codes.Any() ? campaign.Codes.Max(c => c.ImportOrder) : 0;

        foreach (var code in codes)
        {
            maxOrder++;
            _dbContext.CampaignCodes.Add(new CampaignCode
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Code = code,
                ImportOrder = maxOrder,
                ImportedAt = now
            });
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Campaign {CampaignId}: imported {Count} vendor-generated codes",
            campaignId, codes.Count);
    }

    public async Task ActivateAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await _dbContext.Campaigns
            .Include(c => c.Codes)
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
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
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Campaign {CampaignId} activated", campaignId);
    }

    public async Task CompleteAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await _dbContext.Campaigns
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        if (campaign.Status != CampaignStatus.Active)
            throw new InvalidOperationException(
                $"Campaign {campaignId} must be in Active status to complete (current: {campaign.Status}).");

        campaign.Status = CampaignStatus.Completed;
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Campaign {CampaignId} completed", campaignId);
    }

    public async Task<WaveSendPreview> PreviewWaveSendAsync(Guid campaignId, Guid teamId,
        CancellationToken ct = default)
    {
        _ = await _dbContext.Campaigns
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        var activeTeamUserIds = await GetActiveTeamUserIdsAsync(teamId, ct);

        var alreadyGrantedUserIds = await _dbContext.CampaignGrants
            .Where(g => g.CampaignId == campaignId)
            .Select(g => g.UserId)
            .Distinct()
            .ToListAsync(ct);

        var alreadyGrantedSet = alreadyGrantedUserIds.ToHashSet();

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
        var availableCodes = await CountAvailableCodesAsync(campaignId, ct);

        return new WaveSendPreview(
            EligibleCount: eligibleCount,
            AlreadyGrantedExcluded: activeTeamUserIds.Count(id => alreadyGrantedSet.Contains(id)),
            UnsubscribedExcluded: optedOutCount,
            CodesAvailable: availableCodes,
            CodesRemainingAfterSend: availableCodes - eligibleCount);
    }

    public async Task<int> SendWaveAsync(Guid campaignId, Guid teamId, CancellationToken ct = default)
    {
        var campaign = await _dbContext.Campaigns
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        if (campaign.Status != CampaignStatus.Active)
            throw new InvalidOperationException(
                $"Campaign {campaignId} must be in Active status to send a wave (current: {campaign.Status}).");

        // Get eligible users
        var activeTeamUserIds = await GetActiveTeamUserIdsAsync(teamId, ct);

        var alreadyGrantedUserIds = await _dbContext.CampaignGrants
            .Where(g => g.CampaignId == campaignId)
            .Select(g => g.UserId)
            .Distinct()
            .ToListAsync(ct);
        var alreadyGrantedSet = alreadyGrantedUserIds.ToHashSet();

        var candidateUsers = await _dbContext.Users
            .Where(u => activeTeamUserIds.Contains(u.Id)
                        && !alreadyGrantedSet.Contains(u.Id))
            .ToListAsync(ct);

        // Filter out users who have opted out of CampaignCodes (always-on today, so this is a no-op).
        var eligibleUsers = new List<User>(candidateUsers.Count);
        foreach (var user in candidateUsers)
        {
            if (!await _commPrefService.IsOptedOutAsync(user.Id, MessageCategory.CampaignCodes, ct))
                eligibleUsers.Add(user);
        }

        if (eligibleUsers.Count == 0)
            return 0;

        // Get available codes ordered by ImportedAt, Id
        var availableCodes = await _dbContext.CampaignCodes
            .Where(c => c.CampaignId == campaignId
                        && !_dbContext.CampaignGrants.Any(g => g.CampaignCodeId == c.Id))
            .OrderBy(c => c.ImportOrder)
            .Take(eligibleUsers.Count)
            .ToListAsync(ct);

        if (availableCodes.Count < eligibleUsers.Count)
            throw new InvalidOperationException(
                $"Not enough codes available. Need {eligibleUsers.Count}, have {availableCodes.Count}.");

        var now = _clock.GetCurrentInstant();
        var failedCount = 0;

        // Batch-resolve notification emails for all eligible users via IUserEmailService,
        // avoiding a cross-domain .Include(u => u.UserEmails).
        var notificationEmails = await _userEmailService
            .GetNotificationEmailsByUserIdsAsync(eligibleUsers.Select(u => u.Id), ct);

        // Persist and enqueue one grant at a time. If an enqueue throws mid-loop, we
        // flip that single grant to Failed locally so subsequent grants still get
        // processed and RetryAllFailedAsync picks the failed ones up on the next pass.
        // A batch-level save-then-enqueue would orphan the tail: saved grants whose
        // outbox row never landed, with LatestEmailStatus == Queued, neither retriable
        // nor re-granted on the next wave.
        for (var i = 0; i < eligibleUsers.Count; i++)
        {
            var user = eligibleUsers[i];
            var code = availableCodes[i];

            var grant = new CampaignGrant
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                CampaignCodeId = code.Id,
                UserId = user.Id,
                AssignedAt = now,
                LatestEmailStatus = EmailOutboxStatus.Queued,
                LatestEmailAt = now
            };
            _dbContext.CampaignGrants.Add(grant);
            await _dbContext.SaveChangesAsync(ct);

            try
            {
                var recipientEmail = ResolveRecipientEmail(user, notificationEmails);
                await _emailService.SendCampaignCodeAsync(
                    BuildCampaignCodeRequest(campaign, user, recipientEmail, code.Code, grant.Id),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to enqueue campaign code email for user {UserId} grant {GrantId} in campaign {CampaignId}",
                    user.Id, grant.Id, campaignId);
                grant.LatestEmailStatus = EmailOutboxStatus.Failed;
                await _dbContext.SaveChangesAsync(ct);
                failedCount++;
            }
        }

        _logger.LogInformation(
            "Campaign {CampaignId}: sent wave to team {TeamId}, {Count} grants created, {FailedCount} failed to enqueue",
            campaignId, teamId, eligibleUsers.Count, failedCount);

        // In-app notification to each recipient (best-effort)
        try
        {
            var recipientIds = eligibleUsers.Select(u => u.Id).ToList();
            await _notificationService.SendAsync(
                NotificationSource.CampaignReceived,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"You received a code from campaign: {campaign.Title}",
                recipientIds,
                body: "Check your email for your campaign code.",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch CampaignReceived notifications for campaign {CampaignId}", campaignId);
        }

        return eligibleUsers.Count;
    }

    public async Task ResendToGrantAsync(Guid grantId, CancellationToken ct = default)
    {
        // Keep .Include(g => g.User) — CampaignGrant.User is owned by the Campaign
        // section; only the cross-domain .ThenInclude(u => u.UserEmails) is stripped.
        var grant = await _dbContext.CampaignGrants
            .Include(g => g.Campaign)
            .Include(g => g.Code)
            .Include(g => g.User)
            .FirstOrDefaultAsync(g => g.Id == grantId, ct)
            ?? throw new InvalidOperationException($"Grant {grantId} not found.");

        var now = _clock.GetCurrentInstant();

        grant.LatestEmailStatus = EmailOutboxStatus.Queued;
        grant.LatestEmailAt = now;

        await _dbContext.SaveChangesAsync(ct);

        var recipientEmail = await _userEmailService.GetNotificationEmailAsync(grant.User.Id, ct)
            ?? grant.User.Email!;

        await _emailService.SendCampaignCodeAsync(
            BuildCampaignCodeRequest(grant.Campaign, grant.User, recipientEmail, grant.Code.Code, grant.Id),
            ct);

        _logger.LogInformation("Resent campaign email for grant {GrantId}", grantId);
    }

    public async Task<int> MarkGrantsRedeemedAsync(
        IReadOnlyCollection<DiscountCodeRedemption> redemptions,
        CancellationToken ct = default)
    {
        if (redemptions.Count == 0)
            return 0;

        // Case-insensitive set of codes we need to look up in the database.
        var codeStrings = redemptions
            .Where(r => !string.IsNullOrEmpty(r.Code))
            .Select(r => r.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (codeStrings.Count == 0)
            return 0;

        // Load unredeemed grants on active/completed campaigns. Filter by code in
        // memory so the DB query stays simple and collation-independent.
        var unredeemed = (await _dbContext.CampaignGrants
            .Include(g => g.Code)
            .Include(g => g.Campaign)
            .Where(g => g.Code != null
                && (g.Campaign.Status == CampaignStatus.Active || g.Campaign.Status == CampaignStatus.Completed)
                && g.RedeemedAt == null)
            .ToListAsync(ct))
            .Where(g => g.Code != null && codeStrings.Contains(g.Code.Code))
            .ToList();

        // Iterate redemptions in input order, matching one grant per redemption so
        // that N orders with the same code redeem N distinct grants (matching the
        // prior single-service behavior). When a code matches grants in multiple
        // campaigns, the most recently created campaign wins.
        var redeemedCount = 0;
        foreach (var redemption in redemptions)
        {
            if (string.IsNullOrEmpty(redemption.Code))
                continue;

            var grant = unredeemed
                .Where(g => string.Equals(g.Code!.Code, redemption.Code, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(g => g.Campaign.CreatedAt)
                .FirstOrDefault();

            if (grant is null)
                continue;

            grant.RedeemedAt = redemption.RedeemedAt;
            unredeemed.Remove(grant);
            redeemedCount++;
        }

        if (redeemedCount > 0)
        {
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogInformation("Marked {Count} campaign grants redeemed from discount-code matches", redeemedCount);
        }

        return redeemedCount;
    }

    public async Task RetryAllFailedAsync(Guid campaignId, CancellationToken ct = default)
    {
        // Keep .Include(g => g.User) — CampaignGrant.User is owned by the Campaign
        // section; only the cross-domain .ThenInclude(u => u.UserEmails) is stripped.
        var failedGrants = await _dbContext.CampaignGrants
            .Include(g => g.Campaign)
            .Include(g => g.Code)
            .Include(g => g.User)
            .Where(g => g.CampaignId == campaignId
                        && g.LatestEmailStatus == EmailOutboxStatus.Failed)
            .ToListAsync(ct);

        if (failedGrants.Count == 0)
            return;

        var now = _clock.GetCurrentInstant();
        var stillFailedCount = 0;

        // Batch-resolve notification emails for the affected users.
        var notificationEmails = await _userEmailService
            .GetNotificationEmailsByUserIdsAsync(
                failedGrants.Select(g => g.User.Id).Distinct(),
                ct);

        // Flip-and-enqueue one grant at a time. A batch flip-to-Queued + loop enqueue
        // would lose grants whose enqueue throws: they would leave the Failed set
        // without a corresponding outbox row and never be retriable again.
        foreach (var grant in failedGrants)
        {
            grant.LatestEmailStatus = EmailOutboxStatus.Queued;
            grant.LatestEmailAt = now;
            await _dbContext.SaveChangesAsync(ct);

            try
            {
                var recipientEmail = ResolveRecipientEmail(grant.User, notificationEmails);
                await _emailService.SendCampaignCodeAsync(
                    BuildCampaignCodeRequest(grant.Campaign, grant.User, recipientEmail, grant.Code.Code, grant.Id),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Retry failed to re-enqueue campaign code email for grant {GrantId} in campaign {CampaignId}",
                    grant.Id, campaignId);
                grant.LatestEmailStatus = EmailOutboxStatus.Failed;
                await _dbContext.SaveChangesAsync(ct);
                stillFailedCount++;
            }
        }

        _logger.LogInformation(
            "Campaign {CampaignId}: retried {Count} failed grants, {StillFailedCount} still failed",
            campaignId, failedGrants.Count, stillFailedCount);
    }

    private async Task<List<Guid>> GetActiveTeamUserIdsAsync(Guid teamId, CancellationToken ct)
    {
        return await _dbContext.TeamMembers
            .Where(tm => tm.TeamId == teamId && tm.LeftAt == null)
            .Select(tm => tm.UserId)
            .ToListAsync(ct);
    }

    private async Task<int> CountAvailableCodesAsync(Guid campaignId, CancellationToken ct)
    {
        return await _dbContext.CampaignCodes
            .Where(c => c.CampaignId == campaignId
                        && !_dbContext.CampaignGrants.Any(g => g.CampaignCodeId == c.Id))
            .CountAsync(ct);
    }

    /// <summary>
    /// Build a <see cref="CampaignCodeEmailRequest"/> describing the raw campaign content
    /// (unrendered markdown template, recipient, grant id). The outbox email service
    /// performs the actual markdown→HTML rendering and template wrapping — keeping
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

    private static string ResolveRecipientEmail(
        User user,
        IReadOnlyDictionary<Guid, string> notificationEmails)
    {
        return notificationEmails.TryGetValue(user.Id, out var email) && !string.IsNullOrEmpty(email)
            ? email
            : user.Email!;
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var grants = await _dbContext.CampaignGrants
            .AsNoTracking()
            .Include(cg => cg.Campaign)
            .Include(cg => cg.Code)
            .Where(cg => cg.UserId == userId)
            .OrderByDescending(cg => cg.AssignedAt)
            .ToListAsync(ct);

        var shaped = grants.Select(cg => new
        {
            CampaignTitle = cg.Campaign.Title,
            Code = cg.Code.Code,
            AssignedAt = cg.AssignedAt.ToInvariantInstantString(),
            RedeemedAt = cg.RedeemedAt.ToInvariantInstantString(),
            EmailStatus = cg.LatestEmailStatus?.ToString()
        }).ToList();

        return [new UserDataSlice(GdprExportSections.CampaignGrants, shaped)];
    }
}
