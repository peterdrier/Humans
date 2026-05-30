using Humans.Application.Architecture;
using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Campaigns;

/// <summary>
/// Cross-section read surface for the Campaigns section. External sections
/// inject this interface; only DTO projections (no EF entities). See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
[SurfaceBudget(1)]
public interface ICampaignServiceRead
{
    /// <summary>
    /// Returns code tracking data — campaign summaries and individual grant
    /// details for campaigns that are Active or Completed — for the Tickets
    /// admin dashboard. The returned <see cref="CampaignCodeTrackingData"/>
    /// carries recipient user IDs and display names sourced from the Campaigns
    /// section; the caller correlates discount-code redemptions against
    /// ticket orders separately.
    /// </summary>
    Task<CampaignCodeTrackingData> GetCodeTrackingAsync(CancellationToken ct = default);
}
