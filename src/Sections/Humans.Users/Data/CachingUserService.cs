using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Onboarding.Contracts;
using Humans.Base.Caching;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Humans.Base.Helpers;

namespace Humans.Users.Data;

/// <summary>
/// Issue #703. Singleton caching decorator for <see cref="IUserService"/>.
/// Inherits <see cref="TrackedCache{TKey, TValue}"/> for a hit/miss-tracked cache of
/// <see cref="UserInfo"/> entries keyed by userId — the canonical
/// "everything-about-a-person" cache.
/// </summary>
/// <remarks>
/// <para>
/// Dict hits served synchronously; a cache miss refills via the inner Scoped
/// <see cref="IUserService"/>, and every write through this surface delegates
/// and then refreshes the affected entry. Identity-machinery write paths
/// (<c>UserManager.UpdateAsync</c>, sign-in <c>LastLoginAt</c> bumps) are
/// caught by <c>UserInfoSaveChangesInterceptor</c>, which invokes
/// <see cref="IUserInfoInvalidator.InvalidateAsync"/> for every
/// touched userId.
/// </para>
/// <para>
/// Registered as Singleton so the dict persists across requests. Scoped
/// dependencies (the inner <see cref="IUserService"/>) are resolved per-call
/// via <see cref="IServiceScopeFactory"/> to avoid the captured-scoped
/// anti-pattern. Cache warm and miss paths also go through the keyed inner
/// service so the decorator does not bypass the repository-owning
/// application service.
/// </para>
/// </remarks>
internal sealed class CachingUserService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingUserService> logger) : TrackedCache<Guid, UserInfo>("User.UserInfo", warmOnStartup: true, logger),
    IUserServiceInternal, IUserInfoInvalidator, IUserInfoSliceRefresher, IEntityNameContributor
{
    /// <summary>
    /// DI service key under which the undecorated (inner) <see cref="IUserService"/>
    /// is registered. Used by the Singleton decorator to resolve the Scoped inner
    /// service per-call without triggering self-resolution on the unkeyed
    /// <see cref="IUserService"/> registration.
    /// </summary>
    public const string InnerServiceKey = "user-inner";

    // ==========================================================================
    // Merge index
    // ==========================================================================

    /// <summary>
    /// Derived index: for every id that has at least one row merged into it,
    /// transitively, the sorted ids of those rows. Null means dirty — see
    /// <see cref="OnMutated"/>. Rebuilt lazily on first read after a mutation.
    /// </summary>
    /// <remarks>
    /// Rebuild, publication and invalidation all run under <see cref="_mergeIndexLock"/>.
    /// A rebuild reads the live store, so an unlocked one that started before a mutation
    /// could finish after it and publish a pre-mutation index over the null
    /// <see cref="OnMutated"/> had just written, leaving a newly merged id invisible to
    /// consent reads and the GDPR fan-outs until the next mutation, whenever that is. A
    /// version check on publication still has that gap between the check and the write.
    /// The lock makes the stale state unrepresentable: a mutation either lands before the
    /// rebuild starts and is in it, or waits for the publish and then drops it. The cost is
    /// readers serialising behind one O(rows) walk after a mutation, which at this scale is
    /// milliseconds, a few times a day.
    /// </remarks>
    private Dictionary<Guid, Guid[]>? _mergeIndex;
    private readonly Lock _mergeIndexLock = new();

    /// <summary>
    /// Any change to the raw store can change the merge graph — a merge lands as a
    /// per-entry <c>Set</c> via <c>RefreshEntryAsync</c>, not only at warmup — so the
    /// index is dropped on every mutation rather than built once after the snapshot loads.
    /// </summary>
    protected override void OnMutated()
    {
        lock (_mergeIndexLock) _mergeIndex = null;
    }

    /// <inheritdoc cref="_mergeIndex" />
    private Dictionary<Guid, Guid[]> MergeIndex
    {
        get
        {
            lock (_mergeIndexLock)
            {
                if (_mergeIndex is { } cached) return cached;

                // One pass: each tombstone walks forward to its terminus and registers
                // itself against every node on the way, so a survivor ends up carrying
                // the whole chain behind it (A→B→C leaves C with {A, B}). The visited
                // set makes a cyclic or dangling MergedToUserId terminate rather than loop.
                var rows = AsReadOnlyDictionary;
                var builder = new Dictionary<Guid, List<Guid>>();
                foreach (var row in rows.Values)
                {
                    if (row.MergedToUserId is null) continue;

                    var visited = new HashSet<Guid> { row.Id };
                    var next = row.MergedToUserId;
                    while (next is { } nodeId && visited.Add(nodeId))
                    {
                        if (!builder.TryGetValue(nodeId, out var sources))
                            builder[nodeId] = sources = [];
                        sources.Add(row.Id);

                        next = rows.TryGetValue(nodeId, out var node) ? node.MergedToUserId : null;
                    }
                }

                var index = new Dictionary<Guid, Guid[]>(builder.Count);
                foreach (var (id, sources) in builder)
                {
                    var ids = sources.ToArray();
                    Array.Sort(ids);
                    index[id] = ids;
                }

                _mergeIndex = index;
                return index;
            }
        }
    }

    /// <summary>
    /// Stamps <see cref="UserInfo.MergedUserIds"/> from the merge index. Allocates only
    /// for a row that absorbed something, which is rare.
    /// </summary>
    private UserInfo Stamp(UserInfo row) =>
        MergeIndex.TryGetValue(row.Id, out var ids) ? row with { MergedUserIds = ids } : row;

    /// <summary>
    /// Follows a merge tombstone forward to its terminus — the first row on the chain
    /// with no <see cref="UserInfo.MergedToUserId"/>. A living row, and a GDPR-erased row
    /// (erasure reuses <c>MergedAt</c> but leaves <c>MergedToUserId</c> null), resolve to
    /// themselves. A cycle or a pointer at a row that is gone resolves to the last row
    /// reached and logs; it never throws and never loops.
    /// </summary>
    private UserInfo Resolve(UserInfo row)
    {
        if (row.MergedToUserId is null) return row;

        var visited = new HashSet<Guid> { row.Id };
        while (row.MergedToUserId is { } next)
        {
            if (!visited.Add(next))
            {
                logger.LogWarning(
                    "Merge chain cycles at userId={UserId}; resolving to that row.", row.Id);
                break;
            }
            if (!TryGet(next, out var target))
            {
                logger.LogWarning(
                    "Merge chain from userId={UserId} points at missing userId={MissingUserId}; " +
                    "resolving to the last row reached.", row.Id, next);
                break;
            }
            row = target;
        }
        return row;
    }

    // ==========================================================================
    // UserInfo reads
    // ==========================================================================

    public async ValueTask<UserInfo?> GetUserInfoAsync(Guid userId, CancellationToken ct = default)
    {
        // Warm first: MergedUserIds is a property of the whole graph, so a cold cache
        // holding only this one row would stamp an empty chain onto a survivor.
        await EnsureWarmedAsync(ct).ConfigureAwait(false);
        var row = await GetAsync(userId, ct).ConfigureAwait(false);
        return row is null ? null : Stamp(Resolve(row));
    }

    /// <inheritdoc cref="IUserService.GetRawUserInfoAsync" />
    public async ValueTask<UserInfo?> GetRawUserInfoAsync(Guid userId, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct).ConfigureAwait(false);
        var row = await GetAsync(userId, ct).ConfigureAwait(false);
        return row is null ? null : Stamp(row);
    }

    /// <inheritdoc cref="IUserService.GetAllRawUserInfosAsync" />
    public async Task<IReadOnlyCollection<UserInfo>> GetAllRawUserInfosAsync(CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct).ConfigureAwait(false);
        return Values.Select(Stamp).ToArray();
    }

    /// <summary>
    /// Per-key loader plugged into <see cref="TrackedCache{TKey,TValue}.GetAsync"/>.
    /// Resolves the Scoped inner <see cref="IUserService"/> per call; the base
    /// caches the result via <see cref="TrackedCache{TKey,TValue}.Set"/>.
    /// </summary>
    protected override async ValueTask<UserInfo?> LoadRowAsync(Guid userId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserServiceInternal>(InnerServiceKey);
        return await inner.GetUserInfoAsync(userId, ct);
    }

    /// <inheritdoc cref="IUserService.GetAllUserInfosAsync" />
    public async Task<IReadOnlyCollection<UserInfo>> GetAllUserInfosAsync(CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct).ConfigureAwait(false);
        // Tombstones are omitted — one entry per living human. Merge tombstones and
        // GDPR-erased rows alike: outside Users a merge chain does not exist. Both stay
        // reachable by id through GetUserInfoAsync.
        return Values.Where(r => !r.IsTombstone).Select(Stamp).ToArray();
    }

    /// <inheritdoc cref="IUserService.GetUserInfosAsync" />
    public async ValueTask<IReadOnlyDictionary<Guid, UserInfo>> GetUserInfosAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        // Issue #743: warm before the miss-detection loop. Without this, a cold
        // cache turns every requested id into a per-row LoadRowAsync (7 SELECTs
        // each) instead of the single bulk WarmAllAsync.
        await EnsureWarmedAsync(ct).ConfigureAwait(false);

        var result = new Dictionary<Guid, UserInfo>(userIds.Count);
        List<Guid>? misses = null;
        foreach (var id in userIds)
        {
            if (TryGet(id, out var hit))
                result[id] = Stamp(Resolve(hit));
            else
                (misses ??= []).Add(id);
        }

        if (misses is not null)
        {
            foreach (var id in misses)
            {
                var info = await LoadRowAsync(id, ct).ConfigureAwait(false);
                if (info is not null)
                {
                    Set(id, info);
                    result[id] = Stamp(Resolve(info));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// <see cref="IEntityNameContributor"/>: the human ids in a caller's Guid set,
    /// named from the warmed snapshot. Deliberately cache-only — a fan-out hands
    /// every contributor every id, so a foreign id must not cost a row load.
    /// BurnerName is the display name (memory/architecture/burnername-is-the-display-name.md);
    /// humans without one are absent.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, EntityName>> ResolveNamesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, EntityName>();

        await EnsureWarmedAsync(ct).ConfigureAwait(false);

        var cached = AsReadOnlyDictionary;
        var result = new Dictionary<Guid, EntityName>();
        foreach (var id in ids)
        {
            if (cached.TryGetValue(id, out var info)
                && !string.IsNullOrWhiteSpace(info.Profile?.BurnerName))
            {
                result[id] = new EntityName("User", info.Profile.BurnerName);
            }
        }
        return result;
    }

    /// <inheritdoc cref="IUserService.SearchUsersAsync" />
    public async Task<IReadOnlyList<HumanSearchResult>> SearchUsersAsync(
        string query, PersonSearchFields fields, int limit = 10, CancellationToken ct = default)
    {
        if (fields == PersonSearchFields.None || string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        await EnsureWarmedAsync(ct).ConfigureAwait(false);

        // Exact-UserId lookup. Lets anyone paste a UserId from logs / audit
        // trails / URLs and jump straight to that human. Skipped for ExactName
        // queries: those want literal burner-name equality, not id resolution —
        // a GUID-shaped burner name must match by name, never collide with the
        // row whose Id happens to equal the typed text.
        if ((fields & PersonSearchFields.ExactName) == PersonSearchFields.None
            && Guid.TryParse(query, out var idGuid))
        {
            // A pasted id from an audit trail may be a merged-away one: jump to its survivor.
            if (TryGet(idGuid, out var byIdRow)
                && Resolve(byIdRow) is { Profile: not null } byId
                && byId.Profile.RejectedAt is null)
            {
                return [
                    new HumanSearchResult(
                        UserId: byId.Id,
                        ProfileId: byId.Profile.Id,
                        BurnerName: byId.BurnerName,
                        ProfilePictureUrl: byId.ProfilePictureUrl,
                        MatchField: "User ID",
                        MatchSnippet: null,
                        MatchedEmail: null,
                        Score: 100) // Exact id hit — top relevance (sole result, so ordering is moot).
                ];
            }
            return [];
        }

        var results = new List<HumanSearchResult>();
        foreach (var u in Values)
        {
            // Tombstones never surface: a search hit is an id callers act on, and the survivor
            // row carries the same person.
            if (u.IsTombstone) continue;
            if (u.Profile is null) continue;
            if (u.Profile.RejectedAt is not null) continue;

            var match = PersonSearchMatcher.Match(u, query, fields);
            if (match is null) continue;

            results.Add(new HumanSearchResult(
                UserId: u.Id,
                ProfileId: u.Profile.Id,
                BurnerName: u.BurnerName,
                ProfilePictureUrl: u.ProfilePictureUrl,
                MatchField: match.Field,
                MatchSnippet: match.Snippet,
                MatchedEmail: match.MatchedEmail,
                Score: match.Score));

            if (results.Count >= limit) break;
        }

        return results;
    }

    /// <summary>
    /// Rebuilds the cache entry for <paramref name="userId"/> directly from
    /// repositories. If the user no longer exists, the entry is removed.
    /// </summary>
    private Task RefreshEntryAsync(Guid userId) =>
        ReplaceAsync(userId, CancellationToken.None);

    /// <summary>
    /// Populates the inherited cache with a <see cref="UserInfo"/> for every
    /// existing user at startup. Bulk-loads each of the contributing tables
    /// once and indexes by userId so per-user materialization is allocation-only.
    /// Trivial at our small scale.
    /// </summary>
    /// <remarks>
    /// Invoked by <see cref="TrackedCache{TKey,TValue}.EnsureWarmedAsync"/> via
    /// the <see cref="Microsoft.Extensions.Hosting.IHostedService"/> contract
    /// inherited from <see cref="TrackedCache{TKey,TValue}"/>. The base flips
    /// the warmed flag on success. An empty system (fresh dev DB / new deploy)
    /// is a legitimate warm state — this method simply returns early and the
    /// flag still flips.
    /// </remarks>
    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var users = await WithInnerAsync(inner => inner.GetAllUserInfosAsync(ct));
        foreach (var user in users)
            Set(user.Id, user);
    }

    // ==========================================================================
    // IUserInfoInvalidator (cross-section) + IUserInfoSliceRefresher (interceptor)
    // ==========================================================================

    /// <inheritdoc cref="IUserInfoInvalidator.InvalidateAsync" />
    public async Task InvalidateAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        logger.LogDebug(
            "UserInfo invalidate userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));

        // Warmed cache, row updated: replace in-place via the base primitive
        // (LoadRowAsync → Set, or DeleteKey if the inner returns null).
        await ReplaceAsync(userId, ct).ConfigureAwait(false);
    }

    public async Task RefreshUserFieldsAsync(
        User user,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        logger.LogDebug(
            "UserInfo refresh user fields userId={UserId} caller={CallerMember} file={CallerFile}",
            user.Id, memberName, Path.GetFileName(filePath));

        if (TryGet(user.Id, out var current))
        {
            Replace(user.Id, WithUserFields(current, user));
            return;
        }

        if (!IsWarmedUp)
        {
            await ReplaceAsync(user.Id, ct).ConfigureAwait(false);
            return;
        }

        Set(user.Id, UserInfo.Create(
            user,
            user.UserEmails.ToList(),
            user.EventParticipations.ToList(),
            externalLogins: [],
            profile: null,
            communicationPreferences: []));
    }

    public Task RemoveAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        logger.LogDebug(
            "UserInfo remove userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));
        DeleteKey(userId);
        return Task.CompletedTask;
    }

    public async Task RefreshUserEmailsAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        logger.LogDebug(
            "UserInfo refresh user-emails userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));

        await ReplaceAsync(userId, ct).ConfigureAwait(false);
    }

    public async Task RefreshEventParticipationsAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        logger.LogDebug(
            "UserInfo refresh event-participations userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));

        await ReplaceAsync(userId, ct).ConfigureAwait(false);
    }

    public async Task RefreshExternalLoginsAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        logger.LogDebug(
            "UserInfo refresh external-logins userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));

        await ReplaceAsync(userId, ct).ConfigureAwait(false);
    }

    public async Task RefreshCommunicationPreferencesAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        logger.LogDebug(
            "UserInfo refresh communication-preferences userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));

        await ReplaceAsync(userId, ct).ConfigureAwait(false);
    }

    private static UserInfo WithUserFields(UserInfo current, User user)
    {
        var legacyDisplayName = user.DisplayName;
        return current with
        {
            BurnerName = ResolveBurnerName(user.BurnerName, legacyDisplayName),
            // nobodies-collective/Humans#1742: never infer erasure from a user-editable name.
            // Shares UserStateEvaluator's predicate so this projection and User.State cannot
            // disagree; UserInfo.Create carries the same rule for the Contracts-side factory.
            IsGdprAnonymized = UserStateEvaluator.IsGdprTombstoned(user),
            PreferredLanguage = user.PreferredLanguage,
            FallbackPictureUrl = user.ProfilePictureUrl,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            LastConsentReminderSentAt = user.LastConsentReminderSentAt,
            DeletionRequestedAt = user.DeletionRequestedAt,
            DeletionScheduledFor = user.DeletionScheduledFor,
            DeletionEligibleAfter = user.DeletionEligibleAfter,
            UnsubscribedFromCampaigns = user.UnsubscribedFromCampaigns,
            SuppressScheduleChangeEmails = user.SuppressScheduleChangeEmails,
            MagicLinkSentAt = user.MagicLinkSentAt,
            // GoogleEmailStatus is computed from the canonical Google UserEmail row (#687) —
            // UserEmails carry over via `with`, so no explicit assignment here.
            ContactSource = user.ContactSource,
            ExternalSourceId = user.ExternalSourceId,
            MergedToUserId = user.MergedToUserId,
            MergedAt = user.MergedAt,
            IdentityEmailColumn = user.IdentityEmailColumn,
        };
    }

    /// <summary>
    /// nobodies-collective/Humans#1098: <c>User.BurnerName</c> is the sole source, with narrow
    /// tombstone recognition for legacy anonymized rows. Cache-refresh twin of
    /// <c>UserInfo.ResolveBurnerName</c> — see that doc comment for the detail.
    /// </summary>
    private static string ResolveBurnerName(string? userBurnerName, string legacyDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(userBurnerName))
            return userBurnerName;

        return string.Equals(legacyDisplayName, UserInfo.GdprAnonymizedBurnerName, StringComparison.Ordinal)
            ? UserInfo.GdprAnonymizedBurnerName
            : string.Empty;
    }

    // ==========================================================================
    // Inner delegation — every other IUserService method passes through and
    // refreshes the affected entry on writes.
    // ==========================================================================

    private async Task<T> WithInnerAsync<T>(Func<IUserServiceInternal, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserServiceInternal>(InnerServiceKey);
        return await work(inner);
    }

    private async Task WithInnerAsync(Func<IUserServiceInternal, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserServiceInternal>(InnerServiceKey);
        await work(inner);
    }

    public async Task<IReadOnlyList<UserParticipationRow>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct).ConfigureAwait(false);
        var snapshot = Values;
        var result = new List<UserParticipationRow>();
        foreach (var u in snapshot)
        {
            foreach (var p in u.EventParticipations)
            {
                if (p.Year != year) continue;
                result.Add(new UserParticipationRow(u.Id, p.Status, p.Source, p.CheckedInAt));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<OnsiteUserRow>> GetOnsiteUsersAsync(
        int year, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct).ConfigureAwait(false);
        var result = new List<OnsiteUserRow>();
        foreach (var u in Values)
        {
            var onsiteSince = u.OnsiteSinceForYear(year);
            if (onsiteSince is null) continue;
            result.Add(new OnsiteUserRow(u.Id, u.BurnerName, onsiteSince));
        }
        return result;
    }

    public async Task<UserInfo?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default)
    {
        // Verified-email match is served from the warmed snapshot first; only addresses the
        // snapshot misses (unwarmed entries, or a warm/write race) fall through to the inner
        // service's canonical user_emails query. The inner method deliberately does NOT repeat
        // this scan, so a miss costs one targeted query rather than re-deriving the whole
        // snapshot. See UserService.GetByEmailOrAlternateAsync.
        await EnsureWarmedAsync(ct).ConfigureAwait(false);
        foreach (var u in Values)
        {
            if (u.UserEmails.Any(e => e.IsVerified && EmailNormalization.EmailsMatch(e.Email, email)))
                return u;
        }

        return await WithInnerAsync(inner => inner.GetByEmailOrAlternateAsync(email, ct)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetAccountsDueForAnonymizationAsync(now, ct));

    public Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetUsersWithLoginsButNoEmailsAsync(ct));

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetExternalLoginsByUserIdsAsync(userIds, ct));

    // Writes — delegate to inner, then refresh the affected entry.

    public async Task<bool> TrySetGoogleEmailStatusFromSyncAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner =>
            inner.TrySetGoogleEmailStatusFromSyncAsync(userId, status, ct));
        if (result) await RefreshEntryAsync(userId);
        return result;
    }

    public async Task SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default)
    {
        await WithInnerAsync(inner => inner.SetPreferredLanguageAsync(userId, preferredLanguage, ct));
        await RefreshEntryAsync(userId);
    }

    public async Task RecordLoginAsync(Guid userId, CancellationToken ct = default)
    {
        await WithInnerAsync(inner => inner.RecordLoginAsync(userId, ct));
        await RefreshEntryAsync(userId);
    }

    public async Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default)
    {
        var updated = await WithInnerAsync(inner =>
            inner.SetDeletionPendingAsync(userId, requestedAt, scheduledFor, eligibleAfter, ct));
        if (updated) await RefreshEntryAsync(userId);
        return updated;
    }

    public async Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var updated = await WithInnerAsync(inner => inner.ClearDeletionAsync(userId, ct));
        if (updated) await RefreshEntryAsync(userId);
        return updated;
    }

    public async Task<bool> EnsureStubProfileAsync(
        Guid userId,
        string? burnerName = null,
        string? firstName = null,
        string? lastName = null,
        CancellationToken ct = default)
    {
        var created = await WithInnerAsync(inner =>
            inner.EnsureStubProfileAsync(userId, burnerName, firstName, lastName, ct));
        if (created) await RefreshEntryAsync(userId);
        return created;
    }

    public async Task<bool> SetMembershipTierAsync(
        Guid userId,
        MembershipTier tier,
        CancellationToken ct = default)
    {
        var updated = await WithInnerAsync(inner => inner.SetMembershipTierAsync(userId, tier, ct));
        if (updated) await RefreshEntryAsync(userId);
        return updated;
    }

    public async Task<OnboardingResult> ApplyProfileOnboardingMutationAsync(
        Guid userId,
        UserProfileOnboardingCommand command,
        CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner =>
            inner.ApplyProfileOnboardingMutationAsync(userId, command, ct));
        if (result.Success) await RefreshEntryAsync(userId);
        return result;
    }

    public async Task<UserProfileSaveResult> SaveProfileAsync(
        Guid userId,
        UserProfileSaveCommand command,
        CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner =>
            inner.SaveProfileAsync(userId, command, ct));
        await RefreshEntryAsync(userId);
        return result;
    }

    public async Task SaveDietaryMedicalAsync(
        Guid userId,
        UserProfileDietaryMedicalCommand command,
        CancellationToken ct = default)
    {
        await WithInnerAsync(async inner =>
        {
            await inner.SaveDietaryMedicalAsync(userId, command, ct);
            return true;
        });
        await RefreshEntryAsync(userId);
    }

    public async Task<UserProfilePictureContentTypeResult> SetProfilePictureContentTypeAsync(
        Guid userId,
        string contentType,
        CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner =>
            inner.SetProfilePictureContentTypeAsync(userId, contentType, ct));
        if (result.Saved) await RefreshEntryAsync(userId);
        return result;
    }

    public async Task<UserProfileAnonymizeResult> AnonymizeProfileForDeletionAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner =>
            inner.AnonymizeProfileForDeletionAsync(userId, ct));
        if (result.Anonymized) await RefreshEntryAsync(userId);
        return result;
    }

    public async Task<bool> SaveProfileVolunteerHistoryAsync(
        Guid userId,
        IReadOnlyList<CVEntry> entries,
        CancellationToken ct = default)
    {
        var saved = await WithInnerAsync(inner =>
            inner.SaveProfileVolunteerHistoryAsync(userId, entries, ct));
        if (saved) await RefreshEntryAsync(userId);
        return saved;
    }

    public async Task<UserProfileLanguagesSaveResult> SaveProfileLanguagesAsync(
        Guid profileId,
        IReadOnlyList<ProfileLanguageInfo> languages,
        CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner =>
            inner.SaveProfileLanguagesAsync(profileId, languages, ct));
        if (result.UserId is { } userId)
            await RefreshEntryAsync(userId);
        return result;
    }

    public async Task<bool> SetProfileIbanAsync(Guid userId, string? iban, CancellationToken ct = default)
    {
        var updated = await WithInnerAsync(inner => inner.SetProfileIbanAsync(userId, iban, ct));
        if (updated) await RefreshEntryAsync(userId);
        return updated;
    }

    public async Task<IReadOnlySet<Guid>> SuspendProfilesForMissingConsentAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        var mutated = await WithInnerAsync(inner =>
            inner.SuspendProfilesForMissingConsentAsync(userIds, ct));
        foreach (var userId in mutated)
            await RefreshEntryAsync(userId);
        return mutated;
    }

    public async Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>
        DowngradeMembershipTierForExpiredAsync(
            MembershipTier currentTier,
            IReadOnlyCollection<Guid> userIdsToKeep,
            IReadOnlyDictionary<Guid, MembershipTier> fallbackTierByUser,
            Instant now,
            CancellationToken ct = default)
    {
        var downgrades = await WithInnerAsync(inner =>
            inner.DowngradeMembershipTierForExpiredAsync(
                currentTier, userIdsToKeep, fallbackTierByUser, now, ct));
        foreach (var (userId, _) in downgrades)
            await RefreshEntryAsync(userId);
        return downgrades;
    }

    public async Task<UserEmailAddResult> AddUserEmailAsync(
        Guid userId,
        UserEmailAddCommand command,
        CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.AddUserEmailAsync(userId, command, ct));
        if (result.Added) await RefreshUserEmailsAsync(userId, ct);
        return result;
    }

    public async Task<bool> UpdateUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailUpdateCommand command,
        CancellationToken ct = default)
    {
        var updated = await WithInnerAsync(inner => inner.UpdateUserEmailAsync(userId, emailId, command, ct));
        if (updated) await RefreshUserEmailsAsync(userId, ct);
        return updated;
    }

    public async Task<bool> RemoveUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailRemoveCommand command,
        CancellationToken ct = default)
    {
        var removed = await WithInnerAsync(inner => inner.RemoveUserEmailAsync(userId, emailId, command, ct));
        if (removed) await RefreshUserEmailsAsync(userId, ct);
        return removed;
    }

    public async Task<UserEmailReconcilePlanResult> ApplyUserEmailReconcilePlanAsync(
        Guid userId,
        UserEmailReconcilePlanCommand command,
        CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.ApplyUserEmailReconcilePlanAsync(userId, command, ct));
        foreach (var mutatedUserId in result.MutatedUserIds)
            await RefreshUserEmailsAsync(mutatedUserId, ct);
        return result;
    }

    public Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default) =>
        WithInnerAsync(async inner =>
        {
            await inner.SetLastConsentReminderSentAsync(userId, sentAt, ct);
            await RefreshEntryAsync(userId);
        });

    public async Task DeclareNotAttendingAsync(
        Guid userId, int year, CancellationToken ct = default)
    {
        await WithInnerAsync(inner => inner.DeclareNotAttendingAsync(userId, year, ct));
        await RefreshEntryAsync(userId);
    }

    public async Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.UndoNotAttendingAsync(userId, year, ct));
        if (result) await RefreshEntryAsync(userId);
        return result;
    }

    public async Task SetParticipationFromTicketSyncAsync(
        Guid userId, int year, ParticipationStatus status, Instant? checkedInAt, CancellationToken ct = default)
    {
        await WithInnerAsync(inner =>
            inner.SetParticipationFromTicketSyncAsync(userId, year, status, checkedInAt, ct));
        await RefreshEventParticipationsAsync(userId, ct);
    }

    public async Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default)
    {
        await WithInnerAsync(inner => inner.RemoveTicketSyncParticipationAsync(userId, year, ct));
        await RefreshEventParticipationsAsync(userId, ct);
    }

    public async Task<int> BackfillParticipationsAsync(
        int year,
        List<(Guid UserId, ParticipationStatus Status)> entries,
        CancellationToken ct = default)
    {
        var count = await WithInnerAsync(inner => inner.BackfillParticipationsAsync(year, entries, ct));
        foreach (var userId in entries.Select(e => e.UserId).Distinct())
            await RefreshEventParticipationsAsync(userId, ct);
        return count;
    }

    public async Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.ApplyExpiredDeletionAnonymizationAsync(userId, ct));
        if (result is not null) await RefreshEntryAsync(userId);
        return result;
    }

    public async Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner =>
            inner.AnonymizeForMergeAsync(sourceUserId, targetUserId, now, ct));
        if (result)
        {
            await RefreshEntryAsync(sourceUserId);
            await RefreshEntryAsync(targetUserId);
        }
        return result;
    }

    public async Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var deleted = await WithInnerAsync(inner => inner.DeleteUsersAsync(userIds, ct));
        foreach (var userId in userIds)
        {
            DeleteKey(userId);
        }
        return deleted;
    }

    // ==========================================================================
    // IUserMerge — delegate to inner, then refresh both ends.
    // ==========================================================================

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct)
    {
        await WithInnerAsync(inner =>
            inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct));
        await RefreshEntryAsync(mergedFromUserId);
        await RefreshEntryAsync(mergedToUserId);
    }

}
