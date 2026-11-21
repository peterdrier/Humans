using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Campaigns.Contracts;

/// <summary><paramref name="RedeemedAt"/> is when the redeeming ticket order was purchased.</summary>
public record DiscountCodeRedemption(string Code, Instant RedeemedAt);

/// <summary>Cross-section reads; writes are on <see cref="ICampaignWrite"/>.</summary>
public interface ICampaignService : IApplicationService
{
    /// <summary>
    /// Active and Completed campaigns, with recipient ids and display names. The caller correlates redemptions against
    /// ticket orders.
    /// </summary>
    Task<CampaignCodeTrackingData> GetCodeTrackingAsync(CancellationToken ct = default);
}
