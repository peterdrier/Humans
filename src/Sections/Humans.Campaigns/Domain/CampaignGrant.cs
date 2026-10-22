using Humans.Base.Enums;
using NodaTime;

namespace Humans.Campaigns.Domain;

internal sealed class CampaignGrant
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Guid CampaignCodeId { get; set; }
    public Guid UserId { get; set; }
    public Instant AssignedAt { get; set; }
    public EmailOutboxStatus? LatestEmailStatus { get; set; }
    public Instant? LatestEmailAt { get; set; }

    /// <summary>Used in a ticket purchase. Null if unused.</summary>
    public Instant? RedeemedAt { get; set; }

    public Campaign Campaign { get; set; } = null!;
    public CampaignCode Code { get; set; } = null!;
}
