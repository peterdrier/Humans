using Humans.Base.Interfaces;
using Humans.Base.Enums;
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
/// Cross-section read surface for the Campaigns section. External sections
/// inject this interface; only DTO projections (no EF entities). The writes they
/// make live on <see cref="ICampaignWrite"/>. See
/// <c>memory/architecture/section-public-contract.md</c>.
/// </summary>
public interface ICampaignService : IApplicationService
{
    /// <summary>
    /// Returns code tracking data — campaign summaries and individual grant
    /// details for campaigns that are Active or Completed — for the Tickets
    /// admin dashboard. The returned <see cref="CampaignCodeTrackingData"/>
    /// carries recipient user IDs and display names (resolved via the Users
    /// section); the caller correlates discount-code redemptions against
    /// ticket orders separately.
    /// </summary>
    Task<CampaignCodeTrackingData> GetCodeTrackingAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns campaign grants for a user where the campaign is Active or
    /// Completed. Unordered — callers sort.
    /// </summary>
    Task<IReadOnlyList<CampaignGrantSummary>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all campaign grants for a user (any campaign status).
    /// Unordered — callers sort. Used for admin detail views.
    /// </summary>
    Task<IReadOnlyList<CampaignGrantSummary>> GetAllGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);
}
