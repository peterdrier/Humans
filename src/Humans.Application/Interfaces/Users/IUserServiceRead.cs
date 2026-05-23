using Humans.Application.Architecture;
using Humans.Application.DTOs;
using Humans.Application.Services.Profiles;

namespace Humans.Application.Interfaces.Users;

/// <summary>
/// Cross-section read surface for the Users section. External sections inject
/// this interface; it exposes only UserInfo / HumanSearchResult / OnsiteUserRow
/// projections and the merge-chain-follow primitive — no EF entities, no writes,
/// no cache hooks. See memory/architecture/section-read-write-split.md.
/// </summary>
[SurfaceBudget(6)]
public interface IUserServiceRead
{
    /// <summary>
    /// Returns the unified <see cref="UserInfo"/> read-model for the given
    /// user, stitched from <c>users</c>, <c>user_emails</c>,
    /// <c>event_participations</c>, <c>user_logins</c>, <c>profiles</c>,
    /// <c>contact_fields</c>, <c>profile_languages</c>, and
    /// <c>volunteer_history_entries</c>. Issue #703: the caching decorator
    /// serves dict hits synchronously; the inner service rebuilds from
    /// repositories on miss.
    /// </summary>
    ValueTask<UserInfo?> GetUserInfoAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a snapshot of every cached <see cref="UserInfo"/>. The cache is
    /// the canonical "everything-about-a-person" source; admin stat tiles,
    /// debug surfaces, and cross-section aggregates read from this snapshot
    /// rather than re-querying the contributing tables. Returns a new
    /// collection per call — the underlying dictionary is mutable and callers
    /// iterate without locking. Drives warmup on demand if the cache is cold.
    /// </summary>
    Task<IReadOnlyCollection<UserInfo>> GetAllUserInfosAsync(CancellationToken ct = default);

    /// <summary>
    /// Batched <see cref="UserInfo"/> lookup. Returns a dictionary keyed by
    /// user id; ids without a corresponding user are absent. Served from the
    /// caching decorator's in-memory dict for any id already cached; missing
    /// ids are refilled through the same per-user load path used by
    /// <see cref="GetUserInfoAsync"/>. The canonical replacement for
    /// <see cref="IUserService.GetByIdsAsync"/> at reader call sites — that still exists
    /// for the rare consumer that needs a real entity
    /// (Identity machinery, in-place mutations).
    /// </summary>
    ValueTask<IReadOnlyDictionary<Guid, UserInfo>> GetUserInfosAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Single canonical person-search method. Matches <paramref name="query"/>
    /// against the buckets named by <paramref name="fields"/> over the cached
    /// <see cref="UserInfo"/> snapshot and returns up to <paramref name="limit"/>
    /// matches in unspecified order — callers sort + take(N) at the presentation
    /// layer per <c>memory/architecture/display-sort-in-controllers.md</c>.
    ///
    /// <para>Implicit scope: rows are filtered to "not rejected, has a
    /// profile" — the only population anyone is searching. Emergency-contact
    /// data is never reachable regardless of which bits are set.</para>
    ///
    /// <para>Auth boundary is the controller per design-rules §6: services
    /// are auth-free, so a non-admin endpoint passing
    /// <see cref="PersonSearchFields.Admin"/> is a programmer error caught
    /// in code review, not a runtime check.</para>
    /// </summary>
    Task<IReadOnlyList<HumanSearchResult>> SearchUsersAsync(
        string query,
        PersonSearchFields fields,
        int limit = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a flat list of every user currently on-site — that is, whose
    /// participation record for <paramref name="year"/> has status Attended with a
    /// non-null checked-in timestamp. Caller (Web layer) joins in
    /// camp / team / governance-role names via the owning section services and
    /// applies filters. Issue nobodies-collective/Humans#736.
    /// </summary>
    /// <remarks>
    /// Implemented on the caching decorator only — the inner
    /// <c>UserService</c> derives this from the cached <c>UserInfo</c>
    /// snapshot and the inner implementation throws
    /// <see cref="NotSupportedException"/>. Any DI registration that resolves
    /// the inner service directly (test doubles aside) will hit that throw on
    /// first call.
    /// </remarks>
    Task<IReadOnlyList<OnsiteUserRow>> GetOnsiteUsersAsync(
        int year, CancellationToken ct = default);

    /// <summary>
    /// Returns the set of source-tombstone ids whose <c>MergedToUserId</c>
    /// equals <paramref name="targetUserId"/>. Single canonical chain-follow
    /// primitive: AuditLog, Consent, BudgetAuditLog reads call this rather
    /// than each section reinventing the lookup. Set is small (typically
    /// zero, usually one).
    /// </summary>
    Task<IReadOnlySet<Guid>> GetMergedSourceIdsAsync(
        Guid targetUserId, CancellationToken ct = default);
}
