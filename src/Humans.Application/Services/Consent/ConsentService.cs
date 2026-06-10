using System.Security.Cryptography;
using System.Text;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.HumanLifecycle;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Consent;

// consent_records is append-only (design-rules §12). Legal-doc repo migration #547a still pending.
public sealed class ConsentService(
    IConsentRepository repo,
    ILegalDocumentSyncService legalDocumentSyncService,
    INotificationInboxService notificationInboxService,
    IHumanLifecycleService humanLifecycleService,
    IUserServiceRead userService,
    IServiceProvider serviceProvider,
    IHumansMetrics metrics,
    IClock clock,
    ILogger<ConsentService> logger) : IConsentService, IUserDataContributor
{
    // Chain-follow read ids: always {userId ∪ merged-source-ids}. When userId is not a
    // fold target the set is just [userId], so every consent read runs through the
    // multi-id repository methods uniformly (the single-id repo overloads are exactly
    // these called with one id).
    private async Task<IReadOnlyCollection<Guid>> GetChainFollowIdsAsync(
        Guid userId, CancellationToken ct)
    {
        var sourceIds = await userService.GetMergedSourceIdsAsync(userId, ct);

        var allIds = new List<Guid>(sourceIds.Count + 1) { userId };
        allIds.AddRange(sourceIds);
        return allIds;
    }

    public async Task<ConsentDashboard> GetConsentDashboardAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        var membershipCalculator = serviceProvider.GetRequiredService<IMembershipCalculatorRead>();
        var userTeamIds = await membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, ct);

        // Doc listing still goes through ILegalDocumentSyncService (#547a not yet done).
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
                await notificationInboxService.ResolveBySourceAsync(userId, NotificationSource.AccessSuspended, ct);
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

        // TODO(perf): batch GetAllMergedSourceIdsByTargetsAsync(...) → single query. Negligible at ~500 users.
        var sourcesByTarget = new Dictionary<Guid, IReadOnlySet<Guid>>(userIds.Count);
        foreach (var userId in userIds)
        {
            sourcesByTarget[userId] = await userService.GetMergedSourceIdsAsync(userId, ct);
        }

        var hasAnySources = sourcesByTarget.Values.Any(s => s.Count > 0);
        if (!hasAnySources)
        {
            // Common case: no merged sources. Dedup defensively — callers may pass overlapping ids.
            var distinctInputs = userIds.Distinct().ToList();
            return await repo.GetExplicitlyConsentedVersionIdsForUsersAsync(distinctInputs, ct);
        }

        // Build {target ∪ source ids} for the repo batch + reverse source→target map for re-keying.
        // HashSet dedup is required — repo's ToDictionary throws on duplicate keys.
        var allIdsSet = new HashSet<Guid>(userIds);
        var sourceToTarget = new Dictionary<Guid, Guid>();
        foreach (var userId in userIds)
        {
            foreach (var sourceId in sourcesByTarget[userId])
            {
                allIdsSet.Add(sourceId);
                sourceToTarget[sourceId] = userId;
            }
        }

        var raw = await repo.GetExplicitlyConsentedVersionIdsForUsersAsync(allIdsSet.ToList(), ct);

        var result = new Dictionary<Guid, IReadOnlySet<Guid>>(userIds.Count);
        foreach (var userId in userIds)
        {
            var merged = new HashSet<Guid>(raw[userId]);
            foreach (var (sourceId, targetId) in sourceToTarget)
            {
                if (targetId == userId && raw.TryGetValue(sourceId, out var sourceVersions))
                {
                    foreach (var versionId in sourceVersions)
                        merged.Add(versionId);
                }
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

        return RequiredConsentRow.BuildOrdered(documents, consentedVersionIds, clock.GetCurrentInstant());
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
}
