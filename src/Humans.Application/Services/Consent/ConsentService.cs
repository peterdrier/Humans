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
        _serviceProvider = serviceProvider;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
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

        var userConsents = await _repo.GetAllForUserAsync(userId, ct);

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

        var consentRecord = await _repo.GetByUserAndVersionAsync(userId, documentVersionId, ct);

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

        var alreadyConsented = await _repo.ExistsForUserAndVersionAsync(userId, documentVersionId, ct);

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

    public Task<IReadOnlyList<ConsentRecord>> GetUserConsentRecordsAsync(
        Guid userId, CancellationToken ct = default) =>
        _repo.GetAllForUserAsync(userId, ct);

    public Task<int> GetConsentRecordCountAsync(Guid userId, CancellationToken ct = default) =>
        _repo.GetCountForUserAsync(userId, ct);

    public Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(
        Guid userId, CancellationToken ct = default) =>
        _repo.GetExplicitlyConsentedVersionIdsAsync(userId, ct);

    public Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentMapForUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default) =>
        _repo.GetExplicitlyConsentedVersionIdsForUsersAsync(userIds, ct);

    public async Task<IReadOnlyList<string>> GetPendingDocumentNamesAsync(Guid userId, CancellationToken ct = default)
    {
        var membershipCalculator = _serviceProvider.GetRequiredService<IMembershipCalculator>();
        var missingVersionIds = await membershipCalculator.GetMissingConsentVersionsAsync(userId, ct);

        if (missingVersionIds.Count == 0)
            return Array.Empty<string>();

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var versionId in missingVersionIds)
        {
            var version = await _legalDocumentSyncService.GetVersionByIdAsync(versionId, ct);
            if (version?.LegalDocument is { } doc)
                names.Add(doc.Name);
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
        var consents = await _repo.GetAllForUserAsync(userId, ct);

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
