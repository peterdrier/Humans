using Humans.Base.Interfaces;

namespace Humans.Campaigns.Contracts;

/// <summary>Cross-section writes: Tickets marking codes redeemed.</summary>
public interface ICampaignWrite : IApplicationService
{
    /// <summary>
    /// Matches codes case-insensitively against Active and Completed campaigns' unredeemed grants, one grant per
    /// redemption. When a code matches grants in several campaigns, the newest campaign wins. Returns grants marked.
    /// </summary>
    Task<int> MarkGrantsRedeemedAsync(
        IReadOnlyCollection<DiscountCodeRedemption> redemptions,
        CancellationToken ct = default);
}
