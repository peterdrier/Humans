using System.Net;
using System.Text.Json;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class CampaignService : ICampaignService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IHumansMetrics _metrics;
    private readonly ICommunicationPreferenceService _commPrefService;
    private readonly EmailSettings _settings;
    private readonly string _environmentName;
    private readonly ILogger<CampaignService> _logger;

    public CampaignService(
        HumansDbContext dbContext,
        IClock clock,
        IHumansMetrics metrics,
        ICommunicationPreferenceService commPrefService,
        IOptions<EmailSettings> settings,
        IHostEnvironment hostEnvironment,
        ILogger<CampaignService> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _metrics = metrics;
        _commPrefService = commPrefService;
        _settings = settings.Value;
        _environmentName = hostEnvironment.EnvironmentName;
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
        var campaign = await _dbContext.Campaigns
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        var activeTeamUserIds = await GetActiveTeamUserIdsAsync(teamId, ct);

        var alreadyGrantedUserIds = await _dbContext.CampaignGrants
            .Where(g => g.CampaignId == campaignId)
            .Select(g => g.UserId)
            .Distinct()
            .ToListAsync(ct);

        var alreadyGrantedSet = alreadyGrantedUserIds.ToHashSet();

        var eligible = activeTeamUserIds
            .Where(id => !alreadyGrantedSet.Contains(id))
            .ToList();

        var availableCodes = await CountAvailableCodesAsync(campaignId, ct);

        return new WaveSendPreview(
            EligibleCount: eligible.Count,
            AlreadyGrantedExcluded: activeTeamUserIds.Count(id => alreadyGrantedSet.Contains(id)),
            UnsubscribedExcluded: 0,
            CodesAvailable: availableCodes,
            CodesRemainingAfterSend: availableCodes - eligible.Count);
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

        var eligibleUsers = await _dbContext.Users
            .Include(u => u.UserEmails)
            .Where(u => activeTeamUserIds.Contains(u.Id)
                        && !alreadyGrantedSet.Contains(u.Id))
            .ToListAsync(ct);

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

            var outboxMessage = RenderOutboxMessage(campaign, user, code.Code, grant.Id, now);
            _dbContext.EmailOutboxMessages.Add(outboxMessage);

            _metrics.RecordEmailQueued("campaign_code");
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Campaign {CampaignId}: sent wave to team {TeamId}, {Count} grants created",
            campaignId, teamId, eligibleUsers.Count);

        return eligibleUsers.Count;
    }

    public async Task ResendToGrantAsync(Guid grantId, CancellationToken ct = default)
    {
        var grant = await _dbContext.CampaignGrants
            .Include(g => g.Campaign)
            .Include(g => g.Code)
            .Include(g => g.User).ThenInclude(u => u.UserEmails)
            .FirstOrDefaultAsync(g => g.Id == grantId, ct)
            ?? throw new InvalidOperationException($"Grant {grantId} not found.");

        var now = _clock.GetCurrentInstant();

        var outboxMessage = RenderOutboxMessage(
            grant.Campaign, grant.User, grant.Code.Code, grant.Id, now);
        _dbContext.EmailOutboxMessages.Add(outboxMessage);

        grant.LatestEmailStatus = EmailOutboxStatus.Queued;
        grant.LatestEmailAt = now;

        await _dbContext.SaveChangesAsync(ct);

        _metrics.RecordEmailQueued("campaign_code");
        _logger.LogInformation("Resent campaign email for grant {GrantId}", grantId);
    }

    public async Task RetryAllFailedAsync(Guid campaignId, CancellationToken ct = default)
    {
        var failedGrants = await _dbContext.CampaignGrants
            .Include(g => g.Campaign)
            .Include(g => g.Code)
            .Include(g => g.User).ThenInclude(u => u.UserEmails)
            .Where(g => g.CampaignId == campaignId
                        && g.LatestEmailStatus == EmailOutboxStatus.Failed)
            .ToListAsync(ct);

        if (failedGrants.Count == 0)
            return;

        var now = _clock.GetCurrentInstant();

        foreach (var grant in failedGrants)
        {
            var outboxMessage = RenderOutboxMessage(
                grant.Campaign, grant.User, grant.Code.Code, grant.Id, now);
            _dbContext.EmailOutboxMessages.Add(outboxMessage);

            grant.LatestEmailStatus = EmailOutboxStatus.Queued;
            grant.LatestEmailAt = now;

            _metrics.RecordEmailQueued("campaign_code");
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Campaign {CampaignId}: retried {Count} failed grants",
            campaignId, failedGrants.Count);
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

    private EmailOutboxMessage RenderOutboxMessage(
        Campaign campaign, User user, string code, Guid grantId, Instant now)
    {
        var recipientEmail = GetNotificationEmail(user);
        var name = user.DisplayName;

        var encodedCode = WebUtility.HtmlEncode(code);
        var encodedName = WebUtility.HtmlEncode(name);

        // Substitute placeholders in Markdown source, then render to HTML
        var markdown = campaign.EmailBodyTemplate
            .Replace("{{Code}}", encodedCode, StringComparison.Ordinal)
            .Replace("{{Name}}", encodedName, StringComparison.Ordinal);
        var renderedBody = Markdig.Markdown.ToHtml(markdown);

        var renderedSubject = campaign.EmailSubject
            .Replace("{{Code}}", code, StringComparison.Ordinal)
            .Replace("{{Name}}", name, StringComparison.Ordinal);

        // Generate unsubscribe headers and footer link for Marketing category
        var unsubHeaders = _commPrefService.GenerateUnsubscribeHeaders(user.Id, MessageCategory.Marketing);
        string? unsubscribeUrl = null;
        string? extraHeadersJson = null;
        if (unsubHeaders is not null)
        {
            if (unsubHeaders.TryGetValue("List-Unsubscribe", out var listUnsub))
                unsubscribeUrl = listUnsub.Trim('<', '>');
            extraHeadersJson = JsonSerializer.Serialize(unsubHeaders);
        }

        // Wrap in email template
        var (wrappedHtml, plainText) = EmailBodyComposer.Compose(
            renderedBody, _settings.BaseUrl, _environmentName, unsubscribeUrl);

        return new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            RecipientEmail = recipientEmail,
            RecipientName = name,
            Subject = renderedSubject,
            HtmlBody = wrappedHtml,
            PlainTextBody = plainText,
            TemplateName = "campaign_code",
            UserId = user.Id,
            CampaignGrantId = grantId,
            ReplyTo = campaign.ReplyToAddress,
            ExtraHeaders = extraHeadersJson,
            Status = EmailOutboxStatus.Queued,
            CreatedAt = now
        };
    }

    private static string GetNotificationEmail(User user)
    {
        var notificationEmail = user.UserEmails
            .FirstOrDefault(e => e.IsNotificationTarget && e.IsVerified);
        return notificationEmail?.Email ?? user.Email!;
    }
}
