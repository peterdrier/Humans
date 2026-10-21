using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>
/// A member's current membership tier, one row per member (<c>governance_membership_tiers</c>).
/// Replaces <c>profiles.MembershipTier</c>, which Users keeps unwritten and unread until its drop.
/// A member with no row is a Volunteer; a row holding <see cref="MembershipTier.Volunteer"/>
/// records an expired term.
/// </summary>
internal sealed class MembershipTierRecord
{
    /// <summary>The member. Bare id, no FK to <c>users</c>
    /// (memory/architecture/no-cross-section-ef-joins.md).</summary>
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
