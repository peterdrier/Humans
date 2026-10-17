using Humans.Base.Enums;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Campaigns.Contracts;

/// <summary>
/// The writes other sections drive on Campaigns, alongside the read-only
/// <see cref="ICampaignService"/>: Tickets marking codes redeemed and Email recording delivery
/// state. Campaign/code CRUD, activation, wave sending, resend and retry stay internal to
/// <c>Humans.Campaigns</c>.
/// </summary>
public interface ICampaignWrite : IApplicationService
{
    /// <summary>
    /// Marks campaign grants as redeemed based on discovered discount-code redemptions.
    /// Matches codes case-insensitively against active/completed campaigns' unredeemed grants.
    /// When a code matches grants in multiple campaigns, the most recently created campaign wins.
    /// Returns the number of grants marked as redeemed.
    /// </summary>
    Task<int> MarkGrantsRedeemedAsync(
        IReadOnlyCollection<DiscountCodeRedemption> redemptions,
        CancellationToken ct = default);

    /// <summary>
    /// Updates a campaign grant's denormalized email delivery status
    /// (<c>LatestEmailStatus</c>) and timestamp (<c>LatestEmailAt</c>).
    /// Returns <c>true</c> if the grant was found and updated. Used by the
    /// email outbox processor so the job can record Sent/Failed without
    /// touching <c>campaign_grants</c> directly.
    /// </summary>
    Task<bool> UpdateGrantEmailStatusAsync(
        Guid grantId,
        EmailOutboxStatus status,
        Instant latestEmailAt,
        CancellationToken ct = default);
}
