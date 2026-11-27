using Humans.Campaigns.Contracts;
using Humans.Campaigns.Services.Dtos;

namespace Humans.Campaigns.Services;

internal interface ICampaignManagement : ICampaignService, ICampaignWrite
{
    /// <summary>Grants in Active or Completed campaigns only. Unordered.</summary>
    Task<IReadOnlyList<CampaignGrantSummary>> GetGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Any campaign status. Unordered.</summary>
    Task<IReadOnlyList<CampaignGrantSummary>> GetAllGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);
}
