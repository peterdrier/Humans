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
public sealed class CachingConsentService
    : TrackedCache<Guid, UserConsentInfo>,
      IConsentService,
      IConsentCacheInvalidator
{
    /// <summary>
    /// DI service key under which the undecorated (inner)
    /// <see cref="IConsentService"/> is registered.
    /// </summary>
    public const string InnerServiceKey = "consent-inner";

    private readonly IConsentRepository _repository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CachingConsentService> _logger;

    public CachingConsentService(
        IConsentRepository repository,
        IServiceScopeFactory scopeFactory,
        ILogger<CachingConsentService> logger)
        : base("Consent.UserConsentInfo")
    {
        _repository = repository;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ==========================================================================
    // Reads served from cache
    // ==========================================================================

    public async Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(
        Guid userId, CancellationToken ct = default)
    {
        if (TryGet(userId, out var hit))
            return hit.ConsentedVersionIds;

        var loaded = await LoadAndCacheAsync(userId, ct);
        return loaded.ConsentedVersionIds;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentMapForUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlySet<Guid>>();

        var result = new Dictionary<Guid, IReadOnlySet<Guid>>(userIds.Count);
        foreach (var userId in userIds)
        {
            if (result.ContainsKey(userId)) continue;
            if (TryGet(userId, out var hit))
            {
                result[userId] = hit.ConsentedVersionIds;
            }
            else
            {
                var loaded = await LoadAndCacheAsync(userId, ct);
                result[userId] = loaded.ConsentedVersionIds;
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<RequiredConsentRow>> GetRequiredConsentRowsForUserAsync(
        Guid userId, Guid teamId, CancellationToken ct = default)
    {
        // Compose the cached consented-version set with the cached active +
        // required-document list (served by CachingLegalDocumentSyncService
        // through the scope). Both halves are cache hits in the warm path;
        // we never re-enter the inner ConsentService here so the repo isn't
        // touched.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var legal = scope.ServiceProvider.GetRequiredService<ILegalDocumentSyncService>();
        var documents = await legal.GetActiveRequiredDocumentsForTeamsAsync([teamId], ct);
        var consentedVersionIds = await GetConsentedVersionIdsAsync(userId, ct);
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
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
    /// Submits a consent record and <b>synchronously</b> evicts the affected
    /// user(s) from the cache before returning. Controllers redirect
    /// immediately after this call returns; any async/fire-and-forget
    /// invalidation here would race the redirect and serve a stale
    /// "still required" banner on the next page.
    /// </summary>
    public async Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default)
    {
        ConsentSubmitResult result;
        IReadOnlySet<Guid>? sourceIds = null;

        // Resolve the merge-chain source ids OUTSIDE the submit so we know
        // every cache key affected by this write, then evict all of them
        // inline below. We also need the SAME source-id set the inner uses
        // to decide AlreadyConsented; resolving it once here and trusting
        // the inner is consistent (both go through IUserService).
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            sourceIds = await userService.GetMergedSourceIdsAsync(userId, ct);

            var inner = scope.ServiceProvider.GetRequiredKeyedService<IConsentService>(InnerServiceKey);
            result = await inner.SubmitConsentAsync(
                userId, documentVersionId, explicitConsent, ipAddress, userAgent, ct);
        }

        // Invalidate on success only — a failed submit (StubProfile,
        // NotFound, AlreadyConsented) did not change the user's consented
        // set, so the cache entry is still correct.
        if (result.Success)
        {
            InvalidateUser(userId);
            if (sourceIds is not null)
            {
                foreach (var sourceId in sourceIds)
                    InvalidateUser(sourceId);
            }
        }

        return result;
    }

    // ==========================================================================
    // IConsentCacheInvalidator
    // ==========================================================================

    public void InvalidateUser(Guid userId)
    {
        Invalidate(userId);
    }

    public void InvalidateAll()
    {
        Clear();
    }

    // ==========================================================================
    // Load + chain-follow resolution at refresh time
    // ==========================================================================

    private async Task<UserConsentInfo> LoadAndCacheAsync(Guid userId, CancellationToken ct)
    {
        // Resolve the source-id chain BEFORE the repo read so we know whether
        // the union path or the single-id path applies — same logic as the
        // inner ConsentService's GetChainFollowIdsAsync, lifted to warm time
        // so it does not run on every read.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        var sourceIds = await userService.GetMergedSourceIdsAsync(userId, ct);

        IReadOnlySet<Guid> versions;
        if (sourceIds.Count == 0)
        {
            versions = await _repository.GetExplicitlyConsentedVersionIdsAsync(userId, ct);
        }
        else
        {
            var allIds = new List<Guid>(sourceIds.Count + 1);
            allIds.AddRange(sourceIds);
            allIds.Add(userId);
            versions = await _repository.GetExplicitlyConsentedVersionIdsForUserIdsAsync(allIds, ct);
        }

        // Defensively freeze: repo returns IReadOnlySet<Guid>, but if a future
        // impl returns a mutable HashSet we want our cached entry shielded
        // from caller mutation.
        var frozen = versions is HashSet<Guid> ? versions : new HashSet<Guid>(versions);
        var info = new UserConsentInfo(userId, frozen);
        Set(userId, info);
        return info;
    }

    // ==========================================================================
    // Inner-service resolution
    // ==========================================================================

    private async Task<TResult> WithInner<TResult>(Func<IConsentService, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IConsentService>(InnerServiceKey);
        return await action(inner);
    }
}
