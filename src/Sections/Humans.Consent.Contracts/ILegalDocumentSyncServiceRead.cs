using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Consent.Contracts;

/// <summary>
/// The three legal-document reads. The cross-section one is Governance's
/// <c>MembershipCalculator</c> asking which versions a team requires; the other callers
/// (<c>SendReConsentReminderJob</c>, <c>ConsentMetricsService</c>) are in-section.
/// </summary>
/// <remarks>
/// Carved from the call sites rather than from the interface (Notifications' rule, design
/// §15 step 5b): the full <c>ILegalDocumentSyncService</c> has ten members and returns the
/// section's entities from two of them, so moving it whole would have published the whole
/// document aggregate to serve three reads that only need a flat snapshot.
/// </remarks>
public interface ILegalDocumentSyncServiceRead : IApplicationService
{
    /// <summary>
    /// Current versions of every active+required document, across all teams.
    /// </summary>
    Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredVersionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The latest effective version per required+active document scoped to
    /// <paramref name="teamId"/>.
    /// </summary>
    Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredDocumentVersionsForTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count of currently active+required legal documents, for the section's metrics gauge.
    /// </summary>
    Task<int> GetActiveRequiredCountAsync(CancellationToken cancellationToken = default);
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
