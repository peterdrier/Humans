using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
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
    private readonly EmailSettings _settings;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly string _environmentName;
    private readonly ILogger<CampaignService> _logger;

    public CampaignService(
        HumansDbContext dbContext,
        IClock clock,
        IHumansMetrics metrics,
        IOptions<EmailSettings> settings,
        IDataProtectionProvider dataProtectionProvider,
        IHostEnvironment hostEnvironment,
        ILogger<CampaignService> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _metrics = metrics;
        _settings = settings.Value;
        _dataProtectionProvider = dataProtectionProvider;
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
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<List<Campaign>> GetAllAsync(CancellationToken ct = default)
    {
        return await _dbContext.Campaigns
            .Include(c => c.Codes)
            .Include(c => c.Grants)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task ImportCodesAsync(Guid campaignId, IEnumerable<string> codes, CancellationToken ct = default)
    {
        var campaign = await _dbContext.Campaigns
            .Include(c => c.Codes)
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
            ?? throw new InvalidOperationException($"Campaign {campaignId} not found.");

        var existingCodes = campaign.Codes
            .Select(c => c.Code)
            .ToHashSet(StringComparer.Ordinal);

        var now = _clock.GetCurrentInstant();
        var imported = 0;
        var skipped = 0;

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

            _dbContext.CampaignCodes.Add(new CampaignCode
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Code = trimmed,
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

        var unsubscribedUserIds = await _dbContext.Users
            .Where(u => activeTeamUserIds.Contains(u.Id) && u.UnsubscribedFromCampaigns)
            .Select(u => u.Id)
            .ToListAsync(ct);
        var unsubscribedSet = unsubscribedUserIds.ToHashSet();

        var eligible = activeTeamUserIds
            .Where(id => !alreadyGrantedSet.Contains(id) && !unsubscribedSet.Contains(id))
            .ToList();

        var availableCodes = await CountAvailableCodesAsync(campaignId, ct);

        return new WaveSendPreview(
            EligibleCount: eligible.Count,
            AlreadyGrantedExcluded: activeTeamUserIds.Count(id => alreadyGrantedSet.Contains(id)),
            UnsubscribedExcluded: activeTeamUserIds.Count(id =>
                !alreadyGrantedSet.Contains(id) && unsubscribedSet.Contains(id)),
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
                        && !alreadyGrantedSet.Contains(u.Id)
                        && !u.UnsubscribedFromCampaigns)
            .ToListAsync(ct);

        if (eligibleUsers.Count == 0)
            return 0;

        // Get available codes ordered by ImportedAt, Id
        var availableCodes = await _dbContext.CampaignCodes
            .Where(c => c.CampaignId == campaignId
                        && !_dbContext.CampaignGrants.Any(g => g.CampaignCodeId == c.Id))
            .OrderBy(c => c.ImportedAt)
            .ThenBy(c => c.Id)
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

        // Generate unsubscribe token
        var protector = _dataProtectionProvider
            .CreateProtector("CampaignUnsubscribe");
        var timeLimitedProtector = protector.ToTimeLimitedDataProtector();
        var unsubscribeToken = timeLimitedProtector.Protect(
            user.Id.ToString(), TimeSpan.FromDays(90));

        var unsubscribeUrl = $"{_settings.BaseUrl}/Unsubscribe/{Uri.EscapeDataString(unsubscribeToken)}";

        // Append unsubscribe link (lower right, minimal)
        var unsubscribeFooter = $"""
            <p style="font-size: 11px; text-align: right; margin: 24px 0 0 0;">
                <a href="{WebUtility.HtmlEncode(unsubscribeUrl)}" style="color: #8b7355;">Unsubscribe</a>
            </p>
            """;

        var bodyWithFooter = renderedBody + "\n" + unsubscribeFooter;

        // Wrap in email template
        var wrappedHtml = WrapInTemplate(bodyWithFooter);
        var plainText = HtmlToPlainText(bodyWithFooter);

        // Build extra headers with List-Unsubscribe
        var extraHeaders = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["List-Unsubscribe"] = $"<{unsubscribeUrl}>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click"
        });

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
            ExtraHeaders = extraHeaders,
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

    private string WrapInTemplate(string content)
    {
        var isProduction = string.Equals(_environmentName, "Production", StringComparison.OrdinalIgnoreCase);
        var envLabel = string.Equals(_environmentName, "Staging", StringComparison.OrdinalIgnoreCase)
            ? "QA"
            : _environmentName.ToUpperInvariant();
        var envBanner = isProduction
            ? ""
            : $"""
                <div style="background:#a0522d;color:#fff;text-align:center;font-size:11px;font-weight:700;letter-spacing:0.15em;text-transform:uppercase;padding:4px 0;">
                    {WebUtility.HtmlEncode(envLabel)} &bull; {WebUtility.HtmlEncode(envLabel)} &bull; {WebUtility.HtmlEncode(envLabel)}
                </div>
                """;

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <style>
                    body { font-family: 'Source Sans 3', 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; color: #3d2b1f; max-width: 600px; margin: 0 auto; padding: 0; background-color: #faf6f0; }
                    h2 { color: #3d2b1f; font-family: 'Cormorant Garamond', Georgia, 'Times New Roman', serif; font-weight: 600; }
                    a { color: #8b6914; }
                    ul { padding-left: 20px; }
                </style>
            </head>
            <body style="font-family: 'Source Sans 3', 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; color: #3d2b1f; max-width: 600px; margin: 0 auto; padding: 0; background-color: #faf6f0;">
            {{envBanner}}
            <div style="background: #3d2b1f; padding: 16px 24px; border-bottom: 3px solid #c9a96e;">
                <span style="font-family: Georgia, serif; font-size: 22px; color: #c9a96e; letter-spacing: 0.05em;">Humans</span>
                <span style="font-family: Georgia, serif; font-size: 12px; color: #8b7355; margin-left: 8px; letter-spacing: 0.1em;">NOBODIES COLLECTIVE</span>
            </div>
            <div style="padding: 28px 24px 20px 24px;">
            {{content}}
            </div>
            <div style="background: #f0e2c8; padding: 16px 24px; border-top: 1px solid #e8d4ab;">
                <p style="font-size: 12px; color: #6b5a4e; margin: 0; line-height: 1.5;">
                    Humans &mdash; Nobodies Collective<br>
                    <a href="{{_settings.BaseUrl}}" style="color: #8b6914;">{{_settings.BaseUrl}}</a>
                </p>
            </div>
            </body>
            </html>
            """;
    }

    private static string HtmlToPlainText(string html)
    {
        var text = html;
        text = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, "</p>", "\n\n", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, "</li>", "\n", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, "<[^>]+>", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = WebUtility.HtmlDecode(text);
        return text.Trim();
    }
}
