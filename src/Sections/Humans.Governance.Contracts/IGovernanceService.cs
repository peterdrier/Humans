using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Governance.Contracts;

/// <summary>Cross-section reads of membership tiers and active tier terms. Tier applications stay inside Governance, and Governance has no write contract.</summary>
public interface IGovernanceService : IApplicationService
{
    /// <summary>
    /// Distinct users with an Approved <paramref name="tier"/> application whose term is open on
    /// <paramref name="today"/> (<c>TermExpiresAt</c> null or on/after it).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveApprovedTierUserIdsAsync(
        MembershipTier tier, LocalDate today, CancellationToken ct = default);

    /// <summary>Same term rule as <see cref="GetActiveApprovedTierUserIdsAsync"/>, for one user.</summary>
    Task<bool> HasActiveApprovedTierAsync(
        Guid userId, MembershipTier tier, LocalDate today, CancellationToken ct = default);

    /// <summary>Volunteer when they hold none. Follows an account merge to the surviving account.</summary>
    Task<MembershipTier> GetMembershipTierAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Non-Volunteer members only; an absent id means <see cref="MembershipTier.Volunteer"/>.</summary>
    Task<IReadOnlyDictionary<Guid, MembershipTier>> GetMembershipTiersAsync(CancellationToken ct = default);
}
