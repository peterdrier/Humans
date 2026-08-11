using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Campaigns.Contracts;

/// <summary>
/// A discount-code redemption discovered by ticket sync — code string and the
/// instant the redeeming ticket order was purchased.
/// </summary>
public record DiscountCodeRedemption(string Code, Instant RedeemedAt);

/// <summary>
/// One campaign grant as other assemblies see it: the campaign and code it came
/// from, when it was assigned and redeemed, and the denormalized delivery state
/// of its email.
/// </summary>
public sealed record CampaignGrantSummary(
    Guid Id,
    Guid CampaignId,
    string CampaignTitle,
    Guid CampaignCodeId,
    string Code,
    Guid UserId,
    Instant AssignedAt,
    EmailOutboxStatus? LatestEmailStatus,
    Instant? LatestEmailAt,
    Instant? RedeemedAt);

/// <summary>
/// Campaigns' full cross-assembly surface: the read side plus the two grant
/// reads Shell's profile and admin-detail pages render, and the two writes Base
/// drives (ticket sync marking codes redeemed, the email outbox recording
/// delivery state).
/// </summary>
/// <remarks>
/// The section's own controller takes the internal <c>CampaignService</c>
/// directly, so campaign/code CRUD, activation, wave sending, resend and retry —
/// the other sixteen members of the pre-G5 <c>ICampaignService</c> — are not
/// public at all (design §15 step 5).
/// </remarks>
public interface ICampaignService : ICampaignServiceRead, IApplicationService
{
    /// <summary>
    /// Returns campaign grants for a user where the campaign is Active or Completed,
    /// ordered by AssignedAt descending.
    /// </summary>
    Task<IReadOnlyList<CampaignGrantSummary>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all campaign grants for a user (any campaign status),
    /// ordered by AssignedAt descending. Used for admin detail views.
    /// </summary>
    Task<IReadOnlyList<CampaignGrantSummary>> GetAllGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);

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
    /// touching <c>campaign_grants</c> directly (design-rules §2c).
    /// </summary>
    Task<bool> UpdateGrantEmailStatusAsync(
        Guid grantId,
        EmailOutboxStatus status,
        Instant latestEmailAt,
        CancellationToken ct = default);
}
