using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services.Legal;

/// <summary>
/// Singleton caching decorator for <see cref="ILegalDocumentSyncService"/>
/// (T-04). Caches the global active-document set that backs the every-page
/// consent-banner read (<see cref="GetActiveRequiredDocumentsForTeamsAsync"/>)
/// and the per-version snapshot lookup
/// (<see cref="GetVersionByIdAsync"/>). The cache is lazily warmed on first
/// read and invalidated wholesale after any persisted write to
/// <c>legal_documents</c> or <c>document_versions</c> (signalled by
/// <c>LegalDocumentSaveChangesInterceptor</c> via
/// <see cref="ILegalDocumentCacheInvalidator.InvalidateAll"/>).
/// </summary>
/// <remarks>
/// <para>
/// Single underlying cache keyed by document id; we also build a
/// version-id → document-id index on warm so <see cref="GetVersionByIdAsync"/>
/// can serve from memory without scanning. Inherits
/// <see cref="TrackedCache{TKey, TValue}"/> so /Admin/CacheStats surfaces
/// hit/miss/invalidation counters.
/// </para>
/// <para>
/// Writes (admin create/update/archive/sync, version add via
/// <see cref="SyncDocumentAsync"/> / <see cref="SyncAllDocumentsAsync"/>)
/// pass through to the inner service via a freshly resolved scope. The
/// interceptor handles invalidation centrally; the decorator does not call
/// <see cref="ILegalDocumentCacheInvalidator.InvalidateAll"/> itself.
/// </para>
/// </remarks>
public sealed class CachingLegalDocumentSyncService
    : TrackedCache<Guid, LegalDocumentInfo>,
      ILegalDocumentSyncService,
      ILegalDocumentCacheInvalidator
{
    /// <summary>
    /// DI service key under which the undecorated (inner)
    /// <see cref="ILegalDocumentSyncService"/> is registered. The Singleton
    /// decorator resolves the Scoped inner per-call via
    /// <see cref="IServiceScopeFactory"/>.
    /// </summary>
    public const string InnerServiceKey = "legal-document-sync-inner";

    private readonly ILegalDocumentRepository _repository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly ILogger<CachingLegalDocumentSyncService> _logger;

    // Version-id → document-id index. Rebuilt with the main dict on warm so
    // GetVersionByIdAsync can answer without scanning every document.
    private volatile IReadOnlyDictionary<Guid, Guid> _versionToDocument =
        new Dictionary<Guid, Guid>();

    public CachingLegalDocumentSyncService(
        ILegalDocumentRepository repository,
        IServiceScopeFactory scopeFactory,
        IClock clock,
        ILogger<CachingLegalDocumentSyncService> logger)
        : base("Legal.LegalDocumentInfo", warmOnStartup: true)
    {
        _repository = repository;
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
    }

    // ==========================================================================
    // Reads served from cache
    // ==========================================================================

    public async Task<IReadOnlyList<ActiveRequiredLegalDocumentSnapshot>> GetActiveRequiredDocumentsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken = default)
    {
        var docs = await GetActiveRequiredDocumentsAsync(cancellationToken);
        if (teamIds.Count == 0)
            return [];

        var filter = teamIds as IReadOnlySet<Guid> ?? new HashSet<Guid>(teamIds);
        return docs.Values
            .Where(d => filter.Contains(d.TeamId))
            .Select(ToActiveRequiredSnapshot)
            .ToList();
    }

    public async Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredDocumentVersionsForTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default)
    {
        var docs = await GetActiveRequiredDocumentsAsync(cancellationToken);
        var now = _clock.GetCurrentInstant();

        return docs.Values
            .Where(d => d.TeamId == teamId)
            .Select(d =>
            {
                var latest = d.Versions
                    .Where(v => v.EffectiveFrom <= now)
                    .MaxBy(v => v.EffectiveFrom);
                if (latest is null) return null;
                return ToRequiredVersionSnapshot(latest, d);
            })
            .Where(v => v is not null)
            .Cast<RequiredDocumentVersionSnapshot>()
            .ToList();
    }

    public async Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var docs = await GetActiveRequiredDocumentsAsync(cancellationToken);
        var now = _clock.GetCurrentInstant();

        return docs.Values
            .Select(d =>
            {
                var latest = d.Versions
                    .Where(v => v.EffectiveFrom <= now)
                    .MaxBy(v => v.EffectiveFrom);
                if (latest is null) return null;
                return ToRequiredVersionSnapshot(latest, d);
            })
            .Where(v => v is not null)
            .Cast<RequiredDocumentVersionSnapshot>()
            .ToList();
    }

    public async Task<LegalDocumentVersionSnapshot?> GetVersionByIdAsync(
        Guid versionId, CancellationToken cancellationToken = default)
    {
        var docs = await GetActiveRequiredDocumentsAsync(cancellationToken);
        // _versionToDocument was rebuilt alongside docs in WarmAllAsync.
        if (_versionToDocument.TryGetValue(versionId, out var docId) &&
            docs.TryGetValue(docId, out var doc))
        {
            var version = doc.Versions.FirstOrDefault(v => v.Id == versionId);
            if (version is not null)
                return version;
        }

        // Cache miss for the version id. This can happen when the requested
        // version belongs to an inactive or non-required document (the cache
        // holds only active+required). Fall through to the inner service so
        // those callers — admin version-summary edits, sync-job lookups — are
        // still served correctly.
        return await WithInner(inner => inner.GetVersionByIdAsync(versionId, cancellationToken));
    }

    // ==========================================================================
    // Reads passed through to inner (not part of the every-page hot path)
    // ==========================================================================

    public Task<IReadOnlyList<LegalDocumentSnapshot>> GetActiveDocumentsAsync(
        CancellationToken cancellationToken = default) =>
        // Admin list surface. Includes non-required docs and does not benefit
        // from caching at the same shape as the consent-banner read.
        WithInner(inner => inner.GetActiveDocumentsAsync(cancellationToken));

    public Task<IReadOnlyList<LegalDocument>> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default) =>
        // GitHub-API-bound check; not a DB read. Pass through.
        WithInner(inner => inner.CheckForUpdatesAsync(cancellationToken));

    // ==========================================================================
    // Writes — passed through; LegalDocumentSaveChangesInterceptor invalidates
    // ==========================================================================

    public Task<IReadOnlyList<LegalDocument>> SyncAllDocumentsAsync(
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.SyncAllDocumentsAsync(cancellationToken));

    public Task<string?> SyncDocumentAsync(
        Guid documentId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.SyncDocumentAsync(documentId, cancellationToken));

    // ==========================================================================
    // ILegalDocumentCacheInvalidator
    // ==========================================================================

    public void InvalidateAll()
    {
        // Clear() flips the warmed flag back to false and increments the
        // bulk-invalidation counter; the next read drives a fresh
        // WarmAllAsync via EnsureWarmedAsync. Reset the version index here
        // so we don't briefly serve stale version-id→document-id lookups
        // against an empty primary dict.
        _versionToDocument = new Dictionary<Guid, Guid>();
        Clear();
    }

    // ==========================================================================
    // Warm / load
    // ==========================================================================

    private async Task<IReadOnlyDictionary<Guid, LegalDocumentInfo>> GetActiveRequiredDocumentsAsync(
        CancellationToken ct)
    {
        await EnsureWarmedAsync(ct);
        return AsReadOnlyDictionary;
    }

    /// <summary>
    /// Bulk-loads every active+required <see cref="LegalDocumentInfo"/>. Called by
    /// <see cref="TrackedCache{TKey,TValue}.EnsureWarmedAsync"/> at startup
    /// (because the base ctor passes <c>warmOnStartup: true</c>) and again on
    /// demand after <see cref="InvalidateAll"/> drops the dict. The base owns
    /// concurrency coalescing via its warm semaphore, so this body is invoked
    /// at most once at a time.
    /// </summary>
    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        // Pull every active+required document with versions. The repo
        // GetActiveRequiredDocumentsAsync read does NOT walk the
        // deprecated team nav on LegalDocument; team display names are
        // stitched here via ITeamService so the cache warm path stays
        // free of the cross-domain nav (docs/sections/LegalAndConsent.md
        // "Touch-and-clean guidance").
        var docs = await _repository.GetActiveRequiredDocumentsAsync(ct);

        // Resolve team display names via ITeamService — scoped, so
        // pulled through a fresh DI scope per-warm.
        IReadOnlyDictionary<Guid, string> teamNames = await ResolveTeamNamesAsync(docs, ct);

        var versionIndex = new Dictionary<Guid, Guid>();

        foreach (var doc in docs)
        {
            teamNames.TryGetValue(doc.TeamId, out var teamName);
            var info = BuildLegalDocumentInfo(doc, teamName ?? string.Empty);
            Set(doc.Id, info);
            foreach (var v in info.Versions)
                versionIndex[v.Id] = doc.Id;
        }

        _versionToDocument = versionIndex;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveTeamNamesAsync(
        IReadOnlyList<LegalDocument> docs, CancellationToken ct)
    {
        if (docs.Count == 0)
            return new Dictionary<Guid, string>();

        var teamIds = docs.Select(d => d.TeamId).Distinct().ToList();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var teamService = scope.ServiceProvider.GetRequiredService<ITeamService>();
        var teams = await teamService.GetByIdsWithParentsAsync(teamIds, ct);
        return teams.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
    }

    private static LegalDocumentInfo BuildLegalDocumentInfo(LegalDocument document, string teamName) =>
        new(
            document.Id,
            document.Name,
            document.TeamId,
            teamName,
            document.LastSyncedAt,
            document.Versions
                .OrderBy(v => v.EffectiveFrom)
                .Select(BuildVersionSnapshot)
                .ToList());

    private static LegalDocumentVersionSnapshot BuildVersionSnapshot(DocumentVersion version) =>
        new(
            version.Id,
            version.LegalDocumentId,
            version.LegalDocument.Name,
            version.LegalDocument.GracePeriodDays,
            version.VersionNumber,
            new Dictionary<string, string>(version.Content, StringComparer.Ordinal),
            version.EffectiveFrom,
            version.RequiresReConsent,
            version.CreatedAt,
            version.ChangesSummary);

    private static ActiveRequiredLegalDocumentSnapshot ToActiveRequiredSnapshot(LegalDocumentInfo info) =>
        new(
            info.Id,
            info.Name,
            info.TeamId,
            info.TeamName,
            info.LastSyncedAt,
            info.Versions);

    private static RequiredDocumentVersionSnapshot ToRequiredVersionSnapshot(
        LegalDocumentVersionSnapshot version, LegalDocumentInfo document) =>
        new(
            version.Id,
            document.Id,
            document.Name,
            version.LegalDocumentGracePeriodDays,
            version.VersionNumber,
            version.EffectiveFrom,
            version.RequiresReConsent,
            version.ChangesSummary);

    // ==========================================================================
    // Inner-service resolution
    // ==========================================================================

    private async Task<TResult> WithInner<TResult>(Func<ILegalDocumentSyncService, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider
            .GetRequiredKeyedService<ILegalDocumentSyncService>(InnerServiceKey);
        return await action(inner);
    }
}
