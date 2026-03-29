using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class CampaignGrant
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

    // Navigation
    public Campaign Campaign { get; set; } = null!;
    public CampaignCode Code { get; set; } = null!;
    public User User { get; set; } = null!;
    public ICollection<EmailOutboxMessage> OutboxMessages { get; } = new List<EmailOutboxMessage>();
}
