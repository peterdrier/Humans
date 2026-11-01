using System.Globalization;
using System.Resources;
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
/// Singleton caching decorator for <see cref="IUserManagement"/>: the
/// "everything-about-a-person" <see cref="UserInfo"/> cache, keyed by userId.
/// </summary>
/// <remarks>
/// Writes through this surface refresh the affected entry; Identity-machinery writes
/// (<c>UserManager.UpdateAsync</c>, sign-in <c>LastLoginAt</c>) are caught by
/// <c>UserInfoSaveChangesInterceptor</c>. Warm and miss paths go through the keyed inner
/// service, so the decorator never bypasses the repository-owning service.
/// </remarks>
internal sealed class CachingUserService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingUserService> logger) : TrackedCache<Guid, UserInfo>("User.UserInfo", warmOnStartup: true, logger),
    IUserManagement, IUserInfoSliceRefresher, IEntityNameContributor
{
    private static readonly ResourceManager SearchResources = new(typeof(UsersResource));

    /// <summary>DI key of the undecorated inner service, so the decorator can resolve it without resolving itself.</summary>
    public const string InnerServiceKey = "user-inner";

    /// <summary>
    /// For every id with rows merged into it (transitively), the sorted ids of those rows.
    /// Null means dirty; rebuilt lazily on first read after a mutation.
    /// </summary>
    /// <remarks>
    /// Rebuild, publish and invalidate all run under <see cref="_mergeIndexLock"/>. An unlocked
    /// rebuild that began before a mutation could publish a pre-mutation index over the null,
    /// hiding a newly merged id from consent reads and GDPR fan-outs until the next mutation.
    /// </remarks>
    private Dictionary<Guid, Guid[]>? _mergeIndex;
    private readonly Lock _mergeIndexLock = new();

    /// <summary>A merge lands as a per-entry <c>Set</c>, not only at warmup, so every mutation drops the index.</summary>
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

    protected override async ValueTask<UserInfo?> LoadRowAsync(Guid userId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserManagement>(InnerServiceKey);
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
        // Warm first: a cold cache turns every requested id into a per-row load.
        await EnsureWarmedAsync(ct).ConfigureAwait(false);

        var result = new Dictionary<Guid, UserInfo>(userIds.Count);
        foreach (var id in userIds.Distinct())
        {
            var info = await GetAsync(id, ct).ConfigureAwait(false);
            if (info is not null)
                result[id] = Stamp(Resolve(info));
        }

        return result;
    }

    /// <summary>
    /// <see cref="IEntityNameContributor"/>: names from the warmed snapshot only — a fan-out hands
    /// every contributor every id, so a foreign id must not cost a row load. Humans without a
    /// BurnerName are absent.
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

        // Exact-UserId lookup, so a pasted id jumps straight to that human. Skipped for
        // ExactName: a GUID-shaped burner name must match by name, never by row id.
        if ((fields & PersonSearchFields.ExactName) == PersonSearchFields.None
            && Guid.TryParse(query, out var idGuid))
        {
            // A pasted id from an audit trail may be a merged-away one: jump to its survivor.
            if (TryGet(idGuid, out var byIdRow)
                && Resolve(byIdRow) is { Profile: not null } byId
                && !byId.IsTombstone
                && !byId.IsRejected)
            {
                return [
                    new HumanSearchResult(
                        UserId: byId.Id,
                        ProfileId: byId.Profile.Id,
                        BurnerName: byId.BurnerName,
                        ProfilePictureUrl: byId.ProfilePictureUrl,
                        MatchField: SearchResources.GetString("Users_AdminHuman_UserId", CultureInfo.CurrentUICulture),
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
            if (u.IsRejected) continue;

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
    /// Bulk-loads each contributing table once. An empty system is a legitimate warm state.
    /// </summary>
    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var users = await WithInnerAsync(inner => inner.GetAllUserInfosAsync(ct));
        foreach (var user in users)
            Set(user.Id, user);
    }

    /// <inheritdoc cref="IUserInfoSliceRefresher.RefreshAsync" />
    public async Task RefreshAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        logger.LogDebug(
            "UserInfo refresh userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));

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

    private static UserInfo WithUserFields(UserInfo current, User user) =>
        UserInfo.Create(user, [], [], [], current.Profile, []) with
        {
            UserEmails = current.UserEmails,
#pragma warning disable HUM_USER_PARTICIPATIONS, HUM_USER_COMMPREFS // carrying the frozen legacy slices across a user-row refresh, not reading them
            EventParticipations = current.EventParticipations,
            CommunicationPreferences = current.CommunicationPreferences,
#pragma warning restore HUM_USER_PARTICIPATIONS, HUM_USER_COMMPREFS
            ExternalLogins = current.ExternalLogins,
            MergedUserIds = current.MergedUserIds,
        };

    private async Task<T> WithInnerAsync<T>(Func<IUserManagement, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserManagement>(InnerServiceKey);
        return await work(inner);
    }

    private async Task WithInnerAsync(Func<IUserManagement, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserManagement>(InnerServiceKey);
        await work(inner);
    }

    public async Task<UserInfo?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default)
    {
        // Served from the warmed snapshot first; only misses fall through to the inner
        // service's query, which deliberately does not repeat this scan.
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
        CancellationToken ct = default,
        Guid? actorUserId = null)
    {
        var created = await WithInnerAsync(inner =>
            inner.EnsureStubProfileAsync(userId, burnerName, firstName, lastName, ct, actorUserId));
        if (created) await RefreshEntryAsync(userId);
        return created;
    }

    public async Task<OnboardingResult> SetSuspensionAsync(
        Guid userId, bool suspended, bool adminSuspension = false, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner =>
            inner.SetSuspensionAsync(userId, suspended, adminSuspension, ct));
        if (result.Success) await RefreshEntryAsync(userId);
        return result;
    }

    public async Task<OnboardingResult> SetRejectedAsync(Guid userId, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.SetRejectedAsync(userId, ct));
        if (result.Success) await RefreshEntryAsync(userId);
        return result;
    }

    public async Task<UserProfileSaveResult> SaveProfileAsync(
        Guid userId,
        UserProfileSaveCommand command,
        byte[]? profilePictureData = null,
        CancellationToken ct = default,
        IReadOnlyList<CVEntry>? volunteerHistory = null)
    {
        var result = await WithInnerAsync(inner =>
            inner.SaveProfileAsync(userId, command, profilePictureData, ct, volunteerHistory));
        await RefreshEntryAsync(userId);
        return result;
    }

    public async Task SaveMedicalConditionsAsync(
        Guid userId,
        string? medicalConditions,
        CancellationToken ct = default)
    {
        await WithInnerAsync(async inner =>
        {
            await inner.SaveMedicalConditionsAsync(userId, medicalConditions, ct);
            return true;
        });
        await RefreshEntryAsync(userId);
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

    public async Task<UserEmailAddResult> AddUserEmailAsync(
        Guid userId,
        UserEmailAddCommand command,
        CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.AddUserEmailAsync(userId, command, ct));
        // The email row committed; finish refresh so cancellation cannot skip the caller's audit/notice.
        if (result.Added) await RefreshUserEmailsAsync(userId, CancellationToken.None);
        return result;
    }

    public async Task<UpdateEmailResult> UpdateUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailUpdateCommand command,
        CancellationToken ct = default)
    {
        var updated = await WithInnerAsync(inner => inner.UpdateUserEmailAsync(userId, emailId, command, ct));
        // The flag change committed; finish the read-through refresh before returning to its caller.
        if (updated.Succeeded) await RefreshUserEmailsAsync(userId, CancellationToken.None);
        return updated;
    }

    public async Task<bool> RemoveUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailRemoveCommand command,
        CancellationToken ct = default)
    {
        var removed = await WithInnerAsync(inner => inner.RemoveUserEmailAsync(userId, emailId, command, ct));
        // The removal committed; do not leave a stale address cached or skip the caller's audit.
        if (removed) await RefreshUserEmailsAsync(userId, CancellationToken.None);
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
