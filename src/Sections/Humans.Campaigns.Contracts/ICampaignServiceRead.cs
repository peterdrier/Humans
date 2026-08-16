using Humans.Application.Architecture;

namespace Humans.Campaigns.Contracts;

/// <summary>
/// Cross-section read surface for the Campaigns section. External sections
/// inject this interface; only DTO projections (no EF entities). See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
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
}
