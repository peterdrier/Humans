using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services.Consent;

/// <summary>
/// Singleton caching decorator for <see cref="IConsentService"/> (T-04).
/// Caches per-user <see cref="UserConsentInfo"/> — the flat set of
/// document-version ids the user has explicitly consented to, with the
/// account-merge source-id chain resolved at warm/load time.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing invariant of this decorator is <b>synchronous
/// invalidation</b> on <see cref="SubmitConsentAsync"/>: the controller
/// redirects immediately after the call returns, and the next-page
/// consent-banner check (which reads
/// <see cref="GetConsentedVersionIdsAsync"/>) must not observe a stale
/// "still required" entry. The override here calls the inner submit,
/// then evicts the affected user(s) <b>before</b> returning.
/// </para>
/// <para>
/// Cache is lazy — no startup warmup. At 500-user scale a 500-id eager
/// repo round-trip is cheap, but the lazy path is plenty for the
/// consent-banner workload (the banner only fires for users who have
/// outstanding required consents; the cache fills on first banner
/// render). Adding warmup later is mechanical.
/// </para>
/// <para>
/// Reads that depend on the consented-version set
/// (<see cref="GetConsentedVersionIdsAsync"/>,
/// <see cref="GetConsentMapForUsersAsync"/>,
/// <see cref="GetRequiredConsentRowsForUserAsync"/>) route through the
/// cache. Other reads
/// (<see cref="GetConsentDashboardAsync"/>,
/// <see cref="GetConsentReviewDetailAsync"/>,
/// <see cref="GetUserConsentRecordsAsync"/>,
/// <see cref="GetConsentRecordCountAsync"/>,
/// <see cref="GetPendingDocumentNamesAsync"/>)
/// either need richer record data (history view) or are off the hot
/// path; they pass through to the inner service.
/// </para>
/// </remarks>
public sealed class CachingConsentService(
    IConsentRepository repository,
    ILegalDocumentSyncService legalDocumentSync,
    IClock clock,
    IServiceScopeFactory scopeFactory,
    ILogger<CachingConsentService> logger)
    : TrackedCache<Guid, UserConsentInfo>("Consent.UserConsentInfo", warmOnStartup: false, logger), IConsentService,
        IConsentCacheInvalidator
{
    /// <summary>
    /// DI service key under which the undecorated (inner)
    /// <see cref="IConsentService"/> is registered.
    /// </summary>
    public const string InnerServiceKey = "consent-inner";

    private readonly ILogger<CachingConsentService> _logger = logger;

    // ILegalDocumentSyncService and IClock are Singletons — inject directly.
    // _scopeFactory is still needed to resolve the keyed Scoped inner
    // IConsentService and Scoped IUserService for the SubmitConsent /
    // pass-through / chain-resolve paths.

    // ==========================================================================
    // Reads served from cache
    // ==========================================================================

    public async Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Base GetAsync handles TryGet → miss → LoadRowAsync → Set, including
        // hit/miss counter bookkeeping. LoadRowAsync returns a freshly-frozen
        // UserConsentInfo so the cached set is independent of the repo result.
        var info = await GetAsync(userId, ct).ConfigureAwait(false);
        return info?.ConsentedVersionIds ?? new HashSet<Guid>();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentMapForUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlySet<Guid>>();

        // Bucket hits from misses up front. Hits come straight from the
        // dict (no scope, no inner call). Misses are resolved via a SINGLE
        // bulk inner call — not a per-id loop into LoadRowAsync, which
        // would open N DI scopes and do N rounds of repo + IUserService
        // calls. Issue #747.
        var result = new Dictionary<Guid, IReadOnlySet<Guid>>(userIds.Count);
        // Dedupe misses: duplicate ids in userIds must not redo merge/source
        // resolution work in the inner bulk call. Tracked via a HashSet
        // because `result` is only populated after the bulk call, so the
        // result.ContainsKey check above can't catch a repeat-miss id.
        HashSet<Guid>? misses = null;
        foreach (var userId in userIds)
        {
            if (result.ContainsKey(userId)) continue;
            if (TryGet(userId, out var hit))
            {
                result[userId] = hit.ConsentedVersionIds;
            }
            else
            {
                (misses ??= new HashSet<Guid>()).Add(userId);
            }
        }

        if (misses is { Count: > 0 })
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredKeyedService<IConsentService>(InnerServiceKey);
            var missList = misses.ToList();
            var bulk = await inner.GetConsentMapForUsersAsync(missList, ct).ConfigureAwait(false);

            foreach (var userId in missList)
            {
                // Inner contract: every input id appears in the result
                // (empty set if no consents). Defensively freeze the set
                // for cache storage — same invariant LoadRowAsync upholds.
                var versions = bulk.TryGetValue(userId, out var v)
                    ? v
                    : (IReadOnlySet<Guid>)new HashSet<Guid>();
                var frozen = new HashSet<Guid>(versions);
                Set(userId, new UserConsentInfo(userId, frozen));
                result[userId] = frozen;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<RequiredConsentRow>> GetRequiredConsentRowsForUserAsync(
        Guid userId, Guid teamId, CancellationToken ct = default)
    {
        // Compose the cached consented-version set with the cached active +
        // required-document list (served by CachingLegalDocumentSyncService,
        // injected directly as a Singleton). Both halves are cache hits in
        // the warm path; we never re-enter the inner ConsentService here so
        // the repo isn't touched.
        var documents = await legalDocumentSync.GetActiveRequiredDocumentsForTeamsAsync([teamId], ct);
        var consentedVersionIds = await GetConsentedVersionIdsAsync(userId, ct);
        var now = clock.GetCurrentInstant();

        var rows = new List<RequiredConsentRow>(documents.Count);
        foreach (var doc in documents)
        {
            var currentVersion = doc.Versions
                .Where(v => v.EffectiveFrom <= now)
                .MaxBy(v => v.EffectiveFrom);

            if (currentVersion is null) continue;

            rows.Add(new RequiredConsentRow(
                DocumentVersionId: currentVersion.Id,
                Title: doc.Name,
                Signed: consentedVersionIds.Contains(currentVersion.Id)));
        }

        // Unsigned-first ordering matches the inner ConsentService impl so
        // the widget renders the same way through the decorator.
        return rows
            .OrderBy(r => r.Signed)
            .ThenBy(r => r.Title, StringComparer.Ordinal)
            .ToList();
    }

    // ==========================================================================
    // Reads passed through to inner
    // ==========================================================================

    public Task<ConsentDashboard> GetConsentDashboardAsync(Guid userId, CancellationToken ct = default) =>
        // Dashboard view needs full ConsentRecord history with document name
        // + version-number stitching — that's record-level data, not version
        // ids; the per-version-id cache here can't answer it. Pass through.
        WithInner(inner => inner.GetConsentDashboardAsync(userId, ct));

    public Task<ConsentReviewDetail?> GetConsentReviewDetailAsync(
        Guid documentVersionId, Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetConsentReviewDetailAsync(documentVersionId, userId, ct));

    public Task<IReadOnlyList<ConsentRecordSnapshot>> GetUserConsentRecordsAsync(
        Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetUserConsentRecordsAsync(userId, ct));

    public Task<int> GetConsentRecordCountAsync(Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetConsentRecordCountAsync(userId, ct));

    public Task<IReadOnlyList<string>> GetPendingDocumentNamesAsync(
        Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetPendingDocumentNamesAsync(userId, ct));

    // ==========================================================================
    // Writes — synchronous invalidation before return
    // ==========================================================================

    /// <summary>
    /// Submits a consent record and <b>synchronously</b> refreshes the
    /// affected cache entries before returning. Controllers redirect
    /// immediately after this call returns; any async/fire-and-forget
    /// refresh here would race the redirect and serve a stale "still
    /// required" banner on the next page.
    ///
    /// <para>Post-#587, the right primitive on a <c>warmOnStartup: false</c>
    /// cache for a known-mutated row is <see cref="TrackedCache{TKey,TValue}.ReplaceAsync"/>:
    /// it drives <see cref="LoadRowAsync"/> and atomically swaps in the
    /// fresh value (or tombstones the key if the loader returns null).
    /// Awaiting it inline guarantees the cache is correct before the
    /// controller redirects.</para>
    /// </summary>
    public async Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default)
    {
        ConsentSubmitResult result;
        IReadOnlySet<Guid> sourceIds;

        // Resolve the merge-chain source ids OUTSIDE the submit so we know
        // every cache key affected by this write, then refresh all of them
        // inline below. We also need the SAME source-id set the inner uses
        // to decide AlreadyConsented; resolving it once here and trusting
        // the inner is consistent (both go through IUserService).
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            sourceIds = await userService.GetMergedSourceIdsAsync(userId, ct);

            var inner = scope.ServiceProvider.GetRequiredKeyedService<IConsentService>(InnerServiceKey);
            result = await inner.SubmitConsentAsync(
                userId, documentVersionId, explicitConsent, ipAddress, userAgent, ct);
        }

        // Refresh on success only — a failed submit (StubProfile, NotFound,
        // AlreadyConsented) did not change the user's consented set, so the
        // cache entry is still correct.
        if (result.Success)
        {
            await ReplaceAsync(userId, ct).ConfigureAwait(false);
            foreach (var sourceId in sourceIds)
                await ReplaceAsync(sourceId, ct).ConfigureAwait(false);
        }

        return result;
    }

    // ==========================================================================
    // IConsentCacheInvalidator
    // ==========================================================================

    public void InvalidateUser(Guid userId)
    {
        // warmOnStartup:false — dropping the key is safe; the next read
        // lazy-loads via LoadRowAsync. No need to drive ReplaceAsync from
        // the synchronous invalidator surface (callers like AccountMerge
        // hold no CancellationToken contract for an awaited refresh).
        Invalidate(userId);
    }

    public void InvalidateAll()
    {
        Clear();
    }

    // ==========================================================================
    // Per-user loader plugged into TrackedCache.GetAsync / ReplaceAsync
    // ==========================================================================

    /// <summary>
    /// Base-class loader. Resolves the merge-chain source ids, reads the
    /// consented-version set from the repo, and returns a defensively-frozen
    /// <see cref="UserConsentInfo"/>. <see cref="TrackedCache{TKey,TValue}.GetAsync"/>
    /// and <see cref="TrackedCache{TKey,TValue}.ReplaceAsync"/> both route
    /// through this — the synchronous SubmitConsent refresh path and the
    /// lazy first-read path use the same loader.
    /// </summary>
    protected override async ValueTask<UserConsentInfo?> LoadRowAsync(
        Guid userId, CancellationToken ct)
    {
        // Resolve the source-id chain BEFORE the repo read so we know whether
        // the union path or the single-id path applies — same logic as the
        // inner ConsentService's GetChainFollowIdsAsync, lifted to warm time
        // so it does not run on every read.
        await using var scope = scopeFactory.CreateAsyncScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        var sourceIds = await userService.GetMergedSourceIdsAsync(userId, ct);

        IReadOnlySet<Guid> versions;
        if (sourceIds.Count == 0)
        {
            versions = await repository.GetExplicitlyConsentedVersionIdsAsync(userId, ct);
        }
        else
        {
            var allIds = new List<Guid>(sourceIds.Count + 1);
            allIds.AddRange(sourceIds);
            allIds.Add(userId);
            versions = await repository.GetExplicitlyConsentedVersionIdsForUserIdsAsync(allIds, ct);
        }

        // Defensively freeze with a copy on every load. The repo currently
        // returns a fresh HashSet, but we don't trust that across future
        // changes: if a repo impl ever retains and mutates the returned
        // set (e.g., adds an internal cache layer), our cached entry would
        // alias to it. Always-copy makes the cached snapshot independent of
        // the repo's lifetime semantics. Cost is trivial at 500-user scale.
        var frozen = new HashSet<Guid>(versions);
        return new UserConsentInfo(userId, frozen);
    }

    // ==========================================================================
    // Inner-service resolution
    // ==========================================================================

    private async Task<TResult> WithInner<TResult>(Func<IConsentService, Task<TResult>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IConsentService>(InnerServiceKey);
        return await action(inner);
    }
}
