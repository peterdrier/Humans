using Humans.Base.Caching;
using Humans.Consent.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Consent.Services;

/// <summary>
/// Caches each user's consented version ids, lazily. <see cref="SubmitConsentAsync"/> refreshes affected entries
/// before returning: the controller redirects at once and the next page's consent banner must not read a stale
/// set. An entry built over a different id list than the human's current one is rebuilt, so a merge needs no
/// signal from Users.
/// </summary>
internal sealed class CachingConsentService(
    ILegalDocumentSyncManagement legalDocumentSync,
    IUserService userService,
    IClock clock,
    IServiceScopeFactory scopeFactory,
    ILogger<CachingConsentService> logger)
    : TrackedCache<Guid, UserConsentInfo>("Consent.UserConsentInfo", warmOnStartup: false, logger), IConsentManagement
{
    public const string InnerServiceKey = "consent-inner";

    public async Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var userIds = await GetUserIdsAsync(userId, ct).ConfigureAwait(false);
        var info = await GetAsync(userId, ct).ConfigureAwait(false);
        if (info is not null && !info.UserIds.SequenceEqual(userIds))
        {
            // Built over a different id list (a merge since): rebuild over the current one.
            Invalidate(userId);
            info = await GetAsync(userId, ct).ConfigureAwait(false);
        }
        return info?.ConsentedVersionIds ?? new HashSet<Guid>();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentMapForUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlySet<Guid>>();

        // Misses go in one bulk inner call, not a LoadRowAsync per id (N scopes, N rounds of reads). Id lists are
        // read before the load, so an entry is never tagged newer than its data.
        var infos = await userService.GetUserInfosAsync(userIds, ct).ConfigureAwait(false);
        IReadOnlyList<Guid> UserIdsOf(Guid id) => infos.TryGetValue(id, out var info) ? info.AllUserIds : [id];

        var result = new Dictionary<Guid, IReadOnlySet<Guid>>(userIds.Count);
        // A HashSet, because result is filled only after the bulk call and can't catch a repeated miss.
        HashSet<Guid>? misses = null;
        foreach (var userId in userIds)
        {
            if (result.ContainsKey(userId)) continue;
            if (TryGet(userId, out var hit) && hit.UserIds.SequenceEqual(UserIdsOf(userId)))
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
            var inner = scope.ServiceProvider.GetRequiredKeyedService<IConsentManagement>(InnerServiceKey);
            var missList = misses.ToList();
            var bulk = await inner.GetConsentMapForUsersAsync(missList, ct).ConfigureAwait(false);

            foreach (var userId in missList)
            {
                var versions = bulk.TryGetValue(userId, out var v)
                    ? v
                    : new HashSet<Guid>();
                var frozen = new HashSet<Guid>(versions);
                Set(userId, new UserConsentInfo(userId, frozen, UserIdsOf(userId)));
                result[userId] = frozen;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<RequiredConsentRow>> GetRequiredConsentRowsForUserAsync(
        Guid userId, Guid teamId, CancellationToken ct = default)
    {
        // Both halves are cache hits when warm. BuildOrdered is shared with the inner ConsentService, so both
        // produce the same rows.
        var documents = await legalDocumentSync.GetActiveRequiredDocumentsForTeamsAsync([teamId], ct);
        var consentedVersionIds = await GetConsentedVersionIdsAsync(userId, ct);

        return RequiredConsentRows.BuildOrdered(documents, consentedVersionIds, clock.GetCurrentInstant());
    }

    public Task<ConsentDashboard> GetConsentDashboardAsync(Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetConsentDashboardAsync(userId, ct));

    public Task<ConsentReviewDetail?> GetConsentReviewDetailAsync(
        Guid documentVersionId, Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetConsentReviewDetailAsync(documentVersionId, userId, ct));

    public Task<int> GetConsentRecordCountAsync(Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetConsentRecordCountAsync(userId, ct));

    public Task<IReadOnlyList<string>> GetPendingDocumentNamesAsync(
        Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetPendingDocumentNamesAsync(userId, ct));

    public Task<ConsentReviewInfo> GetConsentReviewAsync(Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetConsentReviewAsync(userId, ct));

    public Task<IReadOnlyDictionary<Guid, ConsentReviewInfo>> GetConsentReviewsAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetConsentReviewsAsync(ct));

    /// <summary>
    /// Refreshes affected entries inline with <c>ReplaceAsync</c> before returning: an async refresh would race
    /// the controller's redirect and show a stale "still required" banner.
    /// </summary>
    public async Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default)
    {
        ConsentSubmitResult result;
        IReadOnlyList<Guid> chainIds;

        // Resolve the id list outside the submit to know every cache key the write touches. The inner decides
        // AlreadyConsented from the same list.
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            chainIds = await GetUserIdsAsync(userId, ct);

            var inner = scope.ServiceProvider.GetRequiredKeyedService<IConsentManagement>(InnerServiceKey);
            result = await inner.SubmitConsentAsync(
                userId, documentVersionId, explicitConsent, ipAddress, userAgent, ct);
        }

        if (result.Success)
        {
            // The submitted id is refreshed even when a stale read left it out of the list.
            var affectedIds = chainIds.Contains(userId) ? chainIds : [userId, .. chainIds];
            // Evict every alias first so a failed reload can't leave the rest serving the pre-submit set.
            foreach (var id in affectedIds)
                Invalidate(id);
            // The consent write succeeded; request cancellation must not interrupt its refresh.
            foreach (var id in affectedIds)
                await ReplaceAsync(id, CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }

    protected override async ValueTask<UserConsentInfo?> LoadRowAsync(
        Guid userId, CancellationToken ct)
    {
        // Read the id list first, so the entry is never tagged with a list newer than its set.
        var userIds = await GetUserIdsAsync(userId, ct);
        var versions = await WithInnerAsync(inner => inner.GetConsentedVersionIdsAsync(userId, ct));

        var frozen = new HashSet<Guid>(versions);
        return new UserConsentInfo(userId, frozen, userIds);
    }

    private async Task<IReadOnlyList<Guid>> GetUserIdsAsync(Guid userId, CancellationToken ct) =>
        (await userService.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];

    private async Task<TResult> WithInnerAsync<TResult>(Func<IConsentManagement, Task<TResult>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IConsentManagement>(InnerServiceKey);
        return await action(inner);
    }
}
