using System.Security.Cryptography;
using System.Text;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Consent;

/// <summary>
/// Application-layer implementation of <see cref="IConsentService"/>. Goes
/// through <see cref="IConsentRepository"/> for all consent-record access —
/// this type never imports <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph.
/// </summary>
/// <remarks>
/// <para>
/// <c>consent_records</c> is append-only per design-rules §12 — this service
/// only appends records; there is no update or delete path.
/// </para>
/// <para>
/// Cross-section dependencies are injected as service interfaces
/// (<see cref="IOnboardingService"/>, <see cref="ILegalDocumentSyncService"/>,
/// <see cref="ISystemTeamSync"/>, <see cref="INotificationInboxService"/>,
/// <see cref="IProfileService"/>). Legal-document repository migration is
/// tracked as sub-task #547a; until it lands, legal document data still
/// flows through <see cref="ILegalDocumentSyncService"/>.
/// </para>
/// <para>
/// Implements <see cref="IUserDataContributor"/> so the GDPR export
/// orchestrator can assemble per-user consent slices without crossing the
/// section boundary.
/// </para>
/// </remarks>
public sealed class ConsentService : IConsentService, IUserDataContributor
{
    private readonly IConsentRepository _repo;
    private readonly IOnboardingService _onboardingService;
    private readonly ILegalDocumentSyncService _legalDocumentSyncService;
    private readonly INotificationInboxService _notificationInboxService;
    private readonly ISystemTeamSync _syncJob;
    private readonly IProfileService _profileService;
    private readonly IUserService _userService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHumansMetrics _metrics;
    private readonly IClock _clock;
    private readonly ILogger<ConsentService> _logger;

