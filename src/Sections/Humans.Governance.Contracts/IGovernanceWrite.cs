using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Governance.Contracts;

/// <summary>
/// Governance writes with a consumer outside the section: the membership-tier term expiry that
/// Teams' <c>SystemTeamSyncJob</c> runs before it syncs the Colaboradors and Asociados teams.
/// </summary>
/// <remarks>
/// Submit, approve, reject, withdraw, board voting and the admin reads have no consumer outside the
/// section and stay on the internal <c>ApplicationDecisionService</c>, which the section's own
/// controllers inject by concrete type. Carve the leaf from the *call sites*, not from the
/// interface.
/// </remarks>
public interface IGovernanceWrite : IApplicationService
{
    /// <summary>
    /// Term expiry for <paramref name="tier"/>: every member holding it without an Approved
    /// application for it still in term on <paramref name="today"/> drops to their other in-term
    /// tier, or to Volunteer. Returns each member with the tier written, for the caller's
    /// <c>TierDowngraded</c> audit entries.
    /// </summary>
    Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>> ExpireMembershipTiersAsync(
        MembershipTier tier, LocalDate today, CancellationToken ct = default);
}

public record ApplicationDecisionResult(bool Success, string? ErrorKey = null, Guid? ApplicationId = null);

public sealed record UserApplicationSnapshot(
    Guid Id,
    Guid UserId,
    ApplicationStatus Status,
    MembershipTier MembershipTier,
    Instant SubmittedAt,
    Instant? ResolvedAt,
    LocalDate? TermExpiresAt,
    string Motivation,
    string? AdditionalInfo,
    string? SignificantContribution,
    string? RoleUnderstanding);

public sealed record ApplicationRenewalReminderCandidate(
    Guid Id,
    Guid UserId,
    MembershipTier MembershipTier,
    Instant SubmittedAt,
    LocalDate? TermExpiresAt);

public sealed record SubmittedApplicationSnapshot(
    Guid Id,
    MembershipTier MembershipTier,
    string Motivation);
