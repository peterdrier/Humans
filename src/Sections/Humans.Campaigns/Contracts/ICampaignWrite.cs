using Humans.Base.Interfaces;

namespace Humans.Campaigns.Contracts;

/// <summary>Cross-section writes: Tickets marking codes redeemed.</summary>
public interface ICampaignWrite : IApplicationService
{
    /// <summary>
    /// Reconciles a complete purchase snapshot against Active and Completed campaigns' grants, case-insensitively.
    /// Existing redemptions count toward the snapshot, so replay never redeems additional grants. New purchases
    /// select the newest campaign first; recorded redemption dates stay unchanged. Returns newly marked grants.
    /// </summary>
    Task<int> MarkGrantsRedeemedAsync(
        IReadOnlyCollection<DiscountCodeRedemption> redemptions,
        CancellationToken ct = default);
}
