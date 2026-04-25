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

    /// <summary>
    /// Cross-domain navigation to the recipient user. Do not use — callers
    /// resolve user display names via <see cref="Humans.Application.Interfaces.IUserService"/>
    /// keyed off <see cref="UserId"/>. Retained only so EF's configured
    /// relationship keeps the FK constraint.
    /// </summary>
    [Obsolete("Cross-domain nav. Use UserId + IUserService to resolve the user.")]
    public User User { get; set; } = null!;

    public ICollection<EmailOutboxMessage> OutboxMessages { get; } = new List<EmailOutboxMessage>();
}
