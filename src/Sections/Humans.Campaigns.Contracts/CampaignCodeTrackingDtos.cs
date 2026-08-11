using NodaTime;

namespace Humans.Campaigns.Contracts;

/// <summary>
/// Aggregated code-tracking data sourced from the Campaigns section.
/// Contains a per-campaign summary and a flat list of grant details
/// (including recipient user IDs/display names) for the Tickets admin
/// dashboard. Callers correlate redemptions against ticket orders.
/// </summary>
public record CampaignCodeTrackingData(
    IReadOnlyList<CampaignCodeTrackingSummary> Campaigns,
    IReadOnlyList<CampaignCodeTrackingGrant> Grants);

public record CampaignCodeTrackingSummary(
    Guid CampaignId,
    string CampaignTitle,
    int TotalGrants,
    int Redeemed);

public record CampaignCodeTrackingGrant(
    Guid GrantId,
    Guid CampaignId,
    string CampaignTitle,
    Guid UserId,
    string RecipientName,
    string? Code,
    Instant? RedeemedAt,
    string? LatestEmailStatus);