    public ConsentService(
        IConsentRepository repo,
        IOnboardingService onboardingService,
        ILegalDocumentSyncService legalDocumentSyncService,
        INotificationInboxService notificationInboxService,
        ISystemTeamSync syncJob,
        IProfileService profileService,
        IUserService userService,
        IServiceProvider serviceProvider,
        IHumansMetrics metrics,
        IClock clock,
        ILogger<ConsentService> logger)
    {
        _repo = repo;
        _onboardingService = onboardingService;
        _legalDocumentSyncService = legalDocumentSyncService;
        _notificationInboxService = notificationInboxService;
        _syncJob = syncJob;
        _profileService = profileService;
        _userService = userService;
        _serviceProvider = serviceProvider;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Returns <paramref name="userId"/> by itself, or <c>{merged-source-ids ∪
    /// userId}</c> when <paramref name="userId"/> is a fold target. Used by
    /// the per-user chain-follow read path so consent records that stayed
    /// attributed to merged-source tombstones surface for the fold target.
    /// </summary>
    private async Task<IReadOnlyCollection<Guid>?> GetChainFollowIdsAsync(
        Guid userId, CancellationToken ct)
    {
        var sourceIds = await _userService.GetMergedSourceIdsAsync(userId, ct);
        if (sourceIds.Count == 0)
            return null;

        var allIds = new List<Guid>(sourceIds.Count + 1);
        allIds.AddRange(sourceIds);
        allIds.Add(userId);
        return allIds;
    }

    public async Task<(List<(Team Team, List<(DocumentVersion Version, ConsentRecord? Consent)> Documents)> Groups,
        List<ConsentRecord> History)>
        GetConsentDashboardAsync(Guid userId, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();

        var membershipCalculator = _serviceProvider.GetRequiredService<IMembershipCalculator>();
        var userTeamIds = await membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, ct);

        // Documents + Teams still flow through the legacy Legal document service
        // because LegalDocumentService / AdminLegalDocumentService are scoped out
        // of this migration (#547a). The document-listing surface lives on
        // ILegalDocumentSyncService today.
        var documents = await _legalDocumentSyncService.GetActiveRequiredDocumentsForTeamsAsync(userTeamIds, ct);

        // Chain-follow merge tombstones so a fold-target's consent dashboard
        // transparently surfaces records still attributed to merged source ids.
        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        var userConsents = chainIds is null
            ? await _repo.GetAllForUserAsync(userId, ct)
            : await _repo.GetAllForUserIdsAsync(chainIds, ct);

        var groups = documents
            .GroupBy(d => d.TeamId)
            .Select(g =>
            {
                var team = g.First().Team;
                var docPairs = new List<(DocumentVersion Version, ConsentRecord? Consent)>();

                foreach (var doc in g)
                {
                    var currentVersion = doc.Versions
                        .Where(v => v.EffectiveFrom <= now)
                        .MaxBy(v => v.EffectiveFrom);

                    if (currentVersion is not null)
                    {
                        var consent = userConsents.FirstOrDefault(c => c.DocumentVersionId == currentVersion.Id);
                        docPairs.Add((currentVersion, consent));
                    }
                }

                return (team, docPairs);
            })
            .ToList();

        return (groups, userConsents.ToList());
    }

    public async Task<(DocumentVersion? Version, ConsentRecord? ExistingConsent, string? UserFullName)>
        GetConsentReviewDetailAsync(Guid documentVersionId, Guid userId, CancellationToken ct = default)
    {
        var version = await _legalDocumentSyncService.GetVersionByIdAsync(documentVersionId, ct);

        if (version is null)
            return (null, null, null);

        // Chain-follow merge tombstones so a fold-target's review detail
        // transparently surfaces a consent record still attributed to a
        // merged source id.
        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        var consentRecord = chainIds is null
            ? await _repo.GetByUserAndVersionAsync(userId, documentVersionId, ct)
            : await _repo.GetByUserIdsAndVersionAsync(chainIds, documentVersionId, ct);

        // Cross-section lookup: profile is owned by Profiles section. Route
        // through IProfileService rather than querying _dbContext.Profiles.
        var profile = await _profileService.GetProfileAsync(userId, ct);

        return (version, consentRecord, profile?.FullName);
    }

    public async Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default)
    {
        var version = await _legalDocumentSyncService.GetVersionByIdAsync(documentVersionId, ct);

        if (version is null)
            return new ConsentSubmitResult(false, ErrorKey: "NotFound");

        // Chain-follow merge tombstones so a fold-target re-submitting a
        // consent already given by a merged source short-circuits with
        // AlreadyConsented rather than appending a duplicate.
        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        var alreadyConsented = chainIds is null
            ? await _repo.ExistsForUserAndVersionAsync(userId, documentVersionId, ct)
            : await _repo.ExistsForUserIdsAndVersionAsync(chainIds, documentVersionId, ct);

        if (alreadyConsented)
            return new ConsentSubmitResult(false, ErrorKey: "AlreadyConsented");

        var canonicalContent = version.Content.GetValueOrDefault("es", string.Empty);
        var contentHash = ComputeContentHash(canonicalContent);

        var consentRecord = new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DocumentVersionId = documentVersionId,
            ConsentedAt = _clock.GetCurrentInstant(),
            IpAddress = ipAddress,
            UserAgent = userAgent.Length > 500 ? userAgent[..500] : userAgent,
            ContentHash = contentHash,
            ExplicitConsent = explicitConsent
        };

        await _repo.AddAsync(consentRecord, ct);
        _metrics.RecordConsentGiven();

        _logger.LogInformation(
            "User {UserId} consented to document {DocumentName} version {Version}",
            userId, version.LegalDocument.Name, version.VersionNumber);

        await _onboardingService.SetConsentCheckPendingIfEligibleAsync(userId, ct);

        // Sync system team memberships (adds user if eligible + all consents done).
        await _syncJob.SyncVolunteersMembershipForUserAsync(userId);
        await _syncJob.SyncCoordinatorsMembershipForUserAsync(userId);

        // Auto-resolve AccessSuspended notifications only once ALL required consents are complete.
        try
        {
            var membershipCalc = _serviceProvider.GetRequiredService<IMembershipCalculator>();
            if (await membershipCalc.HasAllRequiredConsentsAsync(userId, ct))
            {
                await _notificationInboxService.ResolveBySourceAsync(userId, NotificationSource.AccessSuspended, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve AccessSuspended notifications for user {UserId}", userId);
        }

        return new ConsentSubmitResult(true, DocumentName: version.LegalDocument.Name);
    }

    public async Task<IReadOnlyList<ConsentRecord>> GetUserConsentRecordsAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Chain-follow merge tombstones so a fold-target's history transparently
        // surfaces records still attributed to merged source ids.
        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        return chainIds is null
            ? await _repo.GetAllForUserAsync(userId, ct)
            : await _repo.GetAllForUserIdsAsync(chainIds, ct);
    }

