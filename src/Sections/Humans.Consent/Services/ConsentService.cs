using System.Security.Cryptography;
using System.Text;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Consent.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Governance.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Users.Contracts;

namespace Humans.Consent.Services;

internal sealed class ConsentService(
    IConsentRepository repo,
    CachingConsentService consentCache,
    ILegalDocumentSyncManagement legalDocumentSyncService,
    IHumanLifecycleService humanLifecycleService,
    IUserService userService,
    IServiceProvider serviceProvider,
    IHumansMetrics metrics,
    IClock clock,
    ILogger<ConsentService> logger) : IConsentManagement, IUserDataContributor
{
    private readonly ConsentReader reader = new(
        repo, legalDocumentSyncService, userService, serviceProvider, clock, logger);

    internal const string Consents = "Consents";

    public Task<ConsentDashboard> GetConsentDashboardAsync(Guid userId, CancellationToken ct = default) =>
        reader.GetConsentDashboardAsync(userId, ct);

    public Task<ConsentReviewDetail?> GetConsentReviewDetailAsync(
        Guid documentVersionId, Guid userId, CancellationToken ct = default) =>
        reader.GetConsentReviewDetailAsync(documentVersionId, userId, ct);

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
            var membershipCalc = serviceProvider.GetRequiredService<IMembershipCalculator>();
            if (await membershipCalc.HasAllRequiredConsentsAsync(userId, CancellationToken.None))
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

    public Task<int> GetConsentRecordCountAsync(Guid userId, CancellationToken ct = default) =>
        reader.GetConsentRecordCountAsync(userId, ct);

    public Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(Guid userId, CancellationToken ct = default) =>
        reader.GetConsentedVersionIdsAsync(userId, ct);

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetUserIdChainsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        reader.GetUserIdChainsAsync(userIds, ct);

    public Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentMapForUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default) =>
        reader.GetConsentMapForUsersAsync(userIds, ct);

    public Task<IReadOnlyList<RequiredConsentRow>> GetRequiredConsentRowsForUserAsync(
        Guid userId, Guid teamId, CancellationToken ct = default) =>
        reader.GetRequiredConsentRowsForUserAsync(userId, teamId, ct);

    public Task<IReadOnlyList<string>> GetPendingDocumentNamesAsync(Guid userId, CancellationToken ct = default) =>
        reader.GetPendingDocumentNamesAsync(userId, ct);

    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var chainIds = await reader.GetChainFollowIdsAsync(userId, ct);
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
}
