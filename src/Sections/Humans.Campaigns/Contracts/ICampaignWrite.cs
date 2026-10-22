using Humans.Base.Enums;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Campaigns.Contracts;

/// <summary>Cross-section writes: Tickets marking codes redeemed and Email recording delivery state.</summary>
public interface ICampaignWrite : IApplicationService
{
    /// <summary>
    /// Matches codes case-insensitively against Active and Completed campaigns' unredeemed grants, one grant per
    /// redemption. When a code matches grants in several campaigns, the newest campaign wins. Returns grants marked.
    /// </summary>
    Task<int> MarkGrantsRedeemedAsync(
        IReadOnlyCollection<DiscountCodeRedemption> redemptions,
        CancellationToken ct = default);

    /// <summary>Sets <c>LatestEmailStatus</c> and <c>LatestEmailAt</c>. False when the grant does not exist.</summary>
    Task<bool> UpdateGrantEmailStatusAsync(
        Guid grantId,
        EmailOutboxStatus status,
        Instant latestEmailAt,
        CancellationToken ct = default);
}
