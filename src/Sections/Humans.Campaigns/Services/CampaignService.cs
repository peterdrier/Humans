using System.ComponentModel.DataAnnotations;
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
using Humans.Gdpr.Contracts;
using NodaTime;

namespace Humans.Campaigns.Services;

internal sealed class CampaignService(
    ICampaignRepository repository,
    ITeamService teamService,
    IUserService userService,
    INotificationEmitter notificationService,
    IEmailService emailService,
    CampaignsEmails emailMessages,
    ITicketDiscountCodes ticketDiscountCodes,
    IClock clock,
    IAuditLogService audit,
    ILogger<CampaignService> logger) : ICampaignManagement, IUserDataContributor, IUserMerge
{
    private readonly CampaignWaveDelivery waveDelivery = new(
        repository, teamService, userService, notificationService, emailService, emailMessages, clock, audit, logger);

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
            Status = CampaignStatus.Draft,
            CreatedAt = clock.GetCurrentInstant(),
            CreatedByUserId = createdByUserId
        };
        ApplyCampaignFields(campaign, title, description, emailSubject, emailBodyTemplate, replyToAddress);

        await repository.AddCampaignAsync(campaign, ct);
        await audit.LogAsync(CampaignsAuditActions.CampaignCreated, "Campaign", campaign.Id,
            "Created campaign.", createdByUserId);

        logger.LogInformation("Campaign {CampaignId} created: {Title}", campaign.Id, title);
        return new CampaignCreateResult(true, campaign);
    }

    private static void ApplyCampaignFields(Campaign campaign, string title, string? description,
        string emailSubject, string emailBodyTemplate, string? replyToAddress)
    {
        campaign.Title = title.Trim();
        campaign.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        campaign.EmailSubject = emailSubject.Trim();
        campaign.EmailBodyTemplate = emailBodyTemplate.Trim();
        campaign.ReplyToAddress = string.IsNullOrWhiteSpace(replyToAddress) ? null : replyToAddress.Trim();
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

        ApplyCampaignFields(campaign, title, description, emailSubject, emailBodyTemplate, replyToAddress);

        await repository.UpdateCampaignAsync(campaign, ct);

        await audit.LogAsync(CampaignsAuditActions.CampaignUpdated, "Campaign", id,
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
            .Where(t => t.IsActive);
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
        await audit.LogAsync(CampaignsAuditActions.CampaignCodesImported, "Campaign", campaignId,
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

        await audit.LogAsync(CampaignsAuditActions.CampaignActivated, "Campaign", campaignId,
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

        await audit.LogAsync(CampaignsAuditActions.CampaignCompleted, "Campaign", campaignId,
            "Completed campaign.", actorUserId);

        logger.LogInformation("Campaign {CampaignId} completed", campaignId);
        return new CampaignUpdateResult(true);
    }

    public Task<WaveSendPreview> PreviewWaveSendAsync(Guid campaignId, Guid teamId,
        CancellationToken ct = default) =>
        waveDelivery.PreviewWaveSendAsync(campaignId, teamId, ct);

    public Task<CampaignSendWaveResult> SendWaveAsync(Guid actorUserId, Guid campaignId, Guid teamId,
        CancellationToken ct = default) =>
        waveDelivery.SendWaveAsync(actorUserId, campaignId, teamId, ct);

    public Task ResendToGrantAsync(Guid actorUserId, Guid grantId, CancellationToken ct = default) =>
        waveDelivery.ResendToGrantAsync(actorUserId, grantId, ct);

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
            .Select(s =>
            {
                grantsByCampaign.TryGetValue(s.CampaignId, out var grants);
                var total = grants?.Count ?? 0;
                var redeemed = grants?.Count(g => g.RedeemedAt is not null) ?? 0;
                return new CampaignCodeTrackingSummary(
                    s.CampaignId, s.CampaignTitle, total, redeemed)
                { CreatedAt = s.CreatedAt };
            })
            .ToList();

        return new CampaignCodeTrackingData(summaries, grantRows);
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
