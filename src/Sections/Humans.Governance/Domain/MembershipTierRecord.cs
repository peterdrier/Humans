using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>
/// One row per member. No row means Volunteer; a <see cref="MembershipTier.Volunteer"/> row
/// records an expired term, so the read never falls back to the legacy <c>profiles.MembershipTier</c>.
/// </summary>
internal sealed class MembershipTierRecord
{
    public Guid UserId { get; init; }

    public MembershipTier Tier { get; set; }

    /// <summary>When this tier was written: the approval, the expiry downgrade, or the copy.</summary>
    public Instant GrantedAt { get; set; }

    /// <summary>The granting application's <c>TermExpiresAt</c>; null for Volunteer.</summary>
    public LocalDate? ExpiresAt { get; set; }

    /// <summary>The approved application that granted the tier; null for Volunteer or a copied
    /// legacy tier with no matching approved application.</summary>
    public Guid? GrantedByApplicationId { get; set; }
}
