using Humans.Application.Interfaces;
using NodaTime;

namespace Humans.Consent.Contracts;

/// <summary>
/// The three legal-document reads that happen outside the section: Governance's
/// <c>MembershipCalculator</c> asks which versions a team requires,
/// <c>SendReConsentReminderJob</c> asks for the required version set, and
/// <c>HumansMetricsService</c> asks how many active+required documents exist.
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
    /// Count of currently active+required legal documents, for the metrics snapshot
    /// refresh — so the metrics service never reads <c>legal_documents</c> directly.
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
