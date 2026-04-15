using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Governance aggregate (<c>applications</c>,
/// <c>application_state_histories</c>, <c>board_votes</c>). The only non-test
/// file that may touch those DbSets.
/// </summary>
/// <remarks>
/// Returns entities with aggregate-local navigation collections
/// (<c>StateHistory</c>, <c>BoardVotes</c>) eagerly loaded when appropriate,
/// but never cross-domain navs — those are FK-only after the migration.
/// See <c>docs/architecture/design-rules.md</c> §3 for the canonical shape.
/// </remarks>
public interface IApplicationRepository
{
    /// <summary>
    /// Loads a single application by id, including its aggregate-local
    /// <c>StateHistory</c> and <c>BoardVotes</c> collections.
    /// </summary>
    Task<MemberApplication?> GetByIdAsync(Guid applicationId, CancellationToken ct = default);

    /// <summary>
    /// Loads every application with aggregate-local collections eagerly
    /// loaded. Used by the startup warmup hosted service to populate
    /// <c>IApplicationStore</c>. Trivial at ~500-user scale.
    /// </summary>
    Task<IReadOnlyList<MemberApplication>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns every application for a user, ordered by <c>SubmittedAt</c>
    /// descending. Aggregate-local <c>StateHistory</c> is included.
    /// </summary>
    Task<IReadOnlyList<MemberApplication>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// True if the user has a pending (Submitted) application. Used by
    /// <c>SubmitAsync</c> to enforce the "one pending application per user"
    /// invariant.
    /// </summary>
    Task<bool> AnySubmittedForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Count applications in a given status. Used by the admin dashboard
    /// and the Board daily digest.
    /// </summary>
    Task<int> CountByStatusAsync(ApplicationStatus status, CancellationToken ct = default);

    /// <summary>
    /// Paginated filtered list of applications for the admin
    /// <c>Applications.cshtml</c> view. Default <paramref name="status"/>
    /// (null) maps to <see cref="ApplicationStatus.Submitted"/>, preserving
    /// pre-migration behavior.
    /// </summary>
    Task<(IReadOnlyList<MemberApplication> Items, int TotalCount)> GetFilteredAsync(
        ApplicationStatus? status,
        MembershipTier? tier,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Persists a new application.
    /// </summary>
    Task AddAsync(MemberApplication application, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to an existing application (e.g. Withdraw).
    /// Does NOT delete BoardVotes — see <see cref="FinalizeAsync"/> for the
    /// approve/reject transactional commit.
    /// </summary>
    Task UpdateAsync(MemberApplication application, CancellationToken ct = default);

    /// <summary>
    /// Atomic finalize for approve/reject: persists the already-mutated
    /// <paramref name="application"/> (state, history row, term expiry,
    /// decision note) AND bulk-deletes every <c>BoardVote</c> row for this
    /// application, all in one <c>SaveChangesAsync</c>. Call
    /// <see cref="GetVoterIdsForApplicationAsync"/> BEFORE this if the
    /// caller needs voter ids for post-write cache invalidation.
    /// </summary>
    Task FinalizeAsync(MemberApplication application, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct user ids of every Board member who has cast a
    /// vote on this application. Used by the caching decorator to
    /// invalidate per-voter voting badges after a successful finalize.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetVoterIdsForApplicationAsync(Guid applicationId, CancellationToken ct = default);
}
