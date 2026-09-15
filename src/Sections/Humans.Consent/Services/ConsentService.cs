using System.Security.Cryptography;
using System.Text;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Consent.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Governance.Contracts;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Users.Contracts;

namespace Humans.Consent.Services;

// consent_records is append-only (design-rules §12).
internal sealed class ConsentService(
    IConsentRepository repo,
    ILegalDocumentSyncService legalDocumentSyncService,
    INotificationAutoResolve notificationAutoResolve,
    IHumanLifecycleService humanLifecycleService,
    IUserServiceRead userService,
    IServiceProvider serviceProvider,
    IHumansMetrics metrics,
    IClock clock,
    ILogger<ConsentService> logger) : IConsentService, IUserDataContributor
{
    // Read ids: every id the human behind userId has held. The read resolves a merge
    // tombstone forward, so the list is the resolved record's (its own id plus the ids
    // merged into it), never [userId ∪ …]: asked with an archived id, that would leave
    // the survivor's own consents out. When nothing was merged the list is just the one
    // id, so every consent read runs through the multi-id repository methods uniformly.
    private async Task<IReadOnlyCollection<Guid>> GetChainFollowIdsAsync(
        Guid userId, CancellationToken ct) =>
        (await userService.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];

    public async Task<ConsentDashboard> GetConsentDashboardAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        var membershipCalculator = serviceProvider.GetRequiredService<IMembershipCalculatorRead>();
        var userTeamIds = await membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, ct);

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
                    var currentVersion = doc.Versions
                        .Where(v => v.EffectiveFrom <= now)
                        .MaxBy(v => v.EffectiveFrom);

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

    public async Task<ConsentReviewDetail?> GetConsentReviewDetailAsync(
        Guid documentVersionId, Guid userId, CancellationToken ct = default)
    {
        var version = await legalDocumentSyncService.GetVersionByIdAsync(documentVersionId, ct);

        if (version is null)
            return null;

        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        var consentRecord = await repo.GetByUserIdsAndVersionAsync(chainIds, documentVersionId, ct);

        // Profile is owned by Profiles section — go through UserInfo cache, not the DbSet.
        var profile = (await userService.GetUserInfoAsync(userId, ct))?.Profile;

        return new ConsentReviewDetail(
            DocumentVersionId: version.Id,
            DocumentName: version.LegalDocumentName,
            VersionNumber: version.VersionNumber,
            Content: new Dictionary<string, string>(version.Content, StringComparer.Ordinal),
            EffectiveFrom: version.EffectiveFrom,
            ChangesSummary: version.ChangesSummary,
            HasAlreadyConsented: consentRecord is not null,
            ConsentedAt: consentRecord?.ConsentedAt,
            UserFullName: profile?.FullName);
    }

    public async Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default)
    {
        // Defense-in-depth Stub gate: never write a ConsentRecord for a profile without verified legal name.
        var info = await userService.GetUserInfoAsync(userId, ct);
        if (info is null || !info.HasRequiredNameFields)
            return new ConsentSubmitResult(false, ErrorKey: "StubProfile");

        var version = await legalDocumentSyncService.GetVersionByIdAsync(documentVersionId, ct);

        if (version is null)
            return new ConsentSubmitResult(false, ErrorKey: "NotFound");

        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        var alreadyConsented = await repo.ExistsForUserIdsAndVersionAsync(chainIds, documentVersionId, ct);

        if (alreadyConsented)
            return new ConsentSubmitResult(false, ErrorKey: "AlreadyConsented");

        var canonicalContent = version.Content.GetValueOrDefault("es", string.Empty);
        var contentHash = ComputeContentHash(canonicalContent);

        var consentRecord = new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DocumentVersionId = documentVersionId,
            ConsentedAt = clock.GetCurrentInstant(),
            IpAddress = ipAddress,
            UserAgent = userAgent.Length > 500 ? userAgent[..500] : userAgent,
            ContentHash = contentHash,
            ExplicitConsent = explicitConsent
        };

        await repo.AddAsync(consentRecord, ct);
        metrics.RecordConsentGiven();

        logger.LogInformation(
            "User {UserId} consented to document {DocumentName} version {Version}",
            userId, version.LegalDocumentName, version.VersionNumber);

        // Name-only access switch: signing a consent no longer provisions system-team membership.
        // SystemTeamSyncJob reconciles Volunteers/Coordinators on name + consents, decoupled from
        // the consent write (access never depended on it).

        // Auto-resolve AccessSuspended notifications only after ALL required consents complete.
        try
        {
            var membershipCalc = serviceProvider.GetRequiredService<IMembershipCalculatorRead>();
            if (await membershipCalc.HasAllRequiredConsentsAsync(userId, ct))
            {
                await notificationAutoResolve.ResolveBySourceAsync(userId, NotificationSource.AccessSuspended, ct);
                await humanLifecycleService.RestoreConsentSuspensionAsync(userId, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to complete post-consent suspension cleanup for user {UserId}", userId);
        }

        return new ConsentSubmitResult(true, DocumentName: version.LegalDocumentName);
    }

    public async Task<int> GetConsentRecordCountAsync(Guid userId, CancellationToken ct = default)
    {
        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        return await repo.GetCountForUserIdsAsync(chainIds, ct);
    }

    public async Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        return await repo.GetExplicitlyConsentedVersionIdsForUserIdsAsync(chainIds, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentMapForUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        // Chain-follow per input id — source ids are never returned as keys, only inputs are.
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlySet<Guid>>();

        // Per input, every id its human has held: the resolved record's own id plus the
        // ids merged into it, as GetChainFollowIdsAsync does for one. An input that is
        // itself an archived id resolves to the survivor, whose own consents are then in.
        var infos = await userService.GetUserInfosAsync(userIds, ct);
        var idsByInput = new Dictionary<Guid, IReadOnlyList<Guid>>(userIds.Count);
        foreach (var userId in userIds)
        {
            idsByInput[userId] = infos.TryGetValue(userId, out var info) ? info.AllUserIds : [userId];
        }

        if (idsByInput.All(kv => kv.Value.Count == 1 && kv.Value[0] == kv.Key))
        {
            // Common case: nothing merged anywhere. Dedup defensively; callers may pass overlapping ids.
            var distinctInputs = userIds.Distinct().ToList();
            return await repo.GetExplicitlyConsentedVersionIdsForUsersAsync(distinctInputs, ct);
        }

        // One repo batch over every id any input needs.
        // HashSet dedup is required — repo's ToDictionary throws on duplicate keys.
        var allIdsSet = new HashSet<Guid>();
        foreach (var ids in idsByInput.Values)
            allIdsSet.UnionWith(ids);

        var raw = await repo.GetExplicitlyConsentedVersionIdsForUsersAsync(allIdsSet.ToList(), ct);

        // Each input unions its own id list. A reverse source→target map would not do:
        // a batch holding both a survivor and one of its tombstones resolves both to the
        // survivor's row, so both inputs carry the same list and the last one written
        // would take sole ownership of it.
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

    public async Task<IReadOnlyList<RequiredConsentRow>> GetRequiredConsentRowsForUserAsync(
        Guid userId, Guid teamId, CancellationToken ct = default)
    {
        var documents = await legalDocumentSyncService
            .GetActiveRequiredDocumentsForTeamsAsync([teamId], ct);

        var consentedVersionIds = await GetConsentedVersionIdsAsync(userId, ct);

        return RequiredConsentRows.BuildOrdered(documents, consentedVersionIds, clock.GetCurrentInstant());
    }

    public async Task<IReadOnlyList<string>> GetPendingDocumentNamesAsync(Guid userId, CancellationToken ct = default)
    {
        var membershipCalculator = serviceProvider.GetRequiredService<IMembershipCalculatorRead>();
        var missingVersionIds = await membershipCalculator.GetMissingConsentVersionsAsync(userId, ct);

        if (missingVersionIds.Count == 0)
            return [];

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var versionId in missingVersionIds)
        {
            var version = await legalDocumentSyncService.GetVersionByIdAsync(versionId, ct);
            if (version is not null)
                names.Add(version.LegalDocumentName);
        }

        return names.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        // Chain-follow for GDPR export. Source User rows are anonymized by AnonymizeForMergeAsync.
        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        var consents = await repo.GetAllForUserIdsAsync(chainIds, ct);

        var shaped = consents.Select(c => new
        {
            DocumentName = c.DocumentVersion.LegalDocument.Name,
            DocumentVersion = c.DocumentVersion.VersionNumber,
            c.ExplicitConsent,
            ConsentedAt = c.ConsentedAt.ToIso8601(),
            c.IpAddress,
            c.UserAgent
        }).ToList();

        return [new UserDataSlice(GdprExportSections.Consents, shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GdprExportSections.Consents] =
                "Retained: the consent ledger (including the IP address and user agent captured " +
                "at the moment of consent) is the evidence that processing had a lawful basis " +
                "— GDPR Art. 7(1) accountability and Art. 17(3)(e) legal claims. The ledger is " +
                "append-only and its UserId points at the tombstone the Users section leaves behind."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
}
