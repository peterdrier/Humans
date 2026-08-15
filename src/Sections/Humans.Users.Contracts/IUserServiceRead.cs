using NodaTime;

namespace Humans.Users.Contracts;

/// <summary>
/// Cross-section read surface for the Users section. External sections inject
/// this interface; it exposes only UserInfo / HumanSearchResult / OnsiteUserRow
/// projections and the merge-chain-follow primitive — no EF entities, no writes,
/// no cache hooks. See memory/architecture/section-read-write-split.md.
/// </summary>
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
    /// <see cref="GetUserInfoAsync"/>. The single batched-lookup surface for
    /// cross-section readers — no entity-returning equivalent exists
    /// (nobodies-collective/Humans#979).
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
    /// <see cref="PersonSearchFields.Admin"/> or
    /// <see cref="PersonSearchFields.LegalName"/> (which deanonymizes burners)
    /// is a programmer error caught in code review, not a runtime check.</para>
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

    /// <summary>
    /// Get all participation records for a given year, projected to the slim
    /// <see cref="UserParticipationRow"/> shape (no EF entity leaves the
    /// section). Served from the caching decorator's <see cref="UserInfo"/>
    /// snapshot.
    /// </summary>
    Task<IReadOnlyList<UserParticipationRow>> GetAllParticipationsForYearAsync(
        int year, CancellationToken ct = default);

    /// <summary>
    /// Returns the user ids of every account with <c>DeletionScheduledFor</c>
    /// in the past (or equal to <paramref name="now"/>) and with
    /// <c>DeletionEligibleAfter</c> either null or already elapsed. Used by
    /// the account deletion job to enumerate candidates without reading the
    /// Users table directly (design-rules §2c).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default);

    /// <summary>
    /// Finds the user whose <c>Email</c> or <c>GoogleEmail</c> matches the given
    /// address (case-insensitive) and returns the cached <see cref="UserInfo"/>
    /// read-model for them. Also checks the gmail/googlemail alternate when
    /// applicable, and falls back to the legacy <c>User.GoogleEmail</c> shadow
    /// column for pre-issue-687 users whose <c>UserEmail.IsGoogle</c> rows are
    /// unset. Returns null if no match.
    /// </summary>
    Task<UserInfo?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default);
}

/// <summary>
/// Slim cross-section projection of an <c>EventParticipation</c> row for a given
/// year, returned by <see cref="IUserServiceRead.GetAllParticipationsForYearAsync"/>.
/// Carries only the facts consumers diff against (status, source, check-in
/// instant) keyed by user — no EF entity crosses the section boundary.
/// </summary>
public sealed record UserParticipationRow(
    Guid UserId,
    ParticipationStatus Status,
    ParticipationSource Source,
    Instant? CheckedInAt);

/// <summary>
/// Per-user row returned from <see cref="IUserServiceRead.GetOnsiteUsersAsync"/>.
/// Names of camps / teams / governance roles are not stitched in here; the Web
/// layer joins them via the owning section services before rendering. Issue
/// nobodies-collective/Humans#736.
/// </summary>
public sealed record OnsiteUserRow(
    Guid UserId,
    string DisplayName,
    Instant? CheckedInAt);
