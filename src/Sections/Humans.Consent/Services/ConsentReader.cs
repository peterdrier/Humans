using Humans.Base.Interfaces;
using Humans.Consent.Contracts;
using Humans.Consent.Data;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Consent.Services;

internal sealed class ConsentReader(
    IConsentRepository repo,
    ILegalDocumentSyncManagement legalDocumentSyncService,
    IUserService userService,
    ConsentCompliance compliance,
    IClock clock,
    ILogger<ConsentService> logger) : IApplicationService
{
    internal ConsentCompliance Compliance => compliance;

    // The resolved record's ids, never [userId ∪ …]: asked with an archived id, that would leave the
    // survivor's own consents out.
    internal async Task<IReadOnlyCollection<Guid>> GetChainFollowIdsAsync(
        Guid userId, CancellationToken ct) =>
        (await userService.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];

    internal async Task<ConsentDashboard> GetConsentDashboardAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        var userTeamIds = await compliance.GetRequiredTeamIdsAsync(userId, ct);

        var documents = await legalDocumentSyncService.GetActiveRequiredDocumentsForTeamsAsync(userTeamIds, ct);

        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        var userConsents = await repo.GetAllForUserIdsAsync(chainIds, ct);

        var groups = documents
            .GroupBy(d => d.TeamId)
            .Select(g =>
            {
                var first = g.First();
                var docPairs = new List<ConsentDashboardDocument>();

                foreach (var doc in g)
                {
                    var currentVersion = doc.CurrentVersionAt(now);

                    if (currentVersion is not null)
                    {
                        var consent = userConsents.FirstOrDefault(c => c.DocumentVersionId == currentVersion.Id);
                        docPairs.Add(new ConsentDashboardDocument(
                            DocumentVersionId: currentVersion.Id,
                            DocumentName: doc.Name,
                            VersionNumber: currentVersion.VersionNumber,
                            EffectiveFrom: currentVersion.EffectiveFrom,
                            HasConsented: consent is not null,
                            ConsentedAt: consent?.ConsentedAt,
                            ChangesSummary: currentVersion.ChangesSummary,
                            LastUpdated: doc.LastSyncedAt == default ? null : doc.LastSyncedAt));
                    }
                }

                return new ConsentDashboardTeamGroup(first.TeamId, first.TeamName, docPairs);
            })
            .ToList();

        var history = userConsents.Select(c => new ConsentDashboardHistoryItem(
                DocumentVersionId: c.DocumentVersionId,
                DocumentName: c.DocumentVersion.LegalDocument.Name,
                VersionNumber: c.DocumentVersion.VersionNumber,
                ConsentedAt: c.ConsentedAt))
            .ToList();

        return new ConsentDashboard(groups, history);
    }

    internal async Task<ConsentReviewDetail?> GetConsentReviewDetailAsync(
        Guid documentVersionId, Guid userId, CancellationToken ct = default)
    {
        var version = await legalDocumentSyncService.GetVersionByIdAsync(documentVersionId, ct);

        if (version is null)
            return null;
        if (string.IsNullOrWhiteSpace(version.Content.GetValueOrDefault("es")))
        {
            logger.LogWarning("Canonical Spanish content is unavailable for review of version {DocumentVersionId}",
                documentVersionId);
            return null;
        }

        var info = await userService.GetUserInfoAsync(userId, ct);
        var chainIds = info?.AllUserIds ?? [userId];
        var consentRecord = await repo.GetByUserIdsAndVersionAsync(chainIds, documentVersionId, ct);

        return new ConsentReviewDetail(
            DocumentVersionId: version.Id,
            DocumentName: version.LegalDocumentName,
            VersionNumber: version.VersionNumber,
            Content: new Dictionary<string, string>(version.Content, StringComparer.Ordinal),
            EffectiveFrom: version.EffectiveFrom,
            ChangesSummary: version.ChangesSummary,
            HasAlreadyConsented: consentRecord is not null,
            ConsentedAt: consentRecord?.ConsentedAt,
            UserFullName: info?.Profile is null ? null : info.FullName);
    }

    internal async Task<int> GetConsentRecordCountAsync(Guid userId, CancellationToken ct = default)
    {
        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        return await repo.GetCountForUserIdsAsync(chainIds, ct);
    }

    internal async Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        return await repo.GetExplicitlyConsentedVersionIdsForUserIdsAsync(chainIds, ct);
    }

    internal async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetUserIdChainsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var infos = await userService.GetUserInfosAsync(userIds, ct);
        var idsByInput = new Dictionary<Guid, IReadOnlyList<Guid>>(userIds.Count);
        foreach (var userId in userIds)
            idsByInput[userId] = infos.TryGetValue(userId, out var info) ? info.AllUserIds : [userId];
        return idsByInput;
    }

    internal async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentMapForUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        // Only inputs are returned as keys, never source ids.
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlySet<Guid>>();

        // An archived input resolves to the survivor, whose own consents are then included.
        var idsByInput = await GetUserIdChainsAsync(userIds, ct);

        if (idsByInput.All(kv => kv.Value.Count == 1 && kv.Value[0] == kv.Key))
        {
            // Callers may pass overlapping ids.
            var distinctInputs = userIds.Distinct().ToList();
            return await repo.GetExplicitlyConsentedVersionIdsForUsersAsync(distinctInputs, ct);
        }

        // The repository's ToDictionary throws on duplicate keys.
        var allIdsSet = new HashSet<Guid>();
        foreach (var ids in idsByInput.Values)
            allIdsSet.UnionWith(ids);

        var raw = await repo.GetExplicitlyConsentedVersionIdsForUsersAsync(allIdsSet.ToList(), ct);

        // Each input unions its own id list. A reverse source→target map wouldn't do: a batch holding a survivor
        // and one of its tombstones resolves both to the survivor, and the last written would own the list.
        var result = new Dictionary<Guid, IReadOnlySet<Guid>>(userIds.Count);
        foreach (var userId in userIds)
        {
            var merged = new HashSet<Guid>();
            foreach (var id in idsByInput[userId])
            {
                if (raw.TryGetValue(id, out var versions))
                    merged.UnionWith(versions);
            }
            result[userId] = merged;
        }

        return result;
    }

    internal async Task<IReadOnlyList<RequiredConsentRow>> GetRequiredConsentRowsForUserAsync(
        Guid userId, Guid teamId, CancellationToken ct = default)
    {
        var documents = await legalDocumentSyncService
            .GetActiveRequiredDocumentsForTeamsAsync([teamId], ct);

        var consentedVersionIds = await GetConsentedVersionIdsAsync(userId, ct);

        var now = clock.GetCurrentInstant();
        return documents
            .Select(doc => (Document: doc, Version: doc.CurrentVersionAt(now)))
            .Where(item => item.Version is not null)
            .Select(item => new RequiredConsentRow(
                item.Version!.Id, item.Document.Name, consentedVersionIds.Contains(item.Version.Id)))
            .OrderBy(row => row.Signed)
            .ThenBy(row => row.Title, StringComparer.Ordinal)
            .ToList();
    }

    internal async Task<IReadOnlyList<string>> GetPendingDocumentNamesAsync(Guid userId, CancellationToken ct = default)
    {
        var missingVersionIds = await compliance.GetMissingVolunteerVersionIdsAsync(userId, ct);

        if (missingVersionIds.Count == 0)
            return [];

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var versionId in missingVersionIds)
        {
            var version = await legalDocumentSyncService.GetVersionByIdAsync(versionId, ct);
            if (version is not null)
                names.Add(version.LegalDocumentName);
        }

        return names.ToList();
    }

}
