using NodaTime;

namespace Humans.Campaigns.Contracts;

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
