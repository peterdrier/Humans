using Humans.Domain.Enums;
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

    /// <summary>When the grant's discount code was redeemed (used in a ticket purchase). Null if unused.</summary>
    public Instant? RedeemedAt { get; set; }

    // Navigation (same-section only — Email's outbox rows reference this grant
    // by bare FK and are resolved through the Email section's services).
    public Campaign Campaign { get; set; } = null!;
    public CampaignCode Code { get; set; } = null!;
}
