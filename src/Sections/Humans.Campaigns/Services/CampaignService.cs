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

    private CampaignReader Reader => new(repository, teamService, userService, waveDelivery);

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

    public Task<IReadOnlyList<CampaignGrantSummary>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        Reader.GetActiveOrCompletedGrantsForUserAsync(userId, ct);

    public Task<IReadOnlyList<CampaignGrantSummary>> GetAllGrantsForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        Reader.GetAllGrantsForUserAsync(userId, ct);

    public Task<CampaignEditSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Reader.GetByIdAsync(id, ct);

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

    public Task<IReadOnlyList<CampaignListSummary>> GetAllAsync(CancellationToken ct = default) =>
        Reader.GetAllAsync(ct);

    public Task<CampaignDetailPageDto?> GetDetailPageAsync(Guid id, CancellationToken ct = default) =>
        Reader.GetDetailPageAsync(id, ct);

    public Task<CampaignSendWavePageDto?> GetSendWavePageAsync(
        Guid campaignId, Guid? teamId, CancellationToken ct = default) =>
        Reader.GetSendWavePageAsync(campaignId, teamId, ct);

    public Task<Guid?> GetCampaignIdForGrantAsync(Guid grantId, CancellationToken ct = default) =>
        Reader.GetCampaignIdForGrantAsync(grantId, ct);

    private CampaignCodeInventory Codes => new(repository, ticketDiscountCodes, Reader, clock, audit, logger);

    public Task<CampaignImportResult> ImportCodesAsync(
        Guid actorUserId, Guid campaignId, IEnumerable<string> codes, CancellationToken ct = default) =>
        Codes.ImportCodesAsync(actorUserId, campaignId, codes, ct);

    [ExternalWrite]
    public Task<CampaignGenerateCodesResult> GenerateAndImportDiscountCodesAsync(
        Guid actorUserId, Guid campaignId, int count, string discountType,
        decimal discountValue, CancellationToken ct = default) =>
        Codes.GenerateAndImportDiscountCodesAsync(actorUserId, campaignId, count, discountType, discountValue, ct);

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

    public Task<CampaignCodeTrackingData> GetCodeTrackingAsync(CancellationToken ct = default) =>
        Reader.GetCodeTrackingAsync(ct);

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
