using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Consent.Contracts;

/// <summary>
/// The one legal-document read that crosses the section boundary: Governance's
/// <c>MembershipCalculator</c> asking which versions a team requires.
/// </summary>
/// <remarks>
/// Carved from the call sites rather than from the full interface: the internal
/// <c>ILegalDocumentSyncManagement</c> returns the section's entities from two members,
/// so publishing it whole would have exposed the whole document aggregate to serve a read
/// that only needs a flat snapshot.
/// </remarks>
public interface ILegalDocumentSyncService : IApplicationService
{
    /// <summary>
    /// The latest effective version per required+active document scoped to
    /// <paramref name="teamId"/>.
    /// </summary>
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
