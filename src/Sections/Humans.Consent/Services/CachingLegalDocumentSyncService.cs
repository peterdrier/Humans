using Humans.Base.Caching;
using Humans.Consent.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Consent.Domain;

namespace Humans.Consent.Services;

/// <summary>
/// Caches the active, required documents behind the every-page consent banner, warmed on boot. The inner
/// <c>LegalDocumentSyncService</c>, the sole writer, calls <see cref="InvalidateAll"/> after each write.
/// </summary>
internal sealed class CachingLegalDocumentSyncService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<CachingLegalDocumentSyncService> logger)
    : TrackedCache<Guid, LegalDocumentInfo>("Legal.LegalDocumentInfo", warmOnStartup: true, logger),
        ILegalDocumentSyncManagement
{
    public const string InnerServiceKey = "legal-document-sync-inner";

    private volatile IReadOnlyDictionary<Guid, Guid> _versionToDocument =
        new Dictionary<Guid, Guid>();

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

    public async Task<int> GetActiveRequiredCountAsync(CancellationToken cancellationToken = default)
    {
        var docs = await GetActiveRequiredDocumentsAsync(cancellationToken);
        return docs.Count;
    }

    public async Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredDocumentVersionsForTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default)
    {
        var docs = await GetActiveRequiredDocumentsAsync(cancellationToken);
        var now = clock.GetCurrentInstant();

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
        var now = clock.GetCurrentInstant();

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
        if (_versionToDocument.TryGetValue(versionId, out var docId) &&
            docs.TryGetValue(docId, out var doc))
        {
            var version = doc.Versions.FirstOrDefault(v => v.Id == versionId);
            if (version is not null)
                return version;
        }

        // Not cached: the version belongs to an inactive or optional document.
        return await WithInnerAsync(inner => inner.GetVersionByIdAsync(versionId, cancellationToken));
    }

    public Task<IReadOnlyList<LegalDocumentSnapshot>> GetActiveDocumentsAsync(
        CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.GetActiveDocumentsAsync(cancellationToken));

    public Task<IReadOnlyList<LegalDocument>> SyncAllDocumentsAsync(
        CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.SyncAllDocumentsAsync(cancellationToken));

    public Task<string?> SyncDocumentAsync(
        Guid documentId, CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.SyncDocumentAsync(documentId, cancellationToken));

    /// <summary>Wholesale: the cache is one bag, so there is no per-document evict.</summary>
    public void InvalidateAll()
    {
        // Reset the index too, so it never serves stale version lookups against an empty primary dict.
        _versionToDocument = new Dictionary<Guid, Guid>();
        Clear();
    }

    private async Task<IReadOnlyDictionary<Guid, LegalDocumentInfo>> GetActiveRequiredDocumentsAsync(
        CancellationToken ct)
    {
        await EnsureWarmedAsync(ct);
        return AsReadOnlyDictionary;
    }

    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ILegalDocumentSyncManagement>(InnerServiceKey);
        var allDocs = await inner.GetActiveDocumentsAsync(ct);
        var docs = allDocs
            .Where(d => d.IsActive && d.IsRequired)
            .ToList();

        var teamService = scope.ServiceProvider.GetRequiredService<ITeamService>();
        var teamIds = docs.Select(d => d.TeamId).Distinct().ToList();
        var teams = teamIds.Count == 0
            ? new Dictionary<Guid, TeamInfo>()
            : await teamService.GetTeamsWithParentsAsync(teamIds, ct);

        var versionIndex = new Dictionary<Guid, Guid>();

        foreach (var info in docs)
        {
            var teamName = teams.TryGetValue(info.TeamId, out var team) ? team.Name : string.Empty;
            var cacheInfo = new LegalDocumentInfo(
                info.Id,
                info.Name,
                info.TeamId,
                teamName,
                info.LastSyncedAt,
                info.Versions.OrderBy(v => v.EffectiveFrom).ToList());

            Set(cacheInfo.Id, cacheInfo);
            foreach (var v in cacheInfo.Versions)
                versionIndex[v.Id] = cacheInfo.Id;
        }

        _versionToDocument = versionIndex;
    }

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

    private async Task<TResult> WithInnerAsync<TResult>(Func<ILegalDocumentSyncManagement, Task<TResult>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider
            .GetRequiredKeyedService<ILegalDocumentSyncManagement>(InnerServiceKey);
        return await action(inner);
    }
}