    public async Task<int> GetConsentRecordCountAsync(Guid userId, CancellationToken ct = default)
    {
        // Chain-follow merge tombstones so a fold-target's count transparently
        // includes records still attributed to merged source ids.
        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        return chainIds is null
            ? await _repo.GetCountForUserAsync(userId, ct)
            : await _repo.GetCountForUserIdsAsync(chainIds, ct);
    }

    public async Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Chain-follow merge tombstones so a fold-target's consented-version
        // set transparently includes versions explicitly consented to by
        // merged source ids.
        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        return chainIds is null
            ? await _repo.GetExplicitlyConsentedVersionIdsAsync(userId, ct)
            : await _repo.GetExplicitlyConsentedVersionIdsForUserIdsAsync(chainIds, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentMapForUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        // Chain-follow merge tombstones per input id so each fold-target's
        // entry transparently includes versions explicitly consented to by
        // its merged source ids. Source ids never appear as keys; only the
        // input ids do.
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlySet<Guid>>();

        // Fetch source-id mappings once per input id.
        // TODO(perf): a future batch primitive
        // IUserService.GetAllMergedSourceIdsByTargetsAsync(IReadOnlyCollection<Guid>)
        // doing a single `WHERE MergedToUserId IN (...)` query would reduce
        // this loop's N round-trips to 1. Negligible at 500-user scale per
        // CLAUDE.md "Scale and Deployment Context" — deferred.
        var sourcesByTarget = new Dictionary<Guid, IReadOnlySet<Guid>>(userIds.Count);
        foreach (var userId in userIds)
        {
            sourcesByTarget[userId] = await _userService.GetMergedSourceIdsAsync(userId, ct);
        }

        var hasAnySources = sourcesByTarget.Values.Any(s => s.Count > 0);
        if (!hasAnySources)
        {
            // Common case: no merged sources — single repo call, no remap.
            // Dedup defensively: callers (e.g., admin/all-user partition
            // paths) may pass overlapping ids.
            var distinctInputs = userIds.Distinct().ToList();
            return await _repo.GetExplicitlyConsentedVersionIdsForUsersAsync(distinctInputs, ct);
        }

        // Build a flattened set of {target ∪ all source ids} for the repo
        // batch, plus a reverse map source-id → target-id so we can re-key
        // source-attributed rows under their target. Dedup is required:
        // callers may pass both a source tombstone id and its target in the
        // same input list, and the source is also appended below — without
        // dedup the repo's ToDictionary call throws ArgumentException on
        // duplicate keys.
        var allIdsSet = new HashSet<Guid>(userIds);
        var sourceToTarget = new Dictionary<Guid, Guid>();
        foreach (var userId in userIds)
        {
            foreach (var sourceId in sourcesByTarget[userId])
            {
                allIdsSet.Add(sourceId);
                // If a source already maps to a target, keep the first
                // mapping; chain-follow is a 1:N target→sources fan-out, so
                // each source has exactly one target.
                sourceToTarget[sourceId] = userId;
            }
        }

        var raw = await _repo.GetExplicitlyConsentedVersionIdsForUsersAsync(allIdsSet.ToList(), ct);

        // Re-key: every input id gets a HashSet seeded from its own row, then
        // unioned with each source row that maps back to it.
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

    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        // Chain-follow merge tombstones so a fold-target's GDPR export
        // transparently includes consent records that stayed attributed to
        // merged source ids. Source User rows are anonymized by
        // AnonymizeForMergeAsync, so the records carry no source-actor PII.
        var chainIds = await GetChainFollowIdsAsync(userId, ct);
        var consents = chainIds is null
            ? await _repo.GetAllForUserAsync(userId, ct)
            : await _repo.GetAllForUserIdsAsync(chainIds, ct);

        var shaped = consents.Select(c => new
        {
            DocumentName = c.DocumentVersion.LegalDocument.Name,
            DocumentVersion = c.DocumentVersion.VersionNumber,
            c.ExplicitConsent,
            ConsentedAt = c.ConsentedAt.ToInvariantInstantString(),
            c.IpAddress,
            c.UserAgent
        }).ToList();

        return [new UserDataSlice(GdprExportSections.Consents, shaped)];
    }
}
