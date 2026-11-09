using Humans.Base.Interfaces;
using Humans.Campaigns.Contracts;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Campaigns.Services.Dtos;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;

namespace Humans.Campaigns.Services;

internal sealed class CampaignReader(
    ICampaignRepository repository,
    ITeamService teamService,
    IUserService userService,
    CampaignWaveDelivery waveDelivery) : IApplicationService
{
    internal async Task<IReadOnlyList<CampaignGrantSummary>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var grants = await repository.GetActiveOrCompletedGrantsForUserAsync(userId, ct);
        return grants.Select(ToSummary).ToList();
    }

    internal async Task<IReadOnlyList<CampaignGrantSummary>> GetAllGrantsForUserAsync(
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

    internal async Task<CampaignEditSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default)
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

    internal async Task<IReadOnlyList<CampaignListSummary>> GetAllAsync(CancellationToken ct = default)
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

    internal async Task<CampaignDetailPageDto?> GetDetailPageAsync(Guid id, CancellationToken ct = default)
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

    internal async Task<CampaignSendWavePageDto?> GetSendWavePageAsync(
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
            ? await waveDelivery.PreviewWaveSendAsync(campaignId, teamId.Value, ct)
            : null;

        return new CampaignSendWavePageDto(ToAdminSummary(campaign), teamOptions, teamId, preview);
    }

    internal Task<Guid?> GetCampaignIdForGrantAsync(Guid grantId, CancellationToken ct = default) =>
        repository.GetCampaignIdForGrantAsync(grantId, ct);

    internal async Task<CampaignCodeTrackingData> GetCodeTrackingAsync(CancellationToken ct = default)
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

}
