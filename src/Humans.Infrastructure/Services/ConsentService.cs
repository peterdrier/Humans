using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

public class ConsentService : IConsentService
{
    private readonly HumansDbContext _dbContext;
    private readonly IOnboardingService _onboardingService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly INotificationInboxService _notificationInboxService;
    private readonly ISystemTeamSync _syncJob;
    private readonly IHumansMetrics _metrics;
    private readonly IClock _clock;
    private readonly ILogger<ConsentService> _logger;

    public ConsentService(
        HumansDbContext dbContext,
        IOnboardingService onboardingService,
        IMembershipCalculator membershipCalculator,
        INotificationInboxService notificationInboxService,
        ISystemTeamSync syncJob,
        IHumansMetrics metrics,
        IClock clock,
        ILogger<ConsentService> logger)
    {
        _dbContext = dbContext;
        _onboardingService = onboardingService;
        _membershipCalculator = membershipCalculator;
        _notificationInboxService = notificationInboxService;
        _syncJob = syncJob;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
    }

    public async Task<(List<(Team Team, List<(DocumentVersion Version, ConsentRecord? Consent)> Documents)> Groups,
        List<ConsentRecord> History)>
        GetConsentDashboardAsync(Guid userId, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();

        var userTeamIds = await _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, ct);

        var documents = await _dbContext.LegalDocuments
            .Where(d => d.IsActive && d.IsRequired && userTeamIds.Contains(d.TeamId))
            .Include(d => d.Team)
            .Include(d => d.Versions)
            .ToListAsync(ct);

        var userConsents = await _dbContext.ConsentRecords
            .Where(c => c.UserId == userId)
            .Include(c => c.DocumentVersion)
            .ThenInclude(v => v.LegalDocument)
            .OrderByDescending(c => c.ConsentedAt)
            .ToListAsync(ct);

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

        return (groups, userConsents);
    }

    public async Task<(DocumentVersion? Version, ConsentRecord? ExistingConsent, string? UserFullName)>
        GetConsentReviewDetailAsync(Guid documentVersionId, Guid userId, CancellationToken ct = default)
    {
        var version = await _dbContext.DocumentVersions
            .Include(v => v.LegalDocument)
            .FirstOrDefaultAsync(v => v.Id == documentVersionId, ct);

        if (version is null)
            return (null, null, null);

        var consentRecord = await _dbContext.ConsentRecords
            .FirstOrDefaultAsync(c => c.UserId == userId && c.DocumentVersionId == documentVersionId, ct);

        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        return (version, consentRecord, profile?.FullName);
    }

    public async Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default)
    {
        var version = await _dbContext.DocumentVersions
            .Include(v => v.LegalDocument)
            .FirstOrDefaultAsync(v => v.Id == documentVersionId, ct);

        if (version is null)
            return new ConsentSubmitResult(false, ErrorKey: "NotFound");

        var alreadyConsented = await _dbContext.ConsentRecords
            .AnyAsync(c => c.UserId == userId && c.DocumentVersionId == documentVersionId, ct);

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

        _dbContext.ConsentRecords.Add(consentRecord);
        await _dbContext.SaveChangesAsync(ct);
        _metrics.RecordConsentGiven();

        _logger.LogInformation(
            "User {UserId} consented to document {DocumentName} version {Version}",
            userId, version.LegalDocument.Name, version.VersionNumber);

        await _onboardingService.SetConsentCheckPendingIfEligibleAsync(userId, ct);

        // Sync system team memberships (adds user if eligible + all consents done)
        await _syncJob.SyncVolunteersMembershipForUserAsync(userId);
        await _syncJob.SyncCoordinatorsMembershipForUserAsync(userId);

        // Auto-resolve AccessSuspended notifications only once ALL required consents are complete
        try
        {
            if (await _membershipCalculator.HasAllRequiredConsentsAsync(userId, ct))
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

    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
