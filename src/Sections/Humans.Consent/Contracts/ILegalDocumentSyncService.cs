using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Consent.Contracts;

public interface ILegalDocumentSyncService : IApplicationService
{
    /// <summary>The latest effective version per required, active document.</summary>
    Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredDocumentVersionsForTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default);
}

public sealed record RequiredDocumentVersionSnapshot(
    Guid Id,
    Guid LegalDocumentId,
    string LegalDocumentName,
    int LegalDocumentGracePeriodDays,
    string VersionNumber,
    Instant EffectiveFrom,
    bool RequiresReConsent,
    string? ChangesSummary);
