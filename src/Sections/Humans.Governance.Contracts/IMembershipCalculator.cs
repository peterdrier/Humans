using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Governance.Contracts;

public interface IMembershipCalculator : IOrchestrator
{
    /// <summary>The consents the Volunteers team requires.</summary>
    Task<bool> HasAllRequiredConsentsAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetMissingConsentVersionsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Users with an active assignment, missing required consents, whose grace period has expired. A positive
    /// <paramref name="expiringWithin"/> also includes deadlines within it, for advance reminders.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUsersRequiringStatusUpdateAsync(CancellationToken cancellationToken = default, Duration? expiringWithin = null);

    /// <summary>The subset of <paramref name="userIds"/> with all Volunteers-team consents, in one batch.</summary>
    Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default);

    Task<bool> HasAllRequiredConsentsForTeamAsync(Guid userId, Guid teamId, CancellationToken ct = default);

    Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsForTeamAsync(
        IEnumerable<Guid> userIds, Guid teamId, CancellationToken ct = default);

    Task<MembershipSnapshot> GetMembershipSnapshotAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Current memberships plus system teams the user is eligible for (e.g. Leads, if they lead any
    /// user-created team).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetRequiredTeamIdsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every input user lands in exactly one bucket. Priority: PendingDeletion, Suspended, IncompleteSignup,
    /// PendingApproval, then MissingConsents or Active.
    /// </summary>
    Task<MembershipPartition> PartitionUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default);
}
