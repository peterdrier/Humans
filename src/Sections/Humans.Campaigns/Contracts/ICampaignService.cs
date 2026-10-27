using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Campaigns.Contracts;

/// <summary><paramref name="RedeemedAt"/> is when the redeeming ticket order was purchased.</summary>
public record DiscountCodeRedemption(string Code, Instant RedeemedAt);

public sealed record CampaignGrantSummary(
    Guid Id,
    Guid CampaignId,
    string CampaignTitle,
    Guid CampaignCodeId,
    string Code,
    Guid UserId,
    Instant AssignedAt,
    Instant? RedeemedAt);

/// <summary>Cross-section reads; writes are on <see cref="ICampaignWrite"/>.</summary>
public interface ICampaignService : IApplicationService
{
    /// <summary>
    /// Active and Completed campaigns, with recipient ids and display names. The caller correlates redemptions against
    /// ticket orders.
    /// </summary>
    Task<CampaignCodeTrackingData> GetCodeTrackingAsync(CancellationToken ct = default);

    /// <summary>Unordered.</summary>
    Task<IReadOnlyList<CampaignGrantSummary>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Any campaign status. Unordered.</summary>
    Task<IReadOnlyList<CampaignGrantSummary>> GetAllGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);
}
