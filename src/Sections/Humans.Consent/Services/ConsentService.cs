using System.Security.Cryptography;
using System.Text;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Consent.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Auth.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Users.Contracts;
using Humans.Base.Enums;

namespace Humans.Consent.Services;

internal sealed class ConsentService(
    IConsentRepository repo,
    CachingConsentService consentCache,
    ILegalDocumentSyncManagement legalDocumentSyncService,
    IHumanLifecycleService humanLifecycleService,
    IUserService userService,
    ITeamService teamService,
    IRoleAssignmentService roleAssignments,
    IServiceProvider serviceProvider,
    IHumansMetrics metrics,
    IClock clock,
    ILogger<ConsentService> logger) : IConsentManagement, IUserDataContributor
{
    internal const string Consents = "Consents";

    public async Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default)
    {
        // Refuse before any write: a false append-only record would block a later valid signature.
        if (!explicitConsent)
            return new ConsentSubmitResult(false, ErrorKey: "ExplicitConsentRequired");

        // Defense in depth: never write a record for a profile without a verified legal name.
        var info = await userService.GetUserInfoAsync(userId, ct);
        if (info is null || !info.HasRequiredNameFields)
            return new ConsentSubmitResult(false, ErrorKey: "StubProfile");
        userId = info.Id;

        var version = await legalDocumentSyncService.GetVersionByIdAsync(documentVersionId, ct);

        if (version is null)
            return new ConsentSubmitResult(false, ErrorKey: "NotFound");

        // Hash only an available canonical document, never a translation or an empty fallback.
        var canonicalContent = version.Content.GetValueOrDefault("es");
        if (string.IsNullOrWhiteSpace(canonicalContent))
        {
            logger.LogWarning("Consent refused: canonical Spanish content is unavailable for version {DocumentVersionId}",
                documentVersionId);
            return new ConsentSubmitResult(false, ErrorKey: "ContentUnavailable");
        }

        var alreadyConsented = await repo.ExistsForUserIdsAndVersionAsync(info.AllUserIds, documentVersionId, ct);

        if (alreadyConsented)
            return new ConsentSubmitResult(false, ErrorKey: "AlreadyConsented");

        var contentHash = ComputeContentHash(canonicalContent);

        var userAgentLength = Math.Min(500, userAgent.Length);
        if (userAgentLength < userAgent.Length && char.IsHighSurrogate(userAgent[userAgentLength - 1])
            && char.IsLowSurrogate(userAgent[userAgentLength]))
            userAgentLength--;

        var consentRecord = new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DocumentVersionId = documentVersionId,
            ConsentedAt = clock.GetCurrentInstant(),
            IpAddress = ipAddress,
            UserAgent = userAgent[..userAgentLength],
            ContentHash = contentHash,
            ExplicitConsent = explicitConsent
        };

        try
        {
            await repo.AddAsync(consentRecord, ct);
        }
        finally
        {
            // A failed acknowledgement can follow a committed signature. Evict every alias before any
            // membership check reads it back, even when the decorator never reaches its inline refresh.
            foreach (var id in info.AllUserIds)
                consentCache.Invalidate(id);
        }
        metrics.RecordConsentGiven();

        logger.LogInformation(
            "User {UserId} consented to document {DocumentName} version {Version}",
            userId, version.LegalDocumentName, version.VersionNumber);

        // Consent is committed: finish suspension cleanup even if the request ends.
        try
        {
            if (await HasAllRequiredConsentsForTeamAsync(
                    userId, SystemTeamIds.Volunteers, CancellationToken.None))
            {
                await humanLifecycleService.RestoreConsentSuspensionAsync(userId, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to complete post-consent suspension cleanup for user {UserId}", userId);
        }

        return new ConsentSubmitResult(true, DocumentName: version.LegalDocumentName);
    }

    public Task<ConsentReviewInfo> GetConsentReviewAsync(Guid userId, CancellationToken ct = default) =>
        serviceProvider.GetRequiredService<ConsentReviewService>().GetReviewAsync(userId, ct);

    public Task<IReadOnlyDictionary<Guid, ConsentReviewInfo>> GetConsentReviewsAsync(CancellationToken ct = default) =>
        serviceProvider.GetRequiredService<ConsentReviewService>().GetReviewsAsync(ct);

    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
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

        return [new UserDataSlice(Consents, shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Consents] =
                "Retained: the consent ledger (including the IP address and user agent captured " +
                "at the moment of consent) is the evidence that processing had a lawful basis " +
                "— GDPR Art. 7(1) accountability and Art. 17(3)(e) legal claims. The ledger is " +
                "append-only and its UserId points at the tombstone the Users section leaves behind."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;

    // The resolved record's ids, never [userId ∪ …]: asked with an archived id, that would leave the
    // survivor's own consents out.
    internal async Task<IReadOnlyCollection<Guid>> GetChainFollowIdsAsync(
        Guid userId, CancellationToken ct) =>
        (await userService.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];

    public async Task<ConsentDashboard> GetConsentDashboardAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        var userTeamIds = await GetRequiredTeamIdsAsync(userId, ct);

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

                return new ConsentDashboardTeamGroup(first.TeamName, docPairs);
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

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetUserIdChainsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var infos = await userService.GetUserInfosAsync(userIds, ct);
        var idsByInput = new Dictionary<Guid, IReadOnlyList<Guid>>(userIds.Count);
        foreach (var userId in userIds)
            idsByInput[userId] = infos.TryGetValue(userId, out var info) ? info.AllUserIds : [userId];
        return idsByInput;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentMapForUsersAsync(
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

    public async Task<IReadOnlyList<RequiredConsentRow>> GetRequiredConsentRowsForUserAsync(
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

    public async Task<IReadOnlyList<string>> GetPendingDocumentNamesAsync(Guid userId, CancellationToken ct = default)
    {
        var missingVersionIds = await GetMissingVolunteerVersionIdsAsync(userId, ct);

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

    public async Task<bool> HasAllRequiredConsentsForTeamAsync(Guid userId, Guid teamId, CancellationToken ct = default)
    {
        var requiredVersions = await legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(teamId, ct);
        if (requiredVersions.Count == 0)
            return true;

        var consentedVersionIds = await consentCache.GetConsentedVersionIdsAsync(userId, ct);
        return requiredVersions.All(v => consentedVersionIds.Contains(v.Id));
    }

    public async Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsForTeamAsync(
        IEnumerable<Guid> userIds, Guid teamId, CancellationToken ct = default)
    {
        var userIdList = userIds.ToList();
        if (userIdList.Count == 0)
            return new HashSet<Guid>();

        var requiredVersions = await legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(teamId, ct);
        if (requiredVersions.Count == 0)
            return userIdList.ToHashSet();

        var consentsByUser = await consentCache.GetConsentMapForUsersAsync(userIdList, ct);
        var requiredIds = requiredVersions.Select(v => v.Id).ToHashSet();

        return userIdList
            .Where(id => consentsByUser.TryGetValue(id, out var consented) && requiredIds.All(consented.Contains))
            .ToHashSet();
    }

    /// <summary>The Volunteers documents the user hasn't signed.</summary>
    internal async Task<IReadOnlyList<Guid>> GetMissingVolunteerVersionIdsAsync(Guid userId, CancellationToken ct)
    {
        var requiredVersions = await legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(SystemTeamIds.Volunteers, ct);
        var consentedVersionIds = await consentCache.GetConsentedVersionIdsAsync(userId, ct);

        return requiredVersions.Select(v => v.Id).Where(id => !consentedVersionIds.Contains(id)).ToList();
    }

    public async Task<ConsentProgress> GetConsentProgressAsync(Guid userId, CancellationToken ct = default)
    {
        var requiredVersionIds = new HashSet<Guid>();
        foreach (var teamId in await GetRequiredTeamIdsAsync(userId, ct))
        {
            var versions = await legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(teamId, ct);
            requiredVersionIds.UnionWith(versions.Select(v => v.Id));
        }

        var consentedVersionIds = await consentCache.GetConsentedVersionIdsAsync(userId, ct);
        return new ConsentProgress(
            requiredVersionIds.Count,
            requiredVersionIds.Where(id => !consentedVersionIds.Contains(id)).ToList());
    }

    public async Task<IReadOnlyList<Guid>> GetUsersPastConsentDeadlineAsync(Duration? expiringWithin = null, CancellationToken ct = default)
    {
        var horizon = expiringWithin ?? Duration.Zero;
        if (horizon < Duration.Zero) throw new ArgumentOutOfRangeException(nameof(expiringWithin));
        var deadline = clock.GetCurrentInstant() + horizon;

        var userIds = await roleAssignments.GetUserIdsWithActiveAssignmentsAsync(ct);
        if (userIds.Count == 0)
            return [];

        var requiredVersions = await legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(SystemTeamIds.Volunteers, ct);
        var dueVersionIds = requiredVersions
            .Where(v => v.EffectiveFrom + Duration.FromDays(v.LegalDocumentGracePeriodDays) <= deadline)
            .Select(v => v.Id)
            .ToHashSet();
        if (dueVersionIds.Count == 0)
            return [];

        var consentsByUser = await consentCache.GetConsentMapForUsersAsync(userIds, ct);
        return userIds
            .Where(id => !consentsByUser.TryGetValue(id, out var consented) || dueVersionIds.Any(v => !consented.Contains(v)))
            .ToList();
    }

    /// <summary>
    /// The user's teams, plus Volunteers (its documents apply to everyone) and Coordinators when they coordinate
    /// a user-created team.
    /// </summary>
    internal async Task<IReadOnlyList<Guid>> GetRequiredTeamIdsAsync(Guid userId, CancellationToken ct)
    {
        // Memberships were re-pointed to the survivor on merge.
        userId = (await userService.GetUserInfoAsync(userId, ct))?.Id ?? userId;

        var teams = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.Members.Any(m => m.UserId == userId))
            .ToList();
        var teamIds = teams.Select(t => t.Id).ToList();

        if (!teamIds.Contains(SystemTeamIds.Volunteers))
            teamIds.Add(SystemTeamIds.Volunteers);

        if (!teamIds.Contains(SystemTeamIds.Coordinators) &&
            teams.Any(t => t.SystemTeamType == SystemTeamType.None &&
                t.Members.Any(m => m.UserId == userId && m.Role == TeamMemberRole.Coordinator)))
        {
            teamIds.Add(SystemTeamIds.Coordinators);
        }

        return teamIds;
    }
}
