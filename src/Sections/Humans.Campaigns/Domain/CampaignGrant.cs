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

    /// <summary>
    /// Set only on grants created before nobodies-collective/Humans#1243 dropped delivery tracking; only the GDPR
    /// export still reads it. This column and <see cref="LatestEmailAt"/> (read by nothing) stay until their drop is
    /// approved.
    /// </summary>
    [Obsolete("Unwritten since nobodies-collective/Humans#1243 dropped delivery tracking. Kept only until its column drop is approved.")]
    public EmailOutboxStatus? LatestEmailStatus { get; set; }

    /// <summary>Unread; kept only until its column drop is approved.</summary>
    [Obsolete("Unwritten since nobodies-collective/Humans#1243 dropped delivery tracking. Kept only until its column drop is approved.")]
    public Instant? LatestEmailAt { get; set; }

    /// <summary>Used in a ticket purchase. Null if unused.</summary>
    public Instant? RedeemedAt { get; set; }

    public Campaign Campaign { get; set; } = null!;
    public CampaignCode Code { get; set; } = null!;
}
